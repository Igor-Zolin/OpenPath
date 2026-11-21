using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace SshEnv.Core;

public static partial class SingBoxConfigDirectRules
{
    public static IReadOnlyList<string> PresetIds { get; } = Array.AsReadOnly(new[] { "ru", "microsoft", "lan" });

    private static readonly string[] LanCidrs =
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16",
        "224.0.0.0/24", "239.0.0.0/8", "255.255.255.255/32",
        "::1/128", "fc00::/7", "fe80::/10", "ff01::/16", "ff02::/16"
    ];

    private static IEnumerable<string> ConnectedLanCidrs()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.PrefixLength == 0)
                {
                    continue;
                }

                var bytes = unicast.Address.GetAddressBytes();
                for (var i = unicast.PrefixLength; i < bytes.Length * 8; i++)
                {
                    bytes[i / 8] &= (byte)~(128 >> (i % 8));
                }
                yield return $"{new IPAddress(bytes)}/{unicast.PrefixLength}";
            }
        }
    }

    private static bool IsPresetRuleSet(SingBoxDirectRule rule) => rule.Type is "domain_rule_set" or "ip_rule_set";

    private static IReadOnlyList<SingBoxDirectRule> ExpandPresets(JsonObject route, IReadOnlyList<SingBoxDirectRule> rules)
    {
        var expanded = rules.Where(rule => rule.Type != "preset").ToList();
        foreach (var preset in rules.Where(rule => rule.Type == "preset"))
        {
            switch (preset.Value)
            {
                case "ru":
                    AddRuleSet("geoip-ru", "ip_rule_set");
                    AddRuleSet("geosite-category-ru", "domain_rule_set");
                    AddRuleSet("cyrillic-domains", "domain_rule_set");
                    break;
                case "microsoft":
                    AddRuleSet("geosite-microsoft", "domain_rule_set");
                    AddRuleSet("microsoft365-ips", "ip_rule_set");
                    break;
                case "lan":
                    expanded.AddRange(LanCidrs.Concat(ConnectedLanCidrs()).Select(cidr => new SingBoxDirectRule("ip_cidr", cidr)));
                    expanded.Add(new SingBoxDirectRule("domain_suffix", "local"));
                    expanded.Add(new SingBoxDirectRule("domain_suffix", "localhost"));
                    expanded.Add(new SingBoxDirectRule("domain_suffix", "home.arpa"));
                    break;
            }
        }

        return expanded.Distinct().ToArray();

        void AddRuleSet(string name, string type)
        {
            var sets = GetOrCreateArray(route, "rule_set");
            var baseTag = $"openpath-{name}";
            var tag = baseTag;
            for (var suffix = 2; sets.OfType<JsonObject>().Any(set => GetString(set["tag"]) == tag); suffix++)
            {
                tag = $"{baseTag}-{suffix}";
            }

            using var stream = typeof(SingBoxConfigDirectRules).Assembly.GetManifestResourceStream($"SshEnv.ExclusionData.{name}.json")
                ?? throw new InvalidOperationException($"Missing built-in exclusion data for {name}.");
            sets.Add(new JsonObject
            {
                ["type"] = "inline",
                ["tag"] = tag,
                ["rules"] = JsonNode.Parse(stream)!["rules"]!.DeepClone()
            });
            expanded.Add(new SingBoxDirectRule(type, tag));
        }
    }
}
