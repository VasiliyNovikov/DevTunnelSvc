using System.Drawing;
using System.Windows.Forms;

namespace DevTunnelSvc.Platform;

internal static class TrayWindowHost
{
    internal static Form CreateOwner()
    {
        // The tray and the standalone updater each need to opt in before creating their first window.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetColorMode(SystemColorMode.System);
        // A hidden owner never paints. If WinForms defers its reveal, DWM keeps its owned dialogs cloaked too.
        return new Form { ShowInTaskbar = false, FormRevealMode = FormRevealMode.Classic };
    }

    internal static bool ConfirmAutoUpdate(Form owner, Icon icon, string question, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var dialog = new TrayDialog(icon, new("DevTunnelSvc Auto-update", question.ReplaceLineEndings()), confirmAction: "Enable");
        using var cancel = ct.Register(() => owner.BeginInvoke((Action)(() => { if (!dialog.IsDisposed) dialog.Close(); })));
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    }

    internal static DialogResult Prompt(TrayDialogContent content, string? confirmAction) =>
        Prompt(content, confirmAction, static (dialog, owner) => dialog.ShowDialog(owner));

    internal static DialogResult Prompt(TrayDialogContent content, string? confirmAction, Func<TrayDialog, Form, DialogResult> show)
    {
        // Update continuations run on the thread pool. Keep the modal loop and its WinForms context on an STA.
        var result = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(ShowOnDialogThread(content, confirmAction, show)); }
            catch (Exception ex) { result.SetException(ex); }
        })
        { IsBackground = true, Name = "DevTunnelSvc update dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task.GetAwaiter().GetResult();
    }

    private static DialogResult ShowOnDialogThread(TrayDialogContent content, string? confirmAction, Func<TrayDialog, Form, DialogResult> show)
    {
        using var owner = CreateOwner();
        owner.TopMost = true;
        using var icon = TrayDialog.LoadArtwork(SystemInformation.SmallIconSize.Width);
        using var dialog = new TrayDialog(icon, content with { Text = content.Text.ReplaceLineEndings() }, confirmAction);
        return show(dialog, owner);
    }
}
