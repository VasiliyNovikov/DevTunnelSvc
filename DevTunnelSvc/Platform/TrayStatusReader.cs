using System.Text.Json;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Platform;

internal sealed record TrayStatusTarget(Action<IReadOnlyList<TunnelSummary>, string?> SetTunnels, CancellationToken Closed);

internal sealed class TrayStatusReader(string pipeName, Func<CancellationToken, Task<IReadOnlyList<TunnelSummary>>> listTunnels, Action<string> writeEvent)
{
    internal async Task ShowAsync(Func<LocalStatus, TrayStatusTarget> open, CancellationToken ct)
    {
        var local = await ReadLocalAsync(ct);
        ct.ThrowIfCancellationRequested();
        var view = open(local);
        if (!local.SignedIn) return;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, view.Closed);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        IReadOnlyList<TunnelSummary> tunnels;
        try
        {
            // The management query must not monopolize either the STA or the serialized control pipe.
            tunnels = await Task.Run(() => listTunnels(deadline.Token), deadline.Token);
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested) return;
            writeEvent($"Tray tunnel list failed: {AppLog.ErrorCode(ex)}");
            var reason = ex is OperationCanceledException ? "The request timed out." : ControlServer.ErrorMessage(ex);
            view.SetTunnels([], $"Could not list tunnels: {reason}");
            return;
        }
        if (!lifetime.IsCancellationRequested) view.SetTunnels(tunnels, null);
    }

    private async Task<LocalStatus> ReadLocalAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var message = await Control.SendAsync(new(LocalStatus.Command), pipeName, limit.Token);
            var local = JsonSerializer.Deserialize(message, StatusJson.Default.LocalStatus);
            return local is not null && !local.Mappings.Contains(null) ? local : throw new InvalidDataException("Invalid service status report.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or IOException { InnerException: TimeoutException })
        {
            throw new IOException("The service did not answer the status request in time. Try again.", ex);
        }
    }
}
