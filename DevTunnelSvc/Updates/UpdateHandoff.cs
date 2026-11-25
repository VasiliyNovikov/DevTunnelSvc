using System.Diagnostics;
using System.Globalization;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed record UpdateHandoff(Func<Request, CancellationToken, Task<string>> Send,
    Func<string, CancellationToken, Task<bool>> Confirm, Func<Task> Restart)
{
    internal static string Question(string current, string tag) =>
        $"Install {tag} (currently {current})?\n\nThis will stop all active port mappings, install the update, and restart DevTunnelSvc. Mappings must be started again.";

    internal static async Task<bool> ConfirmAsync(TextReader input, TextWriter output, string question, CancellationToken ct)
    {
        await output.WriteAsync($"{question} [y/N] ".AsMemory(), ct);
        await output.FlushAsync(ct);
        var answer = (await input.ReadLineAsync(ct))?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<string?> ApplyIfRunningAsync(string current, string tag, Func<CancellationToken, Task> install, CancellationToken ct)
    {
        string target;
        try { target = await Send(new("service-pid"), ct); }
        catch (IOException ex) when (ex.InnerException is TimeoutException) { return null; }
        if (!int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw new InvalidDataException("The service returned an invalid process ID; update not started.");
        if (!await Confirm(Question(current, tag), ct)) return "Update cancelled; the service was not stopped.";
        await StopAsync(pid, Send, ct);
        return await InstallAndRestartAsync(tag, install, Restart, ct);
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

    internal static async Task<string> InstallAndRestartAsync(string tag, Func<CancellationToken, Task> install, Func<Task> restart, CancellationToken ct)
    {
        Exception? installError = null;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
        download.CancelAfter(TimeSpan.FromMinutes(5));
        try { await install(download.Token); }
        catch (Exception ex) { installError = ex; }

        // Once we have stopped the user's service, restore it even if the install failed or was cancelled.
        try { await restart(); }
        catch (Exception ex)
        {
            var result = installError is null ? $"Installed {tag}." : $"Update failed: {installError.Message}";
            throw new InvalidOperationException($"{result}\nThe service could not restart: {ex.Message}\nRun 'devtunnelsvc service start'.", ex);
        }
        if (installError is not null)
            throw new InvalidOperationException($"Update failed: {installError.Message}\nThe service was restarted. Start your port mappings again.", installError);
        return $"Installed {tag} and restarted DevTunnelSvc. Start your port mappings again.";
    }

    internal static async Task RestartInstalledAsync(string executable, Func<ProcessStartInfo, CancellationToken, Task<int>> run)
    {
        // The installed CLI may speak a newer pipe protocol than the updater.
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var code = await run(new ProcessStartInfo(executable, ["service", "start"]) { CreateNoWindow = true }, limit.Token);
        if (code != 0) throw new InvalidOperationException($"The installed 'service start' command exited with code {code}.");
    }
}
