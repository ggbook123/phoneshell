using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PhoneShell.Core.Services;

/// <summary>
/// Manages one-time invite codes for adding devices to a group.
/// Codes are 8-character base64url strings, valid for 5 minutes, single-use.
/// </summary>
public sealed class InviteManager
{
    private readonly ConcurrentDictionary<string, InviteEntry> _invites = new();
    private static readonly TimeSpan InviteTtl = TimeSpan.FromMinutes(5);
    private const int CodeLength = 8;

    public (string Code, DateTimeOffset ExpiresAt) GenerateInviteCode()
    {
        CleanupExpired();

        var bytes = RandomNumberGenerator.GetBytes(CodeLength);
        var code = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=')[..CodeLength];

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(InviteTtl);

        _invites[code] = new InviteEntry(code, now, expiresAt);
        return (code, expiresAt);
    }

    /// <summary>
    /// Check if an invite code is valid without consuming it.
    /// </summary>
    public bool IsValidInviteCode(string code)
    {
        CleanupExpired();

        if (!_invites.TryGetValue(code, out var entry))
            return false;

        return DateTimeOffset.UtcNow <= entry.ExpiresAt;
    }

    /// <summary>
    /// Validate and consume a one-time invite code. Returns true if valid.
    /// </summary>
    public bool ConsumeInviteCode(string code)
    {
        CleanupExpired();

        if (!_invites.TryRemove(code, out var entry))
            return false;

        return DateTimeOffset.UtcNow <= entry.ExpiresAt;
    }

    public void ClearAll()
    {
        _invites.Clear();
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _invites)
        {
            if (now > kvp.Value.ExpiresAt)
                _invites.TryRemove(kvp.Key, out _);
        }
    }

    private sealed record InviteEntry(string Code, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
}
