using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Updates;
using Microsoft.Win32.SafeHandles;
using ControlPipe = DevTunnelSvc.Service.Control;

namespace DevTunnelSvc.Platform;

internal sealed partial class TrayUpdate
{
    private const string Caption = "DevTunnelSvc Update";

    internal static bool ConfirmAutoUpdate(Form owner, Icon icon, string question, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var dialog = new TrayDialog(icon, new("DevTunnelSvc Auto-update", question.ReplaceLineEndings()), confirm: true);
        using var cancel = ct.Register(() => owner.BeginInvoke((Action)(() => { if (!dialog.IsDisposed) dialog.Close(); })));
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    }

    internal static Task<int> FromMenuAsync(int servicePid, CancellationToken ct) => WithFeedbackAsync(async () =>
    {
        using var service = SafeProcessHandle.Open(servicePid); // Pin this instance through confirmation and installation.
        using var http = GitHubOAuth.CreateClient();
        var message = await new ReleaseUpdate(http).CheckAsync(Program.Version, false, (release, t) =>
        {
            Program.RequireSingleFileUpdate();
            var log = new AppLog(InstanceLease.DefaultDirectory, "update", Console.Error.WriteLine);
            log.Write($"Updating {Program.Version} to {release.TagName}.");
            return UpdateWorker.RunAsync(ServiceLifetime.ApplicationExecutable(), [TrayMenu.UpdateWorker, servicePid.ToString(CultureInfo.InvariantCulture)],
                release.TagName, log, t);
        }, ct);
        return message.Length == 0 ? null : message;
    }, text => Prompt(text, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information),
        text => Prompt(text, "DevTunnelSvc", MessageBoxButtons.OK, MessageBoxIcon.Error));

    internal static async Task<string?> FromCopyAsync(int servicePid, string expected, CancellationToken ct)
    {
        Program.RequireSingleFileUpdate();
        var image = ServiceLifetime.ApplicationExecutable();
        var installed = UpdateWorker.InstalledPath(image);
        var directory = InstanceLease.DefaultDirectory;
        var log = new AppLog(directory, "update", Console.Error.WriteLine);
        using var service = SafeProcessHandle.Open(servicePid);
        using var http = GitHubOAuth.CreateClient();
        var updater = new ReleaseUpdate(http);
        var flow = new TrayUpdate(async t =>
        {
            var release = await updater.FindNewerAsync(Program.Version, t);
            if (release is not null) UpdateWorker.RequireTag(expected, release.TagName);
            return release;
        }, text => Prompt(text, Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes, t =>
        {
            log.Write($"Stopping service PID {servicePid}.");
            return UpdateHandoff.StopAsync(servicePid, (r, token) => ControlPipe.SendAsync(r, ControlPipe.PipeName, token), t);
        }, async (release, t) =>
        {
            await WaitForExitAsync(service.WaitForExitAsync);
            await updater.ApplyAsync(release, installed, () => UpdateWorker.AcquireLease(image, installed, directory), t);
        }, () => UpdateHandoff.StartInstalledAsync(installed, ServiceLifetime.ExitCodeAsync, log.Write), log.Write);
        return await log.TraceAsync("Worker", () => UpdateWorker.UntilParentClosesAsync(UpdateWorker.ParentClosed(), t => flow.RunAsync(Program.Version, t), ct));
    }

    private static DialogResult Prompt(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        // The helper has no message pump after the prompt. Don't capture a WinForms context for its async install.
        var previous = SynchronizationContext.Current;
        try
        {
            using var owner = TrayDialog.CreateOwner();
            owner.TopMost = true;
            return MessageBox.Show(owner, text, title, buttons, icon, defaultButton);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
