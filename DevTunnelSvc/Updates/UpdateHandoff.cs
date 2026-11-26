using System.Diagnostics;
using System.Globalization;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed record UpdateHandoff(Func<Request, CancellationToken, Task<string>> Send, Func<Task> Start)
{
    internal static string Question(string current, string tag) =>
        $"Install {tag} (currently {current})?\n\nThis will stop all active port mappings, install the update, and restart DevTunnelSvc. Saved mappings will reconnect.";

    // `update --force` runs unattended: it stops a running service without asking and always leaves DevTunnelSvc running.
    internal async Task<string> ApplyAsync(string tag, Func<CancellationToken, Task> install, CancellationToken ct)
    {
        var wasRunning = await StopIfRunningAsync(ct);
        return await InstallAndStartAsync(tag, wasRunning, install, Start, ct);
    }

    private async Task<bool> StopIfRunningAsync(CancellationToken ct)
    {
        string target;
        try { target = await Send(new("service-pid"), ct); }
        catch (IOException ex) when (ex.InnerException is TimeoutException) { return false; }
        if (!int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw new InvalidDataException("The service returned an invalid process ID; update not started.");
        await StopAsync(pid, Send, ct);
        return true;
    }

    internal static Task StopAsync(int pid, Func<Request, CancellationToken, Task<string>> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Once sent, await the bounded IPC reply even on Ctrl+C so we know whether we owe a restart.
        return send(new("shutdown-for-update", ServicePid: pid), CancellationToken.None);
    }

    internal static async Task<IDisposable> WaitForLeaseAsync(Func<IDisposable> acquire, Func<TimeSpan, Task> delay)
    {
        // The shutdown reply precedes disposal of the service and release of its exclusive lease.
        for (var retry = 0; ; retry++)
        {
            try { return acquire(); }
            catch (IOException) when (retry < 300) { await delay(TimeSpan.FromMilliseconds(100)); }
        }
    }

    internal static async Task<string> InstallAndStartAsync(string tag, bool wasRunning, Func<CancellationToken, Task> install,
        Func<Task> start, CancellationToken ct)
    {
        Exception? installError = null;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
        download.CancelAfter(TimeSpan.FromMinutes(5));
        try { await install(download.Token); }
        catch (Exception ex) { installError = ex; }

        // Leave the service running even if the install failed or was cancelled.
        try { await start(); }
        catch (Exception ex)
        {
            var result = installError is null ? $"Installed {tag}." : $"Update failed: {installError.Message}";
            throw new InvalidOperationException($"{result}\nThe service could not start: {ex.Message}\nRun 'devtunnelsvc service start'.", ex);
        }
        var started = wasRunning ? "restarted" : "started";
        if (installError is not null)
            throw new InvalidOperationException($"Update failed: {installError.Message}\nThe service was {started}.", installError);
        return $"Installed {tag} and {started} DevTunnelSvc.";
    }

    internal static async Task StartInstalledAsync(string executable, Func<ProcessStartInfo, CancellationToken, Task<int>> run)
    {
        // The installed CLI may speak a newer pipe protocol than the updater.
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        async Task<(string Command, int Code)> StartAndProbeAsync()
        {
            var start = await run(new(executable, ["service", "start"]) { CreateNoWindow = true }, limit.Token);
            if (start != 0) return ("service start", start);
            // Probe after the short-lived starter exits, using the newly installed protocol.
            var status = new ProcessStartInfo(executable, ["status"]) { CreateNoWindow = true, RedirectStandardOutput = true };
            return ("status", await run(status, limit.Token));
        }
        var result = await StartAndProbeAsync();
        if (result.Code != 0) result = await StartAndProbeAsync();
        if (result.Code != 0) throw new InvalidOperationException($"The installed '{result.Command}' command exited with code {result.Code}.");
    }
}
