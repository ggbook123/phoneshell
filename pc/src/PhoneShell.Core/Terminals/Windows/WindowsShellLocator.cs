using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneShell.Core.Terminals.Windows;

public sealed class WindowsShellLocator : IShellLocator
{
    public List<ShellInfo> GetAvailableShells()
    {
        var shells = new List<ShellInfo>();

        // PowerShell 7+ (pwsh.exe)
        var pwsh = FindOnPath("pwsh.exe");
        if (pwsh is not null)
            shells.Add(new ShellInfo("pwsh", "PowerShell 7+", pwsh, "-NoLogo -NoExit"));

        // Windows PowerShell 5.1
        var powershell = FindOnPath("powershell.exe");
        if (powershell is not null)
            shells.Add(new ShellInfo("powershell", "Windows PowerShell", powershell, "-NoLogo -NoExit"));

        // CMD
        var cmd = FindOnPath("cmd.exe");
        if (cmd is not null)
            shells.Add(new ShellInfo("cmd", "Command Prompt", cmd, ""));

        // WSL distributions
        var wslExe = FindOnPath("wsl.exe");
        if (wslExe is not null)
        {
            var distros = GetWslDistributions();
            if (distros.Count > 0)
            {
                foreach (var distro in distros)
                {
                    shells.Add(new ShellInfo(
                        $"wsl-{distro.ToLowerInvariant()}",
                        $"WSL: {distro}",
                        wslExe,
                        $"-d {distro}"));
                }
            }
            else
            {
                // Add default WSL entry even if we can't list distros
                shells.Add(new ShellInfo("wsl", "WSL (default)", wslExe, ""));
            }
        }

        // Git Bash
        var gitBash = FindGitBash();
        if (gitBash is not null)
            shells.Add(new ShellInfo("git-bash", "Git Bash", gitBash, "--login -i"));

        return shells;
    }

    public ShellInfo GetDefaultShell()
    {
        var shells = GetAvailableShells();
        return shells.Count > 0
            ? shells[0]
            : new ShellInfo("powershell", "Windows PowerShell", "powershell.exe", "-NoLogo -NoExit");
    }

    private static List<string> GetWslDistributions()
    {
        var distros = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "--list --quiet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            };

            using var process = Process.Start(psi);
            if (process is null) return distros;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0) return distros;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim().Trim('\0', '\r', '\uFEFF');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    distros.Add(trimmed);
            }
        }
        catch
        {
            // WSL not available or error listing distros
        }

        return distros;
    }

    private static string? FindGitBash()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return FindOnPath("bash.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
            return null;

        foreach (var path in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(path.Trim(), fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
