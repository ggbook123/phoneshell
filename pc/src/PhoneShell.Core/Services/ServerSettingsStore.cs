using System.Text.Json;
using PhoneShell.Core.Models;

namespace PhoneShell.Core.Services;

public sealed class ServerSettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public ServerSettingsStore(string baseDirectory)
    {
        var dataDirectory = Path.Combine(baseDirectory, "data");
        _filePath = Path.Combine(dataDirectory, "server-settings.json");
    }

    public ServerSettings Load()
    {
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<ServerSettings>(json, _serializerOptions);
            if (settings is not null)
                return settings;
        }

        return new ServerSettings();
    }

    public void Save(ServerSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, _serializerOptions);
        File.WriteAllText(_filePath, json);
    }
}
