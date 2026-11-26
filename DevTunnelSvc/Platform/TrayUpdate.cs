using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Platform;

internal sealed class TrayUpdate(Func<CancellationToken, Task<ReleaseInfo?>> find, Func<string, bool> confirm,
    Func<CancellationToken, Task> stop, Func<ReleaseInfo, CancellationToken, Task> install, Func<Task> restart, Action<string>? trace = null)
{
    internal async Task<string?> RunAsync(string current, CancellationToken ct)
    {
        var release = await find(ct);
        if (release is null) return $"DevTunnelSvc is up to date ({current}).";
        if (!confirm(UpdateHandoff.Question(current, release.TagName)))
            return null;

        ct.ThrowIfCancellationRequested();
        await stop(ct);
        return await UpdateHandoff.InstallAndStartAsync(release.TagName, wasRunning: true, t => install(release, t), restart, ct, trace);
    }

    [SupportedOSPlatform("windows")]
    internal static Task<int> FromMenuAsync(int servicePid, CancellationToken ct)
    {
        var dialogs = TrayMenu.SystemDialogs;
        return WithFeedbackAsync(async () =>
        {
            using var service = Process.GetProcessById(servicePid);
            _ = service.SafeHandle; // Pin the selected instance through staging, confirmation and installation.
            using var http = GitHubOAuth.CreateClient();
            var message = await new ReleaseUpdate(http).CheckAsync(Program.Version, false, (release, t) =>
            {
                Program.RequireSingleFileUpdate();
                var log = new UpdateLog(InstanceLease.DefaultDirectory, Console.Error.WriteLine);
                log.Begin(Program.Version, release.TagName);
                return UpdateWorker.RunAsync(ServiceLifetime.ApplicationExecutable(), [TrayMenu.UpdateWorker, servicePid.ToString(CultureInfo.InvariantCulture)],
                    release.TagName, log, t);
            }, ct);
            return message.Length == 0 ? null : message;
        }, dialogs);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<string?> FromCopyAsync(int servicePid, string expected, CancellationToken ct)
    {
        Program.RequireSingleFileUpdate();
        var image = ServiceLifetime.ApplicationExecutable();
        var installed = UpdateWorker.InstalledPath(image);
        var directory = InstanceLease.DefaultDirectory;
        var log = new UpdateLog(directory, Console.Error.WriteLine);
        using var service = Process.GetProcessById(servicePid);
        _ = service.SafeHandle;
        using var http = GitHubOAuth.CreateClient();
        var updater = new ReleaseUpdate(http);
        var flow = new TrayUpdate(async t =>
        {
            var release = await updater.FindNewerAsync(Program.Version, t);
            if (release is not null) UpdateWorker.RequireTag(expected, release.TagName);
            return release;
        }, TrayMenu.SystemDialogs.Confirm, t =>
        {
            log.Step($"Stopping service PID {servicePid}.");
            return UpdateHandoff.StopAsync(servicePid, (r, token) => Control.SendAsync(r, Control.PipeName, token), t);
        }, async (release, t) =>
        {
            await WaitForExitAsync(service.WaitForExitAsync);
            await updater.ApplyAsync(release, installed, () => UpdateWorker.AcquireLease(image, installed, directory), t);
        }, () => UpdateHandoff.StartInstalledAsync(installed, ServiceLifetime.ExitCodeAsync, log.Step), log.Step);
        return await log.TraceAsync(() => UpdateWorker.UntilParentClosesAsync(UpdateWorker.ParentClosed(), t => flow.RunAsync(Program.Version, t), ct));
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
            dialogs.ReportError(0, UpdateLog.Detail(ex));
            return 1;
        }
    }

    internal static async Task WaitForExitAsync(Func<CancellationToken, Task> waitForExit)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await waitForExit(limit.Token);
    }
}
