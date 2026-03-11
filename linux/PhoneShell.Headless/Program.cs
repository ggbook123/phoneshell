using PhoneShell.Core.Services;

namespace PhoneShell.Headless;

/// <summary>
/// PhoneShell Headless — cross-platform terminal service for Linux and Windows.
/// Runs as a console application, managed via config file and CLI arguments.
///
/// Usage:
///   phoneshell [options]
///
/// Options:
///   --config &lt;path&gt;           Config file path (default: data/config.json)
///   --name &lt;name&gt;             Device display name
///   --port &lt;port&gt;             Relay server listen port (default: 9090)
///   --relay &lt;url&gt;             Relay server URL to connect to
///   --relay-token &lt;token&gt;     Shared relay bearer token
///   --mode &lt;server|client&gt;    Quick mode switch
///   --setup                    Interactive first-time setup
///
/// Module toggles:
///   --enable-terminal          Enable PTY terminal module
///   --disable-terminal         Disable PTY terminal module
///   --enable-relay-server      Enable relay server module
///   --disable-relay-server     Disable relay server module
///   --enable-relay-client      Enable relay client module
///   --disable-relay-client     Disable relay client module
///   --enable-web-panel         Enable web management panel
///   --disable-web-panel        Disable web management panel
///   --enable-ai-chat           Enable AI chat module (future)
///   --disable-ai-chat          Disable AI chat module
///
/// Examples:
///   # Start as relay server (hub mode)
///   phoneshell --mode server --port 9000 --name "Main Server"
///
///   # Start as client connecting to a relay
///   phoneshell --mode client --relay ws://192.168.1.100:9000 --relay-token &lt;token&gt; --name "Dev Machine"
///
///   # Interactive setup
///   phoneshell --setup
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        // Handle --setup
        if (args.Contains("--setup"))
        {
            RunInteractiveSetup(GetConfigPath(args));
            return;
        }

        // Handle --help
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return;
        }

        // Load config
        var configPath = GetConfigPath(args);
        var config = HeadlessConfig.Load(configPath);
        config.ApplyEnvironmentVariables();
        config.ApplyCliArgs(args);

        try
        {
            config.Normalize();
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return;
        }

        var baseDirectory = GetDataDirectory(configPath);
        ApplyAutoMode(config, baseDirectory);

        // Validate
        if (!config.Modules.RelayServer && !config.Modules.RelayClient)
        {
            Console.WriteLine("Error: At least one of relay-server or relay-client must be enabled.");
            Console.WriteLine("Use --mode server or --mode client, or edit config file.");
            Console.WriteLine($"Config path: {configPath}");
            return;
        }

        if (config.Modules.RelayClient && string.IsNullOrWhiteSpace(config.RelayUrl))
        {
            Console.WriteLine("Error: relay-client is enabled but no relay URL is configured.");
            Console.WriteLine("Use --relay ws://host:port to specify the relay server.");
            return;
        }

        // Run
        using var host = new HeadlessHost(config, baseDirectory);
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await host.StartAsync(cts.Token);

            // Wait until cancelled
            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static string GetConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
                return args[i + 1];
        }

        // Default: platform-appropriate config location
        // Linux: ~/.config/phoneshell/config.json
        // Windows: AppContext.BaseDirectory/data/config.json
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(configHome, "phoneshell", "config.json");
        }

        return Path.Combine(AppContext.BaseDirectory, "data", "config.json");
    }

    /// <summary>
    /// Returns the base directory for runtime data (device.json, server-settings.json, etc.).
    /// On Linux: ~/.config/phoneshell/
    /// On Windows: same directory as the config file.
    /// </summary>
    private static string GetDataDirectory(string configPath)
    {
        // Data lives alongside config — the DeviceIdentityStore appends "data/" internally,
        // so we return the parent of that.
        var configDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(configDir))
            return configDir;
        return AppContext.BaseDirectory;
    }

    private static void RunInteractiveSetup(string configPath)
    {
        Console.WriteLine("PhoneShell Headless Setup");
        Console.WriteLine(new string('=', 40));
        Console.WriteLine();

        Console.WriteLine("[1] Relay Server (hub — other devices connect to this machine)");
        Console.WriteLine("[2] Relay Client (connect to an existing relay server)");
        Console.Write("Select mode [1/2]: ");
        var modeInput = Console.ReadLine()?.Trim();

        var config = new HeadlessConfig();

        if (modeInput == "1")
        {
            config.Modules.RelayServer = true;
            config.Modules.RelayClient = false;

            Console.Write($"Listen port [{config.Port}]: ");
            var portInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(portInput) && int.TryParse(portInput, out var port))
                config.Port = port;

            Console.Write("Public host (for NAT/reverse proxy, e.g. 1.2.3.4:9090) [auto-detect]: ");
            var publicHostInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(publicHostInput))
                config.PublicHost = publicHostInput;

            Console.Write("Group secret [auto-generate]: ");
            var groupSecretInput = Console.ReadLine()?.Trim();
            config.GroupSecret = string.IsNullOrWhiteSpace(groupSecretInput)
                ? GenerateRelayAuthToken()
                : groupSecretInput;
        }
        else
        {
            config.Modules.RelayServer = false;
            config.Modules.RelayClient = true;

            Console.Write("Relay server URL (e.g. ws://192.168.1.100:9090): ");
            var url = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(url))
                config.RelayUrl = url;

            Console.Write("Group secret (required to join the group): ");
            var groupSecretInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(groupSecretInput))
                config.GroupSecret = groupSecretInput;
        }

        Console.Write($"Device name [{Environment.MachineName}]: ");
        var nameInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(nameInput))
            config.DisplayName = nameInput;

        try
        {
            config.Normalize();
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Configuration error: {ex.Message}");
            return;
        }

        config.Save(configPath);

        Console.WriteLine();
        Console.WriteLine($"Config saved to: {configPath}");
        if (!string.IsNullOrWhiteSpace(config.GroupSecret))
            Console.WriteLine($"Group secret: {config.GroupSecret}");
        if (!string.IsNullOrWhiteSpace(config.GroupSecret))
            Console.WriteLine("Share this group secret with other PCs to let them join the group.");
        Console.WriteLine("Run 'phoneshell' to start the service.");
    }

    private static void ApplyAutoMode(HeadlessConfig config, string baseDirectory)
    {
        var groupStore = new GroupStore(baseDirectory);
        var hasGroup = groupStore.LoadGroup() is not null;
        var hasRelayUrl = !string.IsNullOrWhiteSpace(config.RelayUrl);
        var hasGroupSecret = !string.IsNullOrWhiteSpace(config.GroupSecret);

        // If a server group exists locally, prefer relay-server.
        if (hasGroup)
        {
            config.Modules.RelayServer = true;
            config.Modules.RelayClient = false;
            config.Modules.WebPanel = true;
            return;
        }

        // First run: no relay URL and no group secret -> auto server (so QR is available).
        if (config.Modules.RelayClient && !hasRelayUrl && !hasGroupSecret)
        {
            config.Modules.RelayServer = true;
            config.Modules.RelayClient = false;
            config.Modules.WebPanel = true;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PhoneShell Headless — Cross-platform terminal service");
        Console.WriteLine();
        Console.WriteLine("Usage: phoneshell [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --config <path>           Config file path (default: data/config.json)");
        Console.WriteLine("  --name <name>             Device display name");
        Console.WriteLine("  --port <port>             Relay server listen port (default: 9090)");
        Console.WriteLine("  --relay <url>             Relay server URL to connect to");
        Console.WriteLine("  --relay-token <token>     Shared relay bearer token (legacy)");
        Console.WriteLine("  --group-secret <key>      Group secret for group-based auth");
        Console.WriteLine("  --public-host <host:port> Public address for NAT/reverse proxy");
        Console.WriteLine("  --mode <server|client>    Quick mode switch");
        Console.WriteLine("  --setup                   Interactive first-time setup");
        Console.WriteLine("  --help, -h                Show this help");
        Console.WriteLine();
        Console.WriteLine("Module toggles:");
        Console.WriteLine("  --enable-terminal         --disable-terminal");
        Console.WriteLine("  --enable-relay-server     --disable-relay-server");
        Console.WriteLine("  --enable-relay-client     --disable-relay-client");
        Console.WriteLine("  --enable-web-panel        --disable-web-panel");
        Console.WriteLine("  --enable-ai-chat          --disable-ai-chat");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  phoneshell --mode server --port 9000 --name \"Main Server\"");
        Console.WriteLine("  phoneshell --mode client --relay ws://192.168.1.100:9000 --relay-token <token>");
        Console.WriteLine("  phoneshell --setup");
        Console.WriteLine();
        Console.WriteLine("Environment overrides:");
        Console.WriteLine("  PHONESHELL_MODE, PHONESHELL_NAME, PHONESHELL_PORT,");
        Console.WriteLine("  PHONESHELL_RELAY_URL, PHONESHELL_RELAY_TOKEN,");
        Console.WriteLine("  PHONESHELL_GROUP_SECRET, PHONESHELL_PUBLIC_HOST");
    }

    private static string GenerateRelayAuthToken()
    {
        Span<byte> buffer = stackalloc byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
