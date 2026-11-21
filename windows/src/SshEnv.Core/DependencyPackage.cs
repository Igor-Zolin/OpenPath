using System.Runtime.InteropServices;
using System.Text.Json;

namespace SshEnv.Core;

internal sealed record DependencyPackage(
    string Id, string Architecture, string Binary, string ArchiveEntry, string Url, string Sha256)
{
    public string ArchiveName => Path.GetFileName(new Uri(Url).AbsolutePath);

    public static DependencyPackage Get(string id)
    {
        using var manifest = typeof(DependencyPackage).Assembly.GetManifestResourceStream("SshEnv.Dependencies.Manifest.json")
            ?? throw new InvalidOperationException("Dependency manifest is missing from this build.");
        var packages = JsonSerializer.Deserialize<DependencyPackage[]>(manifest,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return packages.SingleOrDefault(package => package.Id == id &&
            package.Architecture == RuntimeInformation.ProcessArchitecture.ToString())
            ?? throw new PlatformNotSupportedException($"Unsupported dependency architecture: {RuntimeInformation.ProcessArchitecture}.");
    }
}
