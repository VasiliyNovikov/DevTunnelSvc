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

    internal static async Task<int> WithFeedbackAsync(Func<Task<string?>> update, Action<string> inform, Action<string> reportError)
    {
        try
        {
            var message = await update();
            if (message is not null) inform(message);
            return 0;
        }
        catch (Exception ex)
        {
            reportError(AppLog.Detail(ex));
            return 1;
        }
    }

    internal static async Task WaitForExitAsync(Func<CancellationToken, Task> waitForExit)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await waitForExit(limit.Token);
    }
}
