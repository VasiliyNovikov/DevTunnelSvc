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
    Task<string> CreateAsync(CancellationToken cancellation);
    Task<bool> DeleteAsync(TunnelKey key, CancellationToken cancellation);
    Task<ILiveTunnel> OpenAsync(Mapping map, CancellationToken cancellation);
}

internal sealed class TunnelBackend : ITunnelBackend, IAsyncDisposable
{
    private readonly TunnelManagementClient management;
    private readonly TraceSource trace = new("DevTunnelSvc.Relay", SourceLevels.Off);
    private readonly ITunnelRelayStreamFactory? transport;

    public TunnelBackend(Func<Task<string>> userToken, HttpMessageHandler? handler = null, ITunnelRelayStreamFactory? transport = null)
    {
        this.transport = transport;
        management = new(new ProductInfoHeaderValue("DevTunnelSvc", Program.Version),
            async () => new AuthenticationHeaderValue(TunnelAuthenticationSchemes.GitHub, await userToken()),
            tunnelServiceUri: null, httpHandler: handler);
    }

    public async Task<string> CreateAsync(CancellationToken cancellation)
    {
        var created = await management.CreateTunnelAsync(new Tunnel(), options: null, cancellation);
        return TunnelKey.Parse($"{created.TunnelId}.{created.ClusterId}").ToString();
    }

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
