using System.Diagnostics;
using System.Text.Json;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class TrayMenuTests
{
    private readonly List<TrayDialogContent> dialogs = [];
    internal static readonly TrayQueries NoQueries = new(() => false, new(_ => throw new AssertFailedException("Unexpected release check.")),
        _ => throw new AssertFailedException("Unexpected tunnel list."));

    private static TrayMenu Menu(string pipe, Action exit, Action<TrayDialogContent> show, Action<string> log, TrayQueries? queries = null,
        Func<LocalStatus, TrayStatusTarget>? open = null, Func<string, Process>? launch = null) =>
        new(pipe, exit, queries ?? NoQueries, launch ?? (_ => throw new AssertFailedException("Unexpected updater.")),
            open ?? (_ => throw new AssertFailedException("Unexpected Status window.")), show, log);

    private static Task ClickStatus(TrayMenu menu, CancellationToken ct) => menu.SelectAsync(TrayMenu.Command.Status, ct);

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
        List<string> events = [];
        var exits = 0;
        using var menu = Menu("unused", () => exits++, dialogs.Add, events.Add);
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
        using var pipe = new TestPipe(8);
        var actions = Cli.NoBackgroundUpdates with { RequireSupported = () => { }, FindNewer = _ => Task.FromResult<ReleaseInfo?>(null) };
        var server = ServiceLifetime.RunAsync(TextWriter.Null, pipe.Name, state.FullName, actions, pipe.Token);
        try
        {
            using var menu = Menu(pipe.Name, () => Assert.Fail(), _ => Assert.Fail(), _ => { });
            Task<string> Status() => pipe.SendAsync(new(AutoUpdateConsent.Status));
            foreach (var accept in new[] { false, true })
            {
                var prompted = false;
                await menu.ConfigureAutoUpdateAsync(true, question =>
                {
                    prompted = true;
                    Assert.AreEqual(AutoUpdateConsent.Question, question);
                    return accept;
                }, pipe.Token);
                Assert.IsTrue(prompted);
                Assert.AreEqual(accept ? AutoUpdateConsent.Enabled : AutoUpdateConsent.Disabled, await Status());
            }
            await menu.ConfigureAutoUpdateAsync(false, _ => throw new AssertFailedException("Unexpected confirmation."), pipe.Token);
            Assert.AreEqual(AutoUpdateConsent.Disabled, await Status());
        }
        finally { await pipe.StopAsync(server); state.Delete(true); }
    }

    [TestMethod]
    public async Task StatusShowsAFreshReportOnEachSelectionAndLeavesTheServiceRunningOnFailure()
    {
        using var pipe = new TestPipe(10);
        List<LocalStatus> local = [];
        List<string> cloud = [];
        var tunnel = new TunnelSummary("blue.use2", null, 2, null, [8080]);
        var offline = false;
        var queries = NoQueries with { ListTunnels = _ => offline ? throw new IOException("offline") : Task.FromResult<IReadOnlyList<TunnelSummary>>([tunnel]) };
        using var menu = Menu(pipe.Name, () => Assert.Fail(), dialogs.Add, _ => { }, queries, report =>
        {
            local.Add(report);
            return new((rows, error) => cloud.Add(error ?? rows.Single().Tunnel), CancellationToken.None);
        });
        using var server = pipe.Listen();
        var signedOut = JsonSerializer.Serialize(new LocalStatus(false, []), StatusJson.Default.LocalStatus);
        var active = new LocalMappingStatus(true, "blue.use2", 8080, 3000, "Connected");
        var signedIn = JsonSerializer.Serialize(new LocalStatus(true, [active]), StatusJson.Default.LocalStatus);
        foreach (var reply in new Reply[] { new(true, signedOut), new(false, "Cannot read status."), new(true, signedIn), new(true, signedIn) })
        {
            offline = cloud.Count != 0;
            var selection = ClickStatus(menu, pipe.Token);
            Assert.AreEqual(new Request(LocalStatus.Command), await pipe.ReadRequestAsync(server));
            Assert.AreSame(selection, ClickStatus(menu, pipe.Token), "A second Status click must not send another query.");
            await Control.WriteAsync(server, reply, ControlJson.Default.Reply, pipe.Token);
            await selection.WaitAsync(pipe.Token);
            server.Disconnect();
        }
        Assert.AreEqual(new TrayDialogContent("DevTunnelSvc", "Cannot read status.", true), dialogs.Single());
        Assert.IsFalse(local[0].SignedIn);
        Assert.IsTrue(local[2].SignedIn);
        Assert.AreEqual(active, local[2].Mappings.Single());
        Assert.AreEqual("blue.use2|Could not list tunnels: offline", string.Join('|', cloud));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ClosingStatusOrExitingTheTrayCancelsCloudLookupWithoutUpdatingTheClosedView(bool closeWindow)
    {
        using var pipe = new TestPipe(8);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(pipe.Token);
        using var closed = new CancellationTokenSource();
        using var server = pipe.Listen();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var queries = NoQueries with
        {
            ListTunnels = async ct =>
        {
            reading.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled = ct.IsCancellationRequested; }
            return [];
        }
        };
        var opened = false;
        using var menu = Menu(pipe.Name, shutdown.Cancel, dialogs.Add, _ => { }, queries, local =>
        {
            opened = local.SignedIn;
            return new((_, _) => Assert.Fail("Updated a closed Status window."), closed.Token);
        });
        var pending = ClickStatus(menu, shutdown.Token);
        await pipe.ReadRequestAsync(server);
        await Control.WriteAsync(server, new Reply(true, JsonSerializer.Serialize(new LocalStatus(true, []), StatusJson.Default.LocalStatus)), ControlJson.Default.Reply, pipe.Token);
        await reading.Task.WaitAsync(pipe.Token);
        Assert.IsTrue(opened, "Local status should be visible before the cloud returns.");
        await menu.SelectAsync(TrayMenu.Command.About, shutdown.Token);
        Assert.IsFalse(pending.IsCompleted);
        if (closeWindow) await closed.CancelAsync();
        else await menu.SelectAsync(TrayMenu.Command.Exit, shutdown.Token);
        await pending.WaitAsync(pipe.Token);
        Assert.IsTrue(cancelled);
        Assert.AreEqual("About DevTunnelSvc", dialogs.Single().Title);
    }

    [TestMethod]
    public async Task UpdateChecksRetainTheDiscoveredVersionAndLaunchOnlyTheSelectedReleaseOnce()
    {
        var launches = 0;
        var check = new TaskCompletionSource<ReleaseInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var queries = NoQueries with { Releases = new(_ => { calls++; return check.Task; }) };
        string? launched = null;
        using var menu = Menu("unused", () => Assert.Fail(), dialogs.Add, _ => { }, queries, launch: version =>
        {
            launched = version;
            return ++launches == 1 ? throw new InvalidOperationException("Cannot launch updater.") : Process.GetCurrentProcess();
        });
        var first = menu.SelectUpdateAsync(null, CancellationToken.None);
        Assert.AreSame(first, menu.SelectUpdateAsync(null, CancellationToken.None));
        check.SetResult(null);
        await first;
        Assert.AreEqual(1, calls);
        Assert.Contains("Already up to date", dialogs.Single().Text);
        Assert.IsNull(menu.AvailableTag);
        check = new();
        check.SetResult(new("v2.0.0", []));
        await menu.SelectUpdateAsync(null, CancellationToken.None);
        Assert.AreEqual(new TrayDialogContent("DevTunnelSvc", "Cannot launch updater.", true), dialogs[^1]);
        Assert.AreEqual("v2.0.0", menu.AvailableTag);
        Assert.IsFalse(menu.Updating);
        check = new();
        check.SetResult(new("v3.0.0", []));
        await menu.SelectUpdateAsync("v2.0.0", CancellationToken.None);
        Assert.Contains("Update to v3.0.0", dialogs[^1].Text);
        Assert.AreEqual(1, launches);
        await menu.SelectUpdateAsync(menu.AvailableTag, CancellationToken.None);
        await menu.SelectUpdateAsync(menu.AvailableTag, CancellationToken.None);
        Assert.IsTrue(menu.Updating);
        Assert.AreEqual("v3.0.0", launched);
        Assert.AreEqual(2, launches);
    }

    [TestMethod]
    public async Task OtherCommandsRemainAvailableAndExitCanCancelPendingStatus()
    {
        using var pipe = new TestPipe(5);
        using var shutdown = new CancellationTokenSource();
        using var menu = Menu(pipe.Name, shutdown.Cancel, dialogs.Add, _ => { });
        using var server = pipe.Listen();
        var selection = ClickStatus(menu, shutdown.Token);
        await pipe.ReadRequestAsync(server);
        await menu.SelectAsync(TrayMenu.Command.About, shutdown.Token);
        Assert.IsFalse(selection.IsCompleted);
        await menu.SelectAsync(TrayMenu.Command.Exit, shutdown.Token);
        Assert.IsTrue(shutdown.IsCancellationRequested);
        await selection.WaitAsync(pipe.Token);
        await ClickStatus(menu, shutdown.Token);
        Assert.AreEqual("About DevTunnelSvc", dialogs.Single().Title);
    }

    [TestMethod]
    public void UpdateWorkerIsTheSameExecutableWithTheOriginalServicePidAndNoConsole()
    {
        const string exe = @"C:\Apps with spaces\devtunnelsvc.exe";
        using var process = TrayMenu.StartWorker(exe, 123, "v2.0.0", info =>
        {
            Assert.AreEqual(exe, info.FileName);
            Assert.AreEqual("__tray-update,123,v2.0.0", string.Join(',', info.ArgumentList));
            Assert.IsTrue(info.CreateNoWindow);
            Assert.IsFalse(info.UseShellExecute);
            return Process.GetCurrentProcess();
        });
        Assert.AreEqual(Environment.ProcessId, process.Id);
        Assert.Contains("start the update helper", Assert.ThrowsExactly<InvalidOperationException>(() => TrayMenu.StartWorker(exe, 123, "v2.0.0", _ => null)).Message);
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
    [DataRow(0, 0, "start,status")]
    [DataRow(17, 0, "start,start,status")]
    [DataRow(0, 17, "start,status,start,status")]
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
        AssertCommands(expected, commands);
    }

    [TestMethod]
    [DataRow(true, "start", "start,start")]
    [DataRow(false, "status", "start,status,start,status")]
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
        AssertCommands(expected, commands);
    }

    private static void AssertCommands(string expected, List<string> commands) => CollectionAssert.AreEqual(expected.Split(','), commands);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentOrDeclinedReleaseLeavesAllMappingsAlone(bool available)
    {
        var events = new List<string>();
        var release = available ? new ReleaseInfo("v2.0.0", []) : null;
        var update = NoInstall(_ => { events.Add("check"); return Task.FromResult(release); }, text =>
        {
            events.Add("confirm");
            Assert.Contains("v2.0.0", text);
            Assert.Contains("1.0.0", text);
            Assert.Contains("stop all active port mappings", text);
            Assert.Contains("Saved mappings will reconnect", text);
            return false;
        });
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
        var update = NoInstall(_ => failCheck ? throw new HttpRequestException("offline") : Task.FromResult<ReleaseInfo?>(release),
            _ => !failCheck, _ => throw new IOException("different service"));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => update.RunAsync("1.0.0", CancellationToken.None));
        failCheck = false;
        await Assert.ThrowsExactlyAsync<IOException>(() => update.RunAsync("1.0.0", CancellationToken.None));
        using var cancel = new CancellationTokenSource();
        var cancelled = NoInstall(_ => Task.FromResult<ReleaseInfo?>(release), _ => { cancel.Cancel(); return true; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.RunAsync("1.0.0", cancel.Token));
    }

    private static TrayUpdate NoInstall(Func<CancellationToken, Task<ReleaseInfo?>> check, Func<string, bool> confirm, Func<CancellationToken, Task>? stop = null) =>
        new(check, confirm, stop ?? (_ => throw new AssertFailedException("Unexpected stop.")),
            (_, _) => throw new AssertFailedException("Unexpected install."), () => throw new AssertFailedException("Unexpected restart."));
}
