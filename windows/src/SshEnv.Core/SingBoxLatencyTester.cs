using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SshEnv.Core;

/// <summary>
/// Measures an HTTP request made through a short-lived local sing-box proxy.
/// This validates the configured VLESS outbound rather than only testing its TCP port.
/// </summary>
public static class SingBoxLatencyTester
{
    // A server may block a particular connectivity-check host even when the VLESS route works.
    // Try independent providers so the result reflects the VLESS profile, not one probe endpoint.
    private static readonly Uri[] ProbeUris =
    [
        new("https://www.cloudflare.com/cdn-cgi/trace"),
        new("https://www.gstatic.com/generate_204"),
        new("https://www.msftconnecttest.com/connecttest.txt")
    ];
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    public static async Task<TimeSpan> MeasureAsync(
        string singBoxPath,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(singBoxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        if (!File.Exists(singBoxPath))
        {
            throw new FileNotFoundException("sing-box executable was not found.", singBoxPath);
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("VLESS config file was not found.", configPath);
        }

        var localPort = ReserveLoopbackPort();
        var temporaryConfigPath = Path.Combine(Path.GetTempPath(), $"ssh-env-latency-{Guid.NewGuid():N}.json");
        Process? process = null;
        var output = new StringBuilder();
        try
        {
            File.WriteAllText(temporaryConfigPath, CreateProbeConfig(File.ReadAllText(configPath), localPort));
            process = StartSingBox(singBoxPath, temporaryConfigPath, output);
            await WaitForProxyAsync(process, localPort, output, cancellationToken);

            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy($"http://127.0.0.1:{localPort}") { BypassProxyOnLocal = false }
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            Exception? lastProbeFailure = null;
            foreach (var probeUri in ProbeUris)
            {
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCancellation.CancelAfter(RequestTimeout);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
                    var stopwatch = Stopwatch.StartNew();
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
                    stopwatch.Stop();
                    if ((int)response.StatusCode < 500)
                    {
                        return stopwatch.Elapsed;
                    }

                    lastProbeFailure = new InvalidOperationException($"{probeUri.Host} returned HTTP {(int)response.StatusCode}.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    lastProbeFailure = new TimeoutException($"{probeUri.Host} did not respond in time.");
                }
                catch (HttpRequestException exception)
                {
                    lastProbeFailure = new InvalidOperationException($"{probeUri.Host}: {exception.Message}", exception);
                }
            }

            throw new InvalidOperationException("The VLESS key could not reach any latency probe.", lastProbeFailure);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            TryDelete(temporaryConfigPath);
        }
    }

    internal static string CreateProbeConfig(string sourceConfig, int localPort)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(
                sourceConfig,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not parse VLESS config: {exception.Message}", exception);
        }

        if (parsed is not JsonObject config)
        {
            throw new InvalidOperationException("VLESS config root must be a JSON object.");
        }

        if (config["outbounds"] is not JsonArray outbounds ||
            !outbounds.OfType<JsonObject>().Any(outbound =>
                string.Equals(GetString(outbound["type"]), "vless", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The selected config does not contain a VLESS outbound.");
        }

        var outboundTag = outbounds.OfType<JsonObject>()
            .Select(outbound => GetString(outbound["tag"]))
            .FirstOrDefault(tag => string.Equals(tag, "proxy", StringComparison.OrdinalIgnoreCase))
            ?? GetString(outbounds.OfType<JsonObject>().First(outbound =>
                string.Equals(GetString(outbound["type"]), "vless", StringComparison.OrdinalIgnoreCase))["tag"])
            ?? throw new InvalidOperationException("The VLESS outbound must have a tag.");

        var domainResolver = GetDomainResolver(config);
        config["log"] = new JsonObject { ["level"] = "error" };
        config["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "latency-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = localPort
            }
        };
        config["route"] = new JsonObject
        {
            ["final"] = outboundTag,
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = domainResolver
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetDomainResolver(JsonObject config)
    {
        if (config["route"] is JsonObject sourceRoute &&
            GetString(sourceRoute["default_domain_resolver"]) is { Length: > 0 } sourceResolver)
        {
            return sourceResolver;
        }

        if (config["dns"] is JsonObject dns && dns["servers"] is JsonArray servers)
        {
            var localServer = servers.OfType<JsonObject>()
                .FirstOrDefault(server => string.Equals(GetString(server["type"]), "local", StringComparison.OrdinalIgnoreCase));
            var localServerTag = localServer is null ? null : GetString(localServer["tag"]);
            if (!string.IsNullOrWhiteSpace(localServerTag))
            {
                return localServerTag;
            }
        }

        const string fallbackResolverTag = "latency-local-dns";
        var fallbackDns = config["dns"] as JsonObject ?? new JsonObject();
        var fallbackServers = fallbackDns["servers"] as JsonArray ?? new JsonArray();
        fallbackServers.Add(new JsonObject
        {
            ["type"] = "local",
            ["tag"] = fallbackResolverTag
        });
        fallbackDns["servers"] = fallbackServers;
        config["dns"] = fallbackDns;
        return fallbackResolverTag;
    }

    private static Process StartSingBox(string singBoxPath, string configPath, StringBuilder output)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(singBoxPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(configPath);
        process.OutputDataReceived += (_, eventArgs) => AppendOutput(output, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AppendOutput(output, eventArgs.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start sing-box for the latency test.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForProxyAsync(Process process, int port, StringBuilder output, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartupTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var details = GetOutput(output);
                throw new InvalidOperationException(
                    $"sing-box stopped before the latency test started (exit code {process.ExitCode})" +
                    (string.IsNullOrWhiteSpace(details) ? "." : $": {details}"));
            }

            using var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(80, timeout.Token);
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string? GetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static void AppendOutput(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (output)
        {
            if (output.Length < 2_000)
            {
                output.AppendLine(line);
            }
        }
    }

    private static string GetOutput(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString().ReplaceLineEndings(" ").Trim();
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
    }
}
