using System.Text.Json;
using PhoneShell.Core.Models;

namespace PhoneShell.Core.Services;

public sealed class DeviceIdentityStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public DeviceIdentityStore(string baseDirectory)
    {
        var dataDirectory = Path.Combine(baseDirectory, "data");
        _filePath = Path.Combine(dataDirectory, "device.json");
    }

    public DeviceIdentity LoadOrCreate()
    {
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            var identity = JsonSerializer.Deserialize<DeviceIdentity>(json, _serializerOptions);
            if (identity is not null && !string.IsNullOrWhiteSpace(identity.DeviceId))
            {
                return identity;
            }
        }

        var created = DeviceIdentity.Create();
        Save(created);
        return created;
    }

    public void Save(DeviceIdentity identity)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(identity, _serializerOptions);
        File.WriteAllText(_filePath, json);
    }
}
