using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhoneShell.Core.Terminals.Windows;

public sealed class ConPtySession : ITerminalSession
{
    private SafeFileHandle? _pipeInRead;
    private SafeFileHandle? _pipeInWrite;
    private SafeFileHandle? _pipeOutRead;
    private SafeFileHandle? _pipeOutWrite;
    private IntPtr _consoleHandle;
    private SafeProcessHandle? _processHandle;
    private SafeFileHandle? _processThreadHandle;
    private Thread? _readThread;
    private FileStream? _writeStream;
    private bool _disposed;

    public event Action<byte[]>? OutputReady;

    public void Start(string executable, string arguments, int cols, int rows)
    {
        var commandLine = string.IsNullOrEmpty(arguments)
            ? $"\"{executable}\""
            : $"\"{executable}\" {arguments}";

        CreatePipes(out _pipeInRead, out _pipeInWrite, out _pipeOutRead, out _pipeOutWrite);

        _consoleHandle = CreatePseudoConsole(cols, rows, _pipeInRead, _pipeOutWrite);

        // These ends are now owned by the pseudo console — close our copies.
        _pipeInRead.Dispose();
        _pipeInRead = null;
        _pipeOutWrite.Dispose();
        _pipeOutWrite = null;

        StartProcess(commandLine, _consoleHandle, out _processHandle, out _processThreadHandle);

        _writeStream = new FileStream(_pipeInWrite, FileAccess.Write, bufferSize: 256, isAsync: false);

        _readThread = new Thread(ReadOutputLoop) { IsBackground = true, Name = "ConPTY-Read" };
        _readThread.Start();
    }

    public void Write(byte[] data)
    {
        if (_writeStream is null) return;
        _writeStream.Write(data, 0, data.Length);
        _writeStream.Flush();
    }

    public void Resize(int cols, int rows)
    {
        if (_consoleHandle == IntPtr.Zero) return;
        var coord = new NativeMethods.COORD { X = (short)cols, Y = (short)rows };
        NativeMethods.ResizePseudoConsole(_consoleHandle, coord);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_consoleHandle != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_consoleHandle);
            _consoleHandle = IntPtr.Zero;
        }

        _processThreadHandle?.Dispose();
        _processHandle?.Dispose();
        _writeStream?.Dispose();
        _pipeInWrite?.Dispose();
        _pipeOutRead?.Dispose();
        _pipeInRead?.Dispose();
        _pipeOutWrite?.Dispose();

        // Thread will exit when pipe closes.
        _readThread?.Join(2000);
    }

    private void ReadOutputLoop()
    {
        try
        {
            using var stream = new FileStream(_pipeOutRead!, FileAccess.Read, bufferSize: 4096, isAsync: false);
            var buffer = new byte[4096];
            while (!_disposed)
            {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0) break;
                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                OutputReady?.Invoke(data);
            }
        }
        catch (IOException) { /* Pipe closed */ }
        catch (ObjectDisposedException) { }
    }

    private static void CreatePipes(
        out SafeFileHandle inRead, out SafeFileHandle inWrite,
        out SafeFileHandle outRead, out SafeFileHandle outWrite)
    {
        if (!NativeMethods.CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe failed: {Marshal.GetLastWin32Error()}");
        if (!NativeMethods.CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe failed: {Marshal.GetLastWin32Error()}");
    }

    private static IntPtr CreatePseudoConsole(int cols, int rows, SafeFileHandle input, SafeFileHandle output)
    {
        var size = new NativeMethods.COORD { X = (short)cols, Y = (short)rows };
        int hr = NativeMethods.CreatePseudoConsole(size, input, output, 0, out var handle);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
        return handle;
    }

    private static void StartProcess(string command, IntPtr consoleHandle,
        out SafeProcessHandle processHandle, out SafeFileHandle threadHandle)
    {
        var attrListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize.ToInt32());

        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            if (!NativeMethods.UpdateProcThreadAttribute(attrList, 0,
                    NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, consoleHandle,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>()
                },
                lpAttributeList = attrList
            };

            if (!NativeMethods.CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero,
                    false, NativeMethods.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null,
                    ref startupInfo, out var processInfo))
                throw new InvalidOperationException($"CreateProcessW failed: {Marshal.GetLastWin32Error()}");

            processHandle = new SafeProcessHandle(processInfo.hProcess, true);
            threadHandle = new SafeFileHandle(processInfo.hThread, true);
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    private static class NativeMethods
    {
        internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        internal struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFO { public int cb; public IntPtr lpReserved; public IntPtr lpDesktop; public IntPtr lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    }
}
