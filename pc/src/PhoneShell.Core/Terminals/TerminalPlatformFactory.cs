using System.Runtime.InteropServices;

namespace PhoneShell.Core.Terminals;

/// <summary>
/// Creates platform-appropriate ITerminalSession and IShellLocator instances.
/// </summary>
public static class TerminalPlatformFactory
{
    /// <summary>
    /// Creates a new terminal session for the current OS.
    /// </summary>
    public static ITerminalSession CreateSession()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new Windows.ConPtySession();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new Linux.PtySession();

        throw new PlatformNotSupportedException(
            $"Terminal sessions are not supported on {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// Creates a shell locator for the current OS.
    /// </summary>
    public static IShellLocator CreateShellLocator()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new Windows.WindowsShellLocator();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new Linux.LinuxShellLocator();

        throw new PlatformNotSupportedException(
            $"Shell detection is not supported on {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// Returns the OS identifier string for protocol messages.
    /// </summary>
    public static string GetOsIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";
        return RuntimeInformation.OSDescription;
    }
}
