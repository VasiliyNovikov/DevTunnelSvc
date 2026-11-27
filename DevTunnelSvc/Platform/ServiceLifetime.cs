using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DevTunnelSvc.Platform;

internal static class ServiceLifetime
{
    private const string Unit = "devtunnelsvc.service";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static Task RunAsync(TextWriter log, CancellationToken ct) =>
        RunAsync(log, Control.PipeName, InstanceLease.DefaultDirectory, Program.BackgroundUpdates, ct, TrayLifetime.ForCurrentPlatform);

    internal static async Task RunAsync(TextWriter log, string name, string stateDirectory, AutoUpdateActions backgroundUpdates, CancellationToken ct,
        TrayLifetime.Factory? createTray = null)
    {
        var events = new AppLog(stateDirectory, "service", log.WriteLine);
        events.Write($"Starting DevTunnelSvc {Program.Version}.");
        try
        {
            using var instance = InstanceLease.Acquire(stateDirectory);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var term = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                signal => { signal.Cancel = true; lifetime.Cancel(); }) : null;
            await ServiceHost.RunAsync(name, stateDirectory, log, events.Write, backgroundUpdates, createTray, lifetime);
        }
        catch (Exception ex) { events.Write($"Service failed: {AppLog.ErrorCode(ex)}"); throw; }
        events.Write("Service stopped.");
    }

    public static Task<string> StartAsync(CancellationToken ct) => StartAsync(OperatingSystem.IsWindows(),
        t => Control.SendAsync(new("status"), Control.PipeName, t), LaunchAsync, ct);

    internal static async Task<string> StartAsync(bool checkExisting, Func<CancellationToken, Task<string>> status,
        Func<CancellationToken, Task> launch, CancellationToken ct)
    {
        if (checkExisting)
        {
            try { return await status(ct); }
            catch (IOException ex) when (ex.InnerException is TimeoutException) { /* Launch if the service is not listening. */ }
        }
        await launch(ct);
        for (var attempt = 0; ; attempt++)
        {
            try { return await status(ct); }
            catch (IOException ex) when (ex.InnerException is TimeoutException && attempt < 4)
            {
                await Task.Delay(150, ct);
            }
        }
    }

    private static Task LaunchAsync(CancellationToken ct) => LaunchAsync(OperatingSystem.IsWindows() ? ApplicationExecutable() : null,
        info => Process.Start(info)!.Dispose(), SystemctlAsync, ct);

    internal static Task LaunchAsync(string? windowsExe, Action<ProcessStartInfo> start, Func<string[], CancellationToken, Task> systemctl, CancellationToken ct)
    {
        if (windowsExe is null)
            return systemctl(["start", Unit], ct);
        // Give the service a separate hidden console. Inheriting the starter's redirected handles
        // keeps the update worker's output pipes open even after the starter has exited.
        start(new ProcessStartInfo(windowsExe, ["service", "run"]) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        return Task.CompletedTask;
    }

    public static Task<string> InstallAsync(CancellationToken ct) => InstallAsync(ApplicationExecutable(),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        OperatingSystem.IsWindows() ? SetWindowsStartup : null, SystemctlAsync, ct);

    internal static async Task<string> InstallAsync(string executable, string configDirectory, Action<string, string>? registerStartup,
        Func<string[], CancellationToken, Task> systemctl, CancellationToken ct)
    {
        if (registerStartup is not null)
            registerStartup("DevTunnelSvc", $"\"{executable}\" service start");
        else
            await InstallLinuxAsync(executable, configDirectory, systemctl, ct);
        return "Installed for the current user. Run 'service start'.";
    }

    internal static async Task InstallLinuxAsync(string executable, string configDirectory, Func<string[], CancellationToken, Task> systemctl, CancellationToken ct)
    {
        var directory = Directory.CreateDirectory(Path.Combine(configDirectory, "systemd", "user"));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, Unit), UnitFile(executable), ct);
        await systemctl(["daemon-reload"], ct);
        await systemctl(["enable", Unit], ct);
    }

    internal static string UnitFile(string path)
    {
        if (path.Any(char.IsControl))
            throw new ArgumentException("Control characters are not supported in the executable path.");
        var escaped = path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal);
        return $"[Unit]\nDescription=DevTunnelSvc\n\n[Service]\nExecStart=\"{escaped}\" service run\nRestart=on-failure\n\n[Install]\nWantedBy=default.target\n";
    }

    internal static string ApplicationExecutable() => RequireExecutable(Environment.ProcessPath);

    internal static string RequireExecutable(string? executable)
    {
        var path = executable ?? throw new InvalidOperationException("Executable path unavailable.");
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use the published devtunnelsvc executable for this command.");
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsStartup(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
        key.SetValue(name, command);
    }

    private static Task SystemctlAsync(string[] args, CancellationToken ct) => SystemctlAsync(args, ExitCodeAsync, ct);

    internal static async Task SystemctlAsync(string[] args, Func<ProcessStartInfo, CancellationToken, Task<int>> run, CancellationToken ct)
    {
        var code = await run(new ProcessStartInfo("systemctl", args.Prepend("--user")), ct);
        if (code != 0)
            throw new InvalidOperationException($"systemctl --user {args[0]} failed ({code}).");
    }

    internal static async Task<int> ExitCodeAsync(ProcessStartInfo info, CancellationToken ct)
    {
        using var process = SafeProcessHandle.Start(info);
        return (await process.WaitForExitAsync(ct)).ExitCode;
    }
}
