using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;

namespace DevTunnelSvc.Tunnels;

internal sealed class RegionCatalog(ITunnelManagementClient management, Func<string?, CancellationToken, Task<TimeSpan?>>? measureLatency)
{
    // Only a listed region may reach the SDK, which derives the request host from the cluster ID.
    public async Task<string> FindAsync(string region, CancellationToken ct) =>
        (await management.ListClustersAsync(ct))
            .FirstOrDefault(cluster => string.Equals(cluster.ClusterId, region, StringComparison.OrdinalIgnoreCase))?.ClusterId
        ?? throw new ArgumentException($"Unknown region '{region}'. Run 'regions' to see the available regions.");

    public async Task<IReadOnlyList<TunnelRegion>> ListAsync(CancellationToken ct)
    {
        var clusters = await management.ListClustersAsync(ct);
        var advice = await management.GetClusterRecommendationsAsync(preferredClusterId: null, requiredGeo: null, ct);
        // A per-call client keeps probe connections from lingering in the long-running service.
        using var probeHttp = new HttpClient();
        var measure = measureLatency ?? new LatencyProbe(probeHttp, TimeProvider.System, TimeSpan.FromSeconds(5)).MeasureAsync;
        var latencies = await Task.WhenAll(clusters.Select(cluster => measure(cluster.Uri, ct)));
        return [.. clusters.Select((cluster, index) => Describe(cluster, latencies[index], advice))];
    }

    // The service reports availability and load only for the clusters it considered for this account.
    private static TunnelRegion Describe(ClusterDetails cluster, TimeSpan? latency, ClusterRecommendationResponse advice)
    {
        var recommendation = advice.Recommendations.FirstOrDefault(candidate => candidate.ClusterId == cluster.ClusterId);
        return new(cluster.ClusterId, cluster.AzureLocation, latency, recommendation?.Availability.ToString(),
            recommendation?.UtilizationPercent, recommendation?.Reason, cluster.ClusterId == advice.RecommendedClusterId);
    }
}
