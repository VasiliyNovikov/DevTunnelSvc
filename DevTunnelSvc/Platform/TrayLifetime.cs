using DevTunnelSvc.Tunnels;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Platform;

internal sealed record TrayQueries(Func<bool> AutoUpdateEnabled, ReleaseDiscovery Releases, Func<CancellationToken, Task<IReadOnlyList<TunnelSummary>>> ListTunnels);

internal sealed class TrayLifetime : IDisposable
{
    private readonly TaskCompletionSource<Exception?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable? tray;

    internal delegate IDisposable Factory(string pipeName, Action<string> writeEvent, TrayQueries queries, Action<Exception?> stopped);
#if WINDOWS
    internal static Factory ForCurrentPlatform => static (pipe, log, queries, stopped) => new TrayIcon(pipe, log, queries, stopped);
#else
    internal static Factory? ForCurrentPlatform => null;
#endif

    internal TrayLifetime(string pipeName, Action<string> writeEvent, TrayQueries queries, Factory? create) =>
        tray = create?.Invoke(pipeName, writeEvent, queries, e => stopped.TrySetResult(e));

    internal async Task WaitAsync(Task server, CancellationTokenSource lifetime)
    {
        // Never dispose/join the tray thread inline inside its Exit or failure callback.
        if (tray is not null && await Task.WhenAny(server, stopped.Task) == stopped.Task)
            await lifetime.CancelAsync();
        await server;
        if (stopped.Task.IsCompletedSuccessfully && stopped.Task.Result is { } failure)
            throw new InvalidOperationException($"Notification-area icon failed: {failure.Message}", failure);
    }

    public void Dispose() => tray?.Dispose();
}
