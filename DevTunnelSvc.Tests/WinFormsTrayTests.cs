#if WINDOWS
using System.IO.Pipes;
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
            using (var tray = new TrayIcon("unused", _ => { }, e => stopped.SetResult(e), icon =>
            {
                var owner = Environment.CurrentManagedThreadId;
                Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.IsNotNull(icon.Icon);
                var popup = icon.ContextMenuStrip!;
                Assert.AreEqual("&Status,&Update,&About,,E&xit", string.Join(',', popup.Items.Cast<ToolStripItem>().Select(item => item.Text)));
                icon.Disposed += (_, _) => disposedOnOwner = Environment.CurrentManagedThreadId == owner;
                if (clickExit) SynchronizationContext.Current!.Post(_ => popup.Items[4].PerformClick(), null);
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
            new TrayIcon("unused", _ => { }, _ => Assert.Fail(), _ => throw failure)));
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tray = new TrayIcon("unused", _ => { }, e => stopped.SetResult(e),
            _ => SynchronizationContext.Current!.Post(_ => throw failure, null));
        Assert.AreSame(failure, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ServiceStopInterruptsAStatusClickWithoutBlockingTheWinFormsPump()
    {
        var pipe = $"devtunnel-forms-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, ControlPipe.Options);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var tray = new TrayIcon(pipe, _ => { }, e => stopped.SetResult(e),
            icon => SynchronizationContext.Current!.Post(_ => icon.ContextMenuStrip!.Items[0].PerformClick(), null)))
        {
            await server.WaitForConnectionAsync(limit.Token);
            Assert.AreEqual("status", (await ControlPipe.ReadAsync(server, Service.ControlJson.Default.Request, limit.Token)).Command);
        }
        Assert.IsNull(await stopped.Task.WaitAsync(limit.Token));
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
