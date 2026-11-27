using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Platform;

internal sealed record TrayDialogContent(string Title, string Text, bool Scrollable = false);

internal sealed class TrayMenu(string pipeName, Action exit, TrayQueries queries, Func<string, Process> launchUpdate,
    Func<LocalStatus, TrayStatusTarget> openStatus, Action<TrayDialogContent> show, Action<string> writeEvent) : IDisposable
{
    internal const string UpdateWorker = "__tray-update";
    internal enum Command { Status, About, Exit }
    private Process? updater;
    internal bool Updating => updater is { HasExited: false };
    internal string? AvailableTag => queries.Releases.AvailableTag;
    internal bool AutoUpdateEnabled => queries.AutoUpdateEnabled();
    internal Task PendingStatus { get; private set; } = Task.CompletedTask;
    internal Task PendingConfiguration { get; private set; } = Task.CompletedTask;
    internal Task PendingUpdate { get; private set; } = Task.CompletedTask;

    internal Task SelectAsync(Command command, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.CompletedTask;
        if (command != Command.Status) return ExecuteAsync(command, ct);
        if (PendingStatus.IsCompleted) PendingStatus = ExecuteAsync(command, ct);
        return PendingStatus;
    }

    internal Task SelectUpdateAsync(string? selectedTag, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested && !Updating && PendingUpdate.IsCompleted)
            PendingUpdate = WithFeedbackAsync(() => CheckOrUpdateAsync(selectedTag, ct), ct);
        return PendingUpdate;
    }

    internal Task ConfigureAutoUpdateAsync(bool enable, Func<string, bool> confirm, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !PendingConfiguration.IsCompleted) return PendingConfiguration;
        return PendingConfiguration = WithFeedbackAsync(async () =>
        {
            writeEvent("Tray menu: Auto-update.");
            await AutoUpdateConsent.ChangeAsync(enable, (r, t) => Control.SendAsync(r, pipeName, t), (text, _) => Task.FromResult(confirm(text)), ct);
        }, ct);
    }

    private Task ExecuteAsync(Command command, CancellationToken ct) => WithFeedbackAsync(async () =>
    {
        writeEvent($"Tray menu: {command}.");
        switch (command)
        {
            case Command.Status:
                await new TrayStatusReader(pipeName, queries.ListTunnels, writeEvent).ShowAsync(openStatus, ct);
                break;
            case Command.About:
                show(new("About DevTunnelSvc", $"DevTunnelSvc {Program.Version}\r\n\r\nHosts and forwards Microsoft Dev Tunnels for your Windows user."));
                break;
            case Command.Exit:
                exit();
                break;
        }
    }, ct);

    private async Task WithFeedbackAsync(Func<Task> action, CancellationToken ct)
    {
        try { await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* The tray is closing. */ }
        catch (Exception ex) when (IsMenuFailure(ex))
        {
            writeEvent($"Tray menu failed: {AppLog.ErrorCode(ex)}");
            if (!ct.IsCancellationRequested) show(new("DevTunnelSvc", AppLog.Detail(ex), Scrollable: true));
        }
    }

    private static bool IsMenuFailure(Exception ex) => ex is Win32Exception or IOException or HttpRequestException or
        InvalidOperationException or InvalidDataException or JsonException or UnauthorizedAccessException or OperationCanceledException;

    private async Task CheckOrUpdateAsync(string? selectedTag, CancellationToken ct)
    {
        writeEvent("Tray menu: Update.");
        var latest = await queries.Releases.CheckAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (latest is null)
        {
            show(new("DevTunnelSvc Update", ReleaseUpdate.UpToDate(Program.Version)));
            return;
        }
        if (selectedTag is not null && selectedTag != latest)
        {
            show(new("DevTunnelSvc Update", $"The latest release changed. Select 'Update to {latest}' to install it."));
            return;
        }
        updater?.Dispose();
        updater = null;
        updater = launchUpdate(latest);
    }

    internal static Process LaunchUpdater(string tag)
    {
        Program.RequireSingleFileUpdate();
        return StartWorker(ServiceLifetime.ApplicationExecutable(), Environment.ProcessId, tag, Process.Start);
    }

    internal static Process StartWorker(string executable, int servicePid, string tag, Func<ProcessStartInfo, Process?> start)
    {
        var info = new ProcessStartInfo(executable, [UpdateWorker, servicePid.ToString(CultureInfo.InvariantCulture), tag]) { CreateNoWindow = true };
        return start(info) ?? throw new InvalidOperationException("Could not start the update helper.");
    }

    public void Dispose() => updater?.Dispose(); // The updater must survive the service it is replacing.
}
