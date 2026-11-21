using System.Net;
using System.Text;
using System.Text.Json;
using SshEnv.Core;

internal static class SubscriptionImportTests
{
    public static async Task RunAsync()
    {
        const string prefix = "vless://5fc0fc48-46bf-45f5-9e45-cd9f9829c660@";
        const string first = prefix + "one.example:443?security=reality&type=tcp&pbk=one#same-proxy-tun";
        const string second = prefix + "two.example:8443?security=reality&type=tcp&pbk=two#same-proxy-tun";
        const string tls = prefix + "tls.example:443?alpn=h2%2Chttp%2F1.1&encryption=none&flow=xtls-rprx-vision&fp=firefox&security=tls&sni=sni.example&type=tcp#TLS-Vision";
        const string url = "https://example.invalid/subscription?token=example-secret";
        var text = string.Join("\r\n", "\uFEFF" + first, second, tls, tls, first, "ss://unsupported",
            first.Replace("type=tcp", "type=ws"), "vless://malformed");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var root = Path.Combine(Path.GetTempPath(), "ssh-env-subscription-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var status = HttpStatusCode.OK;
            var body = text;
            var requests = 0;
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Require(request.RequestUri?.AbsoluteUri == url, "Subscription URL was altered.");
                requests++;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }));

            var single = await SingBoxProfileImporter.ImportLinkAsync(first, Path.Combine(root, "single"), 2080, client);
            Require(requests == 0 && Path.GetFileName(single.Profiles.Single().TunConfigPath) == "same-proxy-tun-tun.json",
                "Single-link import changed.");
            using var realityConfig = JsonDocument.Parse(File.ReadAllText(single.Profiles.Single().TunConfigPath));
            var realityTls = realityConfig.RootElement.GetProperty("outbounds")[0].GetProperty("tls");
            Require(realityTls.GetProperty("reality").GetProperty("public_key").GetString() == "one" &&
                realityTls.GetProperty("utls").GetProperty("fingerprint").GetString() == "chrome",
                "REALITY TLS settings changed.");

            var original = single.Profiles.Single();
            var originalProxy = File.ReadAllText(original.SystemProxyConfigPath);
            var originalTun = File.ReadAllText(original.TunConfigPath);
            var shared = Path.Combine(root, "single");
            var differentKey = (await SingBoxProfileImporter.ImportLinkAsync(second, shared, 2080, client)).Profiles.Single();
            var differentPort = (await SingBoxProfileImporter.ImportLinkAsync(first, shared, 2081, client)).Profiles.Single();
            var repeatedKey = (await SingBoxProfileImporter.ImportLinkAsync(second, shared, 2080, client)).Profiles.Single();
            Require(differentKey.TunConfigPath != original.TunConfigPath &&
                differentPort.TunConfigPath != original.TunConfigPath &&
                differentKey == repeatedKey &&
                File.ReadAllText(original.TunConfigPath) == originalTun &&
                File.ReadAllText(original.SystemProxyConfigPath) == originalProxy,
                "Shared-folder imports must reuse matching pairs and preserve differing pairs.");

            var tlsPair = (await SingBoxProfileImporter.ImportLinkAsync(tls, Path.Combine(root, "tls"), 2080, client)).Profiles.Single();
            foreach (var configPath in new[] { tlsPair.TunConfigPath, tlsPair.SystemProxyConfigPath })
            {
                using var config = JsonDocument.Parse(File.ReadAllText(configPath));
                var outbound = config.RootElement.GetProperty("outbounds")[0];
                var tlsOptions = outbound.GetProperty("tls");
                Require(outbound.GetProperty("flow").GetString() == "xtls-rprx-vision" &&
                    tlsOptions.GetProperty("enabled").GetBoolean() &&
                    tlsOptions.GetProperty("server_name").GetString() == "sni.example" &&
                    tlsOptions.GetProperty("utls").GetProperty("fingerprint").GetString() == "firefox" &&
                    tlsOptions.GetProperty("alpn").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "h2", "http/1.1" }) &&
                    !tlsOptions.TryGetProperty("reality", out _), "VLESS TLS Vision settings were not preserved.");
            }

            var output = Path.Combine(root, "configs");
            var plain = await SingBoxProfileImporter.ImportLinkAsync(url, output, 2080, client);
            Require(plain.Profiles.Count == 3 && plain.SkippedEntries == 5, "Mixed TLS and REALITY subscription was parsed incorrectly.");
            var files = Directory.GetFiles(output).Order().ToArray();
            var servers = files.Select(file =>
            {
                using var config = JsonDocument.Parse(File.ReadAllText(file));
                return config.RootElement.GetProperty("outbounds")[0].GetProperty("server").GetString();
            }).Order();
            Require(servers.SequenceEqual(new[] { "one.example", "one.example", "tls.example", "tls.example", "two.example", "two.example" }),
                "Servers with the same profile name overwrote each other.");

            body = encoded;
            var repeated = await SingBoxProfileImporter.ImportLinkAsync(url, output, 2080, client);
            Require(plain.Profiles.SequenceEqual(repeated.Profiles) && files.SequenceEqual(Directory.GetFiles(output).Order()),
                "Base64URL subscription or repeated import produced different profiles.");

            var invalidOutput = Path.Combine(root, "invalid");
            body = "ss://unsupported";
            try
            {
                await SingBoxProfileImporter.ImportLinkAsync(url, invalidOutput, 2080, client);
                throw new InvalidOperationException("An unsupported subscription was accepted.");
            }
            catch (ArgumentException)
            {
                Require(!Directory.Exists(invalidOutput), "An invalid subscription wrote profile files.");
            }

            status = HttpStatusCode.Forbidden;
            try
            {
                await SingBoxProfileImporter.ImportLinkAsync(url, invalidOutput, 2080, client);
                throw new Exception("An HTTP error was accepted.");
            }
            catch (InvalidOperationException exception)
            {
                Require(exception.Message.Contains("403") && !exception.Message.Contains("example-secret"),
                    "The HTTP error lost the status or exposed the subscription token.");
            }

            Console.WriteLine("PASS: VLESS key and subscription imports, repeated imports and HTTP errors");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
