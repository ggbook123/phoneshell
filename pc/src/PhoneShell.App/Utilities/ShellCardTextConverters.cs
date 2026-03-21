using System;
using System.Globalization;
using System.Windows.Data;
using PhoneShell.Core.Terminals;

namespace PhoneShell.Utilities;

public sealed class ShellCardTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ShellInfo shell)
        {
            return string.Empty;
        }

        return ShellCardLabelHelper.GetShortLabel(shell);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class ShellCardSubtitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ShellInfo shell)
        {
            return string.Empty;
        }

        return ShellCardLabelHelper.GetDetailLabel(shell);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

internal static class ShellCardLabelHelper
{
    private const string PowerShellId = "powershell";
    private const string CmdId = "cmd";
    private const string GitBashId = "git-bash";
    private const string BashId = "bash";
    private const string WslId = "wsl";
    private const string WslPrefix = "wsl-";

    public static string GetShortLabel(ShellInfo shell)
    {
        var id = NormalizeId(shell.Id);
        if (id == PowerShellId)
            return "powershell";
        if (id == CmdId)
            return "cmd";
        if (IsWslId(id))
            return "WSL";
        if (id == GitBashId || id == BashId)
            return "BASH";

        return !string.IsNullOrWhiteSpace(shell.DisplayName) ? shell.DisplayName : shell.Id;
    }

    public static string GetDetailLabel(ShellInfo shell)
    {
        var id = NormalizeId(shell.Id);
        if (id == PowerShellId)
            return PreferDisplay(shell, "Windows PowerShell");
        if (id == CmdId)
            return PreferDisplay(shell, "Command Prompt");
        if (IsWslId(id))
            return PreferDisplay(shell, shell.Id);
        if (id == GitBashId || id == BashId)
            return PreferDisplay(shell, "Git Bash");

        return shell.Id;
    }

    private static bool IsWslId(string id)
    {
        return id == WslId || id.StartsWith(WslPrefix, StringComparison.Ordinal);
    }

    private static string NormalizeId(string id)
    {
        return string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim().ToLowerInvariant();
    }

    private static string PreferDisplay(ShellInfo shell, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(shell.DisplayName))
            return shell.DisplayName;
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;
        return shell.Id;
    }
}
