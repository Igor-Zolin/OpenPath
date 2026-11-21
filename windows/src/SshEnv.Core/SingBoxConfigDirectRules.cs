using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SshEnv.Core;

public sealed record SingBoxDirectRule(string Type, string Value);

/// <summary>
/// Parses, persists, imports and applies portable sing-box TUN exclusion rules.
/// The source sing-box configuration is never modified.
/// </summary>
public static partial class SingBoxConfigDirectRules
{
    private const int ExportVersion = 1;
    private const string ExportFormat = "ssh-env-exclusions";

    private static readonly string[] DomainRuleTypes = ["domain", "domain_suffix", "domain_keyword", "domain_regex"];
    private static readonly string[] RouteRuleTypes = ["domain", "domain_suffix", "domain_keyword", "domain_regex", "ip_cidr", "process_name", "port", "port_range", "network"];

    public static IReadOnlyList<SingBoxDirectRule> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var rules = new List<SingBoxDirectRule>();
        foreach (var line in value.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                rules.Add(ParseToken(token));
            }
        }

        return Normalize(rules);
    }

    public static IReadOnlyList<SingBoxDirectRule> ParseProcessNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var rules = new List<SingBoxDirectRule>();
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var processName = line.StartsWith("process:", StringComparison.OrdinalIgnoreCase)
                ? line["process:".Length..].Trim()
                : line;
            rules.Add(new SingBoxDirectRule("process_name", processName));
        }

        return Normalize(rules);
    }

    public static string FormatProcessNames(IEnumerable<SingBoxDirectRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return string.Join(
            Environment.NewLine,
            Normalize(rules)
                .Where(rule => string.Equals(rule.Type, "process_name", StringComparison.Ordinal))
                .Select(rule => rule.Value));
    }

    public static IReadOnlyList<SingBoxDirectRule> Normalize(IEnumerable<SingBoxDirectRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var result = new List<SingBoxDirectRule>();
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                continue;
            }

            var normalized = NormalizeRule(rule);
            if (string.Equals(normalized.Type, "strict_route", StringComparison.Ordinal))
            {
                result.RemoveAll(existing => string.Equals(existing.Type, "strict_route", StringComparison.Ordinal));
                result.Add(normalized);
                continue;
            }

            if (!result.Any(existing => SameRule(existing, normalized)))
            {
                result.Add(normalized);
            }
        }

        if (result.Count(rule => rule.Type != "preset") > 200)
        {
            throw new ArgumentException("Enter no more than 200 direct exclusion rules.");
        }

        return result;
    }

    public static string Format(SingBoxDirectRule rule)
    {
        var normalized = NormalizeRule(rule);
        return normalized.Type switch
        {
            "domain_suffix" when string.Equals(normalized.Value, "localhost", StringComparison.OrdinalIgnoreCase) => "suffix:localhost",
            "domain_suffix" => $"*.{normalized.Value}",
            "domain" => $"host:{normalized.Value}",
            "domain_keyword" => $"keyword:{normalized.Value}",
            "domain_regex" => $"regex:{normalized.Value}",
            "ip_cidr" => $"cidr:{normalized.Value}",
            "process_name" => $"process:{normalized.Value}",
            "port" => $"port:{normalized.Value}",
            "port_range" => $"port:{normalized.Value.Replace(':', '-')}",
            "network" => $"network:{normalized.Value}",
            "strict_route" => $"strict-route:{normalized.Value}",
            _ => $"{normalized.Type}:{normalized.Value}"
        };
    }

    public static void Export(string path, IEnumerable<SingBoxDirectRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = new ExclusionExport(ExportFormat, ExportVersion, Normalize(rules).ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IReadOnlyList<SingBoxDirectRule> Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var document = JsonSerializer.Deserialize<ExclusionExport>(File.ReadAllText(path));
            if (document is null || !string.Equals(document.Format, ExportFormat, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The file is not an ssh-env exclusions export.");
            }

            if (document.Version != ExportVersion)
            {
                throw new InvalidOperationException($"Unsupported exclusions export version: {document.Version}.");
            }

            return Normalize(document.Rules ?? []);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not parse exclusions file: {exception.Message}", exception);
        }
    }

    public static SingBoxRuntimeConfig CreateRuntimeConfig(
        string sourceConfigPath,
        IEnumerable<SingBoxDirectRule> directRules,
        SingBoxTlsFragmentOptions? tlsFragmentation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConfigPath);
        var rules = Normalize(directRules);
        if (rules.Count == 0 && tlsFragmentation is null)
        {
            throw new ArgumentException("At least one direct exclusion rule is required.", nameof(directRules));
        }

        JsonNode? parsedConfig;
        try
        {
            parsedConfig = JsonNode.Parse(
                File.ReadAllText(sourceConfigPath),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not parse sing-box config: {exception.Message}", exception);
        }

        if (parsedConfig is not JsonObject config)
        {
            throw new InvalidOperationException("sing-box config root must be a JSON object.");
        }

        var route = GetOrCreateObject(config, "route");
        var routeRules = GetOrCreateArray(route, "rules");
        if (rules.Count > 0)
        {
            rules = ExpandPresets(route, rules);
            EnsureTunRouteExclusions(config, rules);
            ApplyTunOptions(config, rules);
            AddMissingDirectRouteRules(routeRules, rules);

            var dnsRulesToApply = rules.Where(IsLocalDnsRule).ToArray();
            if (dnsRulesToApply.Length > 0)
            {
                EnsureTunDnsPreparationRules(routeRules);
                var dns = GetOrCreateObject(config, "dns");
                var dnsServers = GetOrCreateArray(dns, "servers");
                EnsureLocalDnsServer(dnsServers);
                var dnsRules = GetOrCreateArray(dns, "rules");
                AddMissingLocalDnsRules(dnsRules, dnsRulesToApply);
            }
        }

        if (tlsFragmentation is not null)
        {
            AddTlsFragmentRules(routeRules, tlsFragmentation);
        }

        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceConfigPath));
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new InvalidOperationException("Could not determine sing-box config directory.");
        }

        var runtimeConfigPath = Path.Combine(sourceDirectory, $".ssh-env-{Guid.NewGuid():N}.runtime");
        try
        {
            File.WriteAllText(runtimeConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return new SingBoxRuntimeConfig(runtimeConfigPath);
        }
        catch
        {
            TryDelete(runtimeConfigPath);
            throw;
        }
    }

    public static IReadOnlyList<SingBoxDirectRule> ParseTlsFragmentDomains(string? input)
    {
        var rules = Parse(input);
        if (rules.Count == 0 || rules.Any(rule => !DomainRuleTypes.Contains(rule.Type, StringComparer.Ordinal)))
        {
            throw new ArgumentException("TLS fragmentation requires at least one valid domain rule.");
        }

        return rules;
    }

    private static void AddTlsFragmentRules(JsonArray routeRules, SingBoxTlsFragmentOptions options)
    {
        var domains = Normalize(options.Domains);
        if (domains.Count == 0 || domains.Any(rule => !DomainRuleTypes.Contains(rule.Type, StringComparer.Ordinal)) ||
            options.FallbackDelayMilliseconds is <= 0)
        {
            throw new ArgumentException("Invalid TLS fragmentation rules or delay.", nameof(options));
        }

        if (!routeRules.TakeWhile(IsPreparationRule).OfType<JsonObject>()
                .Any(rule => HasAction(rule, "sniff") && rule.Count == 1))
        {
            routeRules.Insert(0, new JsonObject { ["action"] = "sniff" });
        }

        var index = routeRules.TakeWhile(IsPreparationRule).Count();
        foreach (var domain in domains)
        {
            var rule = new JsonObject
            {
                [domain.Type] = new JsonArray(JsonValue.Create(domain.Value)),
                ["network"] = "tcp",
                ["protocol"] = "tls",
                ["action"] = "route-options"
            };
            if (options.FragmentTcp)
            {
                rule["tls_fragment"] = true;
                if (options.FallbackDelayMilliseconds is int delay)
                {
                    rule["tls_fragment_fallback_delay"] = delay.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
                }
            }
            else
            {
                rule["tls_record_fragment"] = true;
            }

            routeRules.Insert(index++, rule);
        }
    }

    private static SingBoxDirectRule ParseToken(string token)
    {
        var separator = token.IndexOf(':');
        if (separator > 0)
        {
            var type = token[..separator].Trim().ToLowerInvariant();
            var value = token[(separator + 1)..].Trim();
            return type switch
            {
                "preset" => new SingBoxDirectRule("preset", value),
                "host" or "domain" => new SingBoxDirectRule("domain", value),
                "suffix" or "domain_suffix" => new SingBoxDirectRule("domain_suffix", value),
                "keyword" or "domain_keyword" => new SingBoxDirectRule("domain_keyword", value),
                "glob" => ParseGlob(value),
                "regex" or "domain_regex" => new SingBoxDirectRule("domain_regex", value),
                "ip" or "cidr" or "ip_cidr" => new SingBoxDirectRule("ip_cidr", value),
                "process" or "process_name" => new SingBoxDirectRule("process_name", value),
                "port" => ParsePort(value),
                "network" => new SingBoxDirectRule("network", value),
                "strict-route" or "strict_route" => new SingBoxDirectRule("strict_route", value),
                _ => throw new ArgumentException($"Unknown exclusion rule type '{type}'.")
            };
        }

        if (TryParseIpRule(token, out var ipRule))
        {
            return ipRule;
        }

        if (token.EndsWith("/*", StringComparison.Ordinal) && TryParseDomainPathRule(token[..^2], out var domainPathRule))
        {
            return domainPathRule;
        }

        if (token.StartsWith("*.", StringComparison.Ordinal) && token.Count(character => character == '*') == 1)
        {
            return new SingBoxDirectRule("domain_suffix", token[2..]);
        }

        return token.Contains('*', StringComparison.Ordinal)
            ? ParseGlob(token)
            : new SingBoxDirectRule("domain_suffix", token);
    }

    private static bool TryParseDomainPathRule(string value, out SingBoxDirectRule rule)
    {
        try
        {
            rule = new SingBoxDirectRule("domain_suffix", NormalizeDomain(value));
            return true;
        }
        catch (ArgumentException)
        {
            rule = null!;
            return false;
        }
    }

    private static SingBoxDirectRule ParseGlob(string value)
    {
        var pattern = value.Trim();
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            throw new ArgumentException("A wildcard exclusion must contain a domain name.");
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.Count(character => character == '*') == 1)
        {
            return new SingBoxDirectRule("domain_suffix", pattern[2..]);
        }

        var expression = "^" + string.Concat(pattern.Select(character => character == '*' ? ".*" : Regex.Escape(character.ToString()))) + "$";
        return new SingBoxDirectRule("domain_regex", expression);
    }

    private static bool TryParseIpRule(string value, out SingBoxDirectRule rule)
    {
        try
        {
            rule = new SingBoxDirectRule("ip_cidr", NormalizeCidr(value));
            return true;
        }
        catch (ArgumentException)
        {
            rule = null!;
            return false;
        }
    }

    private static SingBoxDirectRule ParsePort(string value)
    {
        var trimmed = value.Trim();
        if (int.TryParse(trimmed, out var port) && port is >= 1 and <= 65535)
        {
            return new SingBoxDirectRule("port", port.ToString(CultureInfo.InvariantCulture));
        }

        var parts = trimmed.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var start) &&
            int.TryParse(parts[1], out var end) &&
            start is >= 1 and <= 65535 &&
            end >= start &&
            end <= 65535)
        {
            return new SingBoxDirectRule("port_range", $"{start}:{end}");
        }

        throw new ArgumentException($"'{value}' is not a valid port or port range.");
    }

    private static SingBoxDirectRule NormalizeRule(SingBoxDirectRule rule)
    {
        var type = rule.Type?.Trim().ToLowerInvariant().Replace('-', '_') ?? string.Empty;
        var value = rule.Value?.Trim() ?? string.Empty;
        return type switch
        {
            "preset" when PresetIds.Contains(value) => new SingBoxDirectRule(type, value),
            "domain" => new SingBoxDirectRule(type, NormalizeDomain(value)),
            "domain_suffix" => new SingBoxDirectRule(type, NormalizeDomain(value.TrimStart('*', '.'))),
            "domain_keyword" => new SingBoxDirectRule(type, NormalizeKeyword(value)),
            "domain_regex" when TryMigrateLegacyDomainPathRegex(value, out var migratedDomain) => new SingBoxDirectRule("domain_suffix", migratedDomain),
            "domain_regex" => new SingBoxDirectRule(type, NormalizeRegex(value)),
            "ip_cidr" => new SingBoxDirectRule(type, NormalizeCidr(value)),
            "process_name" => new SingBoxDirectRule(type, NormalizeProcessName(value)),
            "port" => NormalizePort(value),
            "port_range" => NormalizePortRange(value),
            "network" => new SingBoxDirectRule(type, NormalizeNetwork(value)),
            "strict_route" => new SingBoxDirectRule(type, NormalizeBoolean(value, "strict-route")),
            _ => throw new ArgumentException($"Unsupported exclusion rule type '{rule.Type}'.")
        };
    }

    private static SingBoxDirectRule NormalizePortRange(string value)
    {
        var normalized = ParsePort(value.Replace(':', '-'));
        if (normalized.Type != "port_range")
        {
            throw new ArgumentException($"'{value}' is not a port range.");
        }

        return normalized;
    }

    private static SingBoxDirectRule NormalizePort(string value)
    {
        var normalized = ParsePort(value);
        if (normalized.Type != "port")
        {
            throw new ArgumentException($"'{value}' is not a single port.");
        }

        return normalized;
    }

    private static string NormalizeDomain(string value)
    {
        var candidate = value.Trim().TrimEnd('.');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            candidate = uri.Host;
        }

        try
        {
            candidate = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"'{value}' is not a valid domain name.", exception);
        }

        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Contains('/', StringComparison.Ordinal) ||
            Uri.CheckHostName(candidate) != UriHostNameType.Dns)
        {
            throw new ArgumentException($"'{value}' is not a valid domain name.");
        }

        return candidate;
    }

    // Versions before flexible rules treated "localhost/*" as a domain regex.
    // A TUN rule receives a hostname, not an HTTP URL path, so recover the intended suffix rule.
    private static bool TryMigrateLegacyDomainPathRegex(string value, out string domain)
    {
        const string prefix = "^";
        const string suffix = "/.*$";
        var candidate = value.Trim();
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal) ||
            !candidate.EndsWith(suffix, StringComparison.Ordinal))
        {
            domain = string.Empty;
            return false;
        }

        candidate = candidate[prefix.Length..^suffix.Length].Replace("\\.", ".", StringComparison.Ordinal);
        try
        {
            domain = NormalizeDomain(candidate);
            return true;
        }
        catch (ArgumentException)
        {
            domain = string.Empty;
            return false;
        }
    }

    private static string NormalizeKeyword(string value)
    {
        var keyword = value.Trim();
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"'{value}' is not a valid domain keyword.");
        }

        return keyword.ToLowerInvariant();
    }

    private static string NormalizeRegex(string value)
    {
        var expression = value.Trim();
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("A domain regular expression is required.");
        }

        try
        {
            _ = new Regex(expression, RegexOptions.CultureInvariant);
            return expression;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"'{value}' is not a valid regular expression.", exception);
        }
    }

    private static string NormalizeCidr(string value)
    {
        var candidate = value.Trim();
        if (candidate.EndsWith("/*", StringComparison.Ordinal))
        {
            candidate = candidate[..^2];
        }

        if (IPAddress.TryParse(candidate, out var address))
        {
            return $"{address}/{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)}";
        }

        if (IPNetwork.TryParse(candidate, out var network))
        {
            return network.ToString();
        }

        var segments = candidate.Split('.', StringSplitOptions.TrimEntries);
        if (segments.Length is >= 1 and <= 4 && segments.Any(segment => segment == "*"))
        {
            var firstWildcard = Array.FindIndex(segments, segment => segment == "*");
            if (firstWildcard >= 0 && segments.Skip(firstWildcard).All(segment => segment == "*"))
            {
                var numericSegments = segments.Take(firstWildcard).ToArray();
                if (numericSegments.All(segment => byte.TryParse(segment, out _)))
                {
                    var octets = numericSegments.Concat(Enumerable.Repeat("0", 4 - numericSegments.Length));
                    return $"{string.Join('.', octets)}/{numericSegments.Length * 8}";
                }
            }
        }

        throw new ArgumentException($"'{value}' is not a valid IP address or CIDR.");
    }

    private static string NormalizeProcessName(string value)
    {
        var processName = value.Trim();
        if (string.IsNullOrWhiteSpace(processName) || processName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || processName.Contains(Path.DirectorySeparatorChar) || processName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException($"'{value}' is not a valid process name.");
        }

        return processName;
    }

    private static string NormalizeNetwork(string value)
    {
        var network = value.Trim().ToLowerInvariant();
        return network is "tcp" or "udp" or "icmp"
            ? network
            : throw new ArgumentException($"'{value}' must be tcp, udp or icmp.");
    }

    private static string NormalizeBoolean(string value, string optionName)
    {
        return bool.TryParse(value.Trim(), out var result)
            ? result.ToString().ToLowerInvariant()
            : throw new ArgumentException($"'{value}' is not a valid value for {optionName}; use true or false.");
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject value)
        {
            return value;
        }

        if (parent[propertyName] is not null)
        {
            throw new InvalidOperationException($"sing-box config property '{propertyName}' must be an object.");
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonArray value)
        {
            return value;
        }

        if (parent[propertyName] is not null)
        {
            throw new InvalidOperationException($"sing-box config property '{propertyName}' must be an array.");
        }

        var created = new JsonArray();
        parent[propertyName] = created;
        return created;
    }

    private static JsonArray GetOrCreateStringArray(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonArray array)
        {
            return array;
        }

        var existingValue = GetString(parent[propertyName]);
        if (existingValue is not null)
        {
            var converted = new JsonArray(existingValue);
            parent[propertyName] = converted;
            return converted;
        }

        if (parent[propertyName] is not null)
        {
            throw new InvalidOperationException($"sing-box config property '{propertyName}' must be a string or an array.");
        }

        var created = new JsonArray();
        parent[propertyName] = created;
        return created;
    }

    private static void EnsureTunRouteExclusions(JsonObject config, IReadOnlyList<SingBoxDirectRule> rules)
    {
        var exclusions = rules
            .Where(rule => string.Equals(rule.Type, "ip_cidr", StringComparison.Ordinal))
            .Select(rule => rule.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (exclusions.Length == 0)
        {
            return;
        }

        if (config["inbounds"] is not JsonArray inbounds)
        {
            throw new InvalidOperationException("sing-box config property 'inbounds' must be an array.");
        }

        foreach (var inbound in inbounds.OfType<JsonObject>()
                     .Where(inbound => string.Equals(GetString(inbound["type"]), "tun", StringComparison.OrdinalIgnoreCase)))
        {
            var routeExclusions = GetOrCreateStringArray(inbound, "route_exclude_address");
            var existingValues = GetStringValues(routeExclusions).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var exclusion in exclusions.Where(exclusion => !existingValues.Contains(exclusion)))
            {
                routeExclusions.Add(exclusion);
            }
        }
    }

    private static void ApplyTunOptions(JsonObject config, IReadOnlyList<SingBoxDirectRule> rules)
    {
        var strictRouteRule = rules.LastOrDefault(rule => string.Equals(rule.Type, "strict_route", StringComparison.Ordinal));
        if (strictRouteRule is null)
        {
            return;
        }

        if (config["inbounds"] is not JsonArray inbounds)
        {
            throw new InvalidOperationException("sing-box config property 'inbounds' must be an array.");
        }

        var strictRoute = bool.Parse(strictRouteRule.Value);
        foreach (var inbound in inbounds.OfType<JsonObject>()
                     .Where(inbound => string.Equals(GetString(inbound["type"]), "tun", StringComparison.OrdinalIgnoreCase)))
        {
            inbound["strict_route"] = strictRoute;
        }
    }

    private static void EnsureTunDnsPreparationRules(JsonArray routeRules)
    {
        var hasSniff = routeRules.OfType<JsonObject>().Any(rule => HasAction(rule, "sniff"));
        var hasDnsHijack = routeRules.OfType<JsonObject>().Any(rule =>
            HasAction(rule, "hijack-dns") && string.Equals(GetString(rule["protocol"]), "dns", StringComparison.OrdinalIgnoreCase));

        var insertionIndex = 0;
        if (!hasSniff)
        {
            routeRules.Insert(insertionIndex++, new JsonObject { ["action"] = "sniff" });
        }

        if (!hasDnsHijack)
        {
            routeRules.Insert(insertionIndex, new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns"
            });
        }
    }

    private static void AddMissingDirectRouteRules(JsonArray routeRules, IReadOnlyList<SingBoxDirectRule> rules)
    {
        var existingRules = CollectExistingDirectRules(routeRules, "outbound");
        var insertionIndex = 0;
        while (insertionIndex < routeRules.Count && IsPreparationRule(routeRules[insertionIndex]))
        {
            insertionIndex++;
        }

        foreach (var rule in rules.Where(rule => !existingRules.Any(existing => SameRule(existing, rule))))
        {
            if (!IsPresetRuleSet(rule) && !RouteRuleTypes.Contains(rule.Type, StringComparer.Ordinal))
            {
                continue;
            }

            routeRules.Insert(insertionIndex++, CreateRouteRule(rule, "outbound", "direct"));
        }
    }

    private static void EnsureLocalDnsServer(JsonArray dnsServers)
    {
        if (dnsServers.OfType<JsonObject>().Any(server => string.Equals(GetString(server["tag"]), "local", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        dnsServers.Add(new JsonObject
        {
            ["tag"] = "local",
            ["type"] = "local"
        });
    }

    private static void AddMissingLocalDnsRules(JsonArray dnsRules, IReadOnlyList<SingBoxDirectRule> rules)
    {
        var existingRules = CollectExistingDirectRules(dnsRules, "server");
        var insertionIndex = 0;
        foreach (var rule in rules.Where(rule => !existingRules.Any(existing => SameRule(existing, rule))))
        {
            dnsRules.Insert(insertionIndex++, CreateRouteRule(rule, "server", "local"));
        }
    }

    private static IReadOnlyList<SingBoxDirectRule> CollectExistingDirectRules(JsonArray rules, string targetProperty)
    {
        var existing = new List<SingBoxDirectRule>();
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (!string.Equals(GetString(rule[targetProperty]), targetProperty == "outbound" ? "direct" : "local", StringComparison.OrdinalIgnoreCase) ||
                (rule["action"] is not null && !HasAction(rule, "route")))
            {
                continue;
            }

            foreach (var type in RouteRuleTypes)
            {
                foreach (var value in GetStringValues(rule[type]))
                {
                    try
                    {
                        existing.Add(NormalizeRule(new SingBoxDirectRule(type, value)));
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }

        return existing;
    }

    private static JsonObject CreateRouteRule(SingBoxDirectRule rule, string targetProperty, string targetValue)
    {
        var normalized = IsPresetRuleSet(rule) ? new SingBoxDirectRule("rule_set", rule.Value) : NormalizeRule(rule);
        var result = new JsonObject
        {
            ["action"] = "route",
            [targetProperty] = targetValue
        };
        result[normalized.Type] = normalized.Type == "port"
            ? JsonValue.Create(int.Parse(normalized.Value, CultureInfo.InvariantCulture))
            : JsonValue.Create(normalized.Value);
        return result;
    }

    private static bool IsDomainRule(SingBoxDirectRule rule)
    {
        return DomainRuleTypes.Contains(rule.Type, StringComparer.Ordinal);
    }

    private static bool IsLocalDnsRule(SingBoxDirectRule rule)
    {
        return rule.Type == "domain_rule_set" || IsDomainRule(rule) || string.Equals(rule.Type, "process_name", StringComparison.Ordinal);
    }

    private static bool IsPreparationRule(JsonNode? node)
    {
        return node is JsonObject rule && (HasAction(rule, "sniff") || HasAction(rule, "hijack-dns"));
    }

    private static bool HasAction(JsonObject rule, string expectedAction)
    {
        return string.Equals(GetString(rule["action"]), expectedAction, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameRule(SingBoxDirectRule left, SingBoxDirectRule right)
    {
        return string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetStringValues(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.Select(GetString).OfType<string>();
        }

        var value = GetString(node);
        return value is null ? [] : [value];
    }

    private static string? GetString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ExclusionExport(string Format, int Version, List<SingBoxDirectRule>? Rules);
}

public sealed class SingBoxRuntimeConfig(string configPath) : IDisposable
{
    private bool disposed;

    public string ConfigPath { get; } = configPath;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            File.Delete(ConfigPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
