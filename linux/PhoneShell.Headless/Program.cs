using PhoneShell.Core.Services;
using PhoneShell.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace PhoneShell.Headless;

/// <summary>
/// PhoneShell Headless — cross-platform terminal service for Linux and Windows.
/// Runs as a console application, managed via config file and CLI arguments.
///
/// Usage:
///   phoneshell [options]
///
/// Options:
///   --config &lt;path&gt;           Config file path (default varies by OS)
///   --name &lt;name&gt;             Device display name
///   --port &lt;port&gt;             Relay server listen port (default: 9090)
///   --relay &lt;url&gt;             Relay server URL to connect to
///   --relay-token &lt;token&gt;     Shared relay bearer token
///   --mode &lt;server|client&gt;    Quick mode switch
///   --setup                    Interactive first-time setup
///   --admin                    Admin management console
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

        // Handle --admin
        if (args.Contains("--admin"))
        {
            RunAdminConsole(GetConfigPath(args));
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
        var allowAutoMode = !HasExplicitModeOverride(args);
        ApplyAutoMode(config, baseDirectory, allowAutoMode);

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
        using var host = new HeadlessHost(config, baseDirectory, configPath);
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
                return Path.GetFullPath(args[i + 1]);
        }

        // Default: platform-appropriate config location
        // Linux: ~/.config/phoneshell/config.json (or $XDG_CONFIG_HOME/phoneshell/config.json)
        // Windows: AppContext.BaseDirectory/data/config.json
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.GetFullPath(Path.Combine(configHome, "phoneshell", "config.json"));
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "config.json"));
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
        if (string.IsNullOrEmpty(configDir))
            return AppContext.BaseDirectory;

        var trimmed = configDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirName = Path.GetFileName(trimmed);

        // If config path is .../data/config.json (Windows default), return the parent to avoid .../data/data.
        if (string.Equals(dirName, "data", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(trimmed);
            return string.IsNullOrEmpty(parent) ? configDir : parent;
        }

        return configDir;
    }

    private static bool HasExplicitModeOverride(string[] args)
    {
        if (args.Any(arg =>
                arg is "--mode"
                    or "--enable-relay-server" or "--disable-relay-server"
                    or "--enable-relay-client" or "--disable-relay-client"))
        {
            return true;
        }

        var envMode = Environment.GetEnvironmentVariable("PHONESHELL_MODE");
        return !string.IsNullOrWhiteSpace(envMode);
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

    private static void ApplyAutoMode(HeadlessConfig config, string baseDirectory, bool allowAutoMode)
    {
        if (!allowAutoMode)
            return;

        var groupStore = new GroupStore(baseDirectory);
        var hasGroup = groupStore.LoadGroup() is not null;
        var hasRelayUrl = !string.IsNullOrWhiteSpace(config.RelayUrl);
        var hasGroupSecret = !string.IsNullOrWhiteSpace(config.GroupSecret);

        // If a server group exists locally, prefer relay-server.
        if (hasGroup)
        {
            // If a relay URL is explicitly configured, do not override client intent.
            if (hasRelayUrl)
                return;

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
        var defaultConfigPath = GetConfigPath(Array.Empty<string>());
        Console.WriteLine($"  --config <path>           Config file path (default: {defaultConfigPath})");
        Console.WriteLine("  --name <name>             Device display name");
        Console.WriteLine("  --port <port>             Relay server listen port (default: 9090)");
        Console.WriteLine("  --relay <url>             Relay server URL to connect to");
        Console.WriteLine("  --relay-token <token>     Shared relay bearer token (legacy)");
        Console.WriteLine("  --group-secret <key>      Group secret for group-based auth");
        Console.WriteLine("  --public-host <host:port> Public address for NAT/reverse proxy");
        Console.WriteLine("  --mode <server|client>    Quick mode switch");
        Console.WriteLine("  --setup                   Interactive first-time setup");
        Console.WriteLine("  --admin                   Admin management console");
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

    private static void RunAdminConsole(string configPath)
    {
        var baseDirectory = GetDataDirectory(configPath);
        var groupStore = new GroupStore(baseDirectory);
        var config = HeadlessConfig.Load(configPath);

        Console.WriteLine("PhoneShell Admin Console");
        Console.WriteLine(new string('=', 40));
        Console.WriteLine();

        // Try to detect running service
        var pidInfo = GetServicePid();
        if (pidInfo is not null)
            Console.WriteLine($"Server Status: Running (PID: {pidInfo})");
        else
            Console.WriteLine("Server Status: Not detected");

        Console.WriteLine($"Config: {configPath}");
        Console.WriteLine($"Data:   {Path.Combine(baseDirectory, "data")}");
        Console.WriteLine();

        while (true)
        {
            Console.WriteLine("[1] View service status");
            Console.WriteLine("[2] Change server port");
            Console.WriteLine("[3] Change public host");
            Console.WriteLine("[4] Change device name");
            Console.WriteLine("[5] View group info");
            Console.WriteLine("[6] Unbind mobile");
            Console.WriteLine("[7] Reset group secret");
            Console.WriteLine("[8] View config file");
            Console.WriteLine("[9] Restart service");
            Console.WriteLine("[0] Exit");
            Console.Write("\nSelect option: ");

            var input = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (input)
            {
                case "1":
                    AdminViewStatus(config, configPath, baseDirectory, groupStore);
                    break;
                case "2":
                    AdminChangePort(config, configPath);
                    break;
                case "3":
                    AdminChangePublicHost(config, configPath);
                    break;
                case "4":
                    AdminChangeDeviceName(config, configPath);
                    break;
                case "5":
                    AdminViewGroup(groupStore);
                    break;
                case "6":
                    AdminUnbindMobile(groupStore);
                    break;
                case "7":
                    AdminResetGroupSecret(config, configPath, groupStore);
                    break;
                case "8":
                    AdminViewConfig(configPath);
                    break;
                case "9":
                    AdminRestartService();
                    break;
                case "0":
                    return;
                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }

            Console.WriteLine();
        }
    }

    private static void AdminViewStatus(HeadlessConfig config, string configPath, string baseDirectory, GroupStore groupStore)
    {
        var group = groupStore.LoadGroup();
        var pidInfo = GetServicePid();

        Console.WriteLine("--- Service Status ---");
        Console.WriteLine($"Status:       {(pidInfo is not null ? $"Running (PID: {pidInfo})" : "Not detected")}");
        Console.WriteLine($"Config:       {configPath}");
        Console.WriteLine($"Display Name: {config.DisplayName}");
        Console.WriteLine($"Port:         {config.Port}");
        Console.WriteLine($"Public Host:  {(string.IsNullOrWhiteSpace(config.PublicHost) ? "(auto-detect)" : config.PublicHost)}");
        Console.WriteLine($"Mode:         {(config.Modules.RelayServer ? "Server" : "Client")}");
        Console.WriteLine($"Web Panel:    {(config.Modules.WebPanel ? "Enabled" : "Disabled")}");

        if (group is not null)
        {
            Console.WriteLine($"Group ID:     {group.GroupId}");
            Console.WriteLine($"Members:      {group.Members.Count}");
            Console.WriteLine($"Bound Mobile: {group.BoundMobileId ?? "(none)"}");
        }
        else
        {
            Console.WriteLine("Group:        Not initialized");
        }
    }

    private static void AdminChangePort(HeadlessConfig config, string configPath)
    {
        Console.Write($"Current port: {config.Port}\nNew port [1-65535]: ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return;
        if (!int.TryParse(input, out var port) || port < 1 || port > 65535)
        {
            Console.WriteLine("Invalid port number.");
            return;
        }
        config.Port = port;
        config.Save(configPath);
        Console.WriteLine($"Port changed to {port}. Restart the service to apply.");
    }

    private static void AdminChangePublicHost(HeadlessConfig config, string configPath)
    {
        Console.Write($"Current public host: {(string.IsNullOrWhiteSpace(config.PublicHost) ? "(auto-detect)" : config.PublicHost)}\n");
        Console.Write("New public host (e.g. 1.2.3.4:9090, empty to clear): ");
        var input = Console.ReadLine()?.Trim();
        config.PublicHost = input ?? string.Empty;
        config.Save(configPath);
        Console.WriteLine(string.IsNullOrWhiteSpace(config.PublicHost)
            ? "Public host cleared (will auto-detect). Restart the service to apply."
            : $"Public host changed to {config.PublicHost}. Restart the service to apply.");
    }

    private static void AdminChangeDeviceName(HeadlessConfig config, string configPath)
    {
        Console.Write($"Current name: {config.DisplayName}\nNew name: ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            Console.WriteLine("Name cannot be empty.");
            return;
        }
        config.DisplayName = input;
        config.Save(configPath);
        Console.WriteLine($"Device name changed to \"{input}\". Restart the service to apply.");
    }

    private static void AdminViewGroup(GroupStore groupStore)
    {
        var group = groupStore.LoadGroup();
        if (group is null)
        {
            Console.WriteLine("No group initialized.");
            return;
        }

        Console.WriteLine("--- Group Info ---");
        Console.WriteLine($"Group ID:      {group.GroupId}");
        Console.WriteLine($"Group Secret:  {group.GroupSecret}");
        Console.WriteLine($"Server Device: {group.ServerDeviceId}");
        Console.WriteLine($"Bound Mobile:  {group.BoundMobileId ?? "(none)"}");
        Console.WriteLine($"Created At:    {group.CreatedAt:u}");
        Console.WriteLine($"Members ({group.Members.Count}):");

        foreach (var member in group.Members)
        {
            Console.WriteLine($"  - {member.DisplayName} ({member.DeviceId})");
            Console.WriteLine($"    Role: {member.Role}, OS: {member.Os}, Joined: {member.JoinedAt:u}");
        }
    }

    private static void AdminUnbindMobile(GroupStore groupStore)
    {
        var group = groupStore.LoadGroup();
        if (group is null)
        {
            Console.WriteLine("No group initialized.");
            return;
        }

        if (string.IsNullOrEmpty(group.BoundMobileId))
        {
            Console.WriteLine("No mobile is currently bound.");
            return;
        }

        var mobileMember = group.Members.FirstOrDefault(m => m.DeviceId == group.BoundMobileId);
        var mobileName = mobileMember?.DisplayName ?? group.BoundMobileId;

        Console.Write($"Unbind mobile \"{mobileName}\" ({group.BoundMobileId})? [y/N]: ");
        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (confirm != "y" && confirm != "yes")
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        // Remove mobile member from the group
        group.Members.RemoveAll(m => m.DeviceId == group.BoundMobileId);
        group.BoundMobileId = null;
        groupStore.SaveGroup(group);
        Console.WriteLine("Mobile unbound successfully. Restart the service to apply.");
    }

    private static void AdminResetGroupSecret(HeadlessConfig config, string configPath, GroupStore groupStore)
    {
        var group = groupStore.LoadGroup();
        if (group is null)
        {
            Console.WriteLine("No group initialized.");
            return;
        }

        Console.Write("Reset group secret? All existing clients will need the new secret to reconnect. [y/N]: ");
        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (confirm != "y" && confirm != "yes")
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        var newSecret = GenerateRelayAuthToken() + GenerateRelayAuthToken(); // 64-char secret
        group.GroupSecret = newSecret;
        groupStore.SaveGroup(group);

        // Also update config if it had an explicit group secret
        if (!string.IsNullOrWhiteSpace(config.GroupSecret))
        {
            config.GroupSecret = newSecret;
            config.Save(configPath);
        }

        Console.WriteLine($"New group secret: {newSecret}");
        Console.WriteLine("Restart the service to apply. Share this secret with authorized clients.");
    }

    private static void AdminViewConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config file not found: {configPath}");
            return;
        }

        Console.WriteLine($"--- {configPath} ---");
        Console.WriteLine(File.ReadAllText(configPath));
    }

    private static void AdminRestartService()
    {
        Console.Write("Restart phoneshell service? [y/N]: ");
        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (confirm != "y" && confirm != "yes")
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "restart phoneshell",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                proc.WaitForExit(10000);
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                if (proc.ExitCode == 0)
                    Console.WriteLine("Service restarted successfully.");
                else
                    Console.WriteLine($"Restart failed (exit {proc.ExitCode}): {stderr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to restart service: {ex.Message}");
            Console.WriteLine("You may need to restart manually: systemctl restart phoneshell");
        }
    }

    private static string? GetServicePid()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "show phoneshell --property=MainPID --value",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                proc.WaitForExit(5000);
                var pid = proc.StandardOutput.ReadToEnd().Trim();
                if (proc.ExitCode == 0 && pid != "0" && !string.IsNullOrEmpty(pid))
                    return pid;
            }
        }
        catch
        {
            // systemctl not available or service not registered
        }
        return null;
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
