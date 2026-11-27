using System.Globalization;
using System.Windows.Forms;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Updates;
using Microsoft.Win32.SafeHandles;
using ControlPipe = DevTunnelSvc.Service.Control;

namespace DevTunnelSvc.Platform;

internal sealed class TrayUpdateUI(HttpClient http, Func<UpdateInstallation> installation, Func<TrayDialogContent, string?, DialogResult> prompt)
{
    private const string Caption = "DevTunnelSvc Update";
    private const int ErrorDisplayedExitCode = 3;

    public static async Task<int> FromTrayAsync(int servicePid, string? selectedTag, string? workerTag, CancellationToken ct)
    {
        using var client = GitHubOAuth.CreateClient();
        return await new TrayUpdateUI(client, Program.GetUpdateInstallation, TrayWindowHost.Prompt).RunAsync(servicePid, selectedTag, workerTag, ct);
    }

    internal async Task<int> RunAsync(int servicePid, string? selectedTag, string? workerTag, CancellationToken ct)
    {
        try
        {
            return workerTag is null ? await RunFromMenuAsync(servicePid, selectedTag, ct) : await RunFromCopyAsync(servicePid, workerTag, ct);
        }
        catch (Exception ex) { ReportError(AppLog.Detail(ex)); return workerTag is null ? 1 : ErrorDisplayedExitCode; }
    }

    private void Inform(string text) => prompt(new(Caption, text), null);
    private void ReportError(string text) => prompt(new("DevTunnelSvc Update Error", text, Scrollable: true), null);

    private async Task<int> RunFromMenuAsync(int servicePid, string? selectedTag, CancellationToken ct)
    {
        using var service = SafeProcessHandle.Open(servicePid); // Pin this instance through confirmation and installation.
        // A current tray already checked before launching us; the private copy rechecks this exact tag before asking.
        var tag = selectedTag ?? (await new ReleaseUpdate(http).FindNewerAsync(Program.Version, ct))?.TagName;
        if (tag is null || !ReleaseUpdate.IsNewer(Program.Version, tag)) { Inform(ReleaseUpdate.UpToDate(Program.Version)); return 0; }
        var target = installation();
        target.Announce(Program.Version, tag);
        // After replacement this process can't lazy-load WinForms from its original bundle.
        var result = await UpdateWorker.RunAsync(target.Image, [TrayMenu.UpdateWorker, servicePid.ToString(CultureInfo.InvariantCulture)],
            tag, target.Log, ErrorDisplayedExitCode, ct);
        return result.ExitCode == 0 ? 0 : 1;
    }

    private async Task<int> RunFromCopyAsync(int servicePid, string expected, CancellationToken ct)
    {
        var target = installation();
        var installed = target.ResolveInstalledPath();
        var log = target.Log;
        using var service = SafeProcessHandle.Open(servicePid);
        var updater = new ReleaseUpdate(http);
        var flow = new TrayUpdate(async t =>
        {
            var release = await updater.FindNewerAsync(Program.Version, t);
            if (release is not null) UpdateWorker.RequireTag(expected, release.TagName);
            return release;
        }, text => prompt(new(Caption, text), "Install") == DialogResult.Yes, t =>
        {
            log.Write($"Stopping service PID {servicePid}.");
            return UpdateHandoff.StopAsync(servicePid, (r, token) => ControlPipe.SendAsync(r, target.Pipe, token), t);
        }, async (release, t) =>
        {
            await TrayUpdate.WaitForExitAsync(service.WaitForExitAsync);
            await updater.ApplyAsync(release, installed, () => target.AcquireLease(installed), t);
        }, () => target.RestartAsync(installed), log.Write);
        var code = await TrayUpdate.WithFeedbackAsync(
            () => log.TraceAsync("Worker", () => UpdateWorker.UntilParentClosesAsync(target.ParentClosed(), t => flow.RunAsync(Program.Version, t), ct)),
            Inform, text => ReportError($"{text}\nApplication log: {log.FilePath}"));
        return code == 0 ? 0 : ErrorDisplayedExitCode;
    }
}
