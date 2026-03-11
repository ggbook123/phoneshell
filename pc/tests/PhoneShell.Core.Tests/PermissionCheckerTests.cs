using PhoneShell.Core.Models;
using PhoneShell.Core.Services;
using Xunit;

namespace PhoneShell.Core.Tests;

public class PermissionCheckerTests
{
    [Theory]
    [InlineData("terminal.open.remote", true)]
    [InlineData("group.server.change", true)]
    [InlineData("group.kick", true)]
    [InlineData("group.secret.rotate", true)]
    [InlineData("mobile.unbind", true)]
    [InlineData("terminal.open.local", false)]
    [InlineData("terminal.input", false)]
    [InlineData("terminal.resize", false)]
    [InlineData("unknown.action", false)]
    public void RequiresMobileAuth_ReturnsCorrectResult(string action, bool expected)
    {
        Assert.Equal(expected, PermissionChecker.RequiresMobileAuth(action));
    }

    [Fact]
    public void RequiresMobileAuth_IsCaseInsensitive()
    {
        Assert.True(PermissionChecker.RequiresMobileAuth("Terminal.Open.Remote"));
        Assert.True(PermissionChecker.RequiresMobileAuth("GROUP.KICK"));
    }

    [Theory]
    [InlineData(MemberRole.Mobile, "terminal.open.remote", true)]
    [InlineData(MemberRole.Mobile, "group.server.change", true)]
    [InlineData(MemberRole.Mobile, "group.kick", true)]
    [InlineData(MemberRole.Mobile, "terminal.input", true)]
    [InlineData(MemberRole.Mobile, "unknown.action", true)]
    public void HasDirectPermission_MobileHasAllPermissions(MemberRole role, string action, bool expected)
    {
        Assert.Equal(expected, PermissionChecker.HasDirectPermission(role, action));
    }

    [Theory]
    [InlineData(MemberRole.Server, "terminal.open.local", true)]
    [InlineData(MemberRole.Server, "terminal.input", true)]
    [InlineData(MemberRole.Server, "terminal.open.remote", false)]
    [InlineData(MemberRole.Server, "group.server.change", false)]
    [InlineData(MemberRole.Server, "group.kick", false)]
    public void HasDirectPermission_ServerPermissions(MemberRole role, string action, bool expected)
    {
        Assert.Equal(expected, PermissionChecker.HasDirectPermission(role, action));
    }

    [Theory]
    [InlineData(MemberRole.Member, "terminal.open.local", true)]
    [InlineData(MemberRole.Member, "terminal.input", true)]
    [InlineData(MemberRole.Member, "terminal.resize", true)]
    [InlineData(MemberRole.Member, "terminal.close", true)]
    [InlineData(MemberRole.Member, "terminal.open.remote", false)]
    [InlineData(MemberRole.Member, "group.server.change", false)]
    [InlineData(MemberRole.Member, "group.kick", false)]
    [InlineData(MemberRole.Member, "unknown.action", false)]
    public void HasDirectPermission_MemberPermissions(MemberRole role, string action, bool expected)
    {
        Assert.Equal(expected, PermissionChecker.HasDirectPermission(role, action));
    }
}
