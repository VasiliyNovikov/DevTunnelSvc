using System.Text.Json;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed record AutoUpdateActions(Action RequireSupported, Func<CancellationToken, Task<ReleaseInfo?>> FindNewer,
    Func<int, CancellationToken, Task> Launch, Func<TimeSpan, CancellationToken, Task> Delay);

internal sealed class AutoUpdates : IAsyncDisposable
{
    internal const string Worker = "__auto-update", Shutdown = "shutdown-for-auto-update";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly AutoUpdateFile file;
    private readonly AutoUpdateActions actions;
    private readonly Action<string> log;
    private readonly CancellationToken lifetime;
    private readonly Lock launchGate = new();
    private CancellationTokenSource? polling;
    private Task monitor = Task.CompletedTask;
    internal bool Enabled => Volatile.Read(ref polling) is not null;
    internal ReleaseDiscovery Releases { get; }

    internal AutoUpdates(string directory, AutoUpdateActions actions, Action<string> log, CancellationToken lifetime)
    {
        file = new(directory);
        this.actions = actions;
        Releases = new(actions.FindNewer);
        this.log = log;
        this.lifetime = lifetime;
        try
        {
            if (file.Read() is { Enabled: true } settings) Start(InitialDelay(settings.LastAttemptUtc));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            log($"Auto-update settings unavailable; automatic updates are off: {AppLog.ErrorCode(ex)}");
        }
    }

    // The control pipe serializes all setting changes and guarded shutdowns, including changes requested by the tray.
    internal Task<string> DispatchAsync(Request request, Func<Request, CancellationToken, Task<string>> execute, CancellationToken ct)
    {
        if (request.Command is not (AutoUpdateConsent.Status or AutoUpdateConsent.Enable or AutoUpdateConsent.Disable or Shutdown)) return execute(request, ct);
        if (request.Version != Control.ProtocolVersion) throw new InvalidDataException("Incompatible control protocol.");
        ct.ThrowIfCancellationRequested();
        return request.Command switch
        {
            AutoUpdateConsent.Status => Task.FromResult(Enabled ? AutoUpdateConsent.Enabled : AutoUpdateConsent.Disabled),
            Shutdown => StopForUpdateAsync(request, execute, ct),
            _ => ConfigureAsync(request.Command == AutoUpdateConsent.Enable)
        };
    }

    private async Task<string> ConfigureAsync(bool enable)
    {
        if (enable)
        {
            if (Enabled) return AutoUpdateConsent.Enabled;
            actions.RequireSupported();
            file.Save(true);
            Start(TimeSpan.Zero);
        }
        else await StopPollingAsync(forget: true);
        var message = enable ? AutoUpdateConsent.Enabled : AutoUpdateConsent.Disabled;
        log(message);
        return message;
    }

    private Task<string> StopForUpdateAsync(Request request, Func<Request, CancellationToken, Task<string>> execute, CancellationToken ct)
    {
        if (request.ServicePid != Environment.ProcessId || !Enabled)
            throw new InvalidOperationException("Auto-update was disabled or the selected service instance has changed.");
        // A failed download also restarts the old service. Don't stop it again immediately for the same release.
        file.Save(true, DateTimeOffset.UtcNow);
        return execute(request with { Command = "shutdown-for-update" }, ct);
    }

    private static TimeSpan InitialDelay(DateTimeOffset? attempted) => attempted is { } last
        ? TimeSpan.FromTicks(Math.Clamp((last - DateTimeOffset.UtcNow + Interval).Ticks, 0, Interval.Ticks)) : TimeSpan.Zero;

    private void Start(TimeSpan initialDelay)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Volatile.Write(ref polling, source);
        monitor = Task.Run(() => MonitorAsync(initialDelay, source.Token));
    }

    private async Task MonitorAsync(TimeSpan initialDelay, CancellationToken ct)
    {
        try
        {
            if (initialDelay > TimeSpan.Zero) await actions.Delay(initialDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(ct);
                await actions.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (await Releases.CheckAsync(limit.Token) is { } tag) await LaunchAsync(tag, limit.Token);
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            log($"Auto-update check or launch failed: {AppLog.ErrorCode(ex)}");
        }
    }

    private Task LaunchAsync(string tag, CancellationToken ct)
    {
        lock (launchGate)
        {
            if (!Enabled) return Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            actions.RequireSupported();
            log($"Auto-update found {tag}; launching updater.");
            return actions.Launch(Environment.ProcessId, ct);
        }
    }

    private async Task StopPollingAsync(bool forget = false)
    {
        CancellationTokenSource? source;
        lock (launchGate)
        {
            if (forget) file.Save(false);
            source = Interlocked.Exchange(ref polling, null);
        }
        if (source is null) return;
        using (source)
        {
            await source.CancelAsync();
            await monitor;
        }
    }

    public async ValueTask DisposeAsync() => await StopPollingAsync();
}
