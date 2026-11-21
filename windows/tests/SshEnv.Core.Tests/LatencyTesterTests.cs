using System.Text.Json.Nodes;
using SshEnv.Core;

internal static class LatencyTesterTests
{
    public static Task RunAsync()
    {
        const string source = """
            {
              // Imported VLESS configs may contain comments and trailing commas.
              "dns": { "servers": [{ "tag": "dns", "type": "local" }] },
              "inbounds": [{ "type": "tun", "tag": "tun-in" }],
              "outbounds": [
                { "type": "vless", "tag": "proxy", "server": "example.com", },
                { "type": "direct", "tag": "direct" }
              ],
              "route": { "final": "direct" },
            }
            """;

        var probe = JsonNode.Parse(SingBoxLatencyTester.CreateProbeConfig(source, 31415))?.AsObject()
            ?? throw new InvalidOperationException("Latency probe config was not created.");
        var inbound = probe["inbounds"]?.AsArray().Single()?.AsObject()
            ?? throw new InvalidOperationException("Latency probe has no inbound.");
        var route = probe["route"]?.AsObject()
            ?? throw new InvalidOperationException("Latency probe has no route.");

        Assert(inbound["type"]?.GetValue<string>() == "mixed", "Latency probe must expose a mixed proxy.");
        Assert(inbound["listen"]?.GetValue<string>() == "127.0.0.1", "Latency probe must listen only on loopback.");
        Assert(inbound["listen_port"]?.GetValue<int>() == 31415, "Latency probe port was not preserved.");
        Assert(route["final"]?.GetValue<string>() == "proxy", "Latency probe must route via the VLESS outbound.");
        Assert(route["default_domain_resolver"]?.GetValue<string>() == "dns", "Latency probe must retain a domain resolver.");
        Assert(probe["dns"] is not null, "Latency probe must retain the source DNS settings.");
        Console.WriteLine("PASS: VLESS latency probe config is isolated and routes through the VLESS outbound");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
