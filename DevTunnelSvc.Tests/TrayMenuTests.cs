using System.Diagnostics;
using System.IO.Pipes;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class TrayMenuTests
{
    [TestMethod]
    public async Task StatusProbeDiscardsLargeOutputAndReturnsTheInstalledClisExitCode()
    {
        var probe = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('x' * 262144); exit 7"])
            : new ProcessStartInfo("/bin/sh", ["-c", "head -c 262144 /dev/zero; exit 7"]);
        using var sink = File.OpenNullHandle();
        probe.StandardOutputHandle = sink;
        probe.CreateNoWindow = true;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.AreEqual(7, await ServiceLifetime.ExitCodeAsync(probe, limit.Token));
    }

    [TestMethod]
    public async Task MenuDispatchesAboutAndExit()
    {
        List<TrayDialogContent> dialogs = [];
        List<string> events = [];
        var exits = 0;
        using var menu = new TrayMenu("unused", () => exits++, () => throw new AssertFailedException(), dialogs.Add, events.Add);
        await menu.SelectAsync(TrayMenu.Command.About, CancellationToken.None);
        var about = dialogs.Single();
        Assert.AreEqual("About DevTunnelSvc", about.Title);
        Assert.Contains(Program.Version, about.Text);
        Assert.Contains("Microsoft Dev Tunnels", about.Text);
        Assert.IsFalse(about.Scrollable);
        await menu.SelectAsync(TrayMenu.Command.Exit, CancellationToken.None);
        Assert.AreEqual(1, exits);
        Assert.AreEqual("Tray menu: About.,Tray menu: Exit.", string.Join(',', events));
    }

    [TestMethod]
    public async Task TrayRequiresTheSameConsentAsTheCliAndOptOutNeedsNoDialog()
    {
        var state = Directory.CreateTempSubdirectory("devtunnel-tray-consent-");
        var pipe = $"devtunnel-auto-tray-{Guid.NewGuid():N}";
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var actions = Cli.NoBackgroundUpdates with { RequireSupported = () => { }, FindNewer = _ => Task.FromResult<ReleaseInfo?>(null) };
        var server = ServiceLifetime.RunAsync(TextWriter.Null, pipe, state.FullName, actions, limit.Token);
        try
        {
            using var menu = new TrayMenu(pipe, () => Assert.Fail(), () => throw new AssertFailedException(), _ => Assert.Fail(), _ => { });
            Task<string> Status() => Control.SendAsync(new(AutoUpdateConsent.Status), pipe, limit.Token);
            foreach (var accept in new[] { false, true })
            {
                var prompted = false;
                await menu.ConfigureAutoUpdateAsync(true, question =>
                {
                    prompted = true;
                    Assert.AreEqual(AutoUpdateConsent.Question, question);
                    return accept;
                }, limit.Token);
                Assert.IsTrue(prompted);
                Assert.AreEqual(accept ? AutoUpdateConsent.Enabled : AutoUpdateConsent.Disabled, await Status());
            }
            await menu.ConfigureAutoUpdateAsync(false, _ => throw new AssertFailedException("Unexpected confirmation."), limit.Token);
            Assert.AreEqual(AutoUpdateConsent.Disabled, await Status());
        }
        finally { await limit.CancelAsync(); await server; state.Delete(true); }
    }

    [TestMethod]
    public async Task StatusShowsAFreshReportOnEachSelectionAndLeavesTheServiceRunningOnFailure()
    {
        var pipe = $"devtunnel-tray-{Guid.NewGuid():N}";
        List<TrayDialogContent> dialogs = [];
        using var menu = new TrayMenu(pipe, () => Assert.Fail(), () => throw new AssertFailedException(), dialogs.Add, _ => { });
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Control.Options);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string signedOut = "Service running; signed out.";
        const string connected = "Service running; signed in.\r\nhost:demo.use2  Connected  demo.use2:8080 -> localhost:3000";
        foreach (var reply in new Reply[] { new(true, signedOut), new(false, "Cannot read status."), new(true, connected) })
        {
            var selection = menu.SelectAsync(TrayMenu.Command.Status, limit.Token);
            await server.WaitForConnectionAsync(limit.Token);
            Assert.AreSame(selection, menu.SelectAsync(TrayMenu.Command.Status, limit.Token), "A second Status click must not send another query.");
            Assert.AreEqual(new Request("status"), await Control.ReadAsync(server, ControlJson.Default.Request, limit.Token));
            await Control.WriteAsync(server, reply, ControlJson.Default.Reply, limit.Token);
            await selection.WaitAsync(limit.Token);
            server.Disconnect();
        }
        CollectionAssert.AreEqual(new TrayDialogContent[] { new("DevTunnelSvc Status", signedOut, true), new("DevTunnelSvc", "Cannot read status.", true),
            new("DevTunnelSvc Status", connected, true) }, dialogs);
    }

    [TestMethod]
    public async Task UpdateOnlyLaunchesOneWorkerAndMenuErrorsDoNotExitTheService()
    {
        List<TrayDialogContent> dialogs = [];
        var launches = 0;
        using var menu = new TrayMenu("unused", () => Assert.Fail(), () =>
            ++launches == 1 ? throw new InvalidOperationException("Cannot launch updater.") : Process.GetCurrentProcess(), dialogs.Add, _ => { });
        await menu.SelectAsync(TrayMenu.Command.Update, CancellationToken.None);
        Assert.AreEqual(new TrayDialogContent("DevTunnelSvc", "Cannot launch updater.", true), dialogs.Single());
        Assert.IsFalse(menu.Updating);
        await menu.SelectAsync(TrayMenu.Command.Update, CancellationToken.None);
        await menu.SelectAsync(TrayMenu.Command.Update, CancellationToken.None);
        Assert.IsTrue(menu.Updating);
        Assert.AreEqual(2, launches);
    }

    [TestMethod]
    public async Task OtherCommandsRemainAvailableAndExitCanCancelPendingStatus()
    {
        var pipe = $"devtunnel-tray-{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource();
        List<TrayDialogContent> dialogs = [];
        using var menu = new TrayMenu(pipe, shutdown.Cancel, () => throw new AssertFailedException(), dialogs.Add, _ => { });
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Control.Options);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selection = menu.SelectAsync(TrayMenu.Command.Status, shutdown.Token);
        await server.WaitForConnectionAsync(limit.Token);
        _ = await Control.ReadAsync(server, ControlJson.Default.Request, limit.Token);
        await menu.SelectAsync(TrayMenu.Command.About, shutdown.Token);
        Assert.IsFalse(selection.IsCompleted);
        await menu.SelectAsync(TrayMenu.Command.Exit, shutdown.Token);
        Assert.IsTrue(shutdown.IsCancellationRequested);
        await selection.WaitAsync(limit.Token);
        await menu.SelectAsync(TrayMenu.Command.Status, shutdown.Token);
        Assert.AreEqual("About DevTunnelSvc", dialogs.Single().Title);
    }

    [TestMethod]
    public void UpdateWorkerIsTheSameExecutableWithTheOriginalServicePidAndNoConsole()
    {
        const string exe = @"C:\Apps with spaces\devtunnelsvc.exe";
        using var process = TrayMenu.StartWorker(exe, 123, info =>
        {
            Assert.AreEqual(exe, info.FileName);
            Assert.AreEqual("__tray-update,123", string.Join(',', info.ArgumentList));
            Assert.IsTrue(info.CreateNoWindow);
            Assert.IsFalse(info.UseShellExecute);
            return Process.GetCurrentProcess();
        });
        Assert.AreEqual(Environment.ProcessId, process.Id);
        Assert.Contains("start the update helper", Assert.ThrowsExactly<InvalidOperationException>(() => TrayMenu.StartWorker(exe, 123, _ => null)).Message);
    }

    [TestMethod]
    public async Task OnlyCompletedUpdatesAndFailuresShowWorkerFeedback()
    {
        List<string> info = [], errors = [];
        Assert.AreEqual(0, await TrayUpdate.WithFeedbackAsync(() => Task.FromResult<string?>(null), info.Add, errors.Add));
        Assert.IsEmpty(info);
        Assert.AreEqual(0, await TrayUpdate.WithFeedbackAsync(() => Task.FromResult<string?>("already current"), info.Add, errors.Add));
        Assert.AreEqual(1, await TrayUpdate.WithFeedbackAsync(() => throw new IOException("offline"), info.Add, errors.Add));
        Assert.AreEqual("already current", info.Single());
        Assert.AreEqual("offline", errors.Single());
    }

    [TestMethod]
    public async Task UpdateWaitsForTheStoppedProcessToExitBeforeTakingTheLease()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = TrayUpdate.WaitForExitAsync(token => { Assert.IsTrue(token.CanBeCanceled); waiting.SetResult(); return exit.Task.WaitAsync(token); });
        await waiting.Task.WaitAsync(limit.Token);
        Assert.IsFalse(stop.IsCompleted);
        exit.SetResult();
        await stop.WaitAsync(limit.Token);
    }

    [TestMethod]
    [DataRow(0, 0, "service start,status")]
    [DataRow(17, 0, "service start,service start,status")]
    [DataRow(0, 17, "service start,status,service start,status")]
    public async Task StartVerifiesReadinessAfterTheInstalledHelperExits(int firstStart, int firstStatus, string expected)
    {
        const string installed = @"C:\Apps with spaces\devtunnelsvc.exe";
        List<string> commands = [];
        Task<int> Start(ProcessStartInfo info, CancellationToken ct)
        {
            Assert.AreEqual(installed, info.FileName);
            var command = string.Join(' ', info.ArgumentList);
            var first = !commands.Contains(command);
            commands.Add(command);
            Assert.IsTrue(info.CreateNoWindow);
            Assert.IsTrue(ct.CanBeCanceled);
            return Task.FromResult(first ? (command == "status" ? firstStatus : firstStart) : 0);
        }
        await UpdateHandoff.StartInstalledAsync(installed, Start);
        Assert.AreEqual(expected, string.Join(',', commands));
    }

    [TestMethod]
    [DataRow(true, "service start", "service start,service start")]
    [DataRow(false, "status", "service start,status,service start,status")]
    public async Task FailedRestartReportsWhetherTheStarterOrTheIndependentProbeFailed(bool failStart, string failedCommand, string expected)
    {
        List<string> commands = [];
        Task<int> Run(ProcessStartInfo info, CancellationToken _)
        {
            var command = string.Join(' ', info.ArgumentList);
            commands.Add(command);
            return Task.FromResult(failStart || command == "status" ? 29 : 0);
        }
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => UpdateHandoff.StartInstalledAsync("installed", Run));
        Assert.Contains($"'{failedCommand}' command exited with code 29", failure.Message);
        Assert.AreEqual(expected, string.Join(',', commands));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentOrDeclinedReleaseLeavesAllMappingsAlone(bool available)
    {
        var events = new List<string>();
        var release = available ? new ReleaseInfo("v2.0.0", []) : null;
        var update = new TrayUpdate(_ => { events.Add("check"); return Task.FromResult(release); }, text =>
        {
            events.Add("confirm");
            Assert.Contains("v2.0.0", text);
            Assert.Contains("1.0.0", text);
            Assert.Contains("stop all active port mappings", text);
            Assert.Contains("Saved mappings will reconnect", text);
            return false;
        }, _ => throw new AssertFailedException(), (_, _) => throw new AssertFailedException(), () => throw new AssertFailedException());
        var message = await update.RunAsync("1.0.0", CancellationToken.None);
        if (available)
        {
            Assert.IsNull(message);
            Assert.AreEqual("check,confirm", string.Join(',', events));
        }
        else
        {
            Assert.Contains("up to date (1.0.0)", message!);
            Assert.AreEqual("check", events.Single());
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ConfirmedUpdateStopsInstallsThatReleaseAndRestartsEvenOnInstallFailure(bool badInstall, bool badRestart)
    {
        var release = new ReleaseInfo("v2.3.4", []);
        var events = new List<string>();
        var update = new TrayUpdate(_ => { events.Add("check"); return Task.FromResult<ReleaseInfo?>(release); },
            text => { Assert.Contains(release.TagName, text); events.Add("confirm"); return true; },
            _ => { events.Add("stop"); return Task.CompletedTask; }, (confirmed, ct) =>
            {
                Assert.AreSame(release, confirmed);
                Assert.IsTrue(ct.CanBeCanceled);
                events.Add("install");
                return badInstall ? throw new InvalidDataException("digest mismatch") : Task.CompletedTask;
            }, () =>
            {
                events.Add("start");
                return badRestart ? throw new IOException("start failed") : Task.CompletedTask;
            });
        if (badInstall || badRestart)
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => update.RunAsync("1.0.0", CancellationToken.None));
            Assert.Contains(badInstall ? "digest mismatch" : "Installed v2.3.4", failure.Message);
            Assert.Contains(badRestart ? "start failed" : "service was restarted", failure.Message);
        }
        else Assert.Contains("Installed v2.3.4 and restarted", (await update.RunAsync("1.0.0", CancellationToken.None))!);
        Assert.AreEqual("check,confirm,stop,install,start", string.Join(',', events));
    }

    [TestMethod]
    public async Task CheckOrShutdownFailureNeverStartsAnInstall()
    {
        var release = new ReleaseInfo("v2.0.0", []);
        var failCheck = true;
        var update = new TrayUpdate(_ => failCheck ? throw new HttpRequestException("offline") : Task.FromResult<ReleaseInfo?>(release),
            _ => !failCheck, _ => throw new IOException("different service"), (_, _) => throw new AssertFailedException(), () => throw new AssertFailedException());
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => update.RunAsync("1.0.0", CancellationToken.None));
        failCheck = false;
        await Assert.ThrowsExactlyAsync<IOException>(() => update.RunAsync("1.0.0", CancellationToken.None));
        using var cancel = new CancellationTokenSource();
        var cancelled = new TrayUpdate(_ => Task.FromResult<ReleaseInfo?>(release), _ => { cancel.Cancel(); return true; },
            _ => throw new AssertFailedException(), (_, _) => throw new AssertFailedException(), () => throw new AssertFailedException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.RunAsync("1.0.0", cancel.Token));
    }

}
