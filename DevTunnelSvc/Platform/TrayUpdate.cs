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
        if (!confirm(UpdateHandoff.Question(current, release.TagName)))
            return null;

        ct.ThrowIfCancellationRequested();
        await stop(ct);
        return await UpdateHandoff.InstallAndStartAsync(release.TagName, wasRunning: true, t => install(release, t), restart, ct);
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
                t => UpdateHandoff.StopAsync(service.Id, (r, token) => Control.SendAsync(r, Control.PipeName, token), t),
                async (release, t) =>
                {
                    await WaitForExitAsync(service.WaitForExitAsync);
                    await updater.ApplyAsync(release, executable, () => InstanceLease.Acquire(InstanceLease.DefaultDirectory), t);
                },
                () => UpdateHandoff.StartInstalledAsync(executable, ServiceLifetime.ExitCodeAsync));
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

    internal static async Task WaitForExitAsync(Func<CancellationToken, Task> waitForExit)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await waitForExit(limit.Token);
    }
}
