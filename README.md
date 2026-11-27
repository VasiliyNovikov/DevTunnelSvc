# DevTunnelSvc

> [!IMPORTANT]
> This repository is **100% vibe-coded**.

DevTunnelSvc hosts and connects to private Microsoft Dev Tunnels on Windows and Linux. A per-user background service owns the SDK connections, so closing the CLI does not stop your forwards.

## Install

Run the matching command as the user who will use the tunnels, **without sudo or an elevated PowerShell**. The installer downloads the latest stable GitHub Release for your architecture, checks its size and SHA-256 digest, registers and starts the per-user service, and immediately runs GitHub device login if you're signed out. Approve the sign-in yourself in a browser. You can inspect the [Linux script](install.sh) or [Windows script](install.ps1) before running it.

**Linux** (glibc 2.28+, `sh`, `curl`, `jq`, GNU coreutils, and a running systemd user manager):

```sh
curl -fsS https://raw.githubusercontent.com/VasiliyNovikov/DevTunnelSvc/main/install.sh | sh
```

**Windows** (PowerShell 7 recommended, Windows PowerShell 5.1 also supported):

```powershell
irm https://raw.githubusercontent.com/VasiliyNovikov/DevTunnelSvc/main/install.ps1 | iex
```

Linux installs to `~/.local/bin/devtunnelsvc`; add `~/.local/bin` to your shell's `PATH` if needed. Windows installs to `%LOCALAPPDATA%\Programs\DevTunnelSvc\devtunnelsvc.exe` and adds that directory to the current PowerShell session and per-user `PATH`. If another terminal still has the old `PATH`, use the printed install path until the next sign-in. Re-running an installer reuses a matching binary and does not sign you out or stop active mappings. For a different existing version, use the [CLI updater](#stop-update-and-remove) instead.

### Install with a coding agent

Use the copy button on this block and paste the prompt into your agent:

```text
Install DevTunnelSvc for my current user on this machine. Detect the OS and run only its official installer, without sudo or elevation:
Linux (sh): curl -fsS https://raw.githubusercontent.com/VasiliyNovikov/DevTunnelSvc/main/install.sh | sh
Windows (PowerShell): irm https://raw.githubusercontent.com/VasiliyNovikov/DevTunnelSvc/main/install.ps1 | iex
The installer registers and starts my per-user service and begins GitHub device login if needed. Let me complete authorization in the browser; do not request my password or access/refresh tokens. If setup fails, show the error rather than bypassing verification or replacing an existing installation. After I finish signing in, run devtunnelsvc --version and devtunnelsvc status (use the printed install path if necessary) and tell me the version and sign-in state.
```

### Manual download

Choose an asset from [GitHub Releases](https://github.com/VasiliyNovikov/DevTunnelSvc/releases). V1 uses **self-contained, single-file ReadyToRun** with the official C# SDK. It includes the .NET runtime and can JIT code; no separate .NET installation is needed. The bundle extracts native runtime libraries to a per-user cache on first launch.

| OS | x64 | Arm64 |
| --- | --- | --- |
| Windows | `devtunnelsvc-win-x64.zip` | `devtunnelsvc-win-arm64.zip` |
| Linux | `devtunnelsvc-linux-x64` | `devtunnelsvc-linux-arm64` |

Keep the executable in a stable, user-writable location on `PATH`. On Windows, extract `devtunnelsvc.exe`. On Linux, install the downloaded binary as `devtunnelsvc` with execute permission (for example, `install -Dm755 devtunnelsvc-linux-x64 ~/.local/bin/devtunnelsvc`). Linux releases target **glibc 2.28 or newer**, not musl/Alpine, and use the distribution's normal .NET system dependencies including ICU and OpenSSL.

## Forward an application

Install on **both machines** and use the same GitHub account for this example. If you downloaded the binary manually, run the following setup commands; the one-line installers already run them. `service install` registers the Windows tray process at sign-in or installs and enables the Linux systemd **user** unit.

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

The `cluster` part of the ID is the tunnel's region. `create` uses the region Dev Tunnels recommends for your network, which isn't always the nearest one. `devtunnelsvc regions` compares every region, fastest first. `LATENCY` is one HTTPS round trip from this machine over an already open connection, and `-` means the region couldn't be reached. `AVAILABILITY`, `LOAD`, and `REASON` come from the service and only cover the regions it considered for your account. To choose a region, pass its `REGION` value, for example `devtunnelsvc create --region euw`. A tunnel can't change regions, so pick one close to both machines.

On the **client**, use the same tunnel ID. Because both machines use the same account, `devtunnelsvc list` shows it as well, along with each tunnel's current host and client connection counts, expiration time (UTC), and registered tunnel ports. A `-` marks a value the Dev Tunnels service didn't report.

```shell
devtunnelsvc list
devtunnelsvc connect TUNNEL --tunnel-port 8080 --listen-port 5000
devtunnelsvc status
```

Connect your TCP client to `localhost:5000`. Traffic travels through tunnel port `8080` to the host's port `3000`. The client binds loopback only; an occupied listen port is an error, not a request to choose a different port.

## Saved sign-in

Sign in once per machine. `devtunnelsvc` keeps its own GitHub credential across service stops, reboots, and CLI updates; it does not read or change the official `devtunnel` CLI's login. When the service next needs an expiring access token, it uses GitHub's device-flow refresh token and saves the rotated pair. If GitHub revokes the authorization or the refresh token itself expires, run `devtunnelsvc logout` and `devtunnelsvc login` again.

If upgrading from a memory-only build, stop the old service before replacing the executable. Start the new service and run `devtunnelsvc login` once to save the sign-in.

- **Windows:** `%LOCALAPPDATA%\DevTunnelSvc\auth\github.dpapi`, encrypted with Windows Data Protection (DPAPI) for the signed-in user.
- **Linux:** `$XDG_DATA_HOME/DevTunnelSvc/auth/github.json` (normally `~/.local/share/DevTunnelSvc/auth/github.json`). The file is **not encrypted**: it is readable and writable only by its owner (`0600`) inside a private directory (`0700`). This works without a desktop keyring. Protect backups and disk access accordingly.

`devtunnelsvc logout` stops and forgets this account's mappings and deletes the local saved credential; it does not revoke the GitHub App authorization or delete cloud tunnels. To revoke the authorization itself, use your GitHub account settings.

## Stop, update, and remove

Successful `host` and `connect` commands save their tunnel ID and exact ports in the per-user `DevTunnelSvc` state directory (`mappings.json`, separate from sign-in). The service re-hosts and reconnects these mappings on startup, including after an update. An offline peer doesn't block startup or other mappings: `devtunnelsvc status` shows which mappings are restoring or retrying. Failed restores retry every 30 seconds; `list` queries your cloud tunnels instead.

Use the mapping name from `status` to close **and forget** one mapping: for example, `devtunnelsvc disconnect connect:blue-tunnel.use2:5000` for a client, or `devtunnelsvc stop host:blue-tunnel.use2` for a host. `stop` also accepts client mappings. Neither command deletes the cloud tunnel; only `delete TUNNEL` does that. One hosted mapping is supported per tunnel at a time. `service stop` and tray **Exit** preserve mappings for the next start. When upgrading from v0.1.6 or earlier, reissue `host` and `connect` once: those versions never saved their mappings.

<img src="DevTunnelSvc/Assets/devtunnelsvc.png" width="32" height="32" alt="Teal tunnel and outbound arrow"> On Windows, right-click the notification-area icon for **Status**, **Update**, **Auto-update**, **About**, or **Exit**. **Status** opens or refreshes a window with the same sign-in state and active mappings as `devtunnelsvc status`. The tray menu stays available while that window is open. **About** shows the running version. The Windows Forms menu and dialogs follow the Windows 11 app theme; high contrast and older Windows versions use system colors. **Exit** stops the service and its mappings without signing you out or removing its startup registration.

```shell
devtunnelsvc update --check
devtunnelsvc update --force
```

On both platforms, `update --check` only reports availability. `update --force` checks for a newer stable release first. Without asking, it stops a running service, verifies the release asset's SHA-256 digest, installs it, and starts the service, even if the service was stopped beforehand or the install failed. It checks that the installed CLI can reach the service after the starter exits. The Windows tray's **Update** action does the same for its running service but asks first. Saved sign-in and mappings are preserved. If you're already up to date, nothing is stopped, reinstalled, or started.

**Automatic updates are off by default.** Select **Auto-update** in the Windows tray, or run `devtunnelsvc auto-update enable` on either platform. Both explain the interruption and ask for confirmation. Once enabled, the running service checks immediately and then every five minutes. It installs newer stable releases without asking again. **Active connections are interrupted** while the service restarts; saved hosts and forwards are restored, but applications may need to reconnect.

The setting is per user and survives service restarts and updates. While the service is running, use `devtunnelsvc auto-update status` to see it or `devtunnelsvc auto-update disable` to turn it off. Unchecking the tray item does the same. Disabling does not stop any tunnels, and a stopped service does not check for releases.

Plain `update` never stops a running service. To control downtime yourself, run `devtunnelsvc service stop`, `devtunnelsvc update`, then `devtunnelsvc service start`. `service run` runs in the foreground for diagnostics. Linux requires a running systemd user manager for `install` and `start`. To keep the service running after the last login session ends, configure user lingering according to your system policy.

If an update reports **Installed** but the service did not restart, run `devtunnelsvc service start`; there is no need to reinstall. See the [application log](#application-log) for the install, restart, and readiness results.

To uninstall, run `devtunnelsvc logout` to remove the credential, then stop the service. On Windows remove the `DevTunnelSvc` value from `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. On Linux run `systemctl --user disable devtunnelsvc.service`, remove `devtunnelsvc.service` from your systemd user configuration directory, and run `systemctl --user daemon-reload`. Then remove the downloaded executable.

## Application log

The service and updater append to the same per-user `app.log`. It records service starts and stops, tray selections, command outcomes, relay state changes, mapping restore attempts, and update stages and exit codes. Each entry has a UTC timestamp, process ID, and source. Successful `status` and service PID probes are omitted. Credentials, tunnel identifiers, ports, routes, exception messages and raw command output are not recorded.

On Windows, read recent events with `Get-Content "$env:LOCALAPPDATA\DevTunnelSvc\app.log" -Tail 50` in PowerShell. On Linux, use `tail -n 50 "${XDG_DATA_HOME:-$HOME/.local/share}/DevTunnelSvc/app.log"`. The newest entries stay in `app.log`; `app.log.1` holds the previous 256 KiB generation. A restart or update does not clear the log.

Existing `update.log` files are left untouched. An upgrade **from v0.1.8** still writes its updater events there because the outgoing executable performs the update. Earlier versions had no update log.

For design requirements, coding guidelines, build commands, and release publishing, see [AGENTS.md](AGENTS.md).
