using System.Net;
using SshEnv.Core;

internal static class WindowsReleaseTests
{
    public static async Task RunAsync()
    {
        const string json = """
            {"tag_name":"v2.10.3","assets":[{"name":"OpenPath-win-x64.exe"},{"name":"OpenPath-win-arm64-Setup.exe"}]}
            """;
        using var client = new HttpClient(new ReleaseHandler(HttpStatusCode.OK, json));
        foreach (var (rid, installed, file) in new[] {
            ("win-x64", false, "OpenPath-win-x64.exe"), ("win-arm64", true, "OpenPath-win-arm64-Setup.exe") })
        {
            var release = await WindowsRelease.GetLatestAsync(client, rid, installed);
            if (release?.Version != new Version(2, 10, 3) ||
                release.DownloadUri.AbsoluteUri != $"https://github.com/Igor-Zolin/OpenPath/releases/download/v2.10.3/{file}")
                throw new InvalidOperationException("Unexpected Windows update target.");
        }

        using var empty = new HttpClient(new ReleaseHandler(HttpStatusCode.NotFound, ""));
        if (await WindowsRelease.GetLatestAsync(empty, "win-x64", false) != null)
            throw new InvalidOperationException("A missing release produced a download.");

        try
        {
            await WindowsRelease.GetLatestAsync(client, "win-x64", true);
            throw new InvalidOperationException("A missing installer produced a download.");
        }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS: Windows release versions and installed/portable download selection");
    }

    private sealed class ReleaseHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
    }
}
