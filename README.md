# OpenPath

OpenPath is a Windows desktop app for SSH and sing-box connections, built with WinUI 3. Download the portable EXE or the per-user installer from [GitHub Releases](https://github.com/Igor-Zolin/OpenPath/releases). The [Linux app](linux/README.md) is a separate Avalonia project.

## Feasibility

The idea is feasible, but there are two different levels:

1. `ssh -D` creates a local SOCKS5 endpoint. Applications must explicitly use that proxy.
2. "All traffic" requires a VPN-like routing layer: a TUN adapter receives IP packets and `tun2socks` forwards supported traffic into the local SOCKS5 endpoint.

The first layer is safe and simple. The second layer requires administrator privileges, route/DNS management, and reliable rollback.

## Chosen Stack

- Desktop app: .NET 10 with WinUI 3.
- CLI companion: diagnostics, dependency management, and headless connections.
- Shared networking logic: `SshEnv.Core`.
- SSH transport: built-in Windows OpenSSH client (`ssh.exe`).
- SSH full-route mode: Wintun + tun2socks.
- UDP-capable mode: sing-box with a user-provided config.

This keeps the security-sensitive networking parts in established components instead of writing a Windows packet driver from scratch.

## Current MVP

The current CLI starts and supervises the SSH dynamic forwarding process, and can optionally run a `tun2socks` route mode:

```powershell
dotnet run --project windows/src/SshEnv -- check
dotnet run --project windows/src/SshEnv -- deps check
dotnet run --project windows/src/SshEnv -- deps install
dotnet run --project windows/src/SshEnv -- plan
dotnet run --project windows/src/SshEnv -- connect --user USER --host IP_SERVER --local-port 8000
dotnet run --project windows/src/SshEnv -- sing-box --config C:\path\to\config.json
```

Equivalent SSH command:

```powershell
ssh -N -T -D 127.0.0.1:8000 -p 22 -o ExitOnForwardFailure=yes USER@IP_SERVER
```

Password authentication is supported through OpenSSH `SSH_ASKPASS`:

```powershell
dotnet run --project windows/src/SshEnv -- connect --user USER --host IP_SERVER --password PASSWORD
```

The desktop app stores saved SSH passwords encrypted with Windows DPAPI for the current Windows user.

## Windows downloads and updates

Choose x64 for Intel and AMD PCs or ARM64 for Windows on Arm. Both formats include the .NET runtime, WinUI and the verified dependency archives. OpenSSH is a Windows optional feature.

| Format | x64 | ARM64 | Updating |
| --- | --- | --- | --- |
| Portable | `OpenPath-win-x64.exe` | `OpenPath-win-arm64.exe` | Exit OpenPath from the tray and replace the EXE. |
| Installer | `OpenPath-win-x64-Setup.exe` | `OpenPath-win-arm64-Setup.exe` | Accept the update in OpenPath to install it and restart. |

Setup installs tun2socks, Wintun and sing-box from the bundled archives. If SSH is missing, Windows requests elevation to enable OpenSSH Client. Windows may need network access for that optional feature.

OpenPath checks for stable releases at startup and from **Settings → Application → Check for updates**. When you accept an installed update, OpenPath downloads the installer for your architecture, verifies its SHA-256 digest, closes active connections and restarts after setup. Portable copies open the matching EXE download in your browser. To upgrade an existing 1.6.0 installation to the first version with this feature, run the new setup once.

Enable **Always start as administrator** in **Settings → Application** to request Windows elevation on subsequent launches. Cancelling the UAC prompt stops that launch.

OpenPath reads existing profiles, saved passwords and configurations from `%LOCALAPPDATA%\SshEnv`. The installer recognizes previous SshEnv WinUI installations. On first use, WinUI can also import legacy Windows Avalonia profiles.

### Build on Windows

Use the .NET 10 SDK to launch WinUI from source or publish a self-contained EXE. Install [Inno Setup 6.3 or later](https://jrsoftware.org/inno-setup/downloads) to build the installer as well.

```powershell
dotnet run --project windows/src/SshEnv.WinUI
.\publish-winui.ps1 -Version 1.2.3
.\publish-installers.ps1 -Version 1.2.3
.\publish-installers.ps1 -RuntimeIdentifier win-arm64 -Version 1.2.3
```

Build outputs are in `dist`. Pass `-InnoCompiler C:\path\to\ISCC.exe` for a custom compiler location. Local builds are unsigned.

### Configure signed GitHub releases

1. Set up an Azure Artifact Signing account, complete identity validation and create a **Public Trust** certificate profile. Check that public identity validation is available for your country and account type. The validated identity is the publisher that Windows displays. [Microsoft setup guide](https://learn.microsoft.com/azure/artifact-signing/quickstart)
2. Create a Microsoft Entra application or user-assigned managed identity. Grant it **Artifact Signing Certificate Profile Signer** on that profile. Configure a GitHub OIDC federated credential with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange` and subject `repo:Igor-Zolin/OpenPath:environment:windows-signing`. [OIDC setup](https://github.com/Azure/artifact-signing-action/blob/main/docs/OIDC.md)
3. In GitHub **Settings → Environments**, create `windows-signing` and restrict its deployment tags to `v*`. Set the environment variables below. Restrict creation of release tags to your release maintainers using a repository ruleset.

| GitHub environment variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | Client ID of the signing identity |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription containing the signing account |
| `SIGNING_ENDPOINT` | Regional endpoint shown on the signing account |
| `SIGNING_ACCOUNT` | Artifact Signing account name |
| `SIGNING_PROFILE` | Public Trust certificate profile name |

For an explicitly unsigned release while arranging a certificate, set the **repository** Actions variable `WINDOWS_SIGNING_MODE` to `unsigned`. This skips Azure login and signing and labels the release notes accordingly. Remove the variable before tagging the first signed build. By default, a signing failure stops publication.

The public update feed uses `Igor-Zolin/OpenPath`. Downloads and update checks require releases that users can read without signing in. If the repository is renamed, update the feed in `WindowsRelease.cs`, the OIDC subject, and the project links.

Once the workflow and all Windows sources are committed and pushed, tag the release commit. For example, to ship version 1.2.3, run the following commands.

```powershell
git tag v1.2.3
git push origin v1.2.3
```

The [Windows release workflow](.github/workflows/windows-release.yml) takes its version from the tag, runs the Windows core tests, builds both architectures, signs each portable EXE, embeds it in its installer, and signs the installer. It checks signatures and timestamps before creating the GitHub Release. If signing fails, fix the credentials or profile and rerun the failed jobs for the same tag.

Authenticode identifies the publisher and lets Windows verify file integrity. SmartScreen can still warn about a new file while its reputation builds, including files signed with an EV certificate. Self-signed certificates cannot establish public publisher trust. [Microsoft SmartScreen guidance](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)

## Dependency Bootstrap

The app can install the portable route-mode dependencies into:

```text
%LOCALAPPDATA%\SshEnv\tools
```

Installed files:

- `tun2socks.exe` v2.6.0, using the baseline AMD64 build on x64 (no AMD64 v3 requirement).
- `wintun.dll` from the official Wintun ZIP, copied next to `tun2socks.exe`.
- `sing-box.exe` v1.13.12, pinned to the version used by the existing config importer.

All three files are placed together in the managed tools directory. Empty,
truncated, and wrong-architecture binaries are repaired on the next install.
Copyright and license files are included in `tools\licenses`.

In the Windows app, `Settings` → `Remove dependencies` removes these
managed files after confirmation. Disconnect any active tunnel first.
OpenSSH, manually installed copies, and connection profiles are preserved.
Use `Install dependencies` to restore the managed files.

Uninstalling the Windows application also removes the three managed binaries and their license files. Portable and CLI copies for the same user share this directory and can reinstall the tools if needed. Setup records whether it enabled the system-wide OpenSSH Client feature. Only in that case does uninstall request elevation to remove it. Profiles and unrelated files are preserved.

Builds launched directly from source download the same pinned, verified archives.
Each download has a 90-second deadline covering both headers and the response
body. There is no BITS job or dependency on Node, curl, or other external runtimes.
The installation view shows progress, errors, and a cancel button. One failed
package does not prevent installing the others.

After installation, the expected `tun2socks.exe` path is:

```text
%LOCALAPPDATA%\SshEnv\tools\tun2socks.exe
```

Dependency paths and status refresh automatically after installation, including
paths saved on another computer that no longer exist.

Regression checks (no third-party test framework required):

```powershell
.\scripts\prepare-dependencies.ps1
dotnet run --project windows/tests/SshEnv.Core.Tests "-p:DependencyBundleDirectory=$PWD\dist\.dependencies-win-x64"
```

Add `-- --online` to also download and verify the actual upstream archives.
The default checks prohibit HTTP and clear PATH during offline installation.
They cover executable startup, repeated installation, damaged files, architecture
mismatch, partial installation, cancellation, and stalled HTTP headers/body.

CLI:

```powershell
dotnet run --project windows/src/SshEnv -- deps check
dotnet run --project windows/src/SshEnv -- deps install
dotnet run --project windows/src/SshEnv -- deps install-sing-box
dotnet run --project windows/src/SshEnv -- deps install --include-openssh
dotnet run --project windows/src/SshEnv -- deps list-wintun
dotnet run --project windows/src/SshEnv -- deps repair-wintun
```

OpenSSH is handled differently: if `ssh.exe` is missing, `--include-openssh` or the UI prompt starts the elevated Windows Optional Feature installer:

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
```

That path is intentional. Shipping a separate third-party SSH client would create a larger security and update surface.

## Wintun Repair

Repeated failed route-mode attempts can leave extra `tun2socks Tunnel` Wintun adapters such as `wintun 1`, `wintun 2`, etc. These can hold old IP addresses and break the next run with `This object already exists`.

Use `Repair Wintun` in the desktop app, or:

```powershell
dotnet run --project windows/src/SshEnv -- deps list-wintun
dotnet run --project windows/src/SshEnv -- deps repair-wintun
```

Repair removes only devices with:

- Instance ID prefix `SWD\Wintun\`
- Device description `tun2socks Tunnel`

It intentionally does not remove `sing-tun`, WireGuard, Ethernet, Wi-Fi, or other unrelated adapters.

## Route Mode

Route mode wires the local SSH SOCKS tunnel into `tun2socks`.

Important behavior:

- The Windows app applies Windows routes and DNS automatically in `tun2socks` mode.
- The CLI starts a process-only diagnostic route unless `--apply-network` is provided.
- Full-system IPv4 routing requires Administrator rights, a primary interface, a primary gateway, and a resolvable SSH host.
- Stop other TUN/VPN clients such as Hiddify/sing-box before full route mode. The app logs a warning when it detects likely `sing-tun`, `tun0`, or another default TUN route.

Prerequisites:

- `tun2socks.exe`.
- `wintun.dll` available next to `tun2socks.exe` or in `PATH`.
- Administrator privileges on Windows to create or open the Wintun adapter.
- A known physical interface name, for example `Wi-Fi`, `Ethernet`, or a localized name like `Провод`, only when using `--apply-network`.

Safe process-only run:

```powershell
dotnet run --project windows/src/SshEnv -- route `
  --user USER `
  --host IP_SERVER `
  --tun2socks-path C:\Tools\tun2socks\tun2socks.exe
```

Full Windows route run:

```powershell
dotnet run --project windows/src/SshEnv -- route `
  --user USER `
  --host IP_SERVER `
  --primary-interface "Wi-Fi" `
  --primary-gateway 192.168.1.1 `
  --ssh-server-address IP_SERVER `
  --tun2socks-path C:\Tools\tun2socks\tun2socks.exe `
  --apply-network
```

`--apply-network` applies `netsh` IP/DNS/routes and rolls back the routes when the process stops.

On Windows the app applies split default routes, not a single default route:

```text
0.0.0.0/1
128.0.0.0/1
```

This avoids fighting the existing default route while still catching normal IPv4 internet traffic. A separate `/32` host route keeps the SSH server reachable through the physical gateway, otherwise the tunnel would try to route its own SSH connection through itself.

## sing-box Mode

sing-box mode is separate from SSH route mode. It is intended for UDP-capable full-tunnel configs, including use cases such as Discord RTC where plain OpenSSH `ssh -D` is not enough.

The app does not generate server credentials yet. Provide a working sing-box JSON config from your own server/client profile:

```powershell
dotnet run --project windows/src/SshEnv -- sing-box --config C:\path\to\config.json
```

The desktop app saves the `sing-box.exe` and config paths in the UI profile. It starts:

```powershell
sing-box run -c C:\path\to\config.json
```

To import a VLESS TLS or REALITY profile in the desktop app:

1. On the Home page, open `Import` and select the VLESS entry.
2. Paste a full `vless://...` share link or an HTTP(S) subscription URL. You can also import either from the clipboard.
3. The generated TUN and System Proxy configs are saved under `%LOCALAPPDATA%\SshEnv\configs`.
4. Select the imported profile and its TUN or System Proxy routing mode.
5. Turn on the connection switch.

Subscriptions can return a plain text or Base64 list with one link per line. The app imports supported VLESS TCP profiles with TLS or REALITY and reports skipped entries in the log. Import the URL again to fetch the latest list. Previously imported profiles remain available for manual removal.

For VLESS TUN, open `Tunneling` and choose `Manage exclusions` to configure traffic that must use the normal network connection. The list is saved across application and computer restarts, and applies to every VLESS TUN profile. The app applies temporary `direct` route rules at launch (and local DNS routing for domain and application rules), without modifying the selected sing-box JSON file.

Enter one rule per line. Supported examples include:

- `example.com` or `*.example.com` for a domain and its subdomains;
- `localhost*` for a domain wildcard;
- `127.0.0.1/*`, `10.0.*.*`, or `192.168.0.0/16` for IP addresses and CIDRs;
- `suffix:localhost`, `127.0.0.0/8`, `::1/128`, and `strict-route:false` together for Windows localhost compatibility when a development server listens on IPv6;
- `host:api.example.com`, `keyword:local`, `regex:^test\..+$`, `process:chrome.exe`, `port:443`, or `network:udp` for explicit rule types.

IP/CIDR exclusions are also added to the TUN inbound's `route_exclude_address`. Nothing is excluded from the TUN route unless the user adds the corresponding rule.

`strict-route:false` is never enabled by default. It is an explicit compatibility option for a sing-box limitation on Windows and may allow IPv6 or DNS traffic outside the tunnel. Prefer binding a local development server to `127.0.0.1` when strict routing must remain enabled.

Application exclusions can also be added from the detected Windows installed and running applications or by selecting an `.exe` file. Running applications (including Discord and portable games) are detected even without a registry entry. Start the application and use `Refresh list` if it was launched after opening the exclusions editor. Search matches display names, executable names, and paths. The portable exclusion stores only the executable name (for example, `chrome.exe`), not the user-specific installation path.

Run application-discovery regression checks separately with `dotnet run --project windows/tests/SshEnv.Core.Tests -- --catalog-only`. These checks exercise real Windows process discovery, newly launched portable executables, duplicate processes, and inaccessible executable paths.

The VLESS TUN runtime config always excludes the local loopback networks `127.0.0.0/8` and `::1/128` from automatic TUN routes, so development servers on `localhost` remain reachable even with `strict_route` enabled.

Use `Export exclusions` to create a portable JSON file without server credentials. `Import exclusions` validates and merges such a file with the existing rules.

CLI import:

```powershell
dotnet run --project windows/src/SshEnv -- import-vless --link "vless://..."
dotnet run --project windows/src/SshEnv -- import-vless --link "https://example.com/subscription"
```

Notes:

- Stop Hiddify, other sing-box clients, WireGuard, and similar TUN/VPN clients before starting this mode.
- TUN configs normally require Administrator rights on Windows.
- System Proxy mode does not create a TUN adapter. It starts a local sing-box mixed proxy and temporarily enables the current user's Windows proxy settings, then restores them on stop.
- Exclusion rules are available only in VLESS TUN mode; they do not apply to System Proxy mode.
- Application exclusions use sing-box process matching on Windows. Restart an excluded application if it keeps an already-open connection after the rules change.
- While a tunnel is active, profiles and modes remain available. A relevant change safely stops the current tunnel and starts it again with the updated settings.
- This mode relies on the provided sing-box config for protocol choice, DNS, routing, and UDP behavior.

## Project Layout

- `windows/` contains the Windows WinUI 3 app, the CLI, the
  AskPass helper, Windows connection and dependency management, tests, and
  publishing scripts. The Windows solution and publish commands are available
  from the repository root.
- `linux/` contains the independent [Linux Avalonia app](linux/README.md).
- `shared/` contains the sing-box profile importer used by both platforms.
- `configs/` contains example sing-box configurations.

## License

Copyright (c) 2026 Igor Zolin. The original OpenPath code is licensed under the [GNU General Public License, version 3 only](LICENSE) (`GPL-3.0-only`). Third-party components and datasets are covered by their respective licenses.
