using System.Net.Http.Headers;

namespace DevTunnelSvc.Tunnels;

internal sealed class LatencyProbe(HttpClient http, TimeProvider clock, TimeSpan timeout)
{
    private static readonly ProductInfoHeaderValue Agent = new("DevTunnelSvc", Program.Version);

    // The first request pays for DNS, TCP, and TLS setup, so only a second request on the warm connection is timed.
    public async Task<TimeSpan?> MeasureAsync(string? endpoint, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await RequestAsync(uri, limit.Token);
            var start = clock.GetTimestamp();
            await RequestAsync(uri, limit.Token);
            return clock.GetElapsedTime(start);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task RequestAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Headers = { UserAgent = { Agent } } };
        using var response = await http.SendAsync(request, ct);
    }
}
