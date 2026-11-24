using System.IO.Pipes;
using System.Text.Json;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class ServiceTests : IDisposable
{
    private DirectoryInfo stateDirectory = null!;
    private SessionCredential credential = null!;

    [TestInitialize]
    public void SetUp()
    {
        stateDirectory = Directory.CreateTempSubdirectory("devtunnel-service-tests-");
        credential = new(new CredentialFile(stateDirectory.FullName), (_, _) => throw new AssertFailedException("Unexpected refresh."), TimeProvider.System);
    }

    public void Dispose() { credential?.Dispose(); stateDirectory?.Delete(true); }

    [TestMethod]
    public async Task CliConnectionsSurviveTheirCallerAndReleaseResourcesOnStop()
    {
        var backend = new TestBackend();
        await using var state = new TunnelService(backend, credential);
        var name = $"DevTunnelSvc-test-{Guid.NewGuid():N}";
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var log = new StringWriter();
        var server = ControlServer.RunAsync(name, state, log, limit.Token);
        Task<string> Send(Request r, CancellationToken ct) => Control.SendAsync(r, ct, name);
        try
        {
            Assert.Contains("signed out", await Send(new("status"), limit.Token));
            Assert.Contains("login", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Send(new("create"), limit.Token))).Message);
            Assert.Contains("Signed in", await Send(new("login", SignIn: new("test-credential")), limit.Token));
            Assert.AreEqual("blue-tunnel.use2", await Send(new("create"), limit.Token));
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.AreEqual(0, await Program.RunAsync(["host", "blue-tunnel.use2", "--tunnel-port", "8080", "--target-port", "3000"], output, error, Send));
            Assert.AreEqual(0, await Program.RunAsync(["connect", "blue-tunnel.use2", "--tunnel-port", "8080", "--listen-port", "5000"], output, error, Send));
            Assert.Contains("connect:blue-tunnel.use2:5000", output.ToString());
            Assert.Contains("host:blue-tunnel.use2", output.ToString());
            Assert.IsEmpty(error.ToString());
            Assert.AreEqual((8080, 3000), (backend.Sessions[0].Map.TunnelPort, backend.Sessions[0].Map.LocalPort));
            Assert.AreEqual((8080, 5000), (backend.Sessions[1].Map.TunnelPort, backend.Sessions[1].Map.LocalPort));
            var status = await Send(new("status"), limit.Token);
            Assert.StartsWith("Service running; signed in.", status);
            Assert.Contains("localhost:5000 -> blue-tunnel.use2:8080", status);
            Assert.Contains("blue-tunnel.use2:8080 -> localhost:3000", status);
            Assert.DoesNotContain("test-credential", status);
            Assert.Contains("Stop", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Send(new("delete", "blue-tunnel.use2"), limit.Token))).Message);
            Assert.AreEqual(0, await Program.RunAsync(["stop", "host:blue-tunnel.use2"], output, error, Send));
            Assert.IsTrue(backend.Sessions[0].Disposed);
            Assert.IsFalse(backend.Sessions[1].Disposed);
            Assert.AreEqual(0, await Program.RunAsync(["logout"], output, error, Send));
            Assert.Contains("Signed out", output.ToString());
            Assert.Contains("signed out", await Send(new("status"), limit.Token));
            Assert.IsTrue(backend.Sessions[1].Disposed);
            await Send(new("shutdown"), limit.Token);
            await server;
        }
        finally { await limit.CancelAsync(); await server; }
    }

    [TestMethod]
    public async Task FailedStartsAndInvalidMappingsAreNeverListed()
    {
        var backend = new TestBackend { Fail = true };
        await using var state = new TunnelService(backend, credential);
        await state.ExecuteAsync(new("login", SignIn: new("token")), CancellationToken.None);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.AreNotEqual(0, await Program.RunAsync(["connect", "blue-tunnel.use2", "--tunnel-port", "8080", "--listen-port", "5000"], output, error, state.ExecuteAsync));
        Assert.Contains("permission denied", error.ToString());
        Assert.DoesNotContain("blue-tunnel", await state.ExecuteAsync(new("status"), CancellationToken.None));
        foreach (var request in new[] { new Request("host", "blue-tunnel.use2", 0, 3000), new("connect", "blue-tunnel.use2", 8080, 65536), new("connect", "bad/id.use2", 1, 2) })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => state.ExecuteAsync(request, CancellationToken.None));
        Assert.Contains("protocol", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => state.ExecuteAsync(new("status", Version: 1), CancellationToken.None))).Message);
        Assert.Contains("Unknown service command", (await Assert.ThrowsExactlyAsync<ArgumentException>(() => state.ExecuteAsync(new("unknown"), CancellationToken.None))).Message);
        Assert.IsEmpty(backend.Sessions);
    }

    [TestMethod]
    public async Task DuplicateMappingsAndAccountChangesAreRejectedUntilStopped()
    {
        await using var service = new TunnelService(new TestBackend(), credential);
        var ct = CancellationToken.None;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("login"), ct));
        Assert.Contains("credential", (await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("login", SignIn: new("two words")), ct))).Message);
        await service.ExecuteAsync(new("login", SignIn: new("token")), ct);
        var request = new Request("host", "blue-tunnel.use2", 8080, 3000);
        await service.ExecuteAsync(request, ct);
        Assert.Contains("already exists", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync(request, ct))).Message);
        Assert.Contains("Stop active", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync(new("login", SignIn: new("other")), ct))).Message);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("stop"), ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("stop", "missing"), ct));
        await service.ExecuteAsync(new("stop", "host:blue-tunnel.use2"), ct);
        Assert.Contains("Deleted blue-tunnel.use2", await service.ExecuteAsync(new("delete", "blue-tunnel.use2"), ct));
        Assert.DoesNotContain("blue-tunnel", await service.ExecuteAsync(new("status"), ct));
    }

    [TestMethod]
    public async Task ValidPortBoundariesArePreservedWithoutNarrowingOrWrapping()
    {
        await using var service = new TunnelService(new TestBackend(), credential);
        await service.ExecuteAsync(new("login", SignIn: new("t")), CancellationToken.None);
        Assert.Contains(":1 -> localhost:65535", await service.ExecuteAsync(new("host", "blue-tunnel.use2", 1, 65535), CancellationToken.None));
        Assert.Contains("localhost:1 -> blue-tunnel.use2:65535", await service.ExecuteAsync(new("connect", "blue-tunnel.use2", 65535, 1), CancellationToken.None));
    }

    [TestMethod]
    [DataRow(null, 8080, 5000)]
    [DataRow("blue-tunnel", 8080, 5000)]
    [DataRow("blue-tunnel.invalid!", 8080, 5000)]
    [DataRow("blue-tunnel.use2", 65536, 5000)]
    [DataRow("blue-tunnel.use2", 8080, 0)]
    public void InvalidPortOrTunnelIdentifierCannotReachTheSdk(string? id, int tunnelPort, int listenPort)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Mapping.Create(false, id, tunnelPort, listenPort));
    }

    [TestMethod]
    public async Task JsonNullOrTruncatedFramesDoNotBecomeValidRequests()
    {
        using var jsonNull = new MemoryStream([0, 0, 0, 4, (byte)'n', (byte)'u', (byte)'l', (byte)'l']);
        Assert.Contains("Empty control message", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Control.ReadAsync(jsonNull, ControlJson.Default.Request, CancellationToken.None))).Message);
        using var truncated = new MemoryStream([0, 0, 0, 4, 1]);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => Control.ReadAsync(truncated, ControlJson.Default.Request, CancellationToken.None));
        using var empty = new MemoryStream([0, 0, 0, 0]);
        Assert.Contains("length", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Control.ReadAsync(empty, ControlJson.Default.Request, CancellationToken.None))).Message);
    }

    [TestMethod]
    public async Task LargestValidControlFrameIsFlushedAndRoundTrips()
    {
        var empty = new Request("stop", Tunnel: "");
        var overhead = JsonSerializer.SerializeToUtf8Bytes(empty, ControlJson.Default.Request).Length;
        var request = empty with { Tunnel = new string('x', 32 * 1024 - overhead) };
        using var wire = new MemoryStream();
        using var buffered = new BufferedStream(wire, 64 * 1024);
        await Control.WriteAsync(buffered, request, ControlJson.Default.Request, CancellationToken.None);
        Assert.AreEqual(32 * 1024 + sizeof(int), wire.Length);
        wire.Position = 0;
        Assert.AreEqual(request, await Control.ReadAsync(wire, ControlJson.Default.Request, CancellationToken.None));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Control.WriteAsync(Stream.Null,
            request with { Tunnel = request.Tunnel + "x" }, ControlJson.Default.Request, CancellationToken.None));
        Assert.Contains("too large", error.Message);
    }

    [TestMethod]
    public async Task ShutdownClosesActiveSessionsButPreservesTheSignIn()
    {
        var backend = new TestBackend();
        await using var service = new TunnelService(backend, credential);
        var ct = CancellationToken.None;
        await service.ExecuteAsync(new("login", SignIn: new("user-token")), ct);
        Assert.AreEqual("user-token", await credential.RequireTokenAsync(ct));
        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync(new("delete", "missing-tunnel.use2"), ct));
        Assert.Contains("not found", missing.Message);
        await service.ExecuteAsync(new("host", "blue-tunnel.use2", 8080, 3000), ct);
        Assert.Contains("stopping", await service.ExecuteAsync(new("shutdown"), ct));
        Assert.IsTrue(backend.Sessions.Single().Disposed);
        Assert.AreEqual("user-token", await credential.RequireTokenAsync(ct));
        Assert.DoesNotContain("blue-tunnel", await service.ExecuteAsync(new("status"), ct));
    }

    [TestMethod]
    public async Task MalformedClientCannotStopOrPoisonTheNextControlConnection()
    {
        await using var service = new TunnelService(new TestBackend(), credential);
        var name = $"DevTunnelSvc-test-{Guid.NewGuid():N}";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var log = new StringWriter();
        var server = ControlServer.RunAsync(name, service, log, ct.Token);
        try
        {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, Control.Options))
            {
                await pipe.ConnectAsync(ct.Token);
                await pipe.WriteAsync(new byte[] { 0x7f, 0xff, 0xff, 0xff }, ct.Token);
            }
            Assert.Contains("running", await Control.SendAsync(new("status"), ct.Token, name));
            Assert.Contains("InvalidDataException", log.ToString());
            await Control.SendAsync(new("shutdown"), ct.Token, name);
        }
        finally { await ct.CancelAsync(); await server; }
    }

    [TestMethod]
    public void ServiceAndUpdateAreMutuallyExclusive()
    {
        var dir = Directory.CreateTempSubdirectory("devtunnel-lease-");
        try
        {
            var stateDirectory = Path.Combine(dir.FullName, "state");
            using (InstanceLease.Acquire(stateDirectory))
            {
                Assert.IsTrue(File.Exists(Path.Combine(stateDirectory, "service.lock")));
                Assert.Contains("Stop the service", Assert.ThrowsExactly<IOException>(() => InstanceLease.Acquire(stateDirectory)).Message);
            }
            using var restarted = InstanceLease.Acquire(stateDirectory);
            Assert.IsTrue(restarted.CanWrite);
        }
        finally { dir.Delete(true); }
    }

    [TestMethod]
    public void SystemdUnitQuotesExecutablePathsWithoutShellExpansion()
    {
        var unit = ServiceLifetime.UnitFile("/home/me/$path/100% ready/\"tunnel\"");
        Assert.Contains("ExecStart=\"/home/me/$$path/100%% ready/\\\"tunnel\\\"\" service run", unit);
        Assert.Contains("WantedBy=default.target", unit);
        Assert.ThrowsExactly<ArgumentException>(() => ServiceLifetime.UnitFile("/tmp/path\nExecStart=bad"));
    }

    private sealed class TestBackend : ITunnelBackend
    {
        public List<TestSession> Sessions { get; } = [];
        public bool Fail { get; init; }
        public Task<string> CreateAsync(CancellationToken ct) => Task.FromResult("blue-tunnel.use2");
        public Task<bool> DeleteAsync(TunnelKey key, CancellationToken ct) => Task.FromResult(key.Id != "missing-tunnel");
        public Task<ILiveTunnel> OpenAsync(Mapping map, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("permission denied");
            var session = new TestSession(map);
            Sessions.Add(session);
            return Task.FromResult<ILiveTunnel>(session);
        }
    }

    private sealed class TestSession(Mapping map) : ILiveTunnel
    {
        public Mapping Map => map;
        public string State => "Connected";
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
