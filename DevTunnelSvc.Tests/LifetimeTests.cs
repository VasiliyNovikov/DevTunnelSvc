using System.IO.Pipes;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class LifetimeTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("devtunnel-lifetime-");
    public void Dispose() => directory.Delete(true);

    [TestMethod]
    public async Task StartIsIdempotentAndWaitsForTheServiceToAnswer()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Assert.AreEqual("existing", await ServiceLifetime.StartAsync(true, _ => Task.FromResult("existing"), _ => throw new AssertFailedException(), limit.Token));
        var starts = 0;
        var attempts = 0;
        var status = await ServiceLifetime.StartAsync(true, _ =>
        {
            attempts++;
            return attempts < 3 ? throw new IOException("not listening", new TimeoutException()) : Task.FromResult("ready");
        }, _ => { starts++; return Task.CompletedTask; }, limit.Token);
        Assert.AreEqual("ready", status);
        Assert.AreEqual(1, starts);
        Assert.AreEqual(3, attempts);
        async Task AssertFailedStart(IOException error, int expectedAttempts)
        {
            var probes = 0;
            await Assert.ThrowsExactlyAsync<IOException>(() => ServiceLifetime.StartAsync(false, _ => { probes++; throw error; },
                _ => Task.CompletedTask, limit.Token));
            Assert.AreEqual(expectedAttempts, probes);
        }
        await AssertFailedStart(new IOException("pipe access failed"), 1);
        await AssertFailedStart(new IOException("not listening", new TimeoutException()), 5);
    }

    [TestMethod]
    public async Task ServiceRunsAcrossCommandsAndReleasesLeaseOnShutdown()
    {
        using var pipe = new TestPipe(10);
        using var log = new StringWriter();
        using var owner = new MemoryStream();
        var run = ServiceLifetime.RunAsync(log, pipe.Name, directory.FullName, Cli.NoBackgroundUpdates, pipe.Token, (name, _, _, _) => { Assert.AreEqual(pipe.Name, name); return owner; });
        try
        {
            Assert.Contains("signed out", await pipe.SendAsync(new("status")));
            await Assert.ThrowsExactlyAsync<IOException>(() => ServiceLifetime.RunAsync(log, pipe.Name, directory.FullName, Cli.NoBackgroundUpdates, pipe.Token));
            Assert.IsTrue(owner.CanWrite);
            Assert.Contains("stopping", await pipe.SendAsync(new("shutdown")));
            await run;
            Assert.IsFalse(owner.CanWrite);
            var history = File.ReadAllText(Path.Combine(directory.FullName, "app.log"));
            Assert.Contains($"[service] Starting DevTunnelSvc {Program.Version}.", history);
            Assert.Contains("Control pipe ready.", history);
            Assert.Contains("Service failed: IOException", history);
            Assert.Contains("Service stopped.", history);
            using var lease = InstanceLease.Acquire(directory.FullName);
            Assert.IsTrue(lease.CanWrite);
        }
        finally { await pipe.StopAsync(run); }
    }

    [TestMethod]
    public async Task ServiceCancellationInterruptsAnIdleControlListener()
    {
        using var pipe = new TestPipe(10);
        var run = RunService(pipe);
        await pipe.StopAsync(run).WaitAsync(TimeSpan.FromSeconds(3));
    }

    private Task RunService(TestPipe pipe, TrayLifetime.Factory? tray = null) =>
        ServiceLifetime.RunAsync(TextWriter.Null, pipe.Name, directory.FullName, Cli.NoBackgroundUpdates, pipe.Token, tray);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SignInSurvivesServiceRecreationButLogoutSurvivesItToo(bool cancelFirstService)
    {
        using var pipe = new TestPipe(15);
        async Task RunOnce(Func<Task> commands, bool cancel = false)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(pipe.Token);
            var service = ServiceLifetime.RunAsync(TextWriter.Null, pipe.Name, directory.FullName, Cli.NoBackgroundUpdates, stop.Token);
            try
            {
                await commands();
                if (cancel) await stop.CancelAsync();
                else await pipe.SendAsync(new("shutdown"));
                await service;
            }
            finally { await stop.CancelAsync(); await service; }
        }
        await RunOnce(async () =>
        {
            Assert.Contains("signed out", await pipe.SendAsync(new("status")));
            Assert.Contains("saved", await pipe.SendAsync(new("login", SignIn: new("test-only-account"))));
        }, cancelFirstService);
        await RunOnce(async () =>
        {
            var status = await pipe.SendAsync(new("status"));
            Assert.Contains("signed in", status);
            Assert.DoesNotContain("test-only-account", status);
            Assert.Contains("removed", await pipe.SendAsync(new("logout")));
        });
        await RunOnce(async () => Assert.Contains("signed out", await pipe.SendAsync(new("status"))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TrayExitStopsTheServiceAndOnlyFailuresAreReportedAsErrors(bool fail)
    {
        using var pipe = new TestPipe(5);
        using var owner = new MemoryStream();
        Action<Exception?>? signal = null;
        var service = RunService(pipe, (_, _, _, callback) => { signal = callback; return owner; });
        Assert.IsNotNull(signal);
        signal(fail ? new IOException("Explorer unavailable") : null);
        if (fail)
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.WaitAsync(pipe.Token));
            Assert.Contains("Explorer unavailable", error.Message);
        }
        else await service.WaitAsync(pipe.Token);
        Assert.IsFalse(owner.CanWrite);
    }

    [TestMethod]
    public async Task LinuxInstallRegistersTheQuotedExecutableInTheUserManager()
    {
        var config = Directory.CreateTempSubdirectory("devtunnel-userunit-");
        try
        {
            List<string> calls = [];
            await ServiceLifetime.InstallAsync("/home/me/apps/dev tunnel", config.FullName, null, (args, _) =>
            {
                calls.Add(string.Join(' ', args));
                return Task.CompletedTask;
            }, CancellationToken.None);
            var unit = await File.ReadAllTextAsync(Path.Combine(config.FullName, "systemd", "user", "devtunnelsvc.service"));
            Assert.Contains("ExecStart=\"/home/me/apps/dev tunnel\" service run", unit);
            Assert.Contains("Restart=on-failure", unit);
            Assert.AreEqual("daemon-reload;enable devtunnelsvc.service", string.Join(';', calls));
            await Assert.ThrowsExactlyAsync<IOException>(() => ServiceLifetime.InstallLinuxAsync("/bin/new", config.FullName,
                (_, _) => throw new IOException("systemd not available"), CancellationToken.None));
        }
        finally { config.Delete(true); }
    }

    [TestMethod]
    public async Task ClientRejectsIncompatibleRepliesAndUnavailableServices()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = $"devtunnel-{Guid.NewGuid():N}";
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Control.Options))
        {
            var send = Control.SendAsync(new("status"), name, ct.Token);
            await server.WaitForConnectionAsync(ct.Token);
            _ = await Control.ReadAsync(server, ControlJson.Default.Request, ct.Token);
            await Control.WriteAsync(server, new Reply(true, "wrong protocol", Version: 1), ControlJson.Default.Reply, ct.Token);
            Assert.Contains("Incompatible", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => send)).Message);
        }
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => Control.SendAsync(new("status"), name, ct.Token));
        Assert.Contains("service start", failure.Message);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Control.WriteAsync(Stream.Null,
            new Request("login", SignIn: new(new string('x', 40000))), ControlJson.Default.Request, ct.Token));
    }

}
