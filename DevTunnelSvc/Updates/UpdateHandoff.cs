using System.Diagnostics;
using System.Globalization;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed record UpdateHandoff(Func<Request, CancellationToken, Task<string>> Send, Func<Task> Start, Action<string>? Trace = null, int? AutoServicePid = null)
{
    internal static string Question(string current, string tag) =>
        $"Install {tag} (currently {current})?\n\nThis will stop all active port mappings, install the update, and restart DevTunnelSvc. Saved mappings will reconnect.";

    // `update --force` runs unattended: it stops a running service without asking and always leaves DevTunnelSvc running.
    internal async Task<string> ApplyAsync(string tag, Func<CancellationToken, Task> install, CancellationToken ct)
    {
        var wasRunning = await StopIfRunningAsync(ct);
        return await InstallAndStartAsync(tag, wasRunning, install, Start, ct, Trace);
    }

    private async Task<bool> StopIfRunningAsync(CancellationToken ct)
    {
        if (AutoServicePid is { } scheduled)
        {
            ct.ThrowIfCancellationRequested();
            Trace?.Invoke($"Requesting automatic update of service PID {scheduled}.");
            await Send(new(AutoUpdates.Shutdown, ServicePid: scheduled), CancellationToken.None);
            return true;
        }
        string target;
        try { target = await Send(new("service-pid"), ct); }
        catch (IOException ex) when (ex.InnerException is TimeoutException) { return false; }
        if (!int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw new InvalidDataException("The service returned an invalid process ID; update not started.");
        Trace?.Invoke($"Stopping service PID {pid}.");
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
        Func<Task> start, CancellationToken ct, Action<string>? trace = null)
    {
        Exception? installError = null;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
        download.CancelAfter(TimeSpan.FromMinutes(5));
        try { trace?.Invoke("Installing verified release."); await install(download.Token); trace?.Invoke($"Installed {tag}."); }
        catch (Exception ex) { installError = ex; trace?.Invoke($"Install failed: {AppLog.ErrorCode(ex)}"); }

        // Leave the service running even if the install failed or was cancelled.
        try { await start(); }
        catch (Exception ex)
        {
            trace?.Invoke($"Restart failed: {AppLog.ErrorCode(ex)}");
            var result = installError is null ? $"Installed {tag}." : $"Update failed: {AppLog.Detail(installError)}";
            throw new InvalidOperationException($"{result}\nThe service could not start: {AppLog.Detail(ex)}\nRun 'devtunnelsvc service start'.", ex);
        }
        var started = wasRunning ? "restarted" : "started";
        if (installError is not null)
            throw new InvalidOperationException($"Update failed: {AppLog.Detail(installError)}\nThe service was {started}.", installError);
        return $"Installed {tag} and {started} DevTunnelSvc.";
    }

    internal static async Task StartInstalledAsync(string executable, Func<ProcessStartInfo, CancellationToken, Task<int>> run, Action<string>? trace = null)
    {
        // The installed CLI may speak a newer pipe protocol than the updater.
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var sink = File.OpenNullHandle();
        async Task<(string Command, int Code)> StartAndProbeAsync()
        {
            trace?.Invoke("Launching installed service start.");
            var start = await run(new(executable, ["service", "start"]) { CreateNoWindow = true, StandardOutputHandle = sink }, limit.Token);
            trace?.Invoke($"service start exit code: {start}.");
            if (start != 0) return ("service start", start);
            // Probe after the short-lived starter exits, using the newly installed protocol.
            var status = new ProcessStartInfo(executable, ["status"]) { CreateNoWindow = true, StandardOutputHandle = sink };
            var code = await run(status, limit.Token);
            trace?.Invoke($"status exit code: {code}.");
            return ("status", code);
        }
        var result = await StartAndProbeAsync();
        if (result.Code != 0) result = await StartAndProbeAsync();
        if (result.Code != 0) throw new InvalidOperationException($"The installed '{result.Command}' command exited with code {result.Code}.");
    }
}
