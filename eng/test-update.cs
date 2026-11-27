#:property PublishAot=false
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

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
        cli.Run(["service", "start"]);
        var before = cli.Run(["smoke-pid"]);
        if (scenario == Scenario.StartFailure) File.WriteAllText(Path.Combine(state, "reject-start"), "");
        string reply;
        if (scenario == Scenario.Automatic)
        {
            Expect(cli.Run(["auto-update", "status"]), "disabled");
            reply = cli.WithInput(["auto-update", "enable"], "yes");
            Expect(reply, "connections will be interrupted", "Auto-update is enabled.");
            WaitForAutomaticUpdate(state, install);
        }
        else reply = cli.Run(["update", "--force"], scenario == Scenario.Success ? 0 : 1);
        var log = File.ReadAllText(Path.Combine(state, "app.log"));
        Expect(log, $"[{before}] [service] Control pipe ready.", "[update] Launching stable update worker.",
            scenario == Scenario.Automatic ? $"Requesting automatic update of service PID {before}." : $"Stopping service PID {before}.");
        if (HashFile(exe) != HashFile(scenario == Scenario.BadDigest ? oldImage : newImage))
            throw new InvalidDataException($"Wrong installed executable after {scenario}.");
        if (install.EnumerateDirectories().Any()) throw new InvalidDataException($"Update helper left behind after {scenario}.");

        if (scenario == Scenario.StartFailure)
        {
            Expect(reply, "exited with code 37", "Application log:");
            Expect(log, "service start exit code: 37");
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
                Expect(cli.Run(["update", "--force"]), "Already up to date");
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

static string HashFile(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(file));
}

static void Expect(string actual, params string[] fragments)
{
    foreach (var expected in fragments)
        if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidDataException($"Missing '{expected}' in: {actual}");
}

internal enum Scenario { Success, BadDigest, StartFailure, Automatic }

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
            Run(["service", "stop"]);
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
