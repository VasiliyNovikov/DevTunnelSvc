# DevTunnelSvc

> [!IMPORTANT]
> This repository is **100% vibe-coded**.

DevTunnelSvc hosts and connects to private Microsoft Dev Tunnels on Windows and Linux. A per-user background service owns the SDK connections, so closing the CLI does not stop your forwards.

## Download and run

Choose an asset from [GitHub Releases](https://github.com/VasiliyNovikov/DevTunnelSvc/releases). V1 uses **self-contained, single-file ReadyToRun** with the official C# SDK. It includes the .NET runtime and can JIT code; no separate .NET installation is needed. The bundle extracts native runtime libraries to a per-user cache on first launch.

| OS | x64 | Arm64 |
| --- | --- | --- |
| Windows | `devtunnelsvc-win-x64.zip` | `devtunnelsvc-win-arm64.zip` |
| Linux | `devtunnelsvc-linux-x64` | `devtunnelsvc-linux-arm64` |

Keep the executable in a stable, user-writable location on `PATH`. On Windows, extract `devtunnelsvc.exe`. On Linux, install the downloaded binary as `devtunnelsvc` with execute permission (for example, `install -Dm755 devtunnelsvc-linux-x64 ~/.local/bin/devtunnelsvc`). Linux releases target **glibc 2.28 or newer**, not musl/Alpine, and use the distribution's normal .NET system dependencies including ICU and OpenSSL.

## Forward an application

On **both machines**, run these commands as the user who will own the service. `service install` registers the Windows tray process at sign-in or installs and enables the Linux systemd **user** unit. Use the same GitHub account on both machines for this example.

```shell
devtunnelsvc service install
devtunnelsvc service start
devtunnelsvc login
```

`devtunnelsvc login` prints the one-time GitHub code, copies only that code to your clipboard if available, and opens GitHub in your default browser. Paste or type the code there and approve the request. Use `devtunnelsvc login --no-clipboard` to leave your clipboard unchanged, or `--no-browser` to open the printed URL yourself. The flags can be combined.

On Linux, clipboard copying uses `wl-copy` on Wayland or `xclip`/`xsel` on X11. The browser uses `xdg-open` or an executable named by `BROWSER` (including a browser-forwarding helper). Neither a clipboard nor a local browser is required: you can enter the printed code on another device.

On the **host**, start your application on `127.0.0.1:3000`. Create a private tunnel, then use the `id.cluster` printed by `create` in place of `TUNNEL`:

```shell
devtunnelsvc create
devtunnelsvc host TUNNEL --tunnel-port 8080 --target-port 3000
```

On the **client**, use the same tunnel ID:

```shell
devtunnelsvc connect TUNNEL --tunnel-port 8080 --listen-port 5000
devtunnelsvc status
```

Connect your TCP client to `localhost:5000`. Traffic travels through tunnel port `8080` to the host's port `3000`. The client binds loopback only; an occupied listen port is an error, not a request to choose a different port.

## Saved sign-in

Sign in once per machine. `devtunnelsvc` keeps its own GitHub credential across service stops, reboots, and CLI updates; it does not read or change the official `devtunnel` CLI's login. When the service next needs an expiring access token, it uses GitHub's device-flow refresh token and saves the rotated pair. If GitHub revokes the authorization or the refresh token itself expires, run `devtunnelsvc logout` and `devtunnelsvc login` again.

If upgrading from a memory-only build, stop the old service before replacing the executable. Start the new service and run `devtunnelsvc login` once to save the sign-in.

- **Windows:** `%LOCALAPPDATA%\DevTunnelSvc\auth\github.dpapi`, encrypted with Windows Data Protection (DPAPI) for the signed-in user.
- **Linux:** `$XDG_DATA_HOME/DevTunnelSvc/auth/github.json` (normally `~/.local/share/DevTunnelSvc/auth/github.json`). The file is **not encrypted**: it is readable and writable only by its owner (`0600`) inside a private directory (`0700`). This works without a desktop keyring. Protect backups and disk access accordingly.

`devtunnelsvc logout` stops active mappings and deletes the local saved credential; it does not revoke the GitHub App authorization or delete cloud tunnels. To revoke the authorization itself, use your GitHub account settings.

## Stop, update, and remove

`status` lists mapping names and SDK connection states. `stop NAME` removes one mapping. `delete TUNNEL` deletes the cloud tunnel explicitly; stopping a mapping does not. V1 supports one hosted mapping per tunnel at a time. Mappings are still in memory: after a restart reissue `host` or `connect`, not `login`.

```shell
devtunnelsvc update --check
devtunnelsvc service stop
devtunnelsvc update
devtunnelsvc service start
```

`update` selects the latest stable asset for the installed OS and architecture and verifies its GitHub SHA-256 digest before replacing the executable. It refuses to update a running service. `service run` runs the service in the foreground for diagnostics. Linux requires a running systemd user manager for `install` and `start`. To keep the service running after the last login session ends, configure user lingering according to your system policy.

To uninstall, run `devtunnelsvc logout` to remove the credential, then stop the service. On Windows remove the `DevTunnelSvc` value from `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. On Linux run `systemctl --user disable devtunnelsvc.service`, remove `devtunnelsvc.service` from your systemd user configuration directory, and run `systemctl --user daemon-reload`. Then remove the downloaded executable.

For design requirements, coding guidelines, build commands, and release publishing, see [AGENTS.md](AGENTS.md).
