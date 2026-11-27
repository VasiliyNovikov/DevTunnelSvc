using System.Diagnostics;
using System.Globalization;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Platform;

internal static class AutoUpdateLauncher
{
    internal static Task LaunchAsync(string executable, int servicePid, CancellationToken ct)
    {
        var pid = servicePid.ToString(CultureInfo.InvariantCulture);
        var info = OperatingSystem.IsWindows() ? WindowsWorker(executable, pid) : LinuxWorker(executable, pid,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), Path.GetTempPath());
        return LaunchAsync(info, ct);
    }

    internal static async Task LaunchAsync(ProcessStartInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (info.StartDetached) { _ = Process.StartAndForget(info); return; }
        var result = await Process.RunAsync(info, ct);
        if (result.Canceled) ct.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not start the auto-update user unit (exit {result.ExitCode}).");
    }

    internal static ProcessStartInfo WindowsWorker(string executable, string pid) => new(executable, [AutoUpdates.Worker, pid]) { StartDetached = true };

    // A child in the service's cgroup would be killed during shutdown. The new unit gets the manager's
    // environment, so pass the service's data and temporary roots to find the same lease and control pipe.
    internal static ProcessStartInfo LinuxWorker(string executable, string pid, string dataHome, string temp) =>
        new("systemd-run", ["--user", "--quiet", "--collect", "--unit=devtunnelsvc-auto-update",
            $"--setenv=XDG_DATA_HOME={dataHome}", $"--setenv=TMPDIR={temp}", "--", executable, AutoUpdates.Worker, pid]);
}
