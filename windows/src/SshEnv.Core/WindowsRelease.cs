using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SshEnv.Core;

public sealed record WindowsRelease(Version Version, Uri DownloadUri)
{
    private string? Digest { get; init; }

    public async Task VerifyInstallerAsync(Stream installer, CancellationToken cancellationToken = default)
    {
        if (Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException("Missing SHA-256 digest for the installer.");
        var hash = await SHA256.HashDataAsync(installer, cancellationToken);
        if (!Convert.ToHexString(hash).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer SHA-256 mismatch.");
    }
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
        var versionText = tag is ['v', ..] ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) ||
            version.Build < 0 || version.Revision >= 0 || versionText != version.ToString())
            throw new InvalidDataException("Unexpected OpenPath release tag.");

        var asset = $"OpenPath-{runtimeIdentifier}{(installed ? "-Setup" : "")}.exe";
        var selected = release!.Assets?.FirstOrDefault(item => item.Name == asset);
        if (selected == null)
            throw new InvalidDataException($"Release {tag} is missing {asset}.");

        return new(version, new Uri($"https://github.com/{Repository}/releases/download/{tag}/{asset}")) { Digest = selected.Digest };
    }

    private sealed record ReleaseResponse([property: JsonPropertyName("tag_name")] string? Tag, Asset[]? Assets);
    private sealed record Asset(string Name, string? Digest);
}
