# sing-box configs

## Smoke test

Use `sing-box-direct-smoke-test.json` first. It verifies that `sing-box` can create TUN, apply routes, and stop cleanly. It does not proxy traffic through a remote server, so the public IP will not change.

## SSH SOCKS TCP test

Use `sing-box-ssh-socks-tcp-test.json` only after starting `Start SOCKS` on port `8000`. This routes TCP through the existing OpenSSH SOCKS endpoint. It is not suitable for Discord RTC because OpenSSH dynamic SOCKS does not provide real UDP forwarding.

## VLESS REALITY template

Use `sing-box-vless-reality-template.json` as the real full-tunnel starting point when you have server credentials. It contains syntactically valid dummy values so `sing-box check` can parse it, but it will not connect until you replace `server`, `uuid`, `server_name`, `public_key`, and `short_id` with values from your server/client profile.
