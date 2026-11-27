namespace DevTunnelSvc.Updates;

internal sealed class ReleaseDiscovery(Func<CancellationToken, Task<ReleaseInfo?>> findNewer)
{
    private readonly Lock gate = new();
    private long nextCheck, lastCompleted;
    private volatile string? availableTag;
    internal string? AvailableTag => availableTag;

    internal async Task<string?> CheckAsync(CancellationToken ct)
    {
        var check = Interlocked.Increment(ref nextCheck);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        var release = await findNewer(limit.Token);
        limit.Token.ThrowIfCancellationRequested();
        lock (gate)
        {
            // A slower automatic check must not overwrite a newer manual result, or vice versa.
            if (check > lastCompleted)
            {
                lastCompleted = check;
                availableTag = release?.TagName;
            }
            return availableTag;
        }
    }
}
