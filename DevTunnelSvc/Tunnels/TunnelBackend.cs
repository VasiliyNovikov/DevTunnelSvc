using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;

namespace DevTunnelSvc.Tunnels;

internal interface ILiveTunnel : IAsyncDisposable
{
    string State { get; }
}

internal interface ITunnelBackend
{
    Task<string> CreateAsync(string? tunnelId, string? region, CancellationToken cancellation);
    Task<IReadOnlyList<TunnelSummary>> ListAsync(CancellationToken cancellation);
    Task<IReadOnlyList<TunnelRegion>> ListRegionsAsync(CancellationToken cancellation);
    Task<bool> DeleteAsync(TunnelKey key, CancellationToken cancellation);
    Task<ILiveTunnel> OpenAsync(Mapping map, CancellationToken cancellation);
}

internal sealed class TunnelBackend : ITunnelBackend, IAsyncDisposable
{
    private readonly TunnelManagementClient management;
    private readonly RegionCatalog regions;
    private readonly TraceSource trace = new("DevTunnelSvc.Relay", SourceLevels.Off);
    private readonly ITunnelRelayStreamFactory? transport;
    private readonly Action<string>? writeEvent;

    public TunnelBackend(Func<Task<string>> userToken, HttpMessageHandler? handler = null, ITunnelRelayStreamFactory? transport = null,
        Func<string?, CancellationToken, Task<TimeSpan?>>? measureLatency = null, Action<string>? writeEvent = null)
    {
        this.transport = transport;
        this.writeEvent = writeEvent;
        management = new(new ProductInfoHeaderValue("DevTunnelSvc", Program.Version),
            async () => new AuthenticationHeaderValue(TunnelAuthenticationSchemes.GitHub, await userToken()),
            tunnelServiceUri: null, httpHandler: handler);
        regions = new(management, measureLatency);
    }

    public async Task<string> CreateAsync(string? tunnelId, string? region, CancellationToken cancellation)
    {
        // Without a cluster, the SDK asks the service to recommend one.
        var tunnel = new Tunnel { TunnelId = tunnelId, ClusterId = region is null ? null : await regions.FindAsync(region, cancellation) };
        var created = await management.CreateTunnelAsync(tunnel, options: null, cancellation);
        return TunnelKey.Parse($"{created.TunnelId}.{created.ClusterId}").ToString();
    }

    public Task<IReadOnlyList<TunnelRegion>> ListRegionsAsync(CancellationToken cancellation) => regions.ListAsync(cancellation);

    public async Task<IReadOnlyList<TunnelSummary>> ListAsync(CancellationToken cancellation)
    {
        // The cross-region listing returns empty port lists even when ports are requested, so read each tunnel
        // from its own cluster. A tunnel deleted in between reads as null and is skipped.
        var listed = await management.ListTunnelsAsync(clusterId: null, domain: null, options: null, ownedTunnelsOnly: null, cancellation);
        var tunnels = await Task.WhenAll(listed.Select(tunnel =>
            management.GetTunnelAsync(tunnel, new TunnelRequestOptions { IncludePorts = true }, cancellation)));
        return [.. tunnels.OfType<Tunnel>().Select(Summarize)];
    }

    // Keep the service's ID verbatim: older generated IDs are valid tunnels that TunnelKey rejects as new IDs.
    private static TunnelSummary Summarize(Tunnel tunnel) => new($"{tunnel.TunnelId}.{tunnel.ClusterId}",
        tunnel.Status?.HostConnectionCount?.Current, tunnel.Status?.ClientConnectionCount?.Current,
        tunnel.Expiration, [.. (tunnel.Ports ?? []).Select(port => (int)port.PortNumber)]);

    public Task<bool> DeleteAsync(TunnelKey key, CancellationToken cancellation) =>
        management.DeleteTunnelAsync(Identify(key), options: null, cancellation);

    public async Task<ILiveTunnel> OpenAsync(Mapping map, CancellationToken cancellation)
    {
        var scope = map.Host ? TunnelAccessScopes.Host : TunnelAccessScopes.Connect;
        var options = new TunnelRequestOptions { TokenScopes = [scope], IncludePorts = true };
        var tunnel = await management.GetTunnelAsync(Identify(map.Tunnel), options, cancellation)
            ?? throw new InvalidOperationException($"Tunnel {map.Tunnel} was not found.");
        if (map.Host && tunnel.Ports?.Any(p => p.PortNumber == map.TunnelPort) != true)
            await management.CreateTunnelPortAsync(tunnel, new TunnelPort { PortNumber = (ushort)map.TunnelPort, Protocol = TunnelProtocol.Tcp }, null, cancellation);

        TunnelRelayConnection connection = map.Host ? new MappedHost(management, trace, map) : new MappedClient(management, trace, map);
        ObserveRelay(connection, map.Role);
        try
        {
            if (transport is not null)
                connection.StreamFactory = transport;
            await connection.ConnectAsync(tunnel, new TunnelConnectionOptions { EnableRetry = false }, cancellation);
            if (connection is MappedClient client)
                await client.WaitForMappingAsync(cancellation);
            return new LiveConnection(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void ObserveRelay(TunnelRelayConnection connection, string role)
    {
        if (writeEvent is null) return;
        connection.ConnectionStatusChanged += (_, change) =>
        {
            var error = change.DisconnectException is { } ex ? $": {AppLog.ErrorCode(ex)}" : "";
            writeEvent($"{role} relay: {change.PreviousStatus} -> {change.Status}{error}.");
        };
    }

    private static Tunnel Identify(TunnelKey key) => new() { TunnelId = key.Id, ClusterId = key.Cluster };

    public async ValueTask DisposeAsync()
    {
        await management.DisposeAsync();
        trace.Close();
    }

    private sealed class LiveConnection(TunnelConnection connection) : ILiveTunnel
    {
        public string State => connection is MappedClient { ConnectionStatus: ConnectionStatus.Connected, IsListening: false }
            ? "WaitingForPort" : connection.ConnectionStatus.ToString();
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
