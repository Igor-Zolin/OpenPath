using System.Text.Json;
using System.Text.Json.Nodes;

namespace SshEnv.WinUI;

internal enum VlessSecurity { None, Tls, Reality }

internal sealed record VlessSettings(
    string Server, int Port, string Uuid, string Flow, VlessSecurity Security, string ServerName,
    string Fingerprint, string Alpn, string PublicKey, string ShortId);

internal static class VlessConfigEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static VlessSettings Read(string path)
    {
        var outbound = FindVless(ReadConfig(path));
        var tls = outbound["tls"] as JsonObject;
        var reality = tls?["reality"] as JsonObject;
        var security = (bool?)tls?["enabled"] == true
            ? (bool?)reality?["enabled"] == true ? VlessSecurity.Reality : VlessSecurity.Tls
            : VlessSecurity.None;
        var alpn = tls?["alpn"] is JsonArray protocols
            ? string.Join(", ", protocols.Select(node => (string?)node))
            : Text(tls, "alpn");
        return new VlessSettings(Text(outbound, "server"), (int?)outbound["server_port"] ?? 443,
            Text(outbound, "uuid"), Text(outbound, "flow"), security, Text(tls, "server_name"),
            Text(tls?["utls"] as JsonObject, "fingerprint"), alpn, Text(reality, "public_key"), Text(reality, "short_id"));
    }

    public static VlessConfigRevision CreateRevision(
        VlessProfile profile, VlessSettings? original, VlessSettings? edited, int? previousProxyPort)
    {
        var sharedChanged = original is not null && edited is not null && original != edited;
        var proxyPortChanged = previousProxyPort is int port && port != profile.SystemProxyPort;
        if (!sharedChanged && !proxyPortChanged)
        {
            return new VlessConfigRevision(profile, []);
        }

        var files = new List<(string Path, JsonObject Config)>();
        var revisionName = $".ssh-env-{Guid.NewGuid():N}";
        string? Prepare(string? source, bool proxy)
        {
            if (source is null || (!sharedChanged && !proxy))
            {
                return source;
            }

            var fullPath = Path.GetFullPath(source);
            var config = ReadConfig(fullPath);
            if (sharedChanged)
            {
                Apply(FindVless(config), original!, edited!);
            }

            if (proxy && proxyPortChanged)
            {
                UpdateProxyPort(config, previousProxyPort!.Value, profile.SystemProxyPort);
            }

            // Keep each revision with its source so relative paths still resolve.
            var suffix = proxy ? "system-proxy" : "tun";
            var revision = Path.Combine(Path.GetDirectoryName(fullPath)!, $"{revisionName}-{suffix}.json");
            files.Add((revision, config));
            return revision;
        }

        // Validate both configurations before writing either revision.
        var result = profile with
        {
            TunConfigPath = Prepare(profile.TunConfigPath, proxy: false),
            SystemProxyConfigPath = Prepare(profile.SystemProxyConfigPath, proxy: true)
        };
        var created = new List<string>(files.Count);
        try
        {
            foreach (var (path, config) in files)
            {
                using var file = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Add(path);
                JsonSerializer.Serialize(file, config, WriteOptions);
            }

            return new VlessConfigRevision(result, created);
        }
        catch
        {
            VlessConfigRevision.Discard(created);
            throw;
        }
    }

    private static JsonObject ReadConfig(string path)
    {
        using var file = File.OpenRead(path);
        return JsonNode.Parse(file, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject ?? throw new InvalidOperationException("The sing-box configuration must be a JSON object.");
    }

    private static JsonObject FindVless(JsonObject config)
    {
        var outbounds = (config["outbounds"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var tag = Text(config["route"] as JsonObject, "final");
        var selected = tag.Length == 0 ? outbounds.FirstOrDefault() : FindTag(tag);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (Text(selected, "type") == "selector")
        {
            tag = Text(selected, "default");
            if (tag.Length == 0 && selected?["outbounds"] is JsonArray members && members.Count > 0)
            {
                tag = (string?)members[0] ?? string.Empty;
            }

            selected = tag.Length > 0 && visited.Add(tag) ? FindTag(tag) : null;
        }

        if (selected is not null && Text(selected, "type") == "vless")
        {
            return selected;
        }

        var candidates = outbounds.Where(item => Text(item, "type") == "vless").Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0]
            : throw new InvalidOperationException("The configuration does not identify a single VLESS outbound.");

        JsonObject? FindTag(string value) => outbounds.FirstOrDefault(item => Text(item, "tag") == value);
    }

    private static void Apply(JsonObject outbound, VlessSettings before, VlessSettings after)
    {
        if (before.Port != after.Port)
        {
            outbound["server_port"] = after.Port;
        }
        if (before.Server != after.Server) SetText(outbound, "server", after.Server);
        if (before.Uuid != after.Uuid) SetText(outbound, "uuid", after.Uuid);
        if (before.Flow != after.Flow) SetText(outbound, "flow", after.Flow);

        if (before.Security != after.Security || before.ServerName != after.ServerName ||
            before.Fingerprint != after.Fingerprint || before.Alpn != after.Alpn ||
            before.PublicKey != after.PublicKey || before.ShortId != after.ShortId)
        {
            var tls = GetObject(outbound, "tls");
            if (before.Security != after.Security)
            {
                tls["enabled"] = after.Security != VlessSecurity.None;
                if (tls["reality"] is JsonObject || after.Security == VlessSecurity.Reality)
                {
                    GetObject(tls, "reality")["enabled"] = after.Security == VlessSecurity.Reality;
                }
            }
            if (before.ServerName != after.ServerName) SetText(tls, "server_name", after.ServerName);
            if (before.Alpn != after.Alpn)
            {
                var protocols = after.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (protocols.Length == 0) tls.Remove("alpn");
                else tls["alpn"] = new JsonArray(protocols.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            }
            if (before.Fingerprint != after.Fingerprint ||
                (before.Security != after.Security && after.Security == VlessSecurity.Reality))
            {
                var utls = GetObject(tls, "utls");
                utls["enabled"] = after.Fingerprint.Length > 0 || after.Security == VlessSecurity.Reality;
                SetText(utls, "fingerprint", after.Fingerprint.Length == 0 && after.Security == VlessSecurity.Reality
                    ? "chrome" : after.Fingerprint);
            }
            if (before.PublicKey != after.PublicKey || before.ShortId != after.ShortId ||
                (before.Security != after.Security && after.Security == VlessSecurity.Reality))
            {
                var reality = GetObject(tls, "reality");
                SetText(reality, "public_key", after.PublicKey);
                SetText(reality, "short_id", after.ShortId);
            }
        }
    }

    private static void UpdateProxyPort(JsonObject config, int previousPort, int newPort)
    {
        var inbounds = (config["inbounds"] as JsonArray)?.OfType<JsonObject>()
            .Where(item => Text(item, "type") == "mixed").ToArray() ?? [];
        var inbound = inbounds.FirstOrDefault(item => (int?)item["listen_port"] == previousPort);
        if (inbound is null && inbounds.Length == 1)
        {
            inbound = inbounds[0];
        }

        if (inbound is null)
        {
            throw new InvalidOperationException("The configuration does not identify a single proxy listener.");
        }

        inbound["listen_port"] = newPort;
    }

    private static string Text(JsonObject? parent, string key) => (string?)parent?[key] ?? string.Empty;

    private static JsonObject GetObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var value = new JsonObject();
        parent[key] = value;
        return value;
    }

    private static void SetText(JsonObject parent, string key, string value)
    {
        if (value.Length == 0) parent.Remove(key);
        else parent[key] = value;
    }
}

internal sealed record VlessConfigRevision(VlessProfile Profile, IReadOnlyList<string> Files)
{
    public static void Discard(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unused revision is harmless if a cleanup attempt fails.
            }
        }
    }
}
