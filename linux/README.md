# SshEnv for Linux

The Linux desktop app has its own project. It links the shared VLESS importer
from source and builds independently of the Windows solution.

## Run from source

Use a Linux desktop with X11 or XWayland, the .NET 10 SDK, and OpenSSH
(`ssh` on `PATH`). The VLESS tab also requires `sing-box` on `PATH`.
Both proxy modes run as the current user.

```bash
dotnet run --project linux/SshEnv.Linux
```

- **SSH SOCKS5.** Enter the host and username, then connect using OpenSSH
  keys or `ssh-agent`. The default endpoint is `127.0.0.1:8000`. SSH fields
  are entered for each launch.
- **VLESS proxy.** Import a VLESS REALITY key or an HTTP(S) subscription
  containing a plain text or Base64 list of VLESS REALITY TCP links. Select a
  profile and connect. The mixed HTTP and SOCKS5 endpoint is
  `127.0.0.1:2080`. Imported profiles are kept under
  `$XDG_DATA_HOME/ssh-env-linux/configs` (default
  `~/.local/share/ssh-env-linux/configs`), with directory permissions of
  `0700`. Import the URL again to fetch an updated subscription.

Configure each client application to use the relevant local proxy. This
milestone covers local proxies. System-wide routing and SSH password
authentication are outside its scope.

## Publish

The following command creates a framework-dependent Linux x64 build. It
requires the .NET 10 runtime and desktop prerequisites on the target machine.

```bash
dotnet publish linux/SshEnv.Linux -c Release -r linux-x64 --self-contained false \
  -p:UseAppHost=false -o dist/linux-x64
dotnet dist/linux-x64/SshEnv.Linux.dll
```
