using System.Runtime.InteropServices;

namespace PhoneShell.Core.Terminals.Linux;

/// <summary>
/// Linux PTY terminal session using a native helper library (libptyhelper.so)
/// to perform the forkpty()+execvp() sequence entirely in native code.
///
/// This avoids the SIGSEGV crash caused by calling fork() inside the multi-threaded
/// .NET runtime, where the child process inherits a broken GC/threadpool state.
/// The native helper ensures only pure C code runs between fork() and execvp().
/// </summary>
public sealed class PtySession : ITerminalSession
{
    private volatile int _masterFd = -1;
    private int _childPid = -1;
    private Thread? _readThread;
    private volatile bool _disposed;

    public event Action<byte[]>? OutputReady;

    static PtySession()
    {
        NativeLibrary.SetDllImportResolver(typeof(PtySession).Assembly, (name, assembly, path) =>
        {
            if (name == "libptyhelper")
            {
                // Try loading from the application directory first
                var appDir = AppContext.BaseDirectory;
                var helperPath = Path.Combine(appDir, "libptyhelper.so");
                if (NativeLibrary.TryLoad(helperPath, out var handle))
                    return handle;
                // Fall back to system search
                if (NativeLibrary.TryLoad("libptyhelper", assembly, path, out handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    public void Start(string executable, string arguments, int cols, int rows)
    {
        var argv = string.IsNullOrWhiteSpace(arguments)
            ? new[] { executable, null! }
            : BuildArgv(executable, arguments);

        int masterFd;
        int childPid;
        var result = NativeMethods.PtySpawn(executable, argv, cols, rows,
            out masterFd, out childPid);

        if (result != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"pty_spawn() failed with errno {errno}");
        }

        _masterFd = masterFd;
        _childPid = childPid;

        _readThread = new Thread(ReadOutputLoop)
        {
            IsBackground = true,
            Name = "PTY-Read"
        };
        _readThread.Start();
    }

    public void Write(byte[] data)
    {
        var fd = _masterFd;
        if (fd < 0) return;
        int offset = 0;
        while (offset < data.Length)
        {
            var written = NativeMethods.Write(fd, data, offset, data.Length - offset);
            if (written < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                if (errno == 4) continue; // EINTR — retry
                break;
            }
            offset += written;
        }
    }

    public void Resize(int cols, int rows)
    {
        var fd = _masterFd;
        if (fd < 0) return;
        var winSize = new NativeMethods.WinSize
        {
            ws_col = (ushort)cols,
            ws_row = (ushort)rows,
            ws_xpixel = 0,
            ws_ypixel = 0
        };
        NativeMethods.Ioctl(fd, NativeMethods.TIOCSWINSZ, ref winSize);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var fd = _masterFd;
        _masterFd = -1;

        // Close master fd — this will cause the read thread to exit
        if (fd >= 0)
            NativeMethods.Close(fd);

        // Wait for read thread to finish first (it should exit once fd is closed)
        _readThread?.Join(2000);

        // Reap child process with WNOHANG + timeout to avoid blocking forever
        if (_childPid > 0)
        {
            const int WNOHANG = 1;
            var waited = NativeMethods.WaitPid(_childPid, out _, WNOHANG);
            if (waited == 0)
            {
                // Child still running — send SIGTERM, wait briefly, then SIGKILL
                NativeMethods.Kill(_childPid, 15); // SIGTERM
                Thread.Sleep(500);
                waited = NativeMethods.WaitPid(_childPid, out _, WNOHANG);
                if (waited == 0)
                {
                    NativeMethods.Kill(_childPid, 9); // SIGKILL
                    NativeMethods.WaitPid(_childPid, out _, 0);
                }
            }
            _childPid = -1;
        }
    }

    private void ReadOutputLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_disposed)
            {
                var fd = _masterFd;
                if (fd < 0) break;
                var bytesRead = NativeMethods.Read(fd, buffer, buffer.Length);
                if (bytesRead <= 0) break;
                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                OutputReady?.Invoke(data);
            }
        }
        catch (Exception)
        {
            // PTY closed or process exited
        }
    }

    private static string[] BuildArgv(string executable, string arguments)
    {
        var args = new List<string> { executable };
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        args.AddRange(parts);
        args.Add(null!); // execvp requires null-terminated array
        return args.ToArray();
    }

    private static class NativeMethods
    {
        // TIOCSWINSZ ioctl number — 0x5414 on Linux
        internal const uint TIOCSWINSZ = 0x5414;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WinSize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;
        }

        // Native helper — does forkpty+execvp entirely in C code.
        [DllImport("libptyhelper", EntryPoint = "pty_spawn", SetLastError = true)]
        internal static extern int PtySpawn(
            [MarshalAs(UnmanagedType.LPStr)] string executable,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv,
            int cols,
            int rows,
            out int masterFd,
            out int childPid);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        internal static extern int Read(int fd, byte[] buf, int count);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static extern int WriteNative(int fd, byte[] buf, int count);

        internal static int Write(int fd, byte[] buf, int offset, int count)
        {
            if (offset == 0)
                return WriteNative(fd, buf, count);

            var sub = new byte[count];
            Array.Copy(buf, offset, sub, 0, count);
            return WriteNative(fd, sub, count);
        }

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int fd);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        internal static extern int Ioctl(int fd, uint request, ref WinSize winSize);

        [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
        internal static extern int WaitPid(int pid, out int status, int options);

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int pid, int signal);
    }
}
