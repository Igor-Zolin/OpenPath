using System.Text.Json;

namespace SshEnv.WinUI;

internal static class ProfileProtocol
{
    public static string Describe(VlessProfile profile, ConnectionMode mode = ConnectionMode.SingBoxTun)
    {
        var (preferredPath, alternatePath) = mode == ConnectionMode.SystemProxy
            ? (profile.SystemProxyConfigPath, profile.TunConfigPath)
            : (profile.TunConfigPath, profile.SystemProxyConfigPath);
        return ReadDescription(preferredPath) ??
            (string.Equals(preferredPath, alternatePath, StringComparison.OrdinalIgnoreCase)
                ? null
                : ReadDescription(alternatePath)) ??
            "VLESS";
    }

    private static string? ReadDescription(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            var config = document.RootElement;
            var outbounds = Property(config, "outbounds");
            if (outbounds.ValueKind != JsonValueKind.Array)
            {
                return "sing-box";
            }

            var finalTag = GetString(Property(config, "route"), "final");
            var outbound = string.IsNullOrEmpty(finalTag)
                ? outbounds.EnumerateArray().FirstOrDefault()
                : FindTaggedOutbound(outbounds, finalTag);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (GetString(outbound, "type") == "selector")
            {
                var target = GetString(outbound, "default");
                var members = Property(outbound, "outbounds");
                if (string.IsNullOrEmpty(target) && members.ValueKind == JsonValueKind.Array)
                {
                    target = AsString(members.EnumerateArray().FirstOrDefault());
                }

                if (string.IsNullOrEmpty(target) || !visited.Add(target))
                {
                    break;
                }

                outbound = FindTaggedOutbound(outbounds, target);
            }

            if (GetProtocolName(outbound) is { } protocol)
            {
                return DescribeOutbound(outbound, protocol);
            }

            // Custom profiles may route some traffic through a proxy even when it isn't the default.
            var candidates = outbounds.EnumerateArray()
                .Select(candidate => (Outbound: candidate, Protocol: GetProtocolName(candidate)))
                .Where(candidate => candidate.Protocol is not null)
                .ToArray();
            return candidates.Length == 1
                ? DescribeOutbound(candidates[0].Outbound, candidates[0].Protocol!)
                : candidates.Any(candidate => candidate.Protocol == "VLESS") ? "VLESS" : "sing-box";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string DescribeOutbound(JsonElement outbound, string protocol)
    {
        if (protocol is not ("VLESS" or "VMess" or "Trojan"))
        {
            return protocol;
        }

        var transport = GetString(Property(outbound, "transport"), "type") switch
        {
            "ws" => " WebSocket",
            "grpc" => " gRPC",
            "http" => " HTTP",
            "httpupgrade" => " HTTP Upgrade",
            "quic" => " QUIC",
            _ => string.Empty
        };
        var tls = Property(outbound, "tls");
        var security = Property(tls, "enabled").ValueKind == JsonValueKind.True
            ? Property(Property(tls, "reality"), "enabled").ValueKind == JsonValueKind.True ? " REALITY" : " TLS"
            : string.Empty;
        var flow = protocol == "VLESS" && GetString(outbound, "flow") == "xtls-rprx-vision" ? " Vision" : string.Empty;
        return $"{protocol}{transport}{security}{flow}";
    }

    private static string? GetProtocolName(JsonElement outbound) => GetString(outbound, "type") switch
    {
        "vless" => "VLESS",
        "vmess" => "VMess",
        "trojan" => "Trojan",
        "shadowsocks" => "Shadowsocks",
        "hysteria" => "Hysteria",
        "hysteria2" => "Hysteria2",
        "tuic" => "TUIC",
        "anytls" => "AnyTLS",
        "shadowtls" => "ShadowTLS",
        "wireguard" => "WireGuard",
        "ssh" => "SSH",
        "socks" => "SOCKS",
        "http" => "HTTP",
        "naive" => "NaiveProxy",
        _ => null
    };

    private static JsonElement FindTaggedOutbound(JsonElement outbounds, string tag) =>
        outbounds.EnumerateArray().FirstOrDefault(outbound => GetString(outbound, "tag") == tag);

    private static string? GetString(JsonElement element, string name) => AsString(Property(element, name));

    private static string? AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
}
