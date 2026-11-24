using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using DevTunnelSvc.Tunnels;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Connections.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class ForwardingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostMapsARelayChannelToTheTargetWithoutBindingTheTunnelPort(bool alreadyRegistered)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = limit.Token;
        using var target = Listen();
        using var occupied = Listen();
        var tunnelPort = Port(occupied);
        using var peer = new RelayPeer();
        await using var messages = new TunnelRelayTunnelClient(peer.Trace) { EnableE2EEncryption = false };
        peer.Ports.MessageFactory = messages;
        peer.Ports.AcceptLocalConnectionsForForwardedPorts = false;
        using var handler = Api(tunnelPort, hosting: true, alreadyRegistered);
        await using var backend = new TunnelBackend(() => Task.FromResult("local-test"), handler, peer);
        await using var host = await backend.OpenAsync(Mapping.Create(true, "blue-tunnel.use2", tunnelPort, Port(target)), ct);
        Assert.AreEqual("Connected", host.State);
        await peer.Ports.WaitForForwardedPortAsync(tunnelPort, ct);
        using var channel = await peer.Ports.ConnectToForwardedPortAsync(tunnelPort, ct);
        Assert.IsNotNull(channel);
        using var app = await target.AcceptTcpClientAsync(ct);
        await RoundTripAsync(channel, app.GetStream(), ct);
        Assert.IsFalse(occupied.Pending());
    }

    [TestMethod]
    public async Task ClientListensOnExactlyTheRequestedPortAndRefusesOtherTunnelPorts()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = limit.Token;
        using var target = Listen();
        using var occupied = Listen();
        using var selection = Listen();
        var localPort = Port(selection);
        selection.Stop();
        var tunnelPort = Port(occupied);
        using var peer = new RelayPeer();
        using var handler = Api(tunnelPort);
        await using var backend = new TunnelBackend(() => Task.FromResult("local-test"), handler, peer);
        var opening = backend.OpenAsync(Mapping.Create(false, "blue-tunnel.use2", tunnelPort, localPort), ct);
        await peer.Connected.Task.WaitAsync(ct);
        Assert.IsNull(await peer.Ports.ForwardFromRemotePortAsync(IPAddress.Loopback, Port(target), "127.0.0.1", Port(target), ct));
        using var forwarding = await peer.Ports.ForwardFromRemotePortAsync(IPAddress.Loopback, tunnelPort, "127.0.0.1", Port(target), ct);
        Assert.IsNotNull(forwarding);
        await using var client = await opening.WaitAsync(ct);
        Assert.AreEqual("Connected", client.State);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, localPort, ct);
        using var app = await target.AcceptTcpClientAsync(ct);
        await RoundTripAsync(caller.GetStream(), app.GetStream(), ct);
        Assert.IsFalse(occupied.Pending());
        forwarding.Dispose();
        while (client.State == "Connected")
            await Task.Delay(20, ct);
        Assert.AreEqual("WaitingForPort", client.State);
        await client.DisposeAsync();
        using var stopped = new TcpClient();
        await Assert.ThrowsExactlyAsync<SocketException>(() => stopped.ConnectAsync(IPAddress.Loopback, localPort, ct).AsTask());
    }

    private static async Task RoundTripAsync(Stream caller, Stream app, CancellationToken ct)
    {
        byte[] buffer = new byte[3];
        await caller.WriteAsync("tcp"u8.ToArray(), ct);
        await app.ReadExactlyAsync(buffer, ct);
        CollectionAssert.AreEqual("tcp"u8.ToArray(), buffer);
        await app.WriteAsync("ack"u8.ToArray(), ct);
        await caller.ReadExactlyAsync(buffer, ct);
        CollectionAssert.AreEqual("ack"u8.ToArray(), buffer);
    }

    [TestMethod]
    public async Task RejectedHostForwardIsNotReportedAsAConnectedHost()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var port = Listen();
        using var peer = new RelayPeer(allowForwarding: false);
        using var handler = Api(Port(port), hosting: true);
        await using var backend = new TunnelBackend(() => Task.FromResult("host"), handler, peer);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() =>
            backend.OpenAsync(Mapping.Create(true, "blue-tunnel.use2", Port(port), 3000), limit.Token));
        Assert.Contains($"rejected tunnel port {Port(port)}", error.Message);
    }

    [TestMethod]
    public async Task BindFailureRejectsTheMappingInsteadOfPickingANearbyPort()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = limit.Token;
        using var conflict = Listen();
        using var tunnelPort = Listen();
        using var peer = new RelayPeer();
        using var handler = Api(Port(tunnelPort));
        await using var backend = new TunnelBackend(() => Task.FromResult("local-test"), handler, peer);
        var opening = backend.OpenAsync(Mapping.Create(false, "blue-tunnel.use2", Port(tunnelPort), Port(conflict)), ct);
        await peer.Connected.Task.WaitAsync(ct);
        var announcement = peer.Ports.ForwardFromRemotePortAsync(IPAddress.Loopback, Port(tunnelPort), "127.0.0.1", Port(conflict), ct);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => opening.WaitAsync(ct));
        Assert.Contains($"localhost:{Port(conflict)}", error.Message);
        Assert.IsInstanceOfType<SocketException>(error.InnerException);
        // The rejected request can race the failing client's disposal of its SSH session.
        try { using var rejected = await announcement; Assert.IsNull(rejected); }
        catch (SshConnectionException) when (opening.IsFaulted) { }
    }

    [TestMethod]
    public async Task EncryptedSdkClientAndHostForwardBytesAcrossThreeIndependentPorts()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = limit.Token;
        using var appListener = Listen();
        using var occupied = Listen();
        using var reservation = Listen();
        var tunnelPort = Port(occupied);
        var listenPort = Port(reservation);
        reservation.Stop();
        List<string> hostKeys = [];
        using var hostPeer = new RelayPeer();
        using var clientPeer = new RelayPeer(encrypted: true);
        await using var extensions = new TunnelRelayTunnelClient(hostPeer.Trace) { EnableE2EEncryption = true };
        hostPeer.Ports.MessageFactory = extensions;
        hostPeer.Ports.AcceptLocalConnectionsForForwardedPorts = false;
        using var hostApi = Api(tunnelPort, hosting: true, hostKeys: hostKeys);
        using var clientApi = Api(tunnelPort, hostKeys: hostKeys);
        await using var hostBackend = new TunnelBackend(() => Task.FromResult("host"), hostApi, hostPeer);
        await using var clientBackend = new TunnelBackend(() => Task.FromResult("client"), clientApi, clientPeer);
        await using var host = await hostBackend.OpenAsync(Mapping.Create(true, "blue-tunnel.use2", tunnelPort, Port(appListener)), ct);
        Assert.IsNotEmpty(hostKeys);
        var opening = clientBackend.OpenAsync(Mapping.Create(false, "blue-tunnel.use2", tunnelPort, listenPort), ct);
        await clientPeer.Connected.Task.WaitAsync(ct);
        using var announced = await clientPeer.Ports.StreamFromRemotePortAsync(IPAddress.Loopback, tunnelPort, ct);
        Assert.IsNotNull(announced);
        var incoming = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        announced.StreamOpened += (_, stream) => incoming.TrySetResult(stream);
        await using var connected = await opening.WaitAsync(ct);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, listenPort, ct);
        using var clientChannel = await incoming.Task.WaitAsync(ct);
        using var hostChannel = await hostPeer.Ports.ConnectToForwardedPortAsync(tunnelPort, ct);
        Assert.IsNotNull(hostChannel);
        using var forwarding = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = Task.WhenAll(hostChannel.CopyToAsync(clientChannel, forwarding.Token), clientChannel.CopyToAsync(hostChannel, forwarding.Token));
        try
        {
            using var app = await appListener.AcceptTcpClientAsync(ct);
            await RoundTripAsync(caller.GetStream(), app.GetStream(), ct);
        }
        finally
        {
            await forwarding.CancelAsync();
            clientChannel.Dispose();
            hostChannel.Dispose();
            try { await pump.WaitAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        }
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static HttpStub Api(int port, bool hosting = false, bool alreadyRegistered = false, List<string>? hostKeys = null)
    {
        string Endpoint() => JsonSerializer.Serialize(new
        {
            connectionMode = "TunnelRelay",
            hostId = "local-test",
            hostRelayUri = "wss://relay.invalid/host",
            clientRelayUri = "wss://relay.invalid/client",
            hostPublicKeys = hostKeys
        });
        var registered = alreadyRegistered;
        var scope = hosting ? "host" : "connect";
        return new(async (r, ct) =>
        {
            if (r.Method == HttpMethod.Get)
            {
                var query = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
                Assert.AreEqual(scope, query["tokenScopes"]);
                Assert.AreEqual("True", query["includePorts"], true);
                var ports = registered ? $$"""[{"portNumber":1},{"portNumber":{{port}}}]""" : """[{"portNumber":1}]""";
                return HttpStub.Json($$"""{"tunnelId":"blue-tunnel","clusterId":"use2","accessTokens":{"{{scope}}":"test-token"},"ports":{{ports}},"endpoints":[{{Endpoint()}}]}""");
            }
            if (r.Method == HttpMethod.Put)
            {
                if (r.RequestUri!.AbsolutePath.Contains("/ports/", StringComparison.Ordinal))
                {
                    Assert.IsTrue(hosting && !registered, "Only a host may register a missing port.");
                    using var body = System.Text.Json.JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
                    Assert.AreEqual(port, body.RootElement.GetProperty("portNumber").GetInt32());
                    Assert.AreEqual("tcp", body.RootElement.GetProperty("protocol").GetString());
                    registered = true;
                    return HttpStub.Json($$"""{"portNumber":{{port}}}""");
                }
                Assert.IsTrue(registered, "Register the requested port before opening the relay endpoint.");
                if (hostKeys is not null)
                {
                    var body = JsonElement.Parse(await r.Content!.ReadAsStringAsync(ct));
                    hostKeys.AddRange(body.GetProperty("hostPublicKeys").EnumerateArray().Select(k => k.GetString()!));
                }
                return HttpStub.Json(Endpoint());
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        })
        { AllowAutoRedirect = false };
    }

    private sealed class RelayPeer : ITunnelRelayStreamFactory, IDisposable
    {
        private readonly SshServerSession session;
        private readonly TcpListener listener = Listen();
        private readonly IKeyPair key = SshAlgorithms.PublicKey.ECDsaSha2Nistp384.GenerateKeyPair();
        public TraceSource Trace { get; } = new("offline relay", SourceLevels.Off);
        public PortForwardingService Ports { get; }
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RelayPeer(bool encrypted = false, bool allowForwarding = true)
        {
            var config = new SshSessionConfiguration();
            config.AddService(typeof(PortForwardingService));
            session = new SshServerSession(config, Trace) { Credentials = new SshServerCredentials(key) };
            session.Authenticating += (_, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
            session.Request += (_, e) => e.IsAuthorized = allowForwarding;
            session.ChannelOpening += (_, e) =>
            {
                if (e.IsRemoteRequest && e.Channel.ChannelType == "forwarded-tcpip")
                    e.OpeningTask = Task.FromResult<ChannelMessage>(new PortRelayConnectResponseMessage { IsE2EEncryptionEnabled = encrypted });
            };
            Ports = session.ActivateService<PortForwardingService>();
        }

        public async Task<(Stream Stream, string SubProtocol)> CreateRelayStreamAsync(Uri uri, string? token, string[] protocols, TraceSource trace, CancellationToken ct)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port(listener), ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            _ = AcceptAsync(server.GetStream(), ct);
            var protocol = uri.AbsolutePath == "/host" ? TunnelRelayConnection.HostWebSocketSubProtocolV2 : TunnelRelayConnection.ClientWebSocketSubProtocolV2;
            Assert.Contains(protocol, protocols);
            return (client.GetStream(), protocol);
        }

        private async Task AcceptAsync(Stream stream, CancellationToken ct)
        {
            try { await session.ConnectAsync(stream, ct); Connected.TrySetResult(); }
            catch (Exception ex) { Connected.TrySetException(ex); }
        }

        public void Dispose() { session.Dispose(); key.Dispose(); listener.Stop(); Trace.Close(); }
    }
}
