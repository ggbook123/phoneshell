namespace PhoneShell.Core.Services;

/// <summary>
/// Determines whether specific actions require mobile device authorization.
/// Used by RelayServer to gate sensitive operations.
/// </summary>
public static class PermissionChecker
{
    private static readonly HashSet<string> MobileAuthActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "terminal.open.remote",     // Opening a terminal on a remote device
        "group.server.change",      // Changing the relay server node
        "group.kick",               // Kicking a member from the group
        "group.secret.rotate",      // Rotating the group secret
        "mobile.unbind",            // Unbinding the mobile device
    };

    /// <summary>
    /// Returns true if the given action requires authorization from the bound mobile device.
    /// </summary>
    public static bool RequiresMobileAuth(string action)
    {
        return MobileAuthActions.Contains(action);
    }

    /// <summary>
    /// Returns true if the given member role has permission to perform the action
    /// without additional authorization.
    /// </summary>
    public static bool HasDirectPermission(Models.MemberRole role, string action)
    {
        // Mobile has direct permission for everything
        if (role == Models.MemberRole.Mobile)
            return true;

        // Server has direct permission for most actions except those requiring mobile auth
        if (role == Models.MemberRole.Server)
        {
            return !RequiresMobileAuth(action);
        }

        // Regular members can only do basic actions
        return action switch
        {
            "terminal.open.local" => true,      // Open terminal on own device
            "terminal.input" => true,
            "terminal.resize" => true,
            "terminal.close" => true,
            _ => false
        };
    }
}
