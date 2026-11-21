using System.Net;

namespace SshEnv.Core;

public sealed record ConnectConfig(
    string User,
    string Host,
    int LocalPort,
    IPAddress BindAddress,
    int SshPort,
    string? IdentityFile,
    string? SshPath,
    string? Password,
    IReadOnlyList<string> SshOptions);

public sealed record RouteConfig(
    ConnectConfig Connect,
    string? Tun2SocksPath,
    string PrimaryInterface,
    string TunName,
    IPAddress TunAddress,
    IPAddress TunMask,
    IPAddress DnsServer,
    bool ApplyNetwork,
    IPAddress? PrimaryGateway,
    IPAddress? SshServerAddress);

public sealed record SingBoxConfig(
    string? SingBoxPath,
    string ConfigPath,
    IReadOnlyList<SingBoxDirectRule>? DirectRules = null,
    SingBoxTlsFragmentOptions? TlsFragmentation = null);

public sealed record SingBoxSystemProxyConfig(
    string? SingBoxPath,
    string ConfigPath,
    int LocalProxyPort,
    SingBoxTlsFragmentOptions? TlsFragmentation = null);

public sealed record SingBoxTlsFragmentOptions(
    IReadOnlyList<SingBoxDirectRule> Domains,
    bool FragmentTcp,
    int? FallbackDelayMilliseconds = null);
