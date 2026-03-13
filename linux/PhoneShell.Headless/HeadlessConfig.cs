using System.Text.Json;
using PhoneShell.Core.Networking;

namespace PhoneShell.Headless;

/// <summary>
/// Headless configuration with modular feature toggles.
/// Each module can be independently enabled/disabled via config or CLI args.
///
/// ┌─────────────────────────────────────────────────────────┐
/// │ Module Architecture                                     │
/// │                                                         │
/// │ [terminal]      — PTY session management (core)         │
/// │ [relay-server]  — WebSocket relay hub mode              │
/// │ [relay-client]  — Connect to existing relay server      │
/// │ [web-panel]     — Web management UI                     │
/// │ [ai-chat]       — AI assistant integration (future)     │
/// │                                                         │
/// │ Modules are toggled via config.json "modules" section   │
/// │ or CLI flags: --enable-relay-server --disable-ai-chat   │
/// └─────────────────────────────────────────────────────────┘
/// </summary>
public sealed class HeadlessConfig
{
    /// <summary>Device display name shown in relay device list.</summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Public hostname or IP for this server, used when behind NAT.
    /// When set, replaces auto-detected internal IPs in advertised URLs and QR codes.
    /// Example: "43.140.37.188" or "relay.example.com"
    /// </summary>
    public string PublicHost { get; set; } = string.Empty;

    /// <summary>Relay server listen port (only used when relay-server module is enabled).</summary>
    public int Port { get; set; } = 9090;

    /// <summary>Relay server URL to connect to (only used when relay-client module is enabled).</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared bearer token used to protect relay WebSocket connections and status APIs.
    /// Can also be provided via the PHONESHELL_RELAY_TOKEN environment variable.
    /// Deprecated: prefer GroupSecret for group-based authentication.
    /// </summary>
    public string RelayAuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Group secret for joining an existing group (client mode) or initializing a new group (server mode).
    /// Can also be provided via the PHONESHELL_GROUP_SECRET environment variable.
    /// Takes precedence over RelayAuthToken when set.
    /// </summary>
    public string GroupSecret { get; set; } = string.Empty;

    /// <summary>Default terminal size.</summary>
    public int DefaultCols { get; set; } = 120;
    public int DefaultRows { get; set; } = 30;

    /// <summary>
    /// Module enable/disable flags.
    /// Set to true to enable, false to disable.
    /// </summary>
    public ModuleConfig Modules { get; set; } = new();

    public sealed class ModuleConfig
    {
        /// <summary>
        /// [MODULE: terminal]
        /// Core PTY session management. Allows remote clients to open/control terminals.
        /// Almost always enabled — disable only if this node is a pure relay proxy.
        /// </summary>
        public bool Terminal { get; set; } = true;

        /// <summary>
        /// [MODULE: relay-server]
        /// Run as a WebSocket relay hub. Other PCs and mobile clients connect to this node.
        /// Mutually exclusive with relay-client in typical deployments.
        /// </summary>
        public bool RelayServer { get; set; } = false;

        /// <summary>
        /// [MODULE: relay-client]
        /// Connect to an existing relay server as a client device.
        /// Requires RelayUrl to be set in the parent config.
        /// </summary>
        public bool RelayClient { get; set; } = true;

        /// <summary>
        /// [MODULE: web-panel]
        /// Serve a web management UI on the relay server HTTP port.
        /// Only effective when relay-server is also enabled.
        /// </summary>
        public bool WebPanel { get; set; } = true;

        /// <summary>
        /// [MODULE: ai-chat]
        /// AI assistant that can analyze terminal output and suggest commands.
        /// Requires API key configuration.
        /// TODO: Implement in future version.
        /// </summary>
        public bool AiChat { get; set; } = false;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Load config from JSON file, or create default if not found.
    /// </summary>
    public static HeadlessConfig Load(string filePath)
    {
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<HeadlessConfig>(json, SerializerOptions);
            if (config is not null)
                return config;
        }

        return new HeadlessConfig();
    }

    /// <summary>
    /// Save config to JSON file.
    /// </summary>
    public void Save(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Apply CLI argument overrides to the config.
    /// Supports: --name, --port, --relay, --enable-*, --disable-*
    /// </summary>
    public void ApplyCliArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--name" when i + 1 < args.Length:
                    DisplayName = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var port) && port is >= 1 and <= 65535)
                        Port = port;
                    break;
                case "--relay" when i + 1 < args.Length:
                    RelayUrl = args[++i];
                    break;
                case "--relay-token" when i + 1 < args.Length:
                    RelayAuthToken = args[++i];
                    break;
                case "--group-secret" when i + 1 < args.Length:
                    GroupSecret = args[++i];
                    break;
                case "--public-host" when i + 1 < args.Length:
                    PublicHost = args[++i];
                    break;
                case "--mode" when i + 1 < args.Length:
                    var mode = args[++i].ToLowerInvariant();
                    if (mode == "server" || mode == "relay-server")
                    {
                        Modules.RelayServer = true;
                        Modules.RelayClient = false;
                    }
                    else if (mode == "client" || mode == "relay-client")
                    {
                        Modules.RelayServer = false;
                        Modules.RelayClient = true;
                    }
                    else if (mode == "standalone")
                    {
                        // Standalone: run local server, no group auth required
                        Modules.RelayServer = true;
                        Modules.RelayClient = false;
                    }
                    break;
                case "--enable-terminal":
                    Modules.Terminal = true;
                    break;
                case "--disable-terminal":
                    Modules.Terminal = false;
                    break;
                case "--enable-relay-server":
                    Modules.RelayServer = true;
                    break;
                case "--disable-relay-server":
                    Modules.RelayServer = false;
                    break;
                case "--enable-relay-client":
                    Modules.RelayClient = true;
                    break;
                case "--disable-relay-client":
                    Modules.RelayClient = false;
                    break;
                case "--enable-web-panel":
                    Modules.WebPanel = true;
                    break;
                case "--disable-web-panel":
                    Modules.WebPanel = false;
                    break;
                case "--enable-ai-chat":
                    Modules.AiChat = true;
                    break;
                case "--disable-ai-chat":
                    Modules.AiChat = false;
                    break;
            }
        }
    }

    /// <summary>
    /// Apply environment variable overrides after loading config and before CLI args.
    /// Supported keys:
    /// PHONESHELL_NAME, PHONESHELL_PORT, PHONESHELL_RELAY_URL,
    /// PHONESHELL_RELAY_TOKEN, PHONESHELL_MODE.
    /// </summary>
    public void ApplyEnvironmentVariables()
    {
        var displayName = GetTrimmedEnvironmentVariable("PHONESHELL_NAME");
        if (displayName is not null)
            DisplayName = displayName;

        var relayUrl = GetTrimmedEnvironmentVariable("PHONESHELL_RELAY_URL");
        if (relayUrl is not null)
            RelayUrl = relayUrl;

        var relayToken = GetTrimmedEnvironmentVariable("PHONESHELL_RELAY_TOKEN");
        if (relayToken is not null)
            RelayAuthToken = relayToken;

        var groupSecret = GetTrimmedEnvironmentVariable("PHONESHELL_GROUP_SECRET");
        if (groupSecret is not null)
            GroupSecret = groupSecret;

        var publicHost = GetTrimmedEnvironmentVariable("PHONESHELL_PUBLIC_HOST");
        if (publicHost is not null)
            PublicHost = publicHost;

        var portValue = GetTrimmedEnvironmentVariable("PHONESHELL_PORT");
        if (portValue is not null && int.TryParse(portValue, out var port) && port is >= 1 and <= 65535)
            Port = port;

        var modeValue = GetTrimmedEnvironmentVariable("PHONESHELL_MODE")?.ToLowerInvariant();
        if (modeValue == "server" || modeValue == "relay-server")
        {
            Modules.RelayServer = true;
            Modules.RelayClient = false;
        }
        else if (modeValue == "client" || modeValue == "relay-client")
        {
            Modules.RelayServer = false;
            Modules.RelayClient = true;
        }
        else if (modeValue == "standalone")
        {
            Modules.RelayServer = true;
            Modules.RelayClient = false;
        }
    }

    private static string? GetTrimmedEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Normalize and validate values that may come from file, env, or CLI.
    /// </summary>
    public void Normalize()
    {
        if (!string.IsNullOrWhiteSpace(RelayUrl))
            RelayUrl = RelayAddressHelper.NormalizeClientWebSocketUrl(RelayUrl, Port);

        RelayAuthToken = RelayAuthToken.Trim();
        GroupSecret = GroupSecret.Trim();
        DisplayName = DisplayName.Trim();
        PublicHost = PublicHost.Trim();
    }
}
