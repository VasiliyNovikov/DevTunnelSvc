using System.Diagnostics;
using System.Runtime.Versioning;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Platform;

internal sealed class TrayUpdate(Func<CancellationToken, Task<ReleaseInfo?>> find, Func<string, bool> confirm,
    Func<CancellationToken, Task> stop, Func<ReleaseInfo, CancellationToken, Task> install, Func<Task> restart)
{
    internal async Task<string?> RunAsync(string current, CancellationToken ct)
    {
        var release = await find(ct);
        if (release is null) return $"DevTunnelSvc is up to date ({current}).";
        if (!confirm($"Install {release.TagName} (currently {current})?\n\nThis will stop all active port mappings, install the update, and restart DevTunnelSvc. Mappings must be started again."))
            return null;

        ct.ThrowIfCancellationRequested();
        await stop(ct);
        Exception? installError = null;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
        download.CancelAfter(TimeSpan.FromMinutes(5));
        try { await install(release, download.Token); }
        catch (Exception ex) { installError = ex; }

        // Once we have stopped the user's service, restore it even if the download failed or was cancelled.
        try { await restart(); }
        catch (Exception ex)
        {
            var result = installError is null ? $"Installed {release.TagName}." : $"Update failed: {installError.Message}";
            throw new InvalidOperationException($"{result}\nThe service could not restart: {ex.Message}\nRun 'devtunnelsvc service start'.", ex);
        }
        if (installError is not null)
            throw new InvalidOperationException($"Update failed: {installError.Message}\nThe service was restarted. Start your port mappings again.", installError);
        return $"Installed {release.TagName} and restarted DevTunnelSvc. Start your port mappings again.";
    }

    [SupportedOSPlatform("windows")]
    internal static Task<int> FromMenuAsync(int servicePid, CancellationToken ct)
    {
        var dialogs = TrayMenu.SystemDialogs;
        return WithFeedbackAsync(async () =>
        {
            using var service = Process.GetProcessById(servicePid);
            _ = service.SafeHandle; // Pin the original process identity while the confirmation dialog is open.
            using var http = GitHubOAuth.CreateClient();
            var updater = new ReleaseUpdate(http);
            var executable = ServiceLifetime.ApplicationExecutable();
            var flow = new TrayUpdate(t => updater.FindNewerAsync(Program.Version, t), text =>
                { Program.RequireSingleFileUpdate(); return dialogs.Confirm(text); },
                t => StopOriginalAsync(service.Id, (r, token) => Control.SendAsync(r, token), service.WaitForExitAsync, t),
                (release, t) => updater.ApplyAsync(release, executable, () => InstanceLease.Acquire(), t),
                () => RestartInstalledAsync(executable, ServiceLifetime.ExitCodeAsync));
            return await flow.RunAsync(Program.Version, ct);
        }, dialogs);
    }

    internal static async Task<int> WithFeedbackAsync(Func<Task<string?>> update, TrayMenu.Dialogs dialogs)
    {
        try
        {
            var message = await update();
            if (message is not null) dialogs.Inform(0, "DevTunnelSvc Update", message);
            return 0;
        }
        catch (Exception ex)
        {
            dialogs.ReportError(0, ex.Message);
            return 1;
        }
    }

    internal static async Task StopOriginalAsync(int servicePid, Func<Request, CancellationToken, Task<string>> send,
        Func<CancellationToken, Task> waitForExit, CancellationToken ct)
    {
        await send(new("shutdown-for-update", ServicePid: servicePid), ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        await waitForExit(limit.Token);
    }

    internal static async Task RestartInstalledAsync(string executable, Func<ProcessStartInfo, CancellationToken, Task<int>> run)
    {
        // Use the installed CLI's own readiness check: its pipe protocol may be newer than this helper's.
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var code = await run(new ProcessStartInfo(executable, ["service", "start"]) { CreateNoWindow = true }, limit.Token);
        if (code != 0) throw new InvalidOperationException($"The installed 'service start' command exited with code {code}.");
    }
}
