using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SshEnv.Core;

public sealed record WindowsRelease(Version Version, Uri DownloadUri)
{
    private const string Repository = "Igor-Zolin/OpenPath";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static Task<WindowsRelease?> GetLatestAsync(string runtimeIdentifier, bool installed) =>
        GetLatestAsync(Client, runtimeIdentifier, installed);

    internal static async Task<WindowsRelease?> GetLatestAsync(HttpClient client, string runtimeIdentifier, bool installed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("OpenPath");
        using var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<ReleaseResponse>();
        var tag = release?.Tag;
        if (tag is not ['v', ..] || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version}")
            throw new InvalidDataException("Unexpected OpenPath release tag.");

        var asset = $"OpenPath-{runtimeIdentifier}{(installed ? "-Setup" : "")}.exe";
        if (release!.Assets?.Any(item => item.Name == asset) != true)
            throw new InvalidDataException($"Release {tag} is missing {asset}.");

        return new(version, new Uri($"https://github.com/{Repository}/releases/download/{tag}/{asset}"));
    }

    private sealed record ReleaseResponse([property: JsonPropertyName("tag_name")] string? Tag, Asset[]? Assets);
    private sealed record Asset(string Name);
}
