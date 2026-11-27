using DevTunnelSvc.Platform;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class UpdateWorkerTests
{
    [TestMethod]
    public async Task PrivateWorkerForwardsItsResultAndCleansUpOnSuccessAndFailure()
    {
        var root = Directory.CreateTempSubdirectory("devtunnel-update-worker-");
        try
        {
            var windows = OperatingSystem.IsWindows();
            var installed = Path.Combine(root.FullName, windows ? "cmd.exe" : "sh");
            File.Copy(windows ? Environment.GetEnvironmentVariable("ComSpec")! : "/bin/sh", installed);
            var log = new AppLog(root.FullName, "update", Assert.Fail);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string[] echo = windows ? ["/d", "/c", "echo"] : ["-c", "printf '%s %s' \"$1\" \"$2\"", "worker"];
            Assert.AreEqual("--update-worker-tag v2.0.0", await UpdateWorker.RunAsync(installed, echo, "v2.0.0", log, timeout.Token));
            string[] fail = windows ? ["/d", "/c", "exit", "/b", "19"] : ["-c", "exit 19"];
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => UpdateWorker.RunAsync(installed, fail, "v2.0.0", log, timeout.Token));
            Assert.Contains("exited with code 19", error.Message);
            Assert.Contains(log.FilePath, error.Message);
            var handled = await UpdateWorker.RunAsync(installed, fail, "v2.0.0", log, handledExitCode: 19, timeout.Token);
            Assert.AreEqual(19, handled.ExitCode); // A helper that showed the failure still returns a failure.
            Assert.IsEmpty(root.GetDirectories());
            Assert.IsTrue(File.Exists(installed));
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public void WorkerCanOnlyReplaceItsMatchingSiblingAndRechecksUnderTheLease()
    {
        var root = Directory.CreateTempSubdirectory("devtunnel-update-target-");
        try
        {
            var installed = Path.Combine(root.FullName, "installed");
            var worker = WorkerPath(root);
            File.WriteAllText(installed, "original");
            File.Copy(installed, worker);
            Assert.Contains("private copy", Assert.ThrowsExactly<InvalidOperationException>(() => UpdateWorker.InstalledPath(installed)).Message);
            Assert.AreEqual(installed, UpdateWorker.InstalledPath(worker));
            var malformed = Path.Combine(root.CreateSubdirectory(".devtunnelsvc-update-not-a-guid").FullName, "installed");
            File.Copy(installed, malformed);
            Assert.ThrowsExactly<InvalidOperationException>(() => UpdateWorker.InstalledPath(malformed));
            using (UpdateWorker.AcquireLease(worker, installed, root.FullName))
                Assert.ThrowsExactly<IOException>(() => InstanceLease.Acquire(root.FullName));
            File.WriteAllText(installed, "newimage");
            Assert.Contains("changed", Assert.ThrowsExactly<InvalidOperationException>(() => UpdateWorker.InstalledPath(worker)).Message);
            Assert.Contains("changed", Assert.ThrowsExactly<InvalidOperationException>(() =>
                UpdateWorker.AcquireLease(worker, installed, root.FullName)).Message);
            using var lease = InstanceLease.Acquire(root.FullName);
            Assert.AreEqual("newimage", File.ReadAllText(installed));
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public async Task ChecksAndCurrentInstallsDoNotResolveOrStageAnInstallation()
    {
        using var http = OfferV2();
        var update = new CliUpdate(http, "1.0.0", () => throw new AssertFailedException());
        Assert.Contains("Available: v2.0.0", await update.RunAsync(new(true, true), CancellationToken.None));
        var current = new CliUpdate(http, "2.0.0", () => throw new AssertFailedException());
        Assert.Contains("Already up to date (2.0.0)", await current.RunAsync(new(false, true), CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false, null, "update")]
    [DataRow(true, null, "update --force")]
    [DataRow(true, 42, "__auto-update 42")]
    public async Task CliParentStagesTheRequestedModeAndPinsTheCheckedRelease(bool force, int? pid, string command)
    {
        if (!OperatingSystem.IsLinux()) return; // Windows CI exercises this boundary using two published EXEs.
        var root = Directory.CreateTempSubdirectory("devtunnel-staging-cli-");
        try
        {
            var exe = Path.Combine(root.FullName, "fake-cli");
            await File.WriteAllTextAsync(exe, "#!/bin/sh\nprintf '%s\\n' \"$0\" \"$*\"\n");
            File.SetUnixFileMode(exe, (UnixFileMode)0x1c0);
            using var http = OfferV2();
            var update = new CliUpdate(http, "1.0.0", () => new(exe, root.FullName, "unused", (_, _) => throw new AssertFailedException(),
                () => throw new AssertFailedException()));
            var result = (await update.RunAsync(new(false, force, AutoServicePid: pid), CancellationToken.None)).Split('\n');
            Assert.AreEqual(command + " --update-worker-tag v2.0.0", result[1]);
            var copy = new FileInfo(result[0]);
            Assert.AreEqual(root.FullName, copy.Directory?.Parent?.FullName);
            Assert.AreEqual("fake-cli", copy.Name);
            Assert.IsFalse(copy.Exists);
            var log = File.ReadAllText(Path.Combine(root.FullName, "app.log"));
            Assert.Contains("Updating 1.0.0 to v2.0.0", log);
            Assert.Contains("Launching stable update worker", log);
            Assert.Contains("Update worker completed", log);
            await File.WriteAllTextAsync(exe, "#!/bin/sh\nprintf 'worker detail' >&2\nexit 5\n");
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => update.RunAsync(new(false, force), CancellationToken.None));
            Assert.Contains("worker detail\nApplication log: ", error.Message);
            var failedLog = File.ReadAllText(Path.Combine(root.FullName, "app.log"));
            Assert.Contains("Update worker completed", failedLog);
            Assert.Contains("Update failed: InvalidOperationException", failedLog);
            Assert.DoesNotContain("worker detail", failedLog);
        }
        finally { root.Delete(true); }
    }

    private static HttpClient OfferV2() => HttpStub.ReleaseMetadata(() => """{"tag_name":"v2.0.0","assets":[]}""");

    internal static string WorkerPath(DirectoryInfo installation) =>
        Path.Combine(installation.CreateSubdirectory($".devtunnelsvc-update-{Guid.NewGuid():N}").FullName, "installed");

    [TestMethod]
    public async Task LosingTheCallerCancelsWorkButWaitsForRestartBeforeReturning()
    {
        var parent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var work = UpdateWorker.UntilParentClosesAsync(parent.Task, async t =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, t); }
            finally { cancelled.SetResult(); await restart.Task.WaitAsync(limit.Token); }
            return 0;
        }, limit.Token);
        parent.SetResult();
        await cancelled.Task.WaitAsync(limit.Token);
        Assert.IsFalse(work.IsCompleted);
        restart.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        var lost = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            UpdateWorker.UntilParentClosesAsync<int>(Task.CompletedTask, _ => throw new AssertFailedException(), limit.Token));
        Assert.Contains("caller disconnected", lost.Message);
    }

    [TestMethod]
    public async Task ACancellationOrBrokenParentPipeBeforeHandoffNeverStartsWork()
    {
        var root = Directory.CreateTempSubdirectory("devtunnel-cancel-update-");
        try
        {
            using var cancel = new CancellationTokenSource();
            await cancel.CancelAsync();
            var log = new AppLog(root.FullName, "update", Assert.Fail);
            await Assert.ThrowsAsync<OperationCanceledException>(() => UpdateWorker.RunAsync(Path.Combine(root.FullName, "absent"),
                ["update"], "v2.0.0", log, cancel.Token));
            Assert.IsEmpty(root.GetFileSystemInfos());
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                UpdateWorker.UntilParentClosesAsync<int>(open.Task, _ => throw new AssertFailedException(), cancel.Token));
            var readFailure = Task.FromException(new IOException("parent pipe failed"));
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                UpdateWorker.UntilParentClosesAsync<int>(readFailure, _ => throw new AssertFailedException(), CancellationToken.None));
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public void WorkerAndDiagnosticsRefuseRedirectedUserFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("devtunnel-linked-update-");
        try
        {
            List<string> warnings = [];
            var state = Path.Combine(root.FullName, "state");
            var log = new AppLog(state, "update", warnings.Add);
            log.Write("Updating 1.0.0 to v2.0.0.");
            Assert.IsTrue(File.Exists(log.FilePath));
            var original = Path.Combine(root.FullName, "unrelated");
            File.WriteAllText(original, "untouched");
            File.Delete(log.FilePath);
            File.CreateSymbolicLink(log.FilePath, original);
            log.Write("must not be appended");
            Assert.Contains("Cannot write application log", warnings.Single());
            var workerDir = root.CreateSubdirectory($".devtunnelsvc-update-{Guid.NewGuid():N}");
            var worker = Path.Combine(workerDir.FullName, "installed");
            var installed = Path.Combine(root.FullName, "installed");
            File.Copy(original, worker);
            File.CreateSymbolicLink(installed, original);
            Assert.ThrowsExactly<IOException>(() => UpdateWorker.InstalledPath(worker));
            File.Delete(installed);
            File.Copy(original, installed);
            var alias = Path.Combine(root.FullName, $".devtunnelsvc-update-{Guid.NewGuid():N}");
            Directory.CreateSymbolicLink(alias, workerDir.FullName);
            Assert.ThrowsExactly<IOException>(() => UpdateWorker.InstalledPath(Path.Combine(alias, "installed")));
            Assert.AreEqual("untouched", File.ReadAllText(original));
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public async Task EmptyLoaderMessageStillIdentifiesTheFailedAssemblyAndStage()
    {
        List<string> steps = [];
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => UpdateHandoff.InstallAndStartAsync("v2.0.0", true,
            _ => Task.CompletedTask, () => throw new FileNotFoundException("", "System.Diagnostics.Process"), CancellationToken.None, steps.Add));
        Assert.Contains("Installed v2.0.0.", failure.Message);
        Assert.Contains("could not start: FileNotFoundException", failure.Message);
        Assert.Contains("System.Diagnostics.Process", failure.Message);
        Assert.StartsWith("Restart failed: FileNotFoundException (HRESULT 0x", steps.Last());
        Assert.DoesNotContain("System.Diagnostics.Process", steps.Last());
    }
}
