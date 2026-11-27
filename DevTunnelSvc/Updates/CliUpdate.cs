using System.Diagnostics;
using System.Globalization;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed record UpdateInstallation(string Image, string Directory, string Pipe,
    Func<ProcessStartInfo, CancellationToken, Task<int>> RunInstalled, Func<Task> ParentClosed)
{
    internal AppLog Log { get; } = new(Directory, "update", Console.Error.WriteLine);
    internal void Announce(string current, string tag) => Log.Write($"Updating {current} to {tag}.");
    internal string ResolveInstalledPath() => UpdateWorker.InstalledPath(Image);
    internal IDisposable AcquireLease(string installed) => UpdateWorker.AcquireLease(Image, installed, Directory);
    internal Task RestartAsync(string installed) => UpdateHandoff.StartInstalledAsync(installed, RunInstalled, Log.Write);
}

internal sealed class CliUpdate(HttpClient http, string version, Func<UpdateInstallation> installation)
{
    internal Task<string> RunAsync(UpdateOptions options, CancellationToken ct)
    {
        if (options.AutoServicePid is { } pid) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        if (!options.CheckOnly && options.WorkerTag is { } tag) return ApplyFromCopyAsync(tag, options, ct);
        return new ReleaseUpdate(http).CheckAsync(version, options.CheckOnly, (release, token) =>
        {
            var target = installation();
            target.Announce(version, release.TagName);
            string[] arguments = options.AutoServicePid is { } servicePid ? [AutoUpdates.Worker, servicePid.ToString(CultureInfo.InvariantCulture)]
                : options.Force ? ["update", "--force"] : ["update"];
            return UpdateWorker.RunAsync(target.Image, arguments, release.TagName, target.Log, token);
        }, ct);
    }

    private Task<string> ApplyFromCopyAsync(string expectedTag, UpdateOptions options, CancellationToken ct)
    {
        var target = installation();
        var installed = target.ResolveInstalledPath();
        var handoff = options.Force ? new UpdateHandoff((r, t) => Control.SendAsync(r, target.Pipe, t),
            () => target.RestartAsync(installed), target.Log.Write, options.AutoServicePid) : null;
        target.Log.Write("Verifying the selected release before installation.");
        return target.Log.TraceAsync("Worker", () => UpdateWorker.UntilParentClosesAsync(target.ParentClosed(), t => new ReleaseUpdate(http, handoff)
            .RunAsync(version, installed, false, () => target.AcquireLease(installed), t, expectedTag), ct));
    }
}
