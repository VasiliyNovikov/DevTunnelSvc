using System.Globalization;

namespace DevTunnelSvc.Tunnels;

internal sealed record TunnelSummary(string Tunnel, ulong? HostConnections, ulong? ClientConnections,
    DateTime? Expiration, IReadOnlyList<int> TunnelPorts)
{
    private static readonly string[] Header = ["TUNNEL", "HOSTS", "CLIENTS", "EXPIRES", "PORTS"];

    // Dev Tunnels allows 10 tunnels per user and 10 ports per tunnel, so the table fits in one control reply.
    public static string FormatTable(IReadOnlyCollection<TunnelSummary> tunnels) => tunnels.Count == 0
        ? "No tunnels found. Run 'create' to create one."
        : TextTable.Format(Header, Rows(tunnels));

    internal static IEnumerable<string[]> Rows(IEnumerable<TunnelSummary> tunnels) =>
        tunnels.OrderBy(t => t.Tunnel, StringComparer.Ordinal).Select(t => t.Cells());

    private string[] Cells() =>
    [
        Tunnel,
        CountCell(HostConnections),
        CountCell(ClientConnections),
        Expiration?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture) ?? "-",
        TunnelPorts.Count == 0 ? "-" : string.Join(',', TunnelPorts.Order()),
    ];

    private static string CountCell(ulong? count) => count?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
