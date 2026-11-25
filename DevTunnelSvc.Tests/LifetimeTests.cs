using System.Collections.Concurrent;
using System.IO.Pipes;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class LifetimeTests
{
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
        attempts = 0;
        await Assert.ThrowsExactlyAsync<IOException>(() => ServiceLifetime.StartAsync(false, _ => { attempts++; throw new IOException("pipe access failed"); },
            _ => Task.CompletedTask, limit.Token));
        Assert.AreEqual(1, attempts);
        attempts = 0;
        await Assert.ThrowsExactlyAsync<IOException>(() => ServiceLifetime.StartAsync(false, _ =>
        { attempts++; throw new IOException("not listening", new TimeoutException()); }, _ => Task.CompletedTask, limit.Token));
        Assert.AreEqual(5, attempts);
    }

    [TestMethod]
    public async Task ServiceRunsAcrossCommandsAndReleasesLeaseOnShutdown()
    {
        var directory = Directory.CreateTempSubdirectory("devtunnel-run-");
        var name = $"devtunnel-{Guid.NewGuid():N}";
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var log = new StringWriter();
        using var owner = new MemoryStream();
        var run = ServiceLifetime.RunAsync(log, name, directory.FullName, limit.Token, _ => owner);
        try
        {
            Assert.Contains("signed out", await Control.SendAsync(new("status"), name, limit.Token));
            await Assert.ThrowsExactlyAsync<IOException>(() => ServiceLifetime.RunAsync(log, name, directory.FullName, limit.Token));
            Assert.IsTrue(owner.CanWrite);
            Assert.Contains("stopping", await Control.SendAsync(new("shutdown"), name, limit.Token));
            await run;
            Assert.IsFalse(owner.CanWrite);
            using var lease = InstanceLease.Acquire(directory.FullName);
            Assert.IsTrue(lease.CanWrite);
        }
        finally { await limit.CancelAsync(); await run; directory.Delete(true); }
    }

    [TestMethod]
    public async Task ServiceCancellationInterruptsAnIdleControlListener()
    {
        var directory = Directory.CreateTempSubdirectory("devtunnel-cancel-");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = ServiceLifetime.RunAsync(TextWriter.Null, $"devtunnel-{Guid.NewGuid():N}", directory.FullName, stop.Token);
        try { await stop.CancelAsync(); await run.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SignInSurvivesServiceRecreationButLogoutSurvivesItToo(bool cancelFirstService)
    {
        var directory = Directory.CreateTempSubdirectory("devtunnel-restart-");
        var name = $"devtunnel-{Guid.NewGuid():N}";
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = limit.Token;
        async Task RunOnce(Func<Task> commands, bool cancel = false)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var service = ServiceLifetime.RunAsync(TextWriter.Null, name, directory.FullName, stop.Token);
            try
            {
                await commands();
                if (cancel) await stop.CancelAsync();
                else await Control.SendAsync(new("shutdown"), name, ct);
                await service;
            }
            finally { await stop.CancelAsync(); await service; }
        }
        try
        {
            await RunOnce(async () =>
            {
                Assert.Contains("signed out", await Control.SendAsync(new("status"), name, ct));
                Assert.Contains("saved", await Control.SendAsync(new("login", SignIn: new("test-only-account")), name, ct));
            }, cancelFirstService);
            await RunOnce(async () =>
            {
                var status = await Control.SendAsync(new("status"), name, ct);
                Assert.Contains("signed in", status);
                Assert.DoesNotContain("test-only-account", status);
                Assert.Contains("removed", await Control.SendAsync(new("logout"), name, ct));
            });
            await RunOnce(async () => Assert.Contains("signed out", await Control.SendAsync(new("status"), name, ct)));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TrayExitStopsTheServiceAndOnlyFailuresAreReportedAsErrors(bool fail)
    {
        var directory = Directory.CreateTempSubdirectory("devtunnel-tray-error-");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var owner = new MemoryStream();
        Action<Exception?>? signal = null;
        try
        {
            var service = ServiceLifetime.RunAsync(TextWriter.Null, $"devtunnel-{Guid.NewGuid():N}", directory.FullName, limit.Token,
                callback => { signal = callback; return owner; });
            Assert.IsNotNull(signal);
            signal(fail ? new IOException("Explorer unavailable") : null);
            if (fail)
            {
                var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.WaitAsync(limit.Token));
                Assert.Contains("Explorer unavailable", error.Message);
            }
            else await service.WaitAsync(limit.Token);
            Assert.IsFalse(owner.CanWrite);
        }
        finally { directory.Delete(true); }
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
    public void TrayShowsAndCleansUpOnOwnerThread()
    {
        using var window = new Window();
        var closed = false;
        using (var tray = new TrayIcon(() => window, ex => { Assert.IsNull(ex); closed = true; }))
        {
            Assert.AreEqual(1, window.Shows);
            Assert.IsTrue(window.Running.Wait(TimeSpan.FromSeconds(2)));
        }
        Assert.AreEqual(1, window.Shows);
        Assert.IsTrue(window.DisposedOnOwner);
        Assert.IsTrue(window.CloseRequested);
        Assert.IsTrue(closed);
    }

    [TestMethod]
    public void TrayCreationFailureAndRuntimeFailureReachTheService()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new TrayIcon(() => throw new InvalidOperationException("no session"), _ => { }));
        using var window = new Window();
        using var failed = new ManualResetEventSlim();
        using var tray = new TrayIcon(() => window, ex => { if (ex is IOException) failed.Set(); });
        window.Events.Add(true);
        Assert.IsTrue(failed.Wait(TimeSpan.FromSeconds(2)));
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

    private sealed class Window : TrayIcon.IWindow
    {
        internal BlockingCollection<bool> Events { get; } = new();
        internal ManualResetEventSlim Running { get; } = new();
        internal int Shows { get; private set; }
        internal bool DisposedOnOwner { get; private set; }
        internal bool CloseRequested { get; private set; }
        private int owner;

        public void Show()
        {
            owner = Environment.CurrentManagedThreadId;
            Shows++;
        }
        public void Run()
        {
            Running.Set();
            if (Events.TryTake(out var fail, 3000) && fail) throw new IOException("no taskbar");
        }
        public void Close() { CloseRequested = true; Events.Add(false); }
        public void Dispose()
        {
            if (owner == Environment.CurrentManagedThreadId) DisposedOnOwner = true;
        }
    }
}
