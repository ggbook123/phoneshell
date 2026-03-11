using PhoneShell.Core.Models;
using PhoneShell.Core.Services;
using Xunit;

namespace PhoneShell.Core.Tests;

public class QrPayloadBuilderTests
{
    private readonly QrPayloadBuilder _builder = new();

    [Fact]
    public void Build_ReturnsValidUri()
    {
        var identity = new DeviceIdentity
        {
            DeviceId = "test-device-id",
            DisplayName = "My PC"
        };
        var result = _builder.Build(identity);

        Assert.StartsWith("phoneshell://bind?", result);
        Assert.Contains("deviceId=test-device-id", result);
        Assert.Contains("name=My%20PC", result);
        Assert.Contains("nonce=", result);
    }

    [Fact]
    public void Build_EscapesSpecialCharacters()
    {
        var identity = new DeviceIdentity
        {
            DeviceId = "dev&id=1",
            DisplayName = "PC & Server"
        };
        var result = _builder.Build(identity);

        Assert.StartsWith("phoneshell://bind?", result);
        Assert.Contains("deviceId=dev%26id%3D1", result);
        Assert.Contains("name=PC%20%26%20Server", result);
    }

    [Fact]
    public void BuildGroupBind_ReturnsValidUri()
    {
        var result = _builder.BuildGroupBind(
            "ws://192.168.1.100:9000/ws/",
            "group-123",
            "secret-abc"
        );

        Assert.StartsWith("phoneshell://bind?", result);
        Assert.Contains("server=", result);
        Assert.Contains("groupId=group-123", result);
        Assert.Contains("groupSecret=secret-abc", result);
        Assert.Contains("nonce=", result);
    }

    [Fact]
    public void BuildGroupBind_EscapesServerUrl()
    {
        var result = _builder.BuildGroupBind(
            "ws://host:9000/ws/",
            "g1",
            "s1"
        );

        Assert.StartsWith("phoneshell://bind?", result);
        // The server URL should be escaped
        Assert.Contains("server=ws%3A%2F%2Fhost%3A9000%2Fws%2F", result);
    }

    [Fact]
    public void Build_GeneratesDifferentNonces()
    {
        var identity = new DeviceIdentity
        {
            DeviceId = "dev1",
            DisplayName = "PC"
        };
        var result1 = _builder.Build(identity);
        var result2 = _builder.Build(identity);

        // Nonces should be different
        Assert.NotEqual(result1, result2);
    }

    [Fact]
    public void BuildGroupBind_GeneratesDifferentNonces()
    {
        var result1 = _builder.BuildGroupBind("ws://a/", "g1", "s1");
        var result2 = _builder.BuildGroupBind("ws://a/", "g1", "s1");

        Assert.NotEqual(result1, result2);
    }
}
