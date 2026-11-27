namespace DevTunnelSvc.Platform;

internal sealed class TrayLifetime : IDisposable
{
    private readonly TaskCompletionSource<Exception?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable? tray;

    internal delegate IDisposable Factory(string pipeName, Action<string> writeEvent, Action<Exception?> stopped);
#if WINDOWS
    internal static Factory ForCurrentPlatform => static (pipe, log, stopped) => new TrayIcon(pipe, log, stopped);
#else
    internal static Factory? ForCurrentPlatform => null;
#endif

    internal TrayLifetime(string pipeName, Action<string> writeEvent, Factory? create) =>
        tray = create?.Invoke(pipeName, writeEvent, e => stopped.TrySetResult(e));

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
