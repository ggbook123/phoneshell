using PhoneShell.Core.Protocol;
using Xunit;

namespace PhoneShell.Core.Tests;

public class MessageSerializerTests
{
    [Fact]
    public void GetMessageType_ReturnsCorrectType()
    {
        var json = """{"type":"device.register","deviceId":"abc","displayName":"PC1","os":"Windows","availableShells":[]}""";
        var type = MessageSerializer.GetMessageType(json);
        Assert.Equal("device.register", type);
    }

    [Fact]
    public void GetMessageType_ReturnsNullForInvalidJson()
    {
        Assert.Null(MessageSerializer.GetMessageType("not json"));
    }

    [Fact]
    public void GetMessageType_ReturnsNullForMissingType()
    {
        Assert.Null(MessageSerializer.GetMessageType("""{"data":"value"}"""));
    }

    [Fact]
    public void Roundtrip_DeviceRegisterMessage()
    {
        var msg = new DeviceRegisterMessage
        {
            DeviceId = "dev1",
            DisplayName = "My PC",
            Os = "Windows",
            AvailableShells = new List<string> { "PowerShell", "CMD" }
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as DeviceRegisterMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("dev1", deserialized.DeviceId);
        Assert.Equal("My PC", deserialized.DisplayName);
        Assert.Equal("Windows", deserialized.Os);
        Assert.Equal(2, deserialized.AvailableShells.Count);
    }

    [Fact]
    public void Roundtrip_TerminalOutputMessage()
    {
        var msg = new TerminalOutputMessage
        {
            DeviceId = "dev1",
            SessionId = "s1",
            Data = "Hello World\r\n"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as TerminalOutputMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("dev1", deserialized.DeviceId);
        Assert.Equal("s1", deserialized.SessionId);
        Assert.Equal("Hello World\r\n", deserialized.Data);
    }

    [Fact]
    public void Roundtrip_TerminalInputMessage()
    {
        var msg = new TerminalInputMessage
        {
            DeviceId = "dev1",
            SessionId = "s1",
            Data = "ls\n"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as TerminalInputMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("ls\n", deserialized.Data);
    }

    [Fact]
    public void Roundtrip_GroupJoinRequestMessage()
    {
        var msg = new GroupJoinRequestMessage
        {
            GroupSecret = "secret123",
            DeviceId = "dev1",
            DisplayName = "Test",
            Os = "Linux",
            AvailableShells = new List<string> { "bash" }
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as GroupJoinRequestMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("secret123", deserialized.GroupSecret);
        Assert.Equal("dev1", deserialized.DeviceId);
    }

    [Fact]
    public void Roundtrip_GroupJoinAcceptedMessage()
    {
        var msg = new GroupJoinAcceptedMessage
        {
            GroupId = "g1",
            Members = new List<GroupMemberInfo>
            {
                new() { DeviceId = "d1", DisplayName = "PC1", Os = "Windows", Role = "Server", IsOnline = true }
            }
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as GroupJoinAcceptedMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("g1", deserialized.GroupId);
        Assert.Single(deserialized.Members);
        Assert.Equal("d1", deserialized.Members[0].DeviceId);
    }

    [Fact]
    public void Roundtrip_MobileBindRequestMessage()
    {
        var msg = new MobileBindRequestMessage
        {
            GroupId = "g1",
            MobileDeviceId = "m1",
            MobileDisplayName = "Phone"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as MobileBindRequestMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("g1", deserialized.GroupId);
        Assert.Equal("m1", deserialized.MobileDeviceId);
    }

    [Fact]
    public void Roundtrip_AuthRequestMessage()
    {
        var msg = new AuthRequestMessage
        {
            RequestId = "r1",
            Action = "terminal.open",
            RequesterId = "dev1",
            RequesterName = "PC1",
            TargetDeviceId = "dev2",
            Description = "Open terminal on PC2"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as AuthRequestMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("r1", deserialized.RequestId);
        Assert.Equal("terminal.open", deserialized.Action);
        Assert.Equal("dev2", deserialized.TargetDeviceId);
    }

    [Fact]
    public void Roundtrip_GroupServerChangeRequestMessage()
    {
        var msg = new GroupServerChangeRequestMessage
        {
            NewServerDeviceId = "dev2",
            RequesterId = "m1"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as GroupServerChangeRequestMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("dev2", deserialized.NewServerDeviceId);
        Assert.Equal("m1", deserialized.RequesterId);
    }

    [Fact]
    public void Roundtrip_GroupServerChangeCommitMessage()
    {
        var msg = new GroupServerChangeCommitMessage
        {
            NewServerUrl = "ws://newhost:9000/ws/",
            GroupId = "g1",
            GroupSecret = "newsecret"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as GroupServerChangeCommitMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("ws://newhost:9000/ws/", deserialized.NewServerUrl);
        Assert.Equal("g1", deserialized.GroupId);
        Assert.Equal("newsecret", deserialized.GroupSecret);
    }

    [Fact]
    public void Roundtrip_GroupSecretRotateRequestMessage()
    {
        var msg = new GroupSecretRotateRequestMessage
        {
            RequesterId = "m1"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as GroupSecretRotateRequestMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("m1", deserialized.RequesterId);
    }

    [Fact]
    public void Roundtrip_GroupSecretRotateDoneMessage()
    {
        var msg = new GroupSecretRotateDoneMessage
        {
            NewSecret = "rotatednewsecret123"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as GroupSecretRotateDoneMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("rotatednewsecret123", deserialized.NewSecret);
    }

    [Fact]
    public void Roundtrip_ErrorMessage()
    {
        var msg = new ErrorMessage
        {
            Code = "permission_denied",
            Message = "Not authorized"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as ErrorMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("permission_denied", deserialized.Code);
        Assert.Equal("Not authorized", deserialized.Message);
    }

    [Fact]
    public void DeserializeMessage_ReturnsNullForUnknownType()
    {
        var json = """{"type":"unknown.message","data":"test"}""";
        Assert.Null(MessageSerializer.DeserializeMessage(json));
    }

    [Fact]
    public void Roundtrip_TerminalOpenMessage()
    {
        var msg = new TerminalOpenMessage
        {
            DeviceId = "dev1",
            ShellId = "powershell"
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as TerminalOpenMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("dev1", deserialized.DeviceId);
        Assert.Equal("powershell", deserialized.ShellId);
    }

    [Fact]
    public void Roundtrip_TerminalOpenedMessage()
    {
        var msg = new TerminalOpenedMessage
        {
            DeviceId = "dev1",
            SessionId = "s1",
            Cols = 120,
            Rows = 40
        };
        var json = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.DeserializeMessage(json) as TerminalOpenedMessage;
        Assert.NotNull(deserialized);
        Assert.Equal(120, deserialized.Cols);
        Assert.Equal(40, deserialized.Rows);
    }
}
