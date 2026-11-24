using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Management;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace DevTunnelSvc.Tunnels;

internal sealed class MappedClient : TunnelRelayTunnelClient, ITcpListenerFactory
{
    private readonly Mapping map;
    private readonly TaskCompletionSource<SocketException> bindFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MappedClient(ITunnelManagementClient management, TraceSource trace, Mapping map) : base(management, trace)
    {
        this.map = map;
        PortForwarding += (_, port) => port.Cancel = port.PortNumber != map.TunnelPort;
    }

    internal bool IsListening => ForwardedPorts?.Any(p => p.RemotePort == map.TunnelPort && p.LocalPort == map.LocalPort) == true;

    protected override void SubscribeSessionEvents(SshClientSession session)
    {
        base.SubscribeSessionEvents(session);
        session.GetService<PortForwardingService>()!.TcpListenerFactory = this;
    }

    public Task<TcpListener> CreateTcpListenerAsync(int? remotePort, IPAddress localIPAddress, int localPort,
        bool canChangeLocalPort, TraceSource trace, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (remotePort != map.TunnelPort)
            throw new InvalidOperationException("Unexpected remote port in a forwarding request.");
        var address = localIPAddress.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        var listener = new TcpListener(address, map.LocalPort) { ExclusiveAddressUse = true };
        try { listener.Start(); return Task.FromResult(listener); }
        catch (SocketException ex)
        {
            listener.Stop();
            if (ex.SocketErrorCode != SocketError.AddressNotAvailable || canChangeLocalPort)
                bindFailure.TrySetResult(ex);
            throw;
        }
    }

    internal async Task WaitForMappingAsync(CancellationToken ct)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forwarded = WaitForForwardedPortAsync(map.TunnelPort, waiting.Token);
        if (await Task.WhenAny(forwarded, bindFailure.Task) == bindFailure.Task)
        {
            await waiting.CancelAsync();
            try { await forwarded; }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
            throw new IOException($"Could not listen on localhost:{map.LocalPort}.", await bindFailure.Task);
        }
        await forwarded;
    }
}
