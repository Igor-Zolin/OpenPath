using System.Text.Json.Nodes;
using SshEnv.Core;

internal static class ExclusionPresetTests
{
    public static async Task RunAsync(string directory, Func<string, Task> checkConfig)
    {
        const string key = "vless://11111111-1111-1111-1111-111111111111@127.0.0.1:443?security=tls&type=tcp#preset-test";
        var source = SingBoxProfileImporter.ImportVlessUriPair(key, directory, 2080).TunConfigPath;
        var original = File.ReadAllText(source);
        var russian = SingBoxConfigDirectRules.GetPresetEntries("ru");
        Require(russian.Contains("*.ru") && russian.Any(entry => entry.Contains('/')), "RU preview lost domains or GeoIP ranges.");
        var microsoft = SingBoxConfigDirectRules.GetPresetEntries("microsoft");
        Require(microsoft.Any(entry => entry.EndsWith("microsoftonline.com")) && microsoft.Any(entry => entry.Contains('/')),
            "Microsoft preview lost domains or IP ranges.");
        var lan = SingBoxConfigDirectRules.GetPresetEntries("lan");
        Require(lan.Contains("cidr:192.168.0.0/16") && lan.Contains("*.local"), "LAN preview lost addresses or domains.");
        for (var mask = 0; mask < 8; mask++)
        {
            bool Enabled(int bit) => (mask & (1 << bit)) != 0;
            var selected = SingBoxConfigDirectRules.PresetIds.Where((_, i) => Enabled(i))
                .Select(id => new SingBoxDirectRule("preset", id)).ToList();
            selected.Add(new("domain_suffix", "custom.example"));
            var export = Path.Combine(directory, "preset-export.json");
            SingBoxConfigDirectRules.Export(export, selected);
            var imported = SingBoxConfigDirectRules.Import(export);
            Require(imported.SequenceEqual(selected), "Export lost an exclusion.");

            using var runtime = SingBoxConfigDirectRules.CreateRuntimeConfig(source, imported);
            var config = JsonNode.Parse(File.ReadAllText(runtime.ConfigPath))!;
            var route = config["route"]!["rules"]!.AsArray();
            var dns = config["dns"]!["rules"]!.AsArray();
            foreach (var (name, bit, domain) in new[] {
                ("geoip-ru", 0, false), ("cyrillic-domains", 0, true), ("geosite-category-ru", 0, true),
                ("geosite-microsoft", 1, true), ("microsoft365-ips", 1, false) })
            {
                var tag = "openpath-" + name;
                Require(route.Any(r => (string?)r?["rule_set"] == tag && (string?)r?["outbound"] == "direct") == Enabled(bit),
                    $"Unexpected route for {name}.");
                Require(dns.Any(r => (string?)r?["rule_set"] == tag && (string?)r?["server"] == "local") == (domain && Enabled(bit)),
                    $"Unexpected DNS rule for {name}.");
            }
            var tun = config["inbounds"]!.AsArray().First(n => (string?)n?["type"] == "tun")!;
            foreach (var cidr in new[] { "192.168.0.0/16", "fc00::/7", "fe80::/10" })
            {
                Require((tun["route_exclude_address"]?.AsArray().Any(n => (string?)n == cidr) == true) == Enabled(2),
                    $"Unexpected LAN exclusion for {cidr}.");
            }
            Require(route.Any(r => (string?)r?["domain_suffix"] == "custom.example"), "Lost manual exclusion.");
            await checkConfig(runtime.ConfigPath);
        }
        Require(File.ReadAllText(source) == original, "Modified the source configuration.");
        Console.WriteLine("PASS: every exclusion preset combination round-trips and passes sing-box check");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
