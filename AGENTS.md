# DevTunnelSvc

Build DevTunnelSvc on the Microsoft Dev Tunnels C# SDK. The following requirements describe the intended service.

## Service lifetime and CLI control

The long-running service owns the SDK connections and port forwards. The CLI controls the service; exiting a CLI command must not tear down the tunnels it started.

On Windows, run the background process as the signed-in user and show a tray icon in that user's session. On Linux, run the process as a systemd service. Keep tunnel management shared across platforms, with the tray and service lifecycle handled by platform-specific code. Both platforms must expose hosting, connecting, and port forwarding through the CLI.

## Port mapping

Keep the client's local listen port, the tunnel port, and the host's local target port independently configurable. For example, a client listening on `localhost:5000` can forward through tunnel port `8080` to a host application listening on `localhost:3000`. Preserve those distinctions in CLI arguments and in the service's forwarding logic.

Save successful host and client mappings in the service's per-user state directory, separately from credentials. Restore them independently on startup so an offline peer does not block the control pipe or another mapping. Shutdown and update preserve them; `stop` forgets either kind, `disconnect` forgets only a client mapping, and `logout` forgets all mappings and the saved grant.

## Publishing and self-update

V1 publishes one untrimmed, self-contained **ReadyToRun single-file** executable per supported OS and architecture, including native runtime libraries. JIT is intentional: the official Dev Tunnels SDK uses reflection in JSON handling and SSH activation. Do not fork the SDK, suppress dependency warnings, or re-enable Native AOT without validating the complete data path. No separate .NET runtime installation is required.

Distribute the binaries through GitHub Releases. Self-update must be available through the CLI on both platforms and select the release asset for the installed OS and architecture.

Build on the target architecture. On Windows use the .NET 11 SDK:

```shell
dotnet eng/release.cs 1.2.3
```

For distributable Linux packages, use the same glibc 2.28 build and smoke-test environment as CI:

```shell
docker buildx build -f eng/linux-release.Dockerfile --build-arg VERSION=1.2.3 --output type=local,dest=artifacts/packages .
```

The Dockerfile builds on UBI 8, runs the exact binary offline in a clean UBI 8 image without .NET, and exports only the verified package. Building directly with `dotnet eng/release.cs` on a newer Linux distro can raise the glibc requirement; don't promote those local builds. For a private restore source, pass a user NuGet configuration as a BuildKit secret (`--secret id=nuget-config,src=PATH`) rather than copying it into the image.

Omit the version for a `0.0.0-dev` package. The release script runs `--version` and `--help` on the actual deliverable (after extraction on Windows). Keep the [asset names](README.md#manual-download) stable for the installers and CLI self-update. The executable is lowercase: Windows ZIPs contain only `devtunnelsvc.exe`; Linux ships the standalone `devtunnelsvc` binary under a RID-suffixed asset name. Both are written to `artifacts/packages/<rid>/`.

Keep the WinForms tray and dialogs in the Windows build only. Windows still ships a console CLI with its desktop runtime bundled; Linux must not depend on Windows Desktop. Load tray and dialog icons at display size from the executable's `ApplicationIcon` resource, not a loose image. Follow the Windows app theme where WinForms supports it and retain system colors in high contrast.

Tray updates run in a separate process. Both the tray and `update --force` must check for a newer stable release first, stop only the service instance they identified, wait for its lease, use the same verified installer as plain `update`, and start the service from the installed path even after an install failure. The tray asks before stopping its service. `update --force` never prompts, so it can run unattended, and it also starts a service that was stopped. Never reinstall the same version, and never stop or start a service for `--check` or when already up to date.

Automatic updates are opt-in per user. The Windows tray checkbox and `auto-update enable` share the warning about interrupted connections and require explicit consent; disabling never requires confirmation. The service owns persistence and checks for newer stable releases immediately and then every five minutes while enabled. A detached updater must verify the same service instance is still opted in before stopping it, and must survive its shutdown (a separate systemd user unit on Linux). Retry a failed install no sooner than the next poll; never enter a restart loop.

Run installation and restart from a private copy of the current single-file executable. The .NET bundle loader can reopen its original path to load framework assemblies lazily; replacing that file under a running updater can break even a process-exit callback. Show the tray's install confirmation and final result in the private copy, not from the replaced launcher. The calling CLI waits synchronously for its worker. Keep update diagnostics bounded and free of credentials and mapping data. Run `dotnet eng\test-update.cs` on Windows to exercise forced, automatic, and tray updates offline across two published bundle layouts, including failed installs and restarts.

The service, tray, and updater share the per-user `app.log`. Append across starts and updates, serialize rotation across processes, and log only known operations, states, and error codes; never log raw requests, replies, or exception messages.

To publish, tag the intended commit (for example `git tag -a v1.2.3 -m "Release v1.2.3"`) and push **that tag** (`git push origin v1.2.3`). CI stamps the version without `v` into each executable. Only a tag push can create a GitHub Release, and only after the Windows/Linux x64 and Arm64 packages, tests, and quality gates pass. CI uploads the already smoke-tested assets without rebuilding and generates release notes. A tag such as `v1.2.3-rc.1` creates a prerelease, not the latest stable release.

## Build and test

Use the .NET 11 SDK from the repository root. Shared build settings belong in `Directory.Build.props`; the recommended analyzer rules follow the target framework, not the newest SDK on the machine. Pin NuGet versions in `Directory.Packages.props` and leave `PackageReference` entries in individual projects versionless.

Write engineering tools and test drivers as .NET 11 C# file-based apps in `eng/` rather than adding a project or a PowerShell/sh driver. Keep the public bootstrap installers SDK-independent. Test the shipped scripts, not a C# reimplementation: `dotnet eng/test-install.cs` uses the minimum supported Windows PowerShell 5.1 (`powershell.exe`, not `pwsh`) on Windows or POSIX `sh` on Linux, with isolated homes and offline substitutes for downloads, registry writes, hardware queries, and service commands. Load C# test cmdlets into the PowerShell session that actually runs the installer; mocks in the parent process cannot intercept child commands.

The CLI sends bounded, versioned messages to a same-user named pipe. `TunnelService` serializes changes to active mappings; `TunnelBackend` owns the management client and opens the relay sessions. `MappedHost` registers an explicit SDK SSH forward instead of the SDK's default same-port mapping. `MappedClient` filters remote ports and supplies an exact loopback listener. Do not re-enable automatic port selection. Tests use actual SDK SSH peers on loopback; GitHub and tunnel management HTTP responses are supplied offline.

The invoking CLI, not the service, owns the browser and clipboard interaction. Only GitHub's `user_code` may be copied, never the `device_code` or access token. Login tests use a substitute clipboard, not the developer's desktop.

The service owns the saved GitHub grant. Serialize refresh, replacement, and logout: GitHub invalidates the previous grant on rotation. Persist the new pair atomically before giving its token to the SDK. Shutdown disposes mappings but must not delete the grant; logout must. Use temporary credential directories and synthetic tokens in tests, never a developer's real store. Wire real per-user resources (the control pipe, state directory, and local CLI actions) only in `Main` and the public production overloads; internals that tests call take them as required arguments. Stryker removes the left operand of `testValue ?? realDefault`, so such a fallback lets a local mutation run drive the developer's running service and overwrite their saved sign-in.

```shell
dotnet build DevTunnelSvc.slnx
dotnet test DevTunnelSvc.slnx --configuration Release
dotnet test DevTunnelSvc.slnx --filter "FullyQualifiedName=DevTunnelSvc.Tests.CliTests.UnknownArgumentFails"
```

For local Linux testing on Windows, use a disposable WSLC container (`wslc run --rm`) with `mcr.microsoft.com/dotnet/sdk:11.0`. Mount the checkout read-only and build in a clean copy inside the container so the Windows and Linux builds do not share `bin` or `obj`. For the release Dockerfile use `wslc build` with a tar output (`--output type=tar,dest=artifacts/linux-release.tar`) instead of Docker's directory exporter.

## Coding conventions

- Apply SOLID where the service has distinct reasons to change: the CLI translates arguments, the shared service owns SDK connections, and Windows/Linux adapters own process lifetime. Pass dependencies at those boundaries. Introduce a small, replaceable interface for an actual second implementation or test seam, not a layer for every class.
- Write for the next reader. Use names that express role and ownership, such as `clientListenPort` and `hostTargetPort`; do not pass three interchangeable integers named `port1`, `port2`, and `port3`. Keep related behavior together and make success, failure, and cancellation paths easy to trace. Comment on a non-obvious constraint rather than restating the code.
- Prefer the BCL and the official Dev Tunnels SDK over new runtime dependencies. Use `System.CommandLine` for subcommands; keep forwarding and service logic out of command handlers.
- Make the service responsible for cancelling and awaiting its forwarding tasks on shutdown or reconnect. Report a connection as ready only after the SDK confirms it; surface failures through the CLI exit code and stderr.
- Keep protocol and settings types compatible with Native AOT. Use a source-generated `System.Text.Json` context for wire types and version incompatible changes so that an older CLI cannot misinterpret a service response.
- Prefer a few focused MSTest cases at the behavior boundary: CLI output and exit status, port mappings, and connection lifetime. Extend existing test classes when the behavior belongs there. Keep the default suite independent of live tunnels, GitHub credentials, and fixed local ports.
- Use LF for text files. Split methods by responsibility rather than removing whitespace or comments to pass a gate.

## Quality gates

Edit [code-metrics-baseline.props](code-metrics-baseline.props) to change a numeric quality threshold. MSBuild and the engineering scripts read the same literal integers; CI never rewrites them. The production build generates the Roslyn analyzers' `CodeMetricsConfig.txt` under `obj/`. Review baseline changes explicitly.

Run the same checks as CI from the repository root. No global .NET tools are required. Run formatting after a build or restore.

```shell
dotnet eng/quality.cs coverage
dotnet eng/metrics.cs
dotnet eng/duplication.cs
dotnet eng/test-duplication.cs
dotnet eng/quality.cs mutation
dotnet format whitespace DevTunnelSvc.slnx --no-restore --verify-no-changes
```

| Gate | Passing condition |
| --- | --- |
| Coverage | Line and branch coverage must each meet their configured minimum in the production assembly. Exclude only the framework generators' JSON serializers and native interop stubs, not the handwritten service or platform code. |
| CRAP (complexity vs. coverage) | No method scores above `crapMethodThreshold`: total CRAP load is zero. Coverage must match production methods. |
| Mutation | The detected/valid score must meet `detectedMutants / validMutants`, without rounding. Each file with valid mutants needs a kill; an empty run fails. `mutationReportHigh` only colors the report. |
| Duplication | No repeated non-overlapping sequence of `minCloneTokens` or more identical C# tokens, within or across files. The check includes production, Windows code, tests, and `eng/` tools. |
| Maintainability | Production builds enforce the Roslyn complexity, inheritance, coupling, and maintainability limits. `eng/metrics.cs` also caps source and executable lines per declared method. |

The [AuthBridge duplication rule](https://github.com/VasiliyNovikov/AuthBridge/blob/e06413cd564f62da46bf656a0365dd679fac2763/duplication.cs) uses Roslyn with `WINDOWS` defined. Using directives, comments, and whitespace do not count; identifiers and literals must match exactly. Hidden directories and build/test outputs are skipped. Extract shared behavior rather than adding exclusions or raising the threshold.

CI measures these gates on Ubuntu x64. Inspect `artifacts/coverage/` for Cobertura XML and CRAP JSON, `artifacts/metrics/report.json` for per-symbol measurements, `artifacts/duplication/report.json` for the file and line range of every copy, and `artifacts/mutation/reports/` for Stryker HTML and JSON. GitHub Actions uploads these as `code-quality` and `mutation`.

The coding conventions and gates adapt [AuthBridge's contributor guidelines](https://github.com/VasiliyNovikov/AuthBridge/blob/f2b94d2a5aec4db6fd622a4b134941e40273b319/AGENTS.md) to DevTunnelSvc.
