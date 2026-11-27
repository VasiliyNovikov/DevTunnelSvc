#if WINDOWS
using System.Drawing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DevTunnelSvc.Platform;
using ControlPipe = DevTunnelSvc.Service.Control;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class WinFormsTrayTests
{
    [TestMethod]
    public async Task TrayMenuExitAndServiceShutdownBothDisposeTheIconOnItsStaThread()
    {
        foreach (var clickExit in new[] { true, false })
        {
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposedOnOwner = false;
            using (var tray = new TrayIcon("unused", _ => { }, () => false, e => stopped.SetResult(e), icon =>
            {
                var owner = Environment.CurrentManagedThreadId;
                Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.IsNotNull(icon.Icon);
                var popup = icon.ContextMenuStrip!;
                Assert.AreEqual("&Status,&Update,Auto-up&date,&About,,E&xit", string.Join(',', popup.Items.Cast<ToolStripItem>().Select(item => item.Text)));
                icon.Disposed += (_, _) => disposedOnOwner = Environment.CurrentManagedThreadId == owner;
                if (clickExit) SynchronizationContext.Current!.Post(_ => popup.Items[5].PerformClick(), null);
            }))
            {
                if (clickExit) Assert.IsNull(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.IsTrue(disposedOnOwner);
            Assert.IsNull(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task TrayFailuresBeforeAndAfterReadinessReachTheService()
    {
        var failure = new IOException("Explorer unavailable");
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() =>
            new TrayIcon("unused", _ => { }, () => false, _ => Assert.Fail(), _ => throw failure)));
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tray = new TrayIcon("unused", _ => { }, () => false, e => stopped.SetResult(e),
            _ => SynchronizationContext.Current!.Post(_ => throw failure, null));
        Assert.AreSame(failure, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ServiceStopAndTrayExitInterruptAStatusClickWithoutBlockingTheMenu(bool clickExit)
    {
        var pipe = $"devtunnel-forms-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, ControlPipe.Options);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new TaskCompletionSource<(SynchronizationContext Thread, ContextMenuStrip Menu)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var tray = new TrayIcon(pipe, _ => { }, () => false, e => stopped.SetResult(e), icon =>
        {
            var popup = icon.ContextMenuStrip!;
            var thread = SynchronizationContext.Current!;
            ui.SetResult((thread, popup));
            thread.Post(_ => popup.Items[0].PerformClick(), null);
        }))
        {
            await server.WaitForConnectionAsync(limit.Token);
            Assert.AreEqual("status", (await ControlPipe.ReadAsync(server, Service.ControlJson.Default.Request, limit.Token)).Command);
            var (thread, popup) = await ui.Task.WaitAsync(limit.Token);
            var canUseMenu = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            thread.Post(_ =>
            {
                popup.Opening += (_, e) =>
                {
                    canUseMenu.SetResult(!e.Cancel && !popup.Items[0].Enabled && popup.Items[3].Enabled && popup.Items[5].Enabled);
                    e.Cancel = true; // Exercise Opening without displaying a menu on the developer's desktop.
                };
                popup.Show(Point.Empty);
                if (clickExit) popup.Items[5].PerformClick();
            }, null);
            Assert.IsTrue(await canUseMenu.Task.WaitAsync(limit.Token));
            if (clickExit) Assert.IsNull(await stopped.Task.WaitAsync(limit.Token));
        }
        Assert.IsNull(await stopped.Task.WaitAsync(limit.Token));
    }

    [STATestMethod]
    [DoNotParallelize]
    public void HiddenTrayOwnerIsNotAppCloakedEvenWhenWinFormsDefersVisibleWindows()
    {
        var previous = Application.DefaultFormRevealMode;
        try
        {
            Application.SetDefaultFormRevealMode(FormRevealMode.Deferred);
            using var owner = TrayWindowHost.CreateOwner();
            Assert.IsFalse(owner.Visible);
            Assert.AreEqual(FormRevealMode.Classic, owner.FormRevealMode);
            var hr = DwmGetWindowAttribute(owner.Handle, 14, out var cloak, sizeof(int)); // DWMWA_CLOAKED
            if (hr == unchecked((int)0x80263001)) return; // No DWM compositor in this session.
            Marshal.ThrowExceptionForHR(hr);
            Assert.AreEqual(0, cloak & 1, "The never-shown owner must not pass an application cloak to its dialogs.");
        }
        finally { Application.SetDefaultFormRevealMode(previous); }
    }

    [STATestMethod]
    public void ReselectingStatusRefreshesOneOwnedWindowAndTheMenuRemainsAvailable()
    {
        using var icon = TrayDialog.LoadArtwork(16);
        TrayDialog status;
        using (var owner = TrayWindowHost.CreateOwner())
        {
            var initial = new TrayDialogContent("DevTunnelSvc Status", "First report", true);
            status = TrayDialog.GetOrCreate(owner, icon, initial);
            Assert.AreSame(status, TrayDialog.GetOrCreate(owner, icon, initial with { Text = "Next report" }));
            Assert.AreEqual("Next report", ((TableLayoutPanel)status.Controls[0]).Controls.OfType<RichTextBox>().Single().Text);
            using var actions = new TrayMenu("unused", () => { }, () => throw new AssertFailedException(), content => TrayDialog.GetOrCreate(owner, icon, content), _ => { });
            var autoUpdate = false;
            using var menu = TrayIcon.CreatePopup(actions, () => autoUpdate, _ => throw new AssertFailedException(), CancellationToken.None);
            var opened = false;
            menu.Opening += (_, e) => { opened = !e.Cancel && menu.Items[0].Enabled; e.Cancel = true; };
            menu.Show(Point.Empty);
            Assert.IsTrue(opened);
            Assert.IsFalse(((ToolStripMenuItem)menu.Items[2]).Checked);
            autoUpdate = true; // A CLI enable while the popup is closed.
            menu.Show(Point.Empty);
            Assert.IsTrue(((ToolStripMenuItem)menu.Items[2]).Checked);
            menu.Items[3].PerformClick();
            Assert.HasCount(2, owner.OwnedForms);
            Assert.IsTrue(owner.OwnedForms.Any(form => form.Text == "About DevTunnelSvc"));
        }
        Assert.IsTrue(status.IsDisposed);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [STATestMethod]
    public void ConsentDialogDefaultsToCancelAndRequiresChoosingEnable()
    {
        using var icon = TrayDialog.LoadArtwork(16);
        using var form = new TrayDialog(icon, new("DevTunnelSvc Auto-update", Updates.AutoUpdateConsent.Question), confirmAction: "Enable");
        var buttons = ((TableLayoutPanel)form.Controls[0]).Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>();
        Assert.AreEqual(DialogResult.Yes, buttons.Single(button => button.Text == "Enable").DialogResult);
        Assert.AreSame((Button)form.CancelButton!, form.ActiveControl);
        Assert.AreSame(form.AcceptButton, form.CancelButton);
        Assert.AreEqual("Cancel", ((Button)form.CancelButton!).Text);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("Already up to date (1.2.3).", null, false)]
    [DataRow("Install failed.\nThe service was restarted.", null, true)]
    [DataRow("Install v2.0.0?\nActive mappings will stop.", "Install", false)]
    public void StandaloneUpdatePromptsFollowSystemThemeWithoutCapturingTheUpdaterThread(string text, string? confirm, bool error)
    {
        var previousMode = Application.ColorMode;
        var previousContext = SynchronizationContext.Current;
        var caller = Environment.CurrentManagedThreadId;
        var content = new TrayDialogContent(error ? "DevTunnelSvc Update Error" : "DevTunnelSvc Update", text, error);
        try
        {
            Application.SetColorMode(SystemColorMode.Classic); // A fresh worker hasn't inherited the service's WinForms settings.
            var callerContext = SynchronizationContext.Current;
            var answer = TrayWindowHost.Prompt(content, confirm, (dialog, owner) =>
            {
                Assert.AreNotEqual(caller, Environment.CurrentManagedThreadId);
                Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.AreEqual(SystemColorMode.System, Application.ColorMode);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                    && DwmGetWindowAttribute(dialog.Handle, 20, out var darkTitle, sizeof(int)) == 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
                    Assert.AreEqual(Application.IsDarkModeEnabled, darkTitle != 0);
                Assert.AreEqual(FormRevealMode.Classic, owner.FormRevealMode);
                Assert.IsTrue(owner.TopMost);
                Assert.AreEqual(content.Title, dialog.Text);
                var layout = (TableLayoutPanel)dialog.Controls[0];
                var body = layout.GetControlFromPosition(1, 0)!;
                _ = body.Handle;
                Assert.AreEqual(content.Text, body.Text.ReplaceLineEndings("\n"));
                Assert.AreEqual(error, body is RichTextBox);
                Assert.AreEqual(SystemColors.Control.ToArgb(), body.BackColor.ToArgb());
                if (!SystemInformation.HighContrast)
                    Assert.AreEqual(Application.IsDarkModeEnabled, body.BackColor.GetBrightness() < body.ForeColor.GetBrightness());
                var buttons = layout.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>();
                Assert.AreEqual(confirm is null ? "OK" : "Cancel", ((Button)dialog.AcceptButton!).Text);
                return confirm is null ? DialogResult.OK : buttons.Single(b => b.Text == confirm).DialogResult;
            });
            Assert.AreEqual(confirm is null ? DialogResult.OK : DialogResult.Yes, answer);
            Assert.AreSame(callerContext, SynchronizationContext.Current);
        }
        finally { Application.SetColorMode(previousMode); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DialogPreservesLiteralTextKeyboardDismissalAndAppropriatelySizedArtwork(bool status)
    {
        using var icon = TrayDialog.LoadArtwork(16);
        var message = status ? string.Join("\r\n", Enumerable.Repeat(@"Mapping & route -> localhost:8080", 80)) : "A & B";
        using var form = new TrayDialog(icon, new("Example", message, status));
        var layout = (TableLayoutPanel)form.Controls[0];
        var picture = (PictureBox)layout.GetControlFromPosition(0, 0)!;
        _ = picture.Handle; // Exercise the real controls without showing a window on the developer's desktop.
        Assert.IsNotNull(picture.Image);
        Assert.AreEqual(picture.ClientSize.Width, picture.Image.Width);
        Assert.IsGreaterThan(icon.Width, picture.Image.Width);
        var body = layout.GetControlFromPosition(1, 0)!;
        if (status)
        {
            var report = (RichTextBox)body;
            report.SelectAll();
            Assert.AreEqual(message, report.SelectedText.ReplaceLineEndings("\r\n"));
            Assert.IsTrue(report.ReadOnly && report.WordWrap);
            Assert.AreEqual(RichTextBoxScrollBars.Vertical, report.ScrollBars); // Not ForcedVertical.
        }
        else
        {
            Assert.AreEqual(message, body.Text);
            Assert.IsFalse(((Label)body).UseMnemonic);
        }
        Assert.AreSame(form.AcceptButton, form.CancelButton);
        Assert.AreEqual("OK", ((Button)form.AcceptButton!).Text);
    }
}
#endif
