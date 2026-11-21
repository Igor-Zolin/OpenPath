using System.Text.Json.Nodes;
using SshEnv.Core;

internal static class FragmentationTests
{
    public static void Run(string directory)
    {
        const string key = "vless://11111111-1111-1111-1111-111111111111@127.0.0.1:443?security=tls&type=tcp#fragment-smoke";
        var profiles = SingBoxProfileImporter.ImportVlessUriPair(key, directory, 2080);
        var domains = SingBoxConfigDirectRules.ParseTlsFragmentDomains("host:example.com\n*.example.net");
        var directRules = SingBoxConfigDirectRules.Parse("host:direct.example.com");

        foreach (var (source, exclusions, fragmentTcp) in new (string, IReadOnlyList<SingBoxDirectRule>, bool)[]
        {
            (profiles.TunConfigPath, directRules, true),
            (profiles.SystemProxyConfigPath, [], false)
        })
        {
            var original = File.ReadAllText(source);
            string temporary;
            using (var runtime = SingBoxConfigDirectRules.CreateRuntimeConfig(source, exclusions,
                       new SingBoxTlsFragmentOptions(domains, fragmentTcp, fragmentTcp ? 125 : null)))
            {
                temporary = runtime.ConfigPath;
                var config = JsonNode.Parse(File.ReadAllText(temporary))!;
                var rules = config["route"]!["rules"]!.AsArray();
                var sniffIndex = rules.ToList().FindIndex(rule => (string?)rule?["action"] == "sniff");
                var fragmentKey = fragmentTcp ? "tls_fragment" : "tls_record_fragment";
                var fragmentIndex = rules.ToList().FindIndex(rule => (bool?)rule?[fragmentKey] == true);
                Require(fragmentIndex >= 0, "No fragmentation rule was generated.");
                var fragment = rules[fragmentIndex]!;
                Require(sniffIndex >= 0 && fragmentIndex > sniffIndex && (string?)fragment["protocol"] == "tls",
                    "Fragmentation must run after sniffing and only match TLS.");
                Require((string?)fragment["domain"]?[0] == "example.com" &&
                        (string?)fragment["tls_fragment_fallback_delay"] == (fragmentTcp ? "125ms" : null),
                    "Fragmentation settings were lost.");
                Require(rules.Any(rule => (string?)rule?["domain_suffix"]?[0] == "example.net" &&
                                          (string?)rule?["action"] == "route-options"), "The suffix rule was lost.");
                if (exclusions.Count > 0)
                {
                    Require(rules.Any(rule => (string?)rule?["outbound"] == "direct" &&
                                              rule?["domain"] is JsonValue domain &&
                                              domain.TryGetValue<string>(out var value) && value == "direct.example.com"),
                        "Direct exclusions were lost.");
                }

            }

            Require(!File.Exists(temporary) && File.ReadAllText(source) == original,
                "Runtime configuration must be removed without modifying the source.");
        }

        Console.WriteLine("PASS: TLS fragmentation rules combine with TUN exclusions and leave source files intact");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
