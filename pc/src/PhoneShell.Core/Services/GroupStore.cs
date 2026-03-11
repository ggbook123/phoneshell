using System.Security.Cryptography;
using System.Text.Json;
using PhoneShell.Core.Models;

namespace PhoneShell.Core.Services;

/// <summary>
/// Persists group data to JSON files.
/// Server-side: data/group.json (full GroupInfo).
/// Client-side: data/group-membership.json (minimal GroupMembership).
/// </summary>
public sealed class GroupStore
{
    private readonly string _groupFilePath;
    private readonly string _membershipFilePath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public GroupStore(string baseDirectory)
    {
        var dataDirectory = Path.Combine(baseDirectory, "data");
        _groupFilePath = Path.Combine(dataDirectory, "group.json");
        _membershipFilePath = Path.Combine(dataDirectory, "group-membership.json");
    }

    // --- Server-side: full group data ---

    /// <summary>Load the server-side group info, or null if not yet created.</summary>
    public GroupInfo? LoadGroup()
    {
        if (!File.Exists(_groupFilePath))
            return null;

        var json = File.ReadAllText(_groupFilePath);
        return JsonSerializer.Deserialize<GroupInfo>(json, _serializerOptions);
    }

    /// <summary>Save the server-side group info.</summary>
    public void SaveGroup(GroupInfo group)
    {
        EnsureDirectory(_groupFilePath);
        var json = JsonSerializer.Serialize(group, _serializerOptions);
        File.WriteAllText(_groupFilePath, json);
    }

    /// <summary>Create a new group with a fresh ID and secret.</summary>
    public GroupInfo CreateGroup(string serverDeviceId)
    {
        var group = new GroupInfo
        {
            GroupId = Guid.NewGuid().ToString("N"),
            GroupSecret = GenerateGroupSecret(),
            ServerDeviceId = serverDeviceId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        SaveGroup(group);
        return group;
    }

    // --- Client-side: minimal membership ---

    /// <summary>Load the client-side group membership, or null if not joined.</summary>
    public GroupMembership? LoadMembership()
    {
        if (!File.Exists(_membershipFilePath))
            return null;

        var json = File.ReadAllText(_membershipFilePath);
        return JsonSerializer.Deserialize<GroupMembership>(json, _serializerOptions);
    }

    /// <summary>Save the client-side group membership.</summary>
    public void SaveMembership(GroupMembership membership)
    {
        EnsureDirectory(_membershipFilePath);
        var json = JsonSerializer.Serialize(membership, _serializerOptions);
        File.WriteAllText(_membershipFilePath, json);
    }

    /// <summary>Delete the client-side membership file (leave group).</summary>
    public void ClearMembership()
    {
        if (File.Exists(_membershipFilePath))
            File.Delete(_membershipFilePath);
    }

    /// <summary>Delete the server-side group file (revoke server role).</summary>
    public void ClearGroup()
    {
        if (File.Exists(_groupFilePath))
            File.Delete(_groupFilePath);
    }

    // --- Helpers ---

    private static string GenerateGroupSecret()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }
}
