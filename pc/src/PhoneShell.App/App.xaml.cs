using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Threading;

namespace PhoneShell;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\PhoneShell.App.SingleInstance";
    private bool _isShuttingDownAfterFatalError;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireSingleInstanceLock())
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceLock();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalErrorAndShutdown("Dispatcher", "PhoneShell Error", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ShowFatalErrorAndShutdown("AppDomain", "PhoneShell Fatal Error", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTask", e.Exception);
        e.SetObserved();
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "crash.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n";
            File.AppendAllText(logFile, entry);
        }
        catch
        {
            // Best effort
        }
    }

    private void ShowFatalErrorAndShutdown(string source, string title, Exception ex)
    {
        if (_isShuttingDownAfterFatalError)
            return;

        _isShuttingDownAfterFatalError = true;
        LogCrash(source, ex);

        try
        {
            MessageBox.Show($"Unhandled error:\n{ex.Message}\n\n{ex.StackTrace}",
                title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Ignore UI failures and continue shutdown.
        }

        try
        {
            Shutdown(-1);
        }
        catch
        {
            ReleaseSingleInstanceLock();
            Environment.Exit(-1);
        }
    }

    private bool TryAcquireSingleInstanceLock()
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (createdNew)
            return true;

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        BringExistingInstanceToFront();
        return false;
    }

    private void ReleaseSingleInstanceLock()
    {
        if (_singleInstanceMutex is null)
            return;

        try
        {
            if (_ownsSingleInstanceMutex)
                _singleInstanceMutex.ReleaseMutex();
        }
        catch
        {
            // Ignore release failures on shutdown.
        }
        finally
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }
    }

    private static void BringExistingInstanceToFront()
    {
        var currentProcess = Process.GetCurrentProcess();

        foreach (var process in Process.GetProcessesByName(currentProcess.ProcessName))
        {
            if (process.Id == currentProcess.Id)
                continue;

            try
            {
                if (!string.Equals(process.MainModule?.FileName, currentProcess.MainModule?.FileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    continue;

                if (IsIconic(handle))
                    ShowWindowAsync(handle, SwRestore);
                else
                    ShowWindowAsync(handle, SwShow);

                SetForegroundWindow(handle);
                break;
            }
            catch
            {
                // Ignore process inspection failures.
            }
        }
    }

    private const int SwRestore = 9;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
