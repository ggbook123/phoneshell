namespace PhoneShell.Core.Terminals.Linux;

/// <summary>
/// Detects available shells on Linux by reading /etc/shells.
/// </summary>
public sealed class LinuxShellLocator : IShellLocator
{
    private const string ShellsFile = "/etc/shells";

    public List<ShellInfo> GetAvailableShells()
    {
        var shells = new List<ShellInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (File.Exists(ShellsFile))
        {
            foreach (var line in File.ReadAllLines(ShellsFile))
            {
                var path = line.Trim();
                if (string.IsNullOrEmpty(path) || path.StartsWith('#'))
                    continue;
                if (!File.Exists(path))
                    continue;

                var name = Path.GetFileName(path);
                // Deduplicate — /bin/bash and /usr/bin/bash are the same shell
                if (!seen.Add(name))
                    continue;

                var displayName = GetDisplayName(name);
                shells.Add(new ShellInfo(name, displayName, path, ""));
            }
        }

        // Fallback: if /etc/shells is missing or empty, try common paths
        if (shells.Count == 0)
        {
            TryAdd(shells, "bash", "/bin/bash");
            TryAdd(shells, "sh", "/bin/sh");
        }

        return shells;
    }

    public ShellInfo GetDefaultShell()
    {
        var shellEnv = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(shellEnv) && File.Exists(shellEnv))
        {
            var name = Path.GetFileName(shellEnv);
            return new ShellInfo(name, GetDisplayName(name), shellEnv, "");
        }

        // Fallback — /bin/sh is virtually guaranteed to exist
        if (File.Exists("/bin/bash"))
            return new ShellInfo("bash", "Bash", "/bin/bash", "");
        return new ShellInfo("sh", "POSIX Shell", "/bin/sh", "");
    }

    private static string GetDisplayName(string shellName) => shellName switch
    {
        "bash" => "Bash",
        "zsh" => "Zsh",
        "fish" => "Fish",
        "sh" => "POSIX Shell",
        "dash" => "Dash",
        "ksh" => "Korn Shell",
        "tcsh" => "TC Shell",
        "csh" => "C Shell",
        "pwsh" => "PowerShell",
        "nushell" or "nu" => "Nushell",
        _ => shellName
    };

    private static void TryAdd(List<ShellInfo> shells, string name, string path)
    {
        if (File.Exists(path))
            shells.Add(new ShellInfo(name, GetDisplayName(name), path, ""));
    }
}
