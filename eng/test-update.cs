#!/usr/bin/env dotnet
#:property PublishAot=false
#:include FixtureFiles.cs
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using static FixtureFiles;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
{
    Console.Error.WriteLine("The published update smoke test requires Windows x64 or Arm64.");
    return 2;
}

var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
var fixture = Path.Combine((string)AppContext.GetData("EntryPointFileDirectoryPath")!, "update-smoke.cs");
var work = Directory.CreateTempSubdirectory("devtunnel-update-smoke-");
try
{
    // Embed symbols only in the replacement so the bundles differ in layout, not just version.
    var oldImage = Publish(fixture, rid, work.CreateSubdirectory("v1"), "1.0.0", "portable");
    var newImage = Publish(fixture, rid, work.CreateSubdirectory("v2"), "2.0.0", "embedded");
    var archive = Path.Combine(work.FullName, "payload.zip");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        zip.CreateEntryFromFile(newImage, "devtunnelsvc.exe");
    foreach (var scenario in Enum.GetValues<Scenario>())
        TestUpdate(work, scenario, oldImage, newImage, archive, rid);
    return 0;
}
finally { work.Delete(recursive: true); }

static string Publish(string fixture, string rid, DirectoryInfo output, string version, string debugType)
{
    var info = new ProcessStartInfo("dotnet", ["publish", fixture, "-c", "Release", "-r", rid,
        "--self-contained", "true", $"-p:Version={version}", $"-p:DebugType={debugType}", "-o", output.FullName, "--verbosity", "quiet"])
    { WorkingDirectory = Path.GetDirectoryName(fixture)! };
    var publish = Process.Run(info);
    if (publish.ExitCode != 0) throw new InvalidOperationException($"Publishing fixture {version} failed ({publish.ExitCode}).");
    return Path.Combine(output.FullName, "DevTunnelSvc.UpdateSmoke.exe");
}

static void TestUpdate(DirectoryInfo work, Scenario scenario, string oldImage, string newImage, string archive, string rid)
{
    // The fixture derives its pipe name from the state directory's final component.
    var state = work.CreateSubdirectory(Guid.NewGuid().ToString("N")).FullName;
    var install = work.CreateSubdirectory(scenario.ToString());
    var exe = Path.Combine(install.FullName, "devtunnelsvc.exe");
    File.Copy(oldImage, exe);
    File.Copy(archive, Path.Combine(state, "asset.zip"));
    var hash = scenario == Scenario.BadDigest ? new string('0', 64) : HashFile(archive);
    var asset = new { name = $"devtunnelsvc-{rid}.zip", size = new FileInfo(archive).Length, digest = $"sha256:{hash}" };
    File.WriteAllBytes(Path.Combine(state, "release.json"), JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v2.0.0", assets = new[] { asset } }));

    var cli = new FixtureCli(exe, state);
    try
    {
        cli.Run(["start"]);
        var before = cli.Run(["smoke-pid"]);
        var rejectStart = scenario is Scenario.StartFailure or Scenario.TrayStartFailure;
        if (rejectStart) File.WriteAllText(Path.Combine(state, "reject-start"), "");
        string reply;
        if (scenario == Scenario.Automatic)
        {
            Expect(cli.Run(["auto-update", "status"]), "disabled");
            reply = cli.WithInput(["auto-update", "enable"], "yes");
            Expect(reply, "connections will be interrupted", "Auto-update is enabled.");
            WaitForAutomaticUpdate(state, install);
        }
        else if (scenario is Scenario.Tray or Scenario.TrayStartFailure)
        {
            if (scenario == Scenario.Tray)
            {
                cli.Run(["__tray-update", before, "v1.5.0"], 1); // The displayed release was superseded before the worker checked.
                Expect(File.ReadAllLines(Path.Combine(state, "dialogs")).Single(), "copy|OK|DevTunnelSvc Update Error|The latest release changed");
                if (cli.Run(["smoke-pid"]) != before) throw new InvalidOperationException("Stopped the service for a superseded release.");
            }
            cli.Run(["__tray-update", before, "v2.0.0"], rejectStart ? 1 : 0);
            var dialogs = File.ReadAllLines(Path.Combine(state, "dialogs"));
            if (dialogs.Length != (scenario == Scenario.Tray ? 3 : 2)) throw new InvalidDataException("Expected one confirmation and one outcome dialog for the selected version.");
            Expect(dialogs[^2], "copy|Install|DevTunnelSvc Update|Install v2.0.0");
            reply = dialogs[^1];
            Expect(reply, rejectStart ? "copy|OK|DevTunnelSvc Update Error|" : "copy|OK|DevTunnelSvc Update|Installed v2.0.0 and restarted");
        }
        else reply = cli.Run(["update", "--force"], scenario == Scenario.Success ? 0 : 1);
        var log = File.ReadAllText(Path.Combine(state, "app.log"));
        Expect(log, $"[{before}] [service] Control pipe ready.", "[update] Launching stable update worker.",
            scenario == Scenario.Automatic ? $"Requesting automatic update of service PID {before}." : $"Stopping service PID {before}.");
        if (HashFile(exe) != HashFile(scenario == Scenario.BadDigest ? oldImage : newImage))
            throw new InvalidDataException($"Wrong installed executable after {scenario}.");
        if (install.EnumerateDirectories().Any()) throw new InvalidDataException($"Update helper left behind after {scenario}.");

        if (rejectStart)
        {
            Expect(reply, "exited with code 37", "Application log:");
            Expect(log, "start exit code: 37");
        }
        else
        {
            var after = cli.Run(["smoke-pid"]);
            if (after == before) throw new InvalidOperationException("The original service was not restarted.");
            Expect(cli.Run(["status"]), "Service running; signed out.");
            Expect(log, $"[{after}] [service] Control pipe ready.", "status exit code: 0");
            if (scenario == Scenario.BadDigest) Expect(reply, "SHA-256");
            else
            {
                if (scenario == Scenario.Automatic)
                {
                    Expect(cli.Run(["auto-update", "status"]), "enabled");
                    Expect(cli.Run(["auto-update", "disable"]), "disabled");
                }
                if (scenario == Scenario.Tray)
                {
                    cli.Run(["__tray-update", after]);
                    Expect(File.ReadAllLines(Path.Combine(state, "dialogs"))[^1], "installed|OK|DevTunnelSvc Update|Already up to date (2.0.0).");
                }
                else Expect(cli.Run(["update", "--force"]), "Already up to date");
                if (cli.Run(["smoke-pid"]) != after) throw new InvalidOperationException("Restarted an up-to-date service.");
            }
        }
    }
    finally { cli.StopIfRunning(); }
    Console.WriteLine($"Published update: {scenario} passed.");
}

static void WaitForAutomaticUpdate(string state, DirectoryInfo installation)
{
    var clock = Stopwatch.StartNew();
    var log = "";
    while (clock.Elapsed < TimeSpan.FromSeconds(60))
    {
        using (var stream = new FileStream(Path.Combine(state, "app.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream)) log = reader.ReadToEnd();
        if (log.Contains("[update] Update worker completed.", StringComparison.Ordinal) && !installation.EnumerateDirectories().Any()) return;
        Thread.Sleep(150);
    }
    throw new TimeoutException($"The isolated automatic updater did not finish:\n{log}");
}

static void Expect(string actual, params string[] fragments)
{
    foreach (var expected in fragments)
        if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidDataException($"Missing '{expected}' in: {actual}");
}

internal enum Scenario { Success, BadDigest, StartFailure, Automatic, Tray, TrayStartFailure }

internal sealed class FixtureCli(string image, string state)
{
    internal string Run(string[] args, params int[] expectedExitCodes) => WithInput(args, null, expectedExitCodes);

    internal string WithInput(string[] args, string? input, params int[] expectedExitCodes)
    {
        var info = new ProcessStartInfo(image, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.RedirectStandardInput = input is not null;
        info.Environment["DEVTUNNELSVC_SMOKE_STATE"] = state;
        using var process = Process.Start(info)!;
        if (input is not null) { process.StandardInput.WriteLine(input); process.StandardInput.Close(); }
        using var drain = new CancellationTokenSource();
        var capture = process.ReadAllTextAsync(drain.Token);
        var command = string.Join(' ', args);
        try
        {
            if (!process.WaitForExit(60_000))
            {
                Terminate(process, entireTree: true);
                throw new TimeoutException($"Timed out: {command}");
            }
            if (!capture.Wait(5_000)) throw new TimeoutException($"Child held the CLI streams open: {command}");
        }
        finally { drain.Cancel(); }
        var (stdout, stderr) = capture.GetAwaiter().GetResult();
        var text = (stdout + stderr).Trim();
        if (expectedExitCodes.Length == 0 ? process.ExitCode != 0 : !expectedExitCodes.Contains(process.ExitCode))
            throw new InvalidOperationException($"{command} returned {process.ExitCode}: {text}");
        return text;
    }

    internal void StopIfRunning()
    {
        var pid = Run(["smoke-pid"], 0, 1);
        if (pid.Length == 0) return;
        using var service = Process.GetProcessById(int.Parse(pid, NumberStyles.None, CultureInfo.InvariantCulture));
        _ = service.SafeHandle;
        if (!string.Equals(service.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PID {pid} is not the isolated service.");
        try
        {
            Run(["stop"]);
            if (!service.WaitForExit(10_000)) throw new TimeoutException("Isolated service did not stop.");
        }
        finally { Terminate(service, entireTree: false); }
    }

    private static void Terminate(Process process, bool entireTree)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: entireTree);
        if (!process.WaitForExit(10_000)) throw new TimeoutException($"Could not terminate fixture process {process.Id}.");
    }
}
