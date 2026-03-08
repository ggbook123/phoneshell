using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneShell.Core.Networking;

public static class RelayAddressHelper
{
    private static readonly string[] VirtualAdapterKeywords =
    {
        "virtual",
        "vmware",
        "hyper-v",
        "vethernet",
        "vpn",
        "wintun",
        "tailscale",
        "zerotier",
        "docker",
        "loopback",
        "bluetooth",
        "npcap",
        "hamachi"
    };

    public static string NormalizeClientWebSocketUrl(string rawInput, int defaultPort = 9090)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            throw new ArgumentException("Relay server address cannot be empty.");

        var normalized = StripNestedScheme(rawInput.Trim());

        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            normalized = $"ws://{normalized["http://".Length..]}";
        else if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            normalized = $"wss://{normalized["https://".Length..]}";
        else if (!normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                 !normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            normalized = $"ws://{normalized}";

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            throw new ArgumentException("Use a valid ws:// or wss:// address.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Missing host.");

        if (IsWildcardHost(uri.Host))
            throw new ArgumentException("Use the PC's real LAN IP, not 0.0.0.0, +, or *.");

        var builder = new UriBuilder(uri);
        if (!HasExplicitPort(normalized))
            builder.Port = defaultPort;

        builder.Path = NormalizeRelayPath(builder.Path);
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;

        return builder.Uri.AbsoluteUri;
    }

    public static IReadOnlyList<string> GetBindableHttpPrefixes(int port)
    {
        var prefixes = GetReachableIpv4Addresses()
            .Select(ip => $"http://{ip}:{port}/ws/")
            .ToList();

        prefixes.Add(GetLocalhostHttpPrefix(port));
        return prefixes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> GetReachableWebSocketUrls(int port)
    {
        var urls = GetReachableIpv4Addresses()
            .Select(ip => $"ws://{ip}:{port}/ws/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
            urls.Add(GetLocalhostWebSocketUrl(port));

        return urls;
    }

    public static string GetLocalhostHttpPrefix(int port) => $"http://localhost:{port}/ws/";

    public static string GetWildcardHttpPrefix(int port) => $"http://+:{port}/ws/";

    public static string GetLocalhostWebSocketUrl(int port) => $"ws://localhost:{port}/ws/";

    public static string ToWebSocketUrl(string httpPrefix)
    {
        var prefix = httpPrefix.Trim();
        if (prefix.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return $"ws://{prefix["http://".Length..]}";
        if (prefix.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"wss://{prefix["https://".Length..]}";
        return prefix;
    }

    private static IReadOnlyList<string> GetReachableIpv4Addresses()
    {
        var candidates = new List<(string Address, int Score)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var nicText = $"{nic.Name} {nic.Description}";
            var looksVirtual = VirtualAdapterKeywords.Any(keyword =>
                nicText.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (!IsBindableAddress(address))
                    continue;

                var score = 0;
                if (IsPrivateIpv4(address))
                    score += 100;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    score += 40;
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                    score += 30;

                if (!looksVirtual)
                    score += 20;

                candidates.Add((address.ToString(), score));
            }
        }

        var privateAddresses = candidates
            .Where(candidate => IsPrivateIpv4(IPAddress.Parse(candidate.Address)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
            .Select(candidate => candidate.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (privateAddresses.Count > 0)
            return privateAddresses;

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
            .Select(candidate => candidate.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsBindableAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224)
            return false;

        if (bytes[0] == 169 && bytes[1] == 254)
            return false;

        // 198.18.0.0/15 is reserved for benchmark testing, not LAN access.
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            return false;

        return true;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string StripNestedScheme(string value)
    {
        var result = value;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var outerScheme in new[] { "ws://", "wss://", "http://", "https://" })
            {
                if (!result.StartsWith(outerScheme, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rest = result[outerScheme.Length..];
                foreach (var innerScheme in new[] { "ws://", "wss://", "http://", "https://" })
                {
                    if (rest.StartsWith(innerScheme, StringComparison.OrdinalIgnoreCase))
                    {
                        result = rest;
                        changed = true;
                        break;
                    }
                }

                if (changed)
                    break;
            }
        }

        return result;
    }

    private static bool HasExplicitPort(string value)
    {
        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        var authority = schemeSeparator >= 0 ? value[(schemeSeparator + 3)..] : value;
        var slashIndex = authority.IndexOf('/');
        if (slashIndex >= 0)
            authority = authority[..slashIndex];

        if (authority.StartsWith("[", StringComparison.Ordinal))
            return authority.Contains("]:", StringComparison.Ordinal);

        return authority.Count(ch => ch == ':') == 1;
    }

    private static string NormalizeRelayPath(string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (trimmed == "/" || trimmed.Length == 0)
            return "/ws/";

        return trimmed.Equals("/ws", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("/ws/", StringComparison.OrdinalIgnoreCase)
            ? "/ws/"
            : trimmed;
    }

    private static bool IsWildcardHost(string host)
    {
        return host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("+", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("*", StringComparison.OrdinalIgnoreCase);
    }
}
