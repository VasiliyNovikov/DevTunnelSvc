# DevTunnelSvc

> [!IMPORTANT]
> This repository is **100% vibe-coded**.

DevTunnelSvc is a Windows and Linux CLI for a planned background service built on the Microsoft Dev Tunnels C# SDK. The goal is to host or connect to a tunnel, close the terminal, and keep forwarding traffic. The current CLI supports `--help` and `--version`.

On Windows, the service will run as the signed-in user with a tray icon. On Linux, it will run under systemd. On either platform, the client port, tunnel port, and host port can be different. For example, a client listening on `localhost:5000` could forward through tunnel port `8080` to an app listening on `localhost:3000` on the host.

## Download and run

Each GitHub Release contains one self-contained Native AOT executable per platform. Choose the asset for your machine from the repository's **Releases** page; no .NET runtime is required.

| OS | x64 | Arm64 |
| --- | --- | --- |
| Windows | `devtunnelsvc-win-x64.zip` | `devtunnelsvc-win-arm64.zip` |
| Linux | `devtunnelsvc-linux-x64` | `devtunnelsvc-linux-arm64` |

On Windows, extract the ZIP and run `.\DevTunnelSvc.exe --version`. Linux releases support **glibc 2.28 or newer** (for example, RHEL 8 or newer); they do not target musl/Alpine. Make the downloaded file executable and run it: for example, `chmod +x devtunnelsvc-linux-x64` followed by `./devtunnelsvc-linux-x64 --version`.

For design requirements, coding guidelines, build commands, and release publishing, see [AGENTS.md](AGENTS.md).
