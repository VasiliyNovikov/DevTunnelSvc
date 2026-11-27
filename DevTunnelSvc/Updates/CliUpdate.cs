using System.Diagnostics;
using System.Globalization;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed record UpdateInstallation(string Image, string Directory, string Pipe,
    Func<ProcessStartInfo, CancellationToken, Task<int>> RunInstalled, Func<Task> ParentClosed);

internal sealed class CliUpdate(HttpClient http, string version, Func<UpdateInstallation> installation)
{
    internal Task<string> RunAsync(UpdateOptions options, CancellationToken ct)
    {
        if (options.AutoServicePid is { } pid) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        if (!options.CheckOnly && options.WorkerTag is { } tag) return ApplyFromCopyAsync(tag, options, ct);
        return new ReleaseUpdate(http).CheckAsync(version, options.CheckOnly, (release, token) =>
        {
            var target = installation();
            var log = new AppLog(target.Directory, "update", Console.Error.WriteLine);
            log.Write($"Updating {version} to {release.TagName}.");
            string[] arguments = options.AutoServicePid is { } servicePid ? [AutoUpdates.Worker, servicePid.ToString(CultureInfo.InvariantCulture)]
                : options.Force ? ["update", "--force"] : ["update"];
            return UpdateWorker.RunAsync(target.Image, arguments, release.TagName, log, token);
        }, ct);
    }

    private Task<string> ApplyFromCopyAsync(string expectedTag, UpdateOptions options, CancellationToken ct)
    {
        var target = installation();
        var installed = UpdateWorker.InstalledPath(target.Image);
        var log = new AppLog(target.Directory, "update", Console.Error.WriteLine);
        var handoff = options.Force ? new UpdateHandoff((r, t) => Control.SendAsync(r, target.Pipe, t),
            () => UpdateHandoff.StartInstalledAsync(installed, target.RunInstalled, log.Write), log.Write, options.AutoServicePid) : null;
        log.Write("Verifying the selected release before installation.");
        return log.TraceAsync("Worker", () => UpdateWorker.UntilParentClosesAsync(target.ParentClosed(), t => new ReleaseUpdate(http, handoff)
            .RunAsync(version, installed, false, () => UpdateWorker.AcquireLease(target.Image, installed, target.Directory), t, expectedTag), ct));
    }
}
