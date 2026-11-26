using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Service;

internal sealed class MappingSession : IAsyncDisposable
{
    private readonly CancellationTokenSource? restore;
    private readonly Task<ILiveTunnel> opening;
    private volatile string state = "Restoring";
    internal Mapping Map { get; }
    internal bool Saved { get; set; } = true;
    internal string State => !Saved ? "Close failed (not saved)" : opening.IsCompletedSuccessfully ? opening.Result.State : state;

    internal MappingSession(Mapping map, ILiveTunnel relay)
    {
        Map = map;
        opening = Task.FromResult(relay);
    }

    internal MappingSession(Mapping map, Func<CancellationToken, Task<ILiveTunnel>> open, Func<TimeSpan, CancellationToken, Task> delay, Action<string> writeEvent)
    {
        Map = map;
        restore = new();
        opening = RestoreAsync(open, delay, writeEvent, restore.Token);
    }

    private async Task<ILiveTunnel> RestoreAsync(Func<CancellationToken, Task<ILiveTunnel>> open,
        Func<TimeSpan, CancellationToken, Task> delay, Action<string> writeEvent, CancellationToken ct)
    {
        // An offline peer must not delay the control pipe, the tray, or another mapping.
        await Task.Yield();
        writeEvent($"Restoring {Map.Role} mapping.");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(50));
            try
            {
                var relay = await open(attempt.Token);
                writeEvent($"Restored {Map.Role} mapping.");
                return relay;
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                writeEvent($"Retrying {Map.Role} mapping: {AppLog.ErrorCode(ex)}");
                var reason = ex is OperationCanceledException ? "connection timed out" : ControlServer.ErrorMessage(ex);
                state = $"Retrying ({reason})";
            }
            await delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (restore is { IsCancellationRequested: false }) await restore.CancelAsync();
            var relay = await opening;
            await relay.DisposeAsync();
        }
        catch (OperationCanceledException) when (opening.IsCanceled && restore?.IsCancellationRequested == true) { }
        finally { restore?.Dispose(); }
    }
}
