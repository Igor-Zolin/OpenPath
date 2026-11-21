using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SshEnv.Core;

namespace SshEnv.WinUI;

internal enum ConnectionMode
{
    Socks,
    Route,
    SingBoxTun,
    SystemProxy
}

internal sealed class DesktopProfiles
{
    public ConnectionMode Mode { get; set; }
    public ConnectionMode? LastSshMode { get; set; }
    public ConnectionMode? LastVlessMode { get; set; }
    public string? SelectedSsh { get; set; }
    public string? SelectedVless { get; set; }
    public string Theme { get; set; } = "System";
    public string Language { get; set; } = "English";
    public string CloseBehavior { get; set; } = "Ask";
    public List<SshProfile> Ssh { get; set; } = [];
    public List<VlessProfile> Vless { get; set; } = [];
    public List<SingBoxDirectRule> DirectRules { get; set; } = [];
    public SingBoxTlsFragmentOptions? TlsFragmentation { get; set; }
}

internal record SshProfile
{
    public string Name { get; init; } = string.Empty;
    public string User { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int SshPort { get; init; } = 22;
    public int LocalPort { get; init; } = 8000;
    public string? IdentityPath { get; init; }
    public string? SshPath { get; init; }
    public string? Tun2SocksPath { get; init; }
    public string? PrimaryInterface { get; init; }
    public string? PrimaryGateway { get; init; }
    public string TunName { get; init; } = "wintun";
    public string TunAddress { get; init; } = "192.168.123.1";
    public string DnsServer { get; init; } = "1.1.1.1";
    public string? EncryptedPassword { get; init; }
}

internal sealed record VlessProfile(
    string Name,
    string? TunConfigPath,
    string? SystemProxyConfigPath,
    int SystemProxyPort = 2080,
    string? SingBoxPath = null);

internal sealed class ProfileStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] PasswordEntropy = "SshEnv.SshPassword.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter<ConnectionMode>() },
        WriteIndented = true
    };
    private static readonly string[] ProxySuffixes = ["-system-proxy", "_system_proxy", "-proxy", "_proxy"];
    private static readonly string[] TunSuffixes = ["-tun", "_tun"];

    private readonly string directory;
    private readonly string profilePath;
    private readonly string previousWinUiDirectory;

    public ProfileStore(string? directory = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        this.directory = Path.GetFullPath(directory ?? Path.Combine(localAppData, "SshEnv"));
        profilePath = Path.Combine(this.directory, "profile-winui.json");
        ConfigDirectory = Path.Combine(this.directory, "configs");
        previousWinUiDirectory = Path.Combine(Path.GetDirectoryName(this.directory) ?? localAppData, "SshEnv.WinUI");
    }

    public string ConfigDirectory { get; }

    public DesktopProfiles Load()
    {
        if (File.Exists(profilePath))
        {
            return ReadProfiles(profilePath);
        }

        var previousWinUiProfile = Path.Combine(previousWinUiDirectory, "profile.json");
        var profiles = File.Exists(previousWinUiProfile)
            ? MigrateWinUiConfigs(ReadProfiles(previousWinUiProfile))
            : LoadLegacyProfiles();
        try
        {
            WriteProfiles(profiles, overwrite: false);
        }
        catch (IOException) when (File.Exists(profilePath))
        {
            // Another instance created the profile file while this one was importing.
            return ReadProfiles(profilePath);
        }

        return profiles;
    }

    public void Save(DesktopProfiles profiles) => WriteProfiles(profiles, overwrite: true);

    private static DesktopProfiles ReadProfiles(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<DesktopProfiles>(stream, JsonOptions)
            ?? throw new InvalidDataException("The WinUI profile file is empty.");
    }

    private void WriteProfiles(DesktopProfiles profiles, bool overwrite)
    {
        Directory.CreateDirectory(directory);
        var pendingPath = Path.Combine(directory, $"profile-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, profiles, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(pendingPath, profilePath, overwrite);
        }
        finally
        {
            File.Delete(pendingPath);
        }
    }

    private DesktopProfiles LoadLegacyProfiles()
    {
        var profiles = new DesktopProfiles();
        var legacyProfilePath = Path.Combine(directory, "profile.json");
        LegacyProfile legacy;
        if (File.Exists(legacyProfilePath))
        {
            using var stream = File.OpenRead(legacyProfilePath);
            legacy = JsonSerializer.Deserialize<LegacyProfile>(stream) ?? new LegacyProfile();
        }
        else
        {
            legacy = new LegacyProfile();
        }

        if (Enum.TryParse<ConnectionMode>(legacy.SelectedMode, true, out var mode) && Enum.IsDefined(mode))
        {
            profiles.Mode = mode;
        }

        profiles.Theme = legacy.Theme?.ToUpperInvariant() switch
        {
            "LIGHT" => "Light",
            "DARK" => "Dark",
            _ => "System"
        };
        profiles.Language = string.Equals(legacy.Language, "Russian", StringComparison.OrdinalIgnoreCase)
            ? "Russian"
            : "English";
        profiles.CloseBehavior = legacy.CloseBehavior?.ToUpperInvariant() switch
        {
            "MINIMIZETOTRAY" => "MinimizeToTray",
            "EXIT" => "Exit",
            _ => "Ask"
        };

        profiles.Ssh.AddRange((legacy.SshProfiles ?? [])
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
            .DistinctBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(NormalizeLegacySsh));

        var selectedSsh = profiles.Mode is ConnectionMode.Socks or ConnectionMode.Route
            ? legacy.SelectedProfileName
            : null;
        if (profiles.Ssh.All(profile => !string.Equals(profile.Name, selectedSsh, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(legacy.Host))
        {
            selectedSsh = string.IsNullOrWhiteSpace(selectedSsh) ? legacy.Host : selectedSsh;
            if (profiles.Ssh.All(profile => !string.Equals(profile.Name, selectedSsh, StringComparison.OrdinalIgnoreCase)))
            {
                // Copy the top-level SSH fields as an ordinary profile for data saved before profile lists.
                profiles.Ssh.Add(NormalizeLegacySsh(legacy with { Name = selectedSsh }));
            }
        }

        profiles.SelectedSsh = profiles.Ssh
            .FirstOrDefault(profile => string.Equals(profile.Name, selectedSsh, StringComparison.OrdinalIgnoreCase))?.Name
            ?? profiles.Ssh.FirstOrDefault()?.Name;

        ImportLegacyVless(legacy, profiles);
        LoadLegacyRules(legacy, profiles);
        return profiles;
    }

    private static SshProfile NormalizeLegacySsh(SshProfile profile) => profile with
    {
        User = profile.User ?? string.Empty,
        Host = profile.Host ?? string.Empty,
        SshPort = ValidPort(profile.SshPort, 22),
        LocalPort = ValidPort(profile.LocalPort, 8000),
        TunName = string.IsNullOrWhiteSpace(profile.TunName) ? "wintun" : profile.TunName,
        TunAddress = string.IsNullOrWhiteSpace(profile.TunAddress) ? "192.168.123.1" : profile.TunAddress,
        DnsServer = string.IsNullOrWhiteSpace(profile.DnsServer) ? "1.1.1.1" : profile.DnsServer
    };

    private void ImportLegacyVless(LegacyProfile legacy, DesktopProfiles profiles)
    {
        var legacyConfigs = ConfigDirectory;
        var configs = new Dictionary<string, VlessProfile>(StringComparer.OrdinalIgnoreCase);

        void AddConfig(string? path, bool? proxyOverride = null, string? nameOverride = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var fileName = Path.GetFileNameWithoutExtension(path);
            var proxySuffix = ProxySuffixes.FirstOrDefault(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            var suffix = proxySuffix ?? TunSuffixes.FirstOrDefault(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            var name = nameOverride ?? (suffix is null ? fileName : fileName[..^suffix.Length]);
            if (string.IsNullOrWhiteSpace(name) || (proxyOverride is null && suffix is null))
            {
                return;
            }

            var configPath = Path.GetFullPath(path);
            var profile = configs.GetValueOrDefault(name) ?? new VlessProfile(name, null, null);
            profile = proxyOverride ?? (proxySuffix is not null)
                ? profile with { SystemProxyConfigPath = configPath }
                : profile with { TunConfigPath = configPath };
            configs[name] = profile;
        }

        if (Directory.Exists(legacyConfigs))
        {
            foreach (var path in Directory.EnumerateFiles(legacyConfigs, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                AddConfig(path);
            }
        }

        var tunPath = legacy.SingBoxTunConfigPath ?? legacy.SingBoxConfigPath;
        var proxyPath = legacy.SingBoxSystemProxyConfigPath;
        var selectedName = profiles.Mode is ConnectionMode.SingBoxTun or ConnectionMode.SystemProxy
            ? legacy.SelectedProfileName
            : null;
        var existingName = FindConfigName(configs.Values, tunPath, proxyPath);
        if (string.IsNullOrWhiteSpace(selectedName))
        {
            selectedName = existingName
                ?? Path.GetFileNameWithoutExtension(!string.IsNullOrWhiteSpace(tunPath) ? tunPath : proxyPath);
        }
        else if (existingName is not null && !string.Equals(existingName, selectedName, StringComparison.OrdinalIgnoreCase))
        {
            if (configs.ContainsKey(selectedName))
            {
                selectedName = existingName;
            }
            else if (configs.Remove(existingName, out var renamed))
            {
                configs.Add(selectedName, renamed with { Name = selectedName });
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            AddConfig(tunPath, false, selectedName);
            AddConfig(proxyPath, true, selectedName);
            if (configs.TryGetValue(selectedName, out var selected))
            {
                configs[selectedName] = selected with
                {
                    SystemProxyPort = ValidPort(legacy.SystemProxyPort, 2080),
                    SingBoxPath = legacy.SingBoxPath
                };
                profiles.SelectedVless = selected.Name;
            }
        }

        profiles.Vless.AddRange(configs.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase));
        profiles.SelectedVless ??= profiles.Vless.FirstOrDefault()?.Name;
    }

    private string? FindConfigName(IEnumerable<VlessProfile> profiles, string? tunPath, string? proxyPath)
    {
        var legacyConfigs = ConfigDirectory;
        var tunName = GetManagedConfigName(tunPath);
        var proxyName = GetManagedConfigName(proxyPath);
        return profiles.FirstOrDefault(profile =>
            (tunName is { Length: > 0 } && string.Equals(Path.GetFileName(profile.TunConfigPath), tunName, StringComparison.OrdinalIgnoreCase)) ||
            (proxyName is { Length: > 0 } && string.Equals(Path.GetFileName(profile.SystemProxyConfigPath), proxyName, StringComparison.OrdinalIgnoreCase)))?.Name;

        string? GetManagedConfigName(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   File.Exists(path) &&
                   string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), legacyConfigs, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(path)
                : null;
        }
    }

    private DesktopProfiles MigrateWinUiConfigs(DesktopProfiles profiles)
    {
        var previousConfigs = Path.Combine(previousWinUiDirectory, "configs");
        var importedCopies = Path.Combine(previousConfigs, "avalonia");
        var invalidNameCharacters = Path.GetInvalidFileNameChars();
        for (var index = 0; index < profiles.Vless.Count; index++)
        {
            var profile = profiles.Vless[index];
            var sources = new[] { profile.TunConfigPath, profile.SystemProxyConfigPath }
                .Select(path => ManagedSource(path, previousConfigs)).ToArray();
            if (sources.All(source => source is null))
            {
                continue;
            }

            var destinations = sources.Select(source => source is null ? null :
                Path.Combine(ConfigDirectory, Path.GetFileName(source.Path))).ToArray();
            var needsRevisionName = sources.Select((source, mode) => (source, mode)).Any(entry =>
                entry.source is { } source &&
                ((!SameDirectory(source.Path, previousConfigs) && !SameDirectory(source.Path, importedCopies)) ||
                 Path.GetFileName(source.Path).StartsWith(".ssh-env-", StringComparison.OrdinalIgnoreCase) ||
                 (File.Exists(destinations[entry.mode]) && !HasSameContents(destinations[entry.mode]!, source.Hash))));

            if (needsRevisionName)
            {
                var pairHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{sources[0]?.Hash}|{sources[1]?.Hash}"))).ToLowerInvariant();
                var name = string.Concat(profile.Name.Select(character =>
                    invalidNameCharacters.Contains(character) ? '_' : character)).Trim(' ', '.');
                var prefix = $"{name[..Math.Min(name.Length, 48)]}-winui-{pairHash[..16]}";
                destinations[0] = Path.Combine(ConfigDirectory, $"{prefix}-tun.json");
                destinations[1] = Path.Combine(ConfigDirectory, $"{prefix}-system-proxy.json");
            }

            profiles.Vless[index] = profile with
            {
                TunConfigPath = CopySource(0) ?? profile.TunConfigPath,
                SystemProxyConfigPath = CopySource(1) ?? profile.SystemProxyConfigPath
            };

            string? CopySource(int mode) => sources[mode] is { } source
                ? CopyConfig(source, destinations[mode]!) : null;
        }

        return profiles;
    }

    private static ConfigSource? ManagedSource(string? path, string previousConfigs)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(previousConfigs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(fullPath)
            ? new ConfigSource(fullPath, FileHash(fullPath)) : null;
    }

    private string CopyConfig(ConfigSource source, string destination)
    {
        Directory.CreateDirectory(ConfigDirectory);
        if (File.Exists(destination))
        {
            if (HasSameContents(destination, source.Hash))
            {
                return destination;
            }

            throw new IOException($"A different sing-box configuration already exists at '{destination}'.");
        }

        var pendingPath = Path.Combine(ConfigDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source.Path, pendingPath);
            File.Move(pendingPath, destination);
        }
        catch (IOException) when (File.Exists(destination) && HasSameContents(destination, source.Hash))
        {
            // The other instance's completed copy can be reused.
        }
        finally
        {
            File.Delete(pendingPath);
        }

        return destination;
    }

    private static bool SameDirectory(string path, string directory) =>
        string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase);

    private static bool HasSameContents(string path, string expectedHash) => FileHash(path) == expectedHash;

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record ConfigSource(string Path, string Hash);

    private static void LoadLegacyRules(LegacyProfile legacy, DesktopProfiles profiles)
    {
        if (legacy.VlessDirectRules is not null)
        {
            foreach (var rule in legacy.VlessDirectRules)
            {
                try
                {
                    profiles.DirectRules.AddRange(SingBoxConfigDirectRules.Normalize([rule]));
                }
                catch (ArgumentException)
                {
                    // Invalid legacy rules were skipped by Avalonia too.
                }
            }
        }
        else
        {
            var domains = legacy.VlessDirectDomains
                ?? legacy.VlessDirectDomainProfiles?.SelectMany(profile => profile.Domains ?? []).ToList()
                ?? [];
            foreach (var domain in domains)
            {
                try
                {
                    profiles.DirectRules.AddRange(SingBoxConfigDirectRules.Parse(domain));
                }
                catch (ArgumentException)
                {
                    // A valid rule in a different entry can still be migrated.
                }
            }
        }

        profiles.DirectRules = SingBoxConfigDirectRules.Normalize(profiles.DirectRules).ToList();
    }

    public static IReadOnlyList<SshProfile> ReadOpenSshConfig(string path)
    {
        var profiles = new List<SshProfile>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliases = Array.Empty<string>();

        void CompleteHost()
        {
            options.TryGetValue("HostName", out var host);
            options.TryGetValue("User", out var user);
            options.TryGetValue("Port", out var port);
            options.TryGetValue("IdentityFile", out var identityPath);
            foreach (var name in aliases.Where(alias => alias.IndexOfAny(['*', '?', '!']) < 0))
            {
                profiles.Add(new SshProfile
                {
                    Name = name,
                    Host = string.IsNullOrWhiteSpace(host) ? name : host,
                    User = string.IsNullOrWhiteSpace(user) ? Environment.UserName : user,
                    SshPort = int.TryParse(port, out var parsedPort) ? ValidPort(parsedPort, 22) : 22,
                    IdentityPath = identityPath is not null &&
                                   (identityPath.StartsWith("~/", StringComparison.Ordinal) ||
                                    identityPath.StartsWith(@"~\", StringComparison.Ordinal))
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), identityPath[2..])
                        : identityPath
                });
            }

            options.Clear();
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[separator..].TrimStart(' ', '\t', '=').Trim('"');
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                CompleteHost();
                aliases = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                CompleteHost();
                aliases = [];
            }
            else if (aliases.Length > 0)
            {
                options.TryAdd(key, value);
            }
        }

        CompleteHost();
        return profiles.DistinctBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string ProtectPassword(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var password = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(RunDataProtection(password, protect: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    public static string UnprotectPassword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The saved SSH password is invalid.", exception);
        }

        var password = RunDataProtection(encrypted, protect: false);
        try
        {
            return Encoding.UTF8.GetString(password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static byte[] RunDataProtection(byte[] value, bool protect)
    {
        var inputHandle = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            var entropyHandle = GCHandle.Alloc(PasswordEntropy, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob(value.Length, inputHandle.AddrOfPinnedObject());
                var entropy = new DataBlob(PasswordEntropy.Length, entropyHandle.AddrOfPinnedObject());
                var succeeded = protect
                    ? CryptProtectData(in input, null, in entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output)
                    : CryptUnprotectData(in input, IntPtr.Zero, in entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
                if (!succeeded)
                {
                    var error = new Win32Exception(Marshal.GetLastPInvokeError());
                    throw new CryptographicException(
                        protect ? "Windows could not encrypt the SSH password." : "Windows could not decrypt the SSH password.",
                        error);
                }

                try
                {
                    var result = new byte[output.Length];
                    Marshal.Copy(output.Data, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    if (!protect)
                    {
                        for (var index = 0; index < output.Length; index++)
                        {
                            Marshal.WriteByte(output.Data, index, 0);
                        }
                    }

                    _ = LocalFree(output.Data);
                }
            }
            finally
            {
                entropyHandle.Free();
            }
        }
        finally
        {
            inputHandle.Free();
        }
    }

    private static int ValidPort(int value, int defaultValue) => value is >= 1 and <= 65535 ? value : defaultValue;

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        in DataBlob input, string? description, in DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        in DataBlob input, IntPtr description, in DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    private sealed record LegacyProfile : SshProfile
    {
        public string? SelectedMode { get; init; }
        public string? SelectedProfileName { get; init; }
        public string? Theme { get; init; }
        public string? Language { get; init; }
        public string? CloseBehavior { get; init; }
        public List<SshProfile>? SshProfiles { get; init; }
        public string? SingBoxPath { get; init; }
        public string? SingBoxConfigPath { get; init; }
        public string? SingBoxTunConfigPath { get; init; }
        public string? SingBoxSystemProxyConfigPath { get; init; }
        public int SystemProxyPort { get; init; }
        public List<SingBoxDirectRule>? VlessDirectRules { get; init; }
        public List<string>? VlessDirectDomains { get; init; }
        public List<LegacyDomainProfile>? VlessDirectDomainProfiles { get; init; }
    }

    private sealed record LegacyDomainProfile(List<string>? Domains);
}
