using PhoneShell.Core.Models;
using PhoneShell.Core.Services;
using Xunit;

namespace PhoneShell.Core.Tests;

public class GroupStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly GroupStore _store;

    public GroupStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"phoneshell-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _store = new GroupStore(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public void SaveAndLoadGroup_Roundtrip()
    {
        var group = new GroupInfo
        {
            GroupId = "test-group-1",
            GroupSecret = "secret123",
            ServerDeviceId = "dev1",
            CreatedAt = DateTimeOffset.UtcNow
        };
        group.Members.Add(new GroupMember
        {
            DeviceId = "dev1",
            DisplayName = "Server PC",
            Os = "Windows",
            Role = MemberRole.Server,
            JoinedAt = DateTimeOffset.UtcNow
        });

        _store.SaveGroup(group);

        var loaded = _store.LoadGroup();
        Assert.NotNull(loaded);
        Assert.Equal("test-group-1", loaded.GroupId);
        Assert.Equal("secret123", loaded.GroupSecret);
        Assert.Equal("dev1", loaded.ServerDeviceId);
        Assert.Single(loaded.Members);
        Assert.Equal("Server PC", loaded.Members[0].DisplayName);
        Assert.Equal(MemberRole.Server, loaded.Members[0].Role);
    }

    [Fact]
    public void LoadGroup_ReturnsNullWhenNoFile()
    {
        var emptyDir = Path.Combine(_testDir, "empty");
        Directory.CreateDirectory(emptyDir);
        var store = new GroupStore(emptyDir);
        Assert.Null(store.LoadGroup());
    }

    [Fact]
    public void SaveAndLoadMembership_Roundtrip()
    {
        var membership = new GroupMembership
        {
            GroupId = "g1",
            GroupSecret = "s123",
            ServerUrl = "ws://localhost:9000/ws/"
        };

        _store.SaveMembership(membership);

        var loaded = _store.LoadMembership();
        Assert.NotNull(loaded);
        Assert.Equal("g1", loaded.GroupId);
        Assert.Equal("s123", loaded.GroupSecret);
        Assert.Equal("ws://localhost:9000/ws/", loaded.ServerUrl);
    }

    [Fact]
    public void LoadMembership_ReturnsNullWhenNoFile()
    {
        var emptyDir = Path.Combine(_testDir, "empty2");
        Directory.CreateDirectory(emptyDir);
        var store = new GroupStore(emptyDir);
        Assert.Null(store.LoadMembership());
    }

    [Fact]
    public void SaveGroup_WithMultipleMembers()
    {
        var group = new GroupInfo
        {
            GroupId = "g2",
            GroupSecret = "multi-secret",
            ServerDeviceId = "server1"
        };
        group.Members.Add(new GroupMember
        {
            DeviceId = "server1",
            DisplayName = "Server",
            Os = "Linux",
            Role = MemberRole.Server,
            JoinedAt = DateTimeOffset.UtcNow
        });
        group.Members.Add(new GroupMember
        {
            DeviceId = "client1",
            DisplayName = "Client PC",
            Os = "Windows",
            Role = MemberRole.Member,
            JoinedAt = DateTimeOffset.UtcNow
        });
        group.Members.Add(new GroupMember
        {
            DeviceId = "mobile1",
            DisplayName = "Phone",
            Os = "HarmonyOS",
            Role = MemberRole.Mobile,
            JoinedAt = DateTimeOffset.UtcNow
        });

        _store.SaveGroup(group);

        var loaded = _store.LoadGroup();
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Members.Count);
    }
}
