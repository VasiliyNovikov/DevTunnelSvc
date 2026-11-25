namespace DevTunnelSvc.Platform;

internal sealed class TrayLifetime : IDisposable
{
    private readonly TaskCompletionSource<Exception?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable? tray;

    internal static Func<Action<Exception?>, IDisposable>? ForCurrentPlatform => OperatingSystem.IsWindows() ? CreateWindowsTray : null;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static TrayIcon CreateWindowsTray(Action<Exception?> stopService) => new(stopService);

    internal TrayLifetime(Func<Action<Exception?>, IDisposable>? create) => tray = create?.Invoke(e => stopped.TrySetResult(e));

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
