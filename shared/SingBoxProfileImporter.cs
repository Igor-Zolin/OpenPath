using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SshEnv.Core;

public sealed record ImportedSingBoxProfile(string Name, string ConfigPath);

public sealed record ImportedSingBoxProfilePair(string Name, string TunConfigPath, string SystemProxyConfigPath);

public sealed record SingBoxImportResult(IReadOnlyList<ImportedSingBoxProfilePair> Profiles, int SkippedEntries);

public static class SingBoxProfileImporter
{
    private static readonly HttpClient SubscriptionClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static ImportedSingBoxProfile ImportVlessUri(string value, string outputDirectory)
    {
        return ImportVlessTunUri(value, outputDirectory);
    }

    public static ImportedSingBoxProfilePair ImportVlessUriPair(string value, string outputDirectory, int listenPort)
    {
        var profile = ParseVlessUri(value);
        var baseName = SanitizeFileName(profile.Name);
        return WriteProfilePair(profile, baseName, outputDirectory, listenPort);
    }

    public static Task<SingBoxImportResult> ImportLinkAsync(string value, string outputDirectory, int listenPort)
    {
        return ImportLinkAsync(value, outputDirectory, listenPort, SubscriptionClient);
    }

    internal static async Task<SingBoxImportResult> ImportLinkAsync(
        string value, string outputDirectory, int listenPort, HttpClient httpClient)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return new SingBoxImportResult([ImportVlessUriPair(value, outputDirectory, listenPort)], 0);
        }

        string response;
        try
        {
            response = await httpClient.GetStringAsync(uri).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is { } statusCode)
        {
            throw new InvalidOperationException($"Subscription download failed (HTTP {(int)statusCode}).");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Could not download the subscription.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The subscription download timed out.");
        }

        return ImportSubscriptionContent(response, outputDirectory, listenPort);
    }

    private static SingBoxImportResult ImportSubscriptionContent(string content, string outputDirectory, int listenPort)
    {
        content = content.Trim().TrimStart('\uFEFF');
        if (!content.Contains("vless://", StringComparison.OrdinalIgnoreCase))
        {
            var base64 = string.Concat(content.Where(character => !char.IsWhiteSpace(character)))
                .Replace('-', '+').Replace('_', '/');
            try
            {
                content = Encoding.UTF8.GetString(Convert.FromBase64String(
                    base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')));
            }
            catch (FormatException)
            {
                throw new ArgumentException("The subscription does not contain a plain text or Base64 list of VLESS links.");
            }
        }

        var profiles = new List<VlessProfile>();
        var uniqueProfiles = new HashSet<VlessProfile>();
        var skipped = 0;
        foreach (var link in content.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var profile = ParseVlessUri(link, requireTcp: true);
                if (uniqueProfiles.Add(profile))
                {
                    profiles.Add(profile);
                    continue;
                }
            }
            catch (ArgumentException)
            {
                // A subscription can contain several protocols or malformed entries.
            }

            skipped++;
        }

        if (profiles.Count == 0)
        {
            throw new ArgumentException("The subscription contains no supported VLESS TLS or REALITY TCP links.");
        }

        var imported = new List<ImportedSingBoxProfilePair>(profiles.Count);
        foreach (var profile in profiles)
        {
            var name = SanitizeFileName(profile.Name.Length == 0 ? profile.Server : profile.Name);
            var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile));
            var baseName = $"{name[..Math.Min(name.Length, 64)]}-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
            imported.Add(WriteProfilePair(profile, baseName, outputDirectory, listenPort));
        }

        return new SingBoxImportResult(imported, skipped);
    }

    private static ImportedSingBoxProfilePair WriteProfilePair(VlessProfile profile, string baseName, string outputDirectory, int listenPort)
    {
        Directory.CreateDirectory(outputDirectory);
        var tun = JsonSerializer.Serialize(BuildSingBoxTunConfig(profile), JsonOptions);
        var proxy = JsonSerializer.Serialize(BuildSingBoxSystemProxyConfig(profile, listenPort), JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tun + "\n" + proxy));
        var alternate = $"{baseName[..Math.Min(baseName.Length, 64)]}-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
        for (var attempt = 0; ; attempt++)
        {
            var name = attempt switch { 0 => baseName, 1 => alternate, _ => $"{alternate}-{attempt}" };
            var tunPath = Path.Combine(outputDirectory, $"{name}-tun.json");
            var proxyPath = Path.Combine(outputDirectory, $"{name}-system-proxy.json");
            if (CanReuseConfig(tunPath, tun) && CanReuseConfig(proxyPath, proxy) &&
                TryPublishConfig(tunPath, tun) && TryPublishConfig(proxyPath, proxy))
            {
                return new ImportedSingBoxProfilePair(profile.Name, tunPath, proxyPath);
            }
        }
    }

    private static bool CanReuseConfig(string path, string content) =>
        !File.Exists(path) || File.ReadAllText(path) == content;

    private static bool TryPublishConfig(string path, string content)
    {
        if (File.Exists(path))
        {
            return File.ReadAllText(path) == content;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return File.ReadAllText(path) == content;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static ImportedSingBoxProfile ImportVlessTunUri(string value, string outputDirectory)
    {
        var profile = ParseVlessUri(value);
        Directory.CreateDirectory(outputDirectory);

        var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(profile.Name) ? "imported-vless-tun" : profile.Name);
        var configPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var config = BuildSingBoxTunConfig(profile);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
        return new ImportedSingBoxProfile(profile.Name, configPath);
    }

    public static ImportedSingBoxProfile ImportVlessSystemProxyUri(string value, string outputDirectory, int listenPort)
    {
        var profile = ParseVlessUri(value);
        Directory.CreateDirectory(outputDirectory);

        var baseFileName = string.IsNullOrWhiteSpace(profile.Name) ? "imported-vless-system-proxy" : $"{profile.Name}-system-proxy";
        var fileName = SanitizeFileName(baseFileName);
        var configPath = Path.Combine(outputDirectory, $"{fileName}.json");
        var config = BuildSingBoxSystemProxyConfig(profile, listenPort);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
        return new ImportedSingBoxProfile(profile.Name, configPath);
    }

    private static VlessProfile ParseVlessUri(string value, bool requireTcp = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Clipboard does not contain a VLESS link.");
        }

        var trimmedValue = value.Trim();
        if (!trimmedValue.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only vless:// links are supported for automatic import right now.");
        }

        if (!Uri.TryCreate(trimmedValue, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Invalid VLESS link.");
        }

        var query = ParseQuery(uri.Query);
        var security = GetQueryValue(query, "security");
        var isReality = security.Equals("reality", StringComparison.OrdinalIgnoreCase);
        var isTls = security.Equals("tls", StringComparison.OrdinalIgnoreCase);
        if (!isReality && !isTls)
        {
            throw new ArgumentException("Only VLESS TLS and REALITY links are supported for import.");
        }

        var transport = GetQueryValue(query, "type");
        var headerType = GetQueryValue(query, "headerType");
        if ((requireTcp || isTls) && (uri.Host.Length == 0 ||
                                     transport.ToLowerInvariant() is not ("" or "tcp" or "raw") ||
                                     headerType.ToLowerInvariant() is not ("" or "none")))
        {
            throw new ArgumentException("A valid host and plain TCP transport are required for this VLESS link.");
        }

        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        if (!Guid.TryParse(uuid, out _))
        {
            throw new ArgumentException("VLESS link contains an invalid UUID.");
        }

        if (isTls && GetQueryValue(query, "encryption").ToLowerInvariant() is not ("" or "none"))
        {
            throw new ArgumentException("Only encryption=none is supported for VLESS TLS links.");
        }

        var fingerprint = GetQueryValue(query, "fp");
        var alpn = GetQueryValue(query, "alpn");
        var serverName = GetQueryValue(query, "sni");
        if (string.IsNullOrWhiteSpace(serverName))
        {
            serverName = GetQueryValue(query, "host");
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            serverName = uri.Host;
        }

        return new VlessProfile(
            Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            uri.Host,
            uri.Port <= 0 ? 443 : uri.Port,
            uuid,
            GetQueryValue(query, "flow"),
            serverName,
            string.IsNullOrWhiteSpace(fingerprint) ? (isReality ? "chrome" : null) : fingerprint,
            isReality ? RequiredQueryValue(query, "pbk") : null,
            GetQueryValue(query, "sid"),
            isTls && !string.IsNullOrWhiteSpace(alpn) ? alpn : null);
    }

    private static object BuildSingBoxTunConfig(VlessProfile profile)
    {
        return new
        {
            log = new
            {
                level = "info",
                timestamp = true
            },
            dns = new
            {
                servers = new object[]
                {
                    new
                    {
                        tag = "cloudflare",
                        type = "tls",
                        server = "1.1.1.1",
                        detour = "proxy"
                    },
                    new
                    {
                        tag = "local",
                        type = "local"
                    }
                },
                strategy = "ipv4_only"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "ssh-env-sing-tun",
                    address = new[] { "172.20.0.1/30" },
                    auto_route = true,
                    strict_route = true,
                    stack = "mixed"
                }
            },
            outbounds = BuildVlessOutbounds(profile),
            route = new
            {
                rules = new object[]
                {
                    new
                    {
                        action = "sniff"
                    },
                    new
                    {
                        protocol = "dns",
                        action = "hijack-dns"
                    },
                    new
                    {
                        ip_is_private = true,
                        outbound = "direct"
                    }
                },
                final = "proxy",
                default_domain_resolver = "local",
                auto_detect_interface = true
            }
        };
    }

    private static object BuildSingBoxSystemProxyConfig(VlessProfile profile, int listenPort)
    {
        return new
        {
            log = new
            {
                level = "info",
                timestamp = true
            },
            dns = new
            {
                servers = new object[]
                {
                    new
                    {
                        tag = "cloudflare",
                        type = "tls",
                        server = "1.1.1.1",
                        detour = "proxy"
                    },
                    new
                    {
                        tag = "local",
                        type = "local"
                    }
                },
                strategy = "ipv4_only"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "mixed",
                    tag = "mixed-in",
                    listen = "127.0.0.1",
                    listen_port = listenPort
                }
            },
            outbounds = BuildVlessOutbounds(profile),
            route = new
            {
                rules = new object[]
                {
                    new
                    {
                        action = "sniff"
                    },
                    new
                    {
                        protocol = "dns",
                        action = "hijack-dns"
                    },
                    new
                    {
                        ip_is_private = true,
                        outbound = "direct"
                    }
                },
                final = "proxy",
                default_domain_resolver = "local"
            }
        };
    }

    private static object[] BuildVlessOutbounds(VlessProfile profile)
    {
        return
        [
            new
            {
                type = "vless",
                tag = "proxy",
                server = profile.Server,
                server_port = profile.Port,
                uuid = profile.Uuid,
                flow = string.IsNullOrWhiteSpace(profile.Flow) ? null : profile.Flow,
                tls = new
                {
                    enabled = true,
                    server_name = profile.ServerName,
                    alpn = profile.Alpn?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    utls = profile.Fingerprint is null ? null : new
                    {
                        enabled = true,
                        fingerprint = profile.Fingerprint
                    },
                    reality = profile.PublicKey is null ? null : new
                    {
                        enabled = true,
                        public_key = profile.PublicKey,
                        short_id = string.IsNullOrWhiteSpace(profile.ShortId) ? null : profile.ShortId
                    }
                },
                packet_encoding = "xudp"
            },
            new
            {
                type = "direct",
                tag = "direct"
            }
        ];
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string GetQueryValue(IReadOnlyDictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string RequiredQueryValue(IReadOnlyDictionary<string, string> query, string key)
    {
        var value = GetQueryValue(query, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"VLESS link is missing required query parameter: {key}.");
        }

        return value;
    }

    private static string SanitizeFileName(string value)
    {
        var decodedValue = Uri.UnescapeDataString(value);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(decodedValue
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "imported-vless" : sanitized;
    }

    private sealed record VlessProfile(
        string Name,
        string Server,
        int Port,
        string Uuid,
        string Flow,
        string ServerName,
        string? Fingerprint,
        string? PublicKey,
        string ShortId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Alpn);
}
