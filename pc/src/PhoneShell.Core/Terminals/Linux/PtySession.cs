using System.Runtime.InteropServices;

namespace PhoneShell.Core.Terminals.Linux;

/// <summary>
/// Linux PTY terminal session using forkpty().
/// Equivalent of ConPtySession for Linux.
///
/// NOTE: forkpty() calls fork() internally. In a managed .NET runtime, the child process
/// after fork has only the calling thread; the GC/threadpool/finalizer are in undefined state.
/// We minimize managed code in the child (only execvp + _exit) to reduce risk.
/// If this proves unstable, consider using a native helper library for the fork+exec sequence.
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
        // Register a DLL import resolver to find forkpty in the correct library.
        // On glibc-based Linux (Debian, Ubuntu, CentOS, etc.) forkpty is in libutil.
        // On musl-based Linux (Alpine) it is in libc itself.
        NativeLibrary.SetDllImportResolver(typeof(PtySession).Assembly, (name, assembly, path) =>
        {
            if (name == "libutil")
            {
                // Try libutil first (glibc), fall back to libc (musl/Alpine)
                if (NativeLibrary.TryLoad("libutil", assembly, path, out var handle))
                    return handle;
                if (NativeLibrary.TryLoad("libc", assembly, path, out handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    public void Start(string executable, string arguments, int cols, int rows)
    {
        var winSize = new NativeMethods.WinSize
        {
            ws_col = (ushort)cols,
            ws_row = (ushort)rows,
            ws_xpixel = 0,
            ws_ypixel = 0
        };

        int masterFd;
        var pid = NativeMethods.ForkPty(out masterFd, IntPtr.Zero, IntPtr.Zero, ref winSize);

        if (pid < 0)
        {
            var errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"forkpty() failed with errno {errno}");
        }

        if (pid == 0)
        {
            // Child process — exec the shell immediately.
            // Minimize managed code here; the .NET runtime is in an undefined state after fork.
            var argv = string.IsNullOrWhiteSpace(arguments)
                ? new[] { executable, null! }
                : BuildArgv(executable, arguments);

            NativeMethods.Execvp(executable, argv);
            // If execvp returns, it failed
            NativeMethods.Exit(1);
        }

        // Parent process
        _masterFd = masterFd;
        _childPid = pid;

        _readThread = new Thread(ReadOutputLoop) { IsBackground = true, Name = "PTY-Read" };
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

        // forkpty lives in libutil on glibc, libc on musl.
        // The DLL import resolver in the static constructor handles the fallback.
        [DllImport("libutil", EntryPoint = "forkpty", SetLastError = true)]
        internal static extern int ForkPty(
            out int masterFd,
            IntPtr name,
            IntPtr termios,
            ref WinSize winSize);

        [DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
        internal static extern int Execvp(
            [MarshalAs(UnmanagedType.LPStr)] string file,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv);

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

        [DllImport("libc", EntryPoint = "_exit")]
        internal static extern void Exit(int status);
    }
}
