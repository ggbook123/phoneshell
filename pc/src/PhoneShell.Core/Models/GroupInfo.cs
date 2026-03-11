namespace PhoneShell.Core.Models;

/// <summary>
/// Server-side group data containing all membership and binding info.
/// Persisted to data/group.json by GroupStore.
/// </summary>
public sealed class GroupInfo
{
    /// <summary>Unique group identifier (GUID).</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Shared secret (Base64Url, 32 bytes) used by clients to join the group.</summary>
    public string GroupSecret { get; set; } = string.Empty;

    /// <summary>Device ID of the current relay server.</summary>
    public string ServerDeviceId { get; set; } = string.Empty;

    /// <summary>Device ID of the bound mobile (null if no mobile is bound).</summary>
    public string? BoundMobileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>All group members (including the server itself).</summary>
    public List<GroupMember> Members { get; set; } = new();
}

/// <summary>
/// A member of a PhoneShell group.
/// </summary>
public sealed class GroupMember
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public MemberRole Role { get; set; } = MemberRole.Member;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> AvailableShells { get; set; } = new();
}

/// <summary>
/// Role within a PhoneShell group.
/// </summary>
public enum MemberRole
{
    /// <summary>The relay server node.</summary>
    Server,

    /// <summary>A PC client node.</summary>
    Member,

    /// <summary>A bound mobile device with admin privileges.</summary>
    Mobile
}

/// <summary>
/// Client-side group membership info (minimal, stored on non-server PCs).
/// Persisted to data/group-membership.json.
/// </summary>
public sealed class GroupMembership
{
    public string GroupId { get; set; } = string.Empty;
    public string GroupSecret { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
}
