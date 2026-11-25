using System.Globalization;

namespace DevTunnelSvc.Tunnels;

internal sealed record TunnelRegion(string Id, string Location, TimeSpan? Latency, string? Availability,
    double? Utilization, string? Reason, bool IsDefault)
{
    private static readonly string[] Header = ["REGION", "LOCATION", "LATENCY", "AVAILABILITY", "LOAD", "REASON"];

    // Fastest first; regions whose latency could not be measured go last.
    public static string FormatTable(IReadOnlyCollection<TunnelRegion> regions)
    {
        var table = TextTable.Format(Header, regions.OrderBy(r => r.Latency ?? TimeSpan.MaxValue)
            .ThenBy(r => r.Id, StringComparer.Ordinal).Select(r => r.Cells()));
        return regions.FirstOrDefault(r => r.IsDefault) is { } recommended
            ? $"{table}{Environment.NewLine}Without --region, 'create' uses the recommended region {recommended.Id}."
            : table;
    }

    private string[] Cells() =>
    [
        Id,
        Location,
        Latency is { } latency ? string.Create(CultureInfo.InvariantCulture, $"{latency.TotalMilliseconds:0} ms") : "-",
        Availability ?? "-",
        Utilization is { } load ? string.Create(CultureInfo.InvariantCulture, $"{load:0}%") : "-",
        Reason ?? "-",
    ];
}
