using System.Collections.Concurrent;
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
    private MappingFile mappings = null!;
    private readonly ConcurrentQueue<string> events = new();
    private readonly TestBackend backend = new();
    private static readonly Request Host = new("host", "blue-tunnel.use2", 8080, 3000);
    private static readonly Request Connect = new("connect", "blue-tunnel.use2", 8080, 5000);
    private static readonly string[] ConnectCommand = ["connect", "blue-tunnel.use2", "--tunnel-port", "8080", "--listen-port", "5000"];

    [TestInitialize]
    public void SetUp()
    {
        stateDirectory = Directory.CreateTempSubdirectory("devtunnel-service-tests-");
        mappings = new(stateDirectory.FullName);
        credential = new(new CredentialFile(stateDirectory.FullName), (_, _) => throw new AssertFailedException("Unexpected refresh."), TimeProvider.System);
    }

    public void Dispose() { credential?.Dispose(); stateDirectory?.Delete(true); }

    private TunnelService CreateService(ITunnelBackend backend) => new(backend, credential, mappings, events.Enqueue);

    private static Task<string> SignInAsync(TunnelService service, string token, CancellationToken ct = default) =>
        service.ExecuteAsync(new("login", SignIn: new(token)), ct);

    private static async Task AssertTunnelAbsentAsync(TunnelService service, CancellationToken ct) =>
        Assert.DoesNotContain("blue-tunnel", await service.ExecuteAsync(new("status"), ct));

    private async Task<TunnelService> SignedInServiceAsync(string token)
    {
        var service = CreateService(backend);
        try { await SignInAsync(service, token); return service; }
        catch { await service.DisposeAsync(); throw; }
    }

    [TestMethod]
    public async Task CliConnectionsSurviveTheirCallerAndReleaseResourcesOnStop()
    {
        await using var state = CreateService(backend);
        using var pipe = new TestPipe(15);
        using var log = new StringWriter();
        var server = pipe.ServeAsync(state, log, events.Enqueue);
        try
        {
            Assert.Contains("signed out", await pipe.SendAsync(new("status")));
            Assert.Contains("login", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pipe.SendAsync(new("create")))).Message);
            Assert.Contains("Signed in", await pipe.SendAsync(new("login", SignIn: new("test-credential"))));
            Assert.AreEqual("blue-tunnel.use2", await pipe.SendAsync(new("create")));
            using var output = new StringWriter();
            using var error = new StringWriter();
            Task<int> Run(params string[] args) => Cli.RunAsync(args, output, error, pipe.SendAsync);
            Assert.AreEqual(0, await Run("host", "blue-tunnel.use2", "--tunnel-port", "8080", "--target-port", "3000"));
            Assert.AreEqual(0, await Run(ConnectCommand));
            Assert.Contains("connect:blue-tunnel.use2:5000", output.ToString());
            Assert.Contains("host:blue-tunnel.use2", output.ToString());
            Assert.IsEmpty(error.ToString());
            Assert.AreEqual((8080, 3000), (backend.Sessions[0].Map.TunnelPort, backend.Sessions[0].Map.LocalPort));
            Assert.AreEqual((8080, 5000), (backend.Sessions[1].Map.TunnelPort, backend.Sessions[1].Map.LocalPort));
            var status = await pipe.SendAsync(new("status"));
            Assert.StartsWith("Service running; signed in.", status);
            Assert.Contains("localhost:5000 -> blue-tunnel.use2:8080", status);
            Assert.Contains("blue-tunnel.use2:8080 -> localhost:3000", status);
            Assert.DoesNotContain("test-credential", status);
            var details = JsonSerializer.Deserialize(await pipe.SendAsync(new(LocalStatus.Command)), StatusJson.Default.LocalStatus)!;
            Assert.IsTrue(details.SignedIn);
            CollectionAssert.AreEquivalent(new LocalMappingStatus[] { new(true, "blue-tunnel.use2", 8080, 3000, "Connected"),
                new(false, "blue-tunnel.use2", 8080, 5000, "Connected") }, details.Mappings);
            Assert.Contains("Stop", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pipe.SendAsync(new("delete", "blue-tunnel.use2")))).Message);
            Assert.AreEqual(0, await Run("stop", "host:blue-tunnel.use2"));
            Assert.IsTrue(backend.Sessions[0].Disposed);
            Assert.IsFalse(backend.Sessions[1].Disposed);
            Assert.AreEqual(0, await Run("logout"));
            Assert.Contains("Signed out", output.ToString());
            Assert.Contains("signed out", await pipe.SendAsync(new("status")));
            Assert.IsTrue(backend.Sessions[1].Disposed);
            await pipe.SendAsync(new("shutdown"));
            await server;
        }
        finally { await pipe.StopAsync(server); }
    }

    [TestMethod]
    public async Task FailedStartsAndInvalidMappingsAreNeverListed()
    {
        var backend = new TestBackend { Fail = true };
        await using var state = CreateService(backend);
        await SignInAsync(state, "token");
        var result = await Cli.CaptureAsync(ConnectCommand, state.ExecuteAsync);
        Assert.AreNotEqual(0, result.Code);
        Assert.Contains("permission denied", result.Error);
        await AssertTunnelAbsentAsync(state, CancellationToken.None);
        foreach (var request in new[] { new Request("host", "blue-tunnel.use2", 0, 3000), new("connect", "blue-tunnel.use2", 8080, 65536), new("connect", "bad/id.use2", 1, 2) })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => state.ExecuteAsync(request, CancellationToken.None));
        Assert.Contains("protocol", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => state.ExecuteAsync(new("status", Version: 1), CancellationToken.None))).Message);
        Assert.Contains("Unknown service command", (await Assert.ThrowsExactlyAsync<ArgumentException>(() => state.ExecuteAsync(new("unknown"), CancellationToken.None))).Message);
        AssertNoMappings(backend);
    }

    [TestMethod]
    public async Task LegacyEphemeralHostRequestCannotCreateADurableMapping()
    {
        await using var service = await SignedInServiceAsync("test-token");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.ExecuteAsync(new("host", "blue-tunnel.use2", 8080, 3000, Version: 2), CancellationToken.None));
        AssertNoMappings(backend);
    }

    private void AssertNoMappings(TestBackend backend)
    {
        Assert.IsEmpty(backend.Sessions);
        Assert.IsEmpty(mappings.Read());
    }

    [TestMethod]
    public async Task RestartRestoresBothRoutesAndDisconnectForgetsOnlyTheNamedClient()
    {
        var host = Mapping.Create(true, "blue-tunnel.use2", 8080, 3000);
        var client = Mapping.Create(false, "blue-tunnel.use2", 8080, 5000);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = limit.Token;
        var first = new TestBackend();
        await using (var service = CreateService(first))
        {
            await SignInAsync(service, "test-only-token", ct);
            await service.ExecuteAsync(new("host", host.Tunnel.ToString(), host.TunnelPort, host.LocalPort), ct);
            await service.ExecuteAsync(new("connect", client.Tunnel.ToString(), client.TunnelPort, client.LocalPort), ct);
            await service.ExecuteAsync(new("shutdown-for-update", ServicePid: Environment.ProcessId), ct);
        }
        Assert.IsTrue(first.Sessions.All(s => s.Disposed));
        var next = new TestBackend();
        await using (var service = new TunnelService(next, credential, new MappingFile(stateDirectory.FullName), events.Enqueue))
        {
            var status = await service.ExecuteAsync(new("status"), ct);
            while (status.Contains("Restoring", StringComparison.Ordinal))
            {
                await Task.Delay(10, ct);
                status = await service.ExecuteAsync(new("status"), ct);
            }
            Assert.Contains($"{host.Name}  Connected  {host.Route}", status);
            Assert.Contains($"{client.Name}  Connected  {client.Route}", status);
            CollectionAssert.AreEquivalent(new[] { host, client }, next.Sessions.Select(s => s.Map).ToArray());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("disconnect", host.Name), ct));
            Assert.AreEqual(0, await Cli.RunAsync(["disconnect", client.Name], TextWriter.Null, TextWriter.Null, service.ExecuteAsync));
            Assert.IsTrue(next.Sessions.Single(s => s.Map == client).Disposed);
            Assert.IsFalse(next.Sessions.Single(s => s.Map == host).Disposed);
        }
        CollectionAssert.AreEqual(new[] { host }, mappings.Read());
        await using var again = CreateService(new TestBackend());
        Assert.DoesNotContain(client.Name, await again.ExecuteAsync(new("status"), ct));
        await again.ExecuteAsync(new("logout"), ct);
        Assert.IsEmpty(mappings.Read());
        Assert.IsFalse(credential.IsSignedIn);
    }

    [TestMethod]
    public async Task PendingRestoreDoesNotBlockStatusAndCanBeStoppedBeforeThePeerAppears()
    {
        var map = Mapping.Create(false, "blue-tunnel.use2", 8080, 5000);
        mappings.Save([map]);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await credential.ReplaceAsync(new("test-token"), limit.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var backend = new TestBackend
        {
            Open = async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled = ct.IsCancellationRequested; }
            throw new AssertFailedException();
        }
        };
        await using var service = CreateService(backend);
        await entered.Task.WaitAsync(limit.Token);
        Assert.Contains($"{map.Name}  Restoring", await service.ExecuteAsync(new("status"), limit.Token));
        await service.ExecuteAsync(new("stop", map.Name), limit.Token).WaitAsync(limit.Token);
        Assert.IsTrue(cancelled);
        Assert.IsEmpty(mappings.Read());
        Assert.DoesNotContain(map.Name, await service.ExecuteAsync(new("status"), limit.Token));
    }

    [TestMethod]
    public async Task OfflineRestoreReportsFailureThenRetriesTheSameRoute()
    {
        var map = Mapping.Create(true, "blue-tunnel.use2", 8080, 3000);
        var relay = new TestSession(map);
        var attempts = 0;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var session = new MappingSession(map, ct =>
        {
            Assert.IsTrue(ct.CanBeCanceled);
            return ++attempts == 1 ? throw new IOException("peer offline") : Task.FromResult<ILiveTunnel>(relay);
        }, (interval, ct) =>
        {
            Assert.IsGreaterThan(TimeSpan.Zero, interval);
            waiting.SetResult();
            return retry.Task.WaitAsync(ct);
        }, events.Enqueue))
        {
            await waiting.Task.WaitAsync(limit.Token);
            Assert.Contains("Retrying (peer offline)", session.State);
            retry.SetResult();
            while (session.State != "Connected") await Task.Delay(10, limit.Token);
        }
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(relay.Disposed);
        var history = string.Join('\n', events);
        Assert.Contains("Restoring host mapping.", history);
        Assert.Contains("Retrying host mapping: IOException", history);
        Assert.Contains("Restored host mapping.", history);
        Assert.DoesNotContain("peer offline", history);
    }

    [TestMethod]
    public async Task FailedSaveDoesNotLeaveAnUnrecordedRelayOrForgetAWorkingOne()
    {
        await using var service = await SignedInServiceAsync("test-token");
        var ct = CancellationToken.None;
        await service.ExecuteAsync(Host, ct);
        await service.ExecuteAsync(Connect, ct);
        Directory.CreateDirectory(Path.Combine(stateDirectory.FullName, "mappings.json.pending"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.ExecuteAsync(new("connect", "blue-tunnel.use2", 8080, 5001), ct));
        Assert.IsTrue(backend.Sessions[2].Disposed);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.ExecuteAsync(new("disconnect", "connect:blue-tunnel.use2:5000"), ct));
        Assert.IsFalse(backend.Sessions[1].Disposed);
        CollectionAssert.AreEquivalent(backend.Sessions.Take(2).Select(s => s.Map).ToArray(), mappings.Read());
        Assert.DoesNotContain(":5001", await service.ExecuteAsync(new("status"), ct));
    }

    [TestMethod]
    public async Task LogoutStillClosesRelaysAndRemovesTheGrantWhenForgettingFails()
    {
        await using var service = await SignedInServiceAsync("old-account");
        var ct = CancellationToken.None;
        await service.ExecuteAsync(Host, ct);
        var path = Path.Combine(stateDirectory.FullName, "mappings.json");
        File.Delete(path);
        Directory.CreateDirectory(path);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.ExecuteAsync(new("logout"), ct));
        Assert.IsTrue(backend.Sessions.Single().Disposed);
        Assert.IsFalse(credential.IsSignedIn);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.ExecuteAsync(new("login", SignIn: new("new-account")), ct));
        Assert.IsFalse(credential.IsSignedIn);
    }

    [TestMethod]
    public async Task AFailedDisconnectKeepsTheRelayOwnedButNeverSavesItAgain()
    {
        await using var service = await SignedInServiceAsync("test-token");
        Task<string> Send(Request request) => service.ExecuteAsync(request, CancellationToken.None);
        await Send(Connect);
        var client = backend.Sessions.Single();
        client.FailCloseOnce = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => Send(new("disconnect", client.Map.Name)));
        Assert.Contains("Close failed (not saved)", await Send(new("status")));
        await Send(Host);
        Assert.IsTrue(mappings.Read().Single().Host);
        await Send(new("disconnect", client.Map.Name));
        Assert.IsTrue(client.Disposed);
    }

    [TestMethod]
    [DataRow("""{"version":2,"mappings":[]}""")]
    [DataRow("""{"version":1,"mappings":[null]}""")]
    [DataRow("""{"version":1,"mappings":[{"kind":"typo","tunnel":"blue-tunnel.use2","tunnelPort":8080,"localPort":3000}]}""")]
    [DataRow("""{"version":1,"mappings":[{"kind":"host","tunnel":"blue-tunnel.use2","tunnelPort":8080,"localPort":0}]}""")]
    [DataRow("""{"version":1,"mappings":[{"kind":"host","tunnel":"blue-tunnel.use2","tunnelPort":8080,"localPort":3000},{"kind":"host","tunnel":"blue-tunnel.use2","tunnelPort":8080,"localPort":3001}]}""")]
    public void InvalidSavedMappingsNeverStartAPartialService(string json)
    {
        mappings.Save([Mapping.Create(true, "blue-tunnel.use2", 8080, 3000)]);
        File.WriteAllText(Path.Combine(stateDirectory.FullName, "mappings.json"), json);
        Assert.Contains("mappings.json", Assert.ThrowsExactly<InvalidDataException>(() => mappings.Read()).Message);
    }

    [TestMethod]
    public async Task DuplicateMappingsAndAccountChangesAreRejectedUntilStopped()
    {
        await using var service = CreateService(new TestBackend());
        var ct = CancellationToken.None;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("login"), ct));
        Assert.Contains("credential", (await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("login", SignIn: new("two words")), ct))).Message);
        await SignInAsync(service, "token");
        await service.ExecuteAsync(Host, ct);
        Assert.Contains("already exists", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync(Host, ct))).Message);
        Assert.Contains("Stop active", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync(new("login", SignIn: new("other")), ct))).Message);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("stop"), ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("stop", "missing"), ct));
        await service.ExecuteAsync(new("stop", "host:blue-tunnel.use2"), ct);
        Assert.Contains("Deleted blue-tunnel.use2", await service.ExecuteAsync(new("delete", "blue-tunnel.use2"), ct));
        await AssertTunnelAbsentAsync(service, ct);
    }

    [TestMethod]
    public async Task ListShowsTheAccountsTunnelsOnlyAfterSignIn()
    {
        await using var service = CreateService(backend);
        Task<(int Code, string Output, string Error)> List() => Cli.CaptureAsync(["list"], service.ExecuteAsync);
        var anonymous = await List();
        Assert.AreEqual(1, anonymous.Code);
        Assert.Contains("Run 'login' first", anonymous.Error);
        Assert.IsEmpty(anonymous.Output);
        await SignInAsync(service, "token");
        var empty = await List();
        Assert.AreEqual(0, empty.Code);
        Assert.AreEqual("No tunnels found. Run 'create' to create one." + Environment.NewLine, empty.Output);
        backend.Tunnels.Add(new("blue-tunnel.use2", 1, null, null, [8080]));
        var result = await List();
        Assert.AreEqual(0, result.Code);
        Assert.AreEqual(string.Join(Environment.NewLine, "TUNNEL            HOSTS  CLIENTS  EXPIRES  PORTS",
            "blue-tunnel.use2  1      -        -        8080", ""), result.Output);
    }

    [TestMethod]
    public async Task RegionsAndRegionalCreatesRequireSignInAndReachTheBackend()
    {
        var backend = new TestBackend
        {
            Regions =
            {
                new("usw2", "WestUs2", TimeSpan.FromMilliseconds(203), null, null, null, false),
                new("brs", "BrazilSouth", null, null, null, null, false),
                new("euw", "WestEurope", TimeSpan.FromMilliseconds(39), null, null, null, false),
                new("eun1", "NorthEurope", TimeSpan.FromMilliseconds(39), null, null, null, false),
            }
        };
        await using var service = CreateService(backend);
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[][] remoteCommands = [["regions"], ["create", "--region", "euw"]];
        foreach (var command in remoteCommands)
        {
            error.GetStringBuilder().Clear();
            Assert.AreEqual(1, await Cli.RunAsync(command, output, error, service.ExecuteAsync));
            Assert.Contains("Run 'login' first", error.ToString());
        }
        await SignInAsync(service, "token");
        foreach (var command in remoteCommands.Append(["create"]))
            Assert.AreEqual(0, await Cli.RunAsync(command, output, error, service.ExecuteAsync));
        // Fastest first, ties by name, unmeasured last; no footer without a recommended region.
        Assert.AreEqual(string.Join(Environment.NewLine,
            "REGION  LOCATION     LATENCY  AVAILABILITY  LOAD  REASON",
            "eun1    NorthEurope  39 ms    -             -     -",
            "euw     WestEurope   39 ms    -             -     -",
            "usw2    WestUs2      203 ms   -             -     -",
            "brs     BrazilSouth  -        -             -     -",
            "blue-tunnel.euw", "blue-tunnel.use2", ""), output.ToString());
        CollectionAssert.AreEqual(new string?[] { "euw", null }, backend.CreatedRegions);
        Assert.Contains("region", (await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.ExecuteAsync(new("create-in-region"), CancellationToken.None))).Message);
    }

    [TestMethod]
    public async Task ValidPortBoundariesArePreservedWithoutNarrowingOrWrapping()
    {
        await using var service = CreateService(new TestBackend());
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
        await using var service = await SignedInServiceAsync("user-token");
        var ct = CancellationToken.None;
        Assert.AreEqual("user-token", await credential.RequireTokenAsync(ct));
        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync(new("delete", "missing-tunnel.use2"), ct));
        Assert.Contains("not found", missing.Message);
        await service.ExecuteAsync(Host, ct);
        Assert.Contains("stopping", await service.ExecuteAsync(new("shutdown"), ct));
        Assert.IsTrue(backend.Sessions.Single().Disposed);
        Assert.AreEqual("user-token", await credential.RequireTokenAsync(ct));
        await AssertTunnelAbsentAsync(service, ct);
    }

    [TestMethod]
    public async Task TrayUpdateStopsOnlyItsOriginalServiceAndPreservesSavedSignIn()
    {
        await using var state = CreateService(backend);
        using var pipe = new TestPipe(10);
        var server = pipe.ServeAsync(state, TextWriter.Null, events.Enqueue);
        try
        {
            await pipe.SendAsync(new("login", SignIn: new("test-only-token")));
            await pipe.SendAsync(Host);
            foreach (var pid in new int?[] { null, -1 })
                Assert.Contains("instance has changed", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => pipe.SendAsync(new("shutdown-for-update", ServicePid: pid)))).Message);
            Assert.IsFalse(backend.Sessions.Single().Disposed);
            Assert.Contains("host:blue-tunnel", await pipe.SendAsync(new("status")));
            var target = await pipe.SendAsync(new("service-pid"));
            Assert.AreEqual(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), target);
            await DevTunnelSvc.Updates.UpdateHandoff.StopAsync(Environment.ProcessId, pipe.SendAsync, pipe.Token);
            await server.WaitAsync(pipe.Token);
            Assert.IsTrue(backend.Sessions.Single().Disposed);
            Assert.AreEqual("test-only-token", await credential.RequireTokenAsync(pipe.Token));
        }
        finally { await pipe.StopAsync(server); }
    }

    [TestMethod]
    public async Task MalformedClientCannotStopOrPoisonTheNextControlConnection()
    {
        await using var service = CreateService(new TestBackend());
        var name = $"DevTunnelSvc-test-{Guid.NewGuid():N}";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var log = new StringWriter();
        var server = ControlServer.RunAsync(name, service.ExecuteAsync, log, events.Enqueue, ct.Token);
        try
        {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, Control.Options))
            {
                await pipe.ConnectAsync(ct.Token);
                await pipe.WriteAsync(new byte[] { 0x7f, 0xff, 0xff, 0xff }, ct.Token);
            }
            Assert.Contains("running", await Control.SendAsync(new("status"), name, ct.Token));
            Assert.Contains("InvalidDataException", log.ToString());
            Assert.Contains("Control connection failed: InvalidDataException", string.Join('\n', events));
            await Control.SendAsync(new("shutdown"), name, ct.Token);
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
    public async Task CommandHistoryRecordsOutcomesWithoutLoggingArgumentsRepliesOrUnknownInput()
    {
        await using var service = CreateService(new TestBackend());
        var ct = CancellationToken.None;
        await service.ExecuteAsync(new("login", SignIn: new("synthetic-secret")), ct);
        var connect = new Request("connect", "blue-tunnel.use2", 8080, 5000);
        await service.ExecuteAsync(connect, ct);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAsync(connect, ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ExecuteAsync(new("untrusted\ncommand"), ct));
        await service.ExecuteAsync(new("status"), ct);
        await service.ExecuteAsync(new("service-pid"), ct);
        await service.ExecuteAsync(new("disconnect", "connect:blue-tunnel.use2:5000"), ct);
        await service.ExecuteAsync(new("logout"), ct);
        var history = string.Join('\n', events);
        foreach (var op in new[] { "login", "connect", "disconnect", "logout" }) Assert.Contains($"Command {op} completed.", history);
        Assert.Contains("Control connect failed: InvalidOperationException", history);
        Assert.Contains("Control request failed: ArgumentException", history);
        foreach (var privateText in new[] { "synthetic-secret", "blue-tunnel", "5000", "8080", "untrusted", "signed in", "status", "service-pid" })
            Assert.DoesNotContain(privateText, history);
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
        public List<TunnelSummary> Tunnels { get; } = [];
        public List<TunnelRegion> Regions { get; } = [];
        public List<string?> CreatedRegions { get; } = [];
        public bool Fail { get; init; }
        public Func<Mapping, CancellationToken, Task<ILiveTunnel>>? Open { get; init; }
        public Task<string> CreateAsync(string? region, CancellationToken ct)
        {
            CreatedRegions.Add(region);
            return Task.FromResult($"blue-tunnel.{region ?? "use2"}");
        }
        public Task<IReadOnlyList<TunnelSummary>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TunnelSummary>>(Tunnels);
        public Task<IReadOnlyList<TunnelRegion>> ListRegionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TunnelRegion>>(Regions);
        public Task<bool> DeleteAsync(TunnelKey key, CancellationToken ct) => Task.FromResult(key.Id != "missing-tunnel");
        public Task<ILiveTunnel> OpenAsync(Mapping map, CancellationToken ct)
        {
            if (Open is not null) return Open(map, ct);
            if (Fail) throw new InvalidOperationException("permission denied");
            var session = new TestSession(map);
            lock (Sessions) Sessions.Add(session);
            return Task.FromResult<ILiveTunnel>(session);
        }
    }

    private sealed class TestSession(Mapping map) : ILiveTunnel
    {
        public Mapping Map => map;
        public string State => "Connected";
        public bool Disposed { get; private set; }
        public bool FailCloseOnce { get; set; }
        public ValueTask DisposeAsync()
        {
            if (FailCloseOnce) { FailCloseOnce = false; throw new IOException("Close failed."); }
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
