using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Platform;

internal sealed record TrayDialogContent(string Title, string Text, bool Scrollable = false);

internal sealed class TrayMenu(string pipeName, Action exit, Func<Process> launchUpdate, Action<TrayDialogContent> show, Action<string> writeEvent) : IDisposable
{
    internal const string UpdateWorker = "__tray-update";
    internal enum Command { Status, Update, About, Exit }
    private Process? updater;
    internal bool Updating => updater is { HasExited: false };
    internal Task PendingStatus { get; private set; } = Task.CompletedTask;
    internal Task PendingConfiguration { get; private set; } = Task.CompletedTask;

    internal Task SelectAsync(Command command, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.CompletedTask;
        if (command != Command.Status) return ExecuteAsync(command, ct);
        if (PendingStatus.IsCompleted) PendingStatus = ExecuteAsync(command, ct);
        return PendingStatus;
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
                var status = await ReadStatusAsync(ct);
                ct.ThrowIfCancellationRequested();
                show(new("DevTunnelSvc Status", status, Scrollable: true));
                break;
            case Command.Update when !Updating:
                updater?.Dispose();
                updater = null;
                updater = launchUpdate();
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
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or InvalidDataException or JsonException or UnauthorizedAccessException or OperationCanceledException)
        {
            writeEvent($"Tray menu failed: {AppLog.ErrorCode(ex)}");
            if (!ct.IsCancellationRequested) show(new("DevTunnelSvc", AppLog.Detail(ex), Scrollable: true));
        }
    }

    private async Task<string> ReadStatusAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(2));
        try { return await Control.SendAsync(new("status"), pipeName, limit.Token); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or IOException { InnerException: TimeoutException })
        {
            throw new IOException("The service did not answer the status request in time. Try again.", ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not read service status: {ex.Message}", ex);
        }
    }

    internal static Process LaunchUpdater()
    {
        Program.RequireSingleFileUpdate();
        return StartWorker(ServiceLifetime.ApplicationExecutable(), Environment.ProcessId, Process.Start);
    }

    internal static Process StartWorker(string executable, int servicePid, Func<ProcessStartInfo, Process?> start)
    {
        var info = new ProcessStartInfo(executable, [UpdateWorker, servicePid.ToString(CultureInfo.InvariantCulture)]) { CreateNoWindow = true };
        return start(info) ?? throw new InvalidOperationException("Could not start the update helper.");
    }

    public void Dispose() => updater?.Dispose(); // The updater must survive the service it is replacing.
}
