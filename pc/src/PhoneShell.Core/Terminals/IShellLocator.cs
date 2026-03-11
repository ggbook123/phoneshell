namespace PhoneShell.Core.Terminals;

public record ShellInfo(string Id, string DisplayName, string ExecutablePath, string Arguments);

public interface IShellLocator
{
    List<ShellInfo> GetAvailableShells();
    ShellInfo GetDefaultShell();
}
