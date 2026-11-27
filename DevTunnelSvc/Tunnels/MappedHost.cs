using System.Diagnostics;
using System.Net;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Management;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace DevTunnelSvc.Tunnels;

internal sealed class MappedHost(ITunnelManagementClient management, TraceSource trace, Mapping map)
    : TunnelRelayTunnelHost(management, trace)
{
    protected override void SubscribeSessionEvents(SshClientSession session)
    {
        base.SubscribeSessionEvents(session);
        Forwarding(session).AcceptRemoteConnectionsForNonForwardedPorts = false;
    }

    protected override async Task ConfigureSessionAsync(Stream stream, bool isReconnect, TunnelConnectionOptions? options, CancellationToken cancellation)
    {
        if (ConnectionProtocol != WebSocketSubProtocolV2)
            throw new NotSupportedException("Explicit host port mapping requires relay protocol v2.");

        // The default host forwards every listed port to localhost at the *same* port.
        // Leave that list empty and register only the explicitly authorized destination.
        Tunnel!.Ports = [];
        await base.ConfigureSessionAsync(stream, isReconnect, options, cancellation);
        var forwarding = Forwarding(SshSession!);
        var accepted = await forwarding.ForwardFromRemotePortAsync(
            IPAddress.Loopback, map.TunnelPort, "127.0.0.1", map.LocalPort, cancellation);
        if (accepted is null)
            throw new IOException($"The relay rejected tunnel port {map.TunnelPort}.");
    }

    private static PortForwardingService Forwarding(SshSession session) => session.GetService<PortForwardingService>()!;

    // A management-side port change must not implicitly expose another local application.
    public override Task RefreshPortsAsync(CancellationToken cancellation) => Task.CompletedTask;
}
