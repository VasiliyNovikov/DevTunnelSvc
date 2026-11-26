using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class TrayMenuTests
{
    [TestMethod]
    public void MenuDispatchesAboutAndExitButNotDismissal()
    {
        var stub = new PopupStub();
        using var menu = stub.Menu;
        stub.Select = TrayMenu.Command.None;
        menu.Show(11, 12);
        Assert.AreEqual("Status,Update,About,---,Exit", string.Join(',', stub.Labels));
        Assert.AreEqual("theme,create,foreground,track,post,destroy", string.Join(',', stub.Tracking));
        Assert.AreEqual(0u, stub.UpdateFlags);
        Assert.IsEmpty(stub.Actions);
        stub.Select = TrayMenu.Command.About;
        menu.Show(11, 12);
        Assert.StartsWith("About DevTunnelSvc:DevTunnelSvc", stub.Actions.Single());
        Assert.Contains(Program.Version, stub.Actions.Single());
        Assert.Contains("Microsoft Dev Tunnels", stub.Actions.Single());
        stub.Select = TrayMenu.Command.Exit;
        menu.Show(11, 12);
        Assert.AreEqual("exit", stub.Actions.Last());
        Assert.AreEqual(3, stub.Destroyed);
    }

    [TestMethod]
    public async Task StatusShowsAFreshReportOnEachSelectionAndLeavesTheServiceRunningOnFailure()
    {
        var stub = new PopupStub { Select = TrayMenu.Command.Status };
        using var menu = stub.Menu;
        using var server = new NamedPipeServerStream(stub.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Control.Options);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string signedOut = "Service running; signed out.";
        const string connected = "Service running; signed in.\r\nhost:demo.use2  Connected  demo.use2:8080 -> localhost:3000";
        foreach (var reply in new Reply[] { new(true, signedOut), new(false, "Cannot read status."), new(true, connected) })
        {
            var selection = Task.Run(() => menu.Show(11, 12), limit.Token);
            await server.WaitForConnectionAsync(limit.Token);
            Assert.AreEqual(new Request("status"), await Control.ReadAsync(server, ControlJson.Default.Request, limit.Token));
            await Control.WriteAsync(server, reply, ControlJson.Default.Reply, limit.Token);
            await selection.WaitAsync(limit.Token);
            server.Disconnect();
        }
        CollectionAssert.AreEqual(new[] { $"DevTunnelSvc Status:{signedOut}", "error:Cannot read status.", $"DevTunnelSvc Status:{connected}" }, stub.Actions);
    }

    [TestMethod]
    public void UpdateOnlyLaunchesOneWorkerAndMenuErrorsDoNotExitTheService()
    {
        var stub = new PopupStub { Select = TrayMenu.Command.Update, LaunchFails = true };
        using var menu = stub.Menu;
        menu.Show(11, 12);
        Assert.AreEqual("error:Cannot launch updater.", stub.Actions.Last());
        stub.LaunchFails = false;
        menu.Show(11, 12);
        menu.Show(11, 12);
        Assert.AreEqual("error:Cannot launch updater.,update", string.Join(',', stub.Actions));
        Assert.AreEqual(1u, stub.UpdateFlags);

        stub.Select = TrayMenu.Command.Exit;
        stub.BrokenItem = "About";
        menu.Show(11, 12);
        Assert.StartsWith("error:", stub.Actions.Last());
        Assert.DoesNotContain("exit", stub.Actions);
        Assert.AreEqual(4, stub.Destroyed);
    }

    [TestMethod]
    public void ReentrantShellNotificationsCannotOpenAnotherPopup()
    {
        var stub = new PopupStub { Select = TrayMenu.Command.About };
        using var menu = stub.Menu;
        stub.WhileTracking = () => menu.Show(11, 12);
        menu.Show(11, 12);
        Assert.HasCount(1, stub.Actions);
        Assert.AreEqual(1, stub.Destroyed);
        stub.CannotCreate = true;
        menu.Show(11, 12);
        Assert.StartsWith("error:", stub.Actions.Last());
        Assert.AreEqual(1, stub.Destroyed);
    }

    [TestMethod]
    public void PopupAnchorsToTheIconEvenOnANegativeCoordinateMonitor()
    {
        var result = 0;
        int Bounds(ref TrayMenu.IconIdentifier icon, out TrayMenu.Rect rect)
        {
            Assert.AreEqual((uint)Marshal.SizeOf<TrayMenu.IconIdentifier>(), icon.Size);
            Assert.AreEqual((nint)23, icon.Window);
            Assert.AreEqual(1u, icon.Id);
            rect = new() { Left = -160, Right = -140, Top = -24, Bottom = 0 };
            return result;
        }
        var cursorWorks = true;
        bool Cursor(out TrayMenu.Point point) { point = new() { X = 100, Y = 200 }; return cursorWorks; }
        var anchor = TrayMenu.IconAnchor(23, Bounds, Cursor);
        Assert.AreEqual((-150, -24), (anchor.X, anchor.Y));
        result = -1;
        anchor = TrayMenu.IconAnchor(23, Bounds, Cursor);
        Assert.AreEqual((100, 200), (anchor.X, anchor.Y));
        cursorWorks = false;
        Assert.ThrowsExactly<Win32Exception>(() => TrayMenu.IconAnchor(23, Bounds, Cursor));
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
    public async Task DialogsRequireAnExplicitYesAndReportWorkerOutcomes()
    {
        List<(string Title, string Text, uint Flags)> shown = [];
        var answer = 7; // IDNO
        var dialogs = new TrayMenu.Dialogs((owner, message, caption, flags) =>
        { Assert.AreEqual((nint)0, owner); shown.Add((caption, message, flags)); return answer; });
        Assert.IsFalse(dialogs.Confirm("Stop mappings?"));
        Assert.AreEqual(("DevTunnelSvc Update", "Stop mappings?", 0x10134u), shown.Single());
        answer = 6;
        Assert.IsTrue(dialogs.Confirm("Stop mappings?"));
        answer = 0;
        Assert.ThrowsExactly<Win32Exception>(() => dialogs.Confirm("Stop mappings?"));
        answer = 1;
        shown.Clear();
        Assert.AreEqual(0, await TrayUpdate.WithFeedbackAsync(() => Task.FromResult<string?>(null), dialogs));
        Assert.IsEmpty(shown);
        Assert.AreEqual(0, await TrayUpdate.WithFeedbackAsync(() => Task.FromResult<string?>("already current"), dialogs));
        Assert.AreEqual(("DevTunnelSvc Update", "already current", 0x10040u), shown.Single());
        Assert.AreEqual(1, await TrayUpdate.WithFeedbackAsync(() => throw new IOException("offline"), dialogs));
        Assert.AreEqual(("DevTunnelSvc", "offline", 0x10010u), shown.Last());
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
    public async Task StartUsesTheInstalledCliAndPropagatesItsReadinessResult()
    {
        const string installed = @"C:\Apps with spaces\devtunnelsvc.exe";
        var result = 0;
        Task<int> Start(ProcessStartInfo info, CancellationToken ct)
        {
            Assert.AreEqual(installed, info.FileName);
            Assert.AreEqual("service,start", string.Join(',', info.ArgumentList));
            Assert.IsTrue(info.CreateNoWindow);
            Assert.IsTrue(ct.CanBeCanceled);
            return Task.FromResult(result);
        }
        await UpdateHandoff.StartInstalledAsync(installed, Start);
        result = 17;
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => UpdateHandoff.StartInstalledAsync(installed, Start));
        Assert.Contains("service start", failure.Message);
        Assert.Contains("17", failure.Message);
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
            Assert.Contains("started again", text);
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

    [TestMethod]
    public void WindowsDispatchesTheShellCallbackToARealMenuOnTheIconOwnerThread()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Inspect the native HMENU instead of displaying it or changing the user's foreground window.
        nint owner = 0;
        var exits = 0;
        var popup = TrayMenu.SystemPopup with
        {
            Anchor = _ => default,
            Foreground = _ => true,
            TrackAt = (menu, _, _, _, hwnd, _) =>
            {
                Assert.AreEqual(owner, hwnd);
                Assert.AreEqual(5, GetMenuItemCount(menu));
                Assert.AreEqual((uint)TrayMenu.Command.Status, GetMenuItemID(menu, 0));
                Assert.AreEqual((uint)TrayMenu.Command.Update, GetMenuItemID(menu, 1));
                Assert.AreEqual((uint)TrayMenu.Command.About, GetMenuItemID(menu, 2));
                Assert.AreEqual((uint)TrayMenu.Command.Exit, GetMenuItemID(menu, 4));
                return (uint)TrayMenu.Command.Exit;
            },
        };
        var actions = new TrayMenu(popup, () => exits++, () => throw new AssertFailedException(),
            new((_, text, _, _) => throw new AssertFailedException(text)), string.Empty, (_, _, _) => Assert.Fail());
        using var window = TrayIcon.OpenWindowsWindow(actions);
        owner = window.Handle;
        Assert.IsTrue(TrayIcon.PostMessageW(owner, TrayIcon.CallbackMessage, 0, (1 << 16) | 0x7B));
        Assert.IsTrue(TrayIcon.PostMessageW(owner, TrayIcon.CloseMessage, 0, 0));
        window.Run();
        Assert.AreEqual(1, exits);
    }

    [DllImport("user32", ExactSpelling = true)] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32", ExactSpelling = true)] private static extern uint GetMenuItemID(nint menu, int position);

    private sealed class PopupStub
    {
        public string PipeName { get; } = $"devtunnel-tray-{Guid.NewGuid():N}";
        public TrayMenu.Command Select { get; set; }
        public bool LaunchFails { get; set; }
        public string? BrokenItem { get; set; }
        public bool CannotCreate { get; set; }
        public Action? WhileTracking { get; set; }
        public List<string> Labels { get; } = [];
        public List<string> Actions { get; } = [];
        public List<string> Tracking { get; } = [];
        public int Destroyed { get; private set; }
        public uint UpdateFlags { get; private set; }

        public TrayMenu Menu => new(new(() => Tracking.Add("theme"), () => { Labels.Clear(); Tracking.Add("create"); return CannotCreate ? 0 : 77; }, Add, owner =>
        {
            Assert.AreEqual((nint)11, owner);
            return new() { X = -30, Y = 240 };
        }, owner => { Assert.AreEqual((nint)11, owner); Tracking.Add("foreground"); return true; }, (m, flags, x, y, owner, parameters) =>
        {
            Tracking.Add("track");
            Assert.AreEqual((-30, 240), (x, y));
            Assert.AreEqual(0x182u, flags);
            Assert.AreEqual((nint)77, m);
            Assert.AreEqual((nint)11, owner);
            var reenter = WhileTracking;
            WhileTracking = null;
            reenter?.Invoke();
            return (uint)Select;
        }, (owner, msg, w, l) =>
        {
            Assert.AreEqual((nint)11, owner);
            Assert.AreEqual(0u, msg);
            Tracking.Add("post");
            return true;
        }, m => { Assert.AreEqual((nint)77, m); Tracking.Add("destroy"); Destroyed++; return true; }), () => Actions.Add("exit"), () =>
        {
            if (LaunchFails) throw new InvalidOperationException("Cannot launch updater.");
            Actions.Add("update");
            return Process.GetCurrentProcess(); // A live process handle; the menu must never terminate it.
        }, new((owner, text, title, flags) =>
        {
            Assert.AreEqual((nint)11, owner);
            Actions.Add((flags & 0x10) != 0 ? $"error:{text}" : $"{title}: {text}");
            return 1;
        }), PipeName, (owner, icon, content) =>
        {
            Assert.AreEqual(((nint)11, (nint)12), (owner, icon));
            Assert.AreEqual(Select == TrayMenu.Command.Status, content.Scrollable);
            Actions.Add($"{content.Title}:{content.Text}");
        });

        private bool Add(nint m, uint flags, nuint id, string? text)
        {
            Assert.AreEqual((nint)77, m);
            var label = text?.Replace("&", "", StringComparison.Ordinal) ?? "---";
            Labels.Add(label);
            if (label == "Update") UpdateFlags = flags;
            else Assert.AreEqual(label == "---" ? 0x800u : 0u, flags);
            if (id != 0) Assert.AreEqual(((TrayMenu.Command)id).ToString(), label);
            return label != BrokenItem;
        }
    }
}
