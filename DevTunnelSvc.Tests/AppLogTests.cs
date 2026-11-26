using System.ComponentModel;
using System.Globalization;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class AppLogTests
{
    private DirectoryInfo state = null!;

    [TestInitialize]
    public void SetUp() => state = Directory.CreateTempSubdirectory("devtunnel-app-log-");

    [TestCleanup]
    public void CleanUp() => state.Delete(true);

    [TestMethod]
    public void ConcurrentSourcesAppendCompleteSingleLineEventsInTheSameFile()
    {
        var path = Path.Combine(state.FullName, "app.log");
        Parallel.For(0, 4, writer =>
        {
            var log = new AppLog(state.FullName, writer % 2 == 0 ? "service" : "update", Assert.Fail);
            for (var entry = 0; entry < 16; entry++) log.Write($"Event {writer}:{entry}\r\ncontinued.");
        });
        var lines = File.ReadAllLines(path);
        var expected = from w in Enumerable.Range(0, 4)
                       from e in Enumerable.Range(0, 16)
                       select $"[{Environment.ProcessId}] [{(w % 2 == 0 ? "service" : "update")}] Event {w}:{e} continued.";
        var records = lines.Select(line => line.Split(' ', 2)).ToArray();
        foreach (var record in records)
            Assert.AreEqual(TimeSpan.Zero, DateTimeOffset.ParseExact(record[0], "O", CultureInfo.InvariantCulture).Offset);
        CollectionAssert.AreEquivalent(expected.ToArray(), records.Select(record => record[1]).ToArray());
    }

    [TestMethod]
    public void RotationPreservesTheLastGenerationWhileAReaderHasTheCurrentFileOpen()
    {
        const int limit = 256 * 1024;
        var service = new AppLog(state.FullName, "service", Assert.Fail);
        var update = new AppLog(state.FullName, "update", Assert.Fail);
        var legacy = Path.Combine(state.FullName, "update.log");
        File.WriteAllText(legacy, "old updater history");
        service.Write("Starting.");
        using var reader = File.Open(service.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var next in new[] { "Installing.", "Restarted." })
        {
            using (var fill = File.Open(service.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                fill.SetLength(limit);
            var before = File.ReadAllBytes(service.FilePath);
            update.Write(next);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(service.FilePath + ".1"));
            Assert.IsLessThan(limit, reader.Length);
            Assert.EndsWith($"[update] {next}", File.ReadAllLines(service.FilePath).Single());
        }
        Assert.AreEqual("old updater history", File.ReadAllText(legacy));
        if (OperatingSystem.IsLinux())
            foreach (var file in new[] { service.FilePath, service.FilePath + ".1", service.FilePath + ".lock" })
                Assert.AreEqual((UnixFileMode)0x180, File.GetUnixFileMode(file));
    }

    [TestMethod]
    public void ABusyWriterCannotBeBypassedAndTheNextEventCanStillBeRecorded()
    {
        List<string> warnings = [];
        var log = new AppLog(state.FullName, "service", warnings.Add);
        log.Write("Before contention.");
        using (File.Open(log.FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            log.Write("Blocked event.");
        Assert.Contains("Cannot write application log", warnings.Single());
        log.Write("After release.");
        var history = File.ReadAllText(log.FilePath);
        Assert.Contains("Before contention.", history);
        Assert.Contains("After release.", history);
        Assert.DoesNotContain("Blocked event.", history);
    }

    [TestMethod]
    public async Task LoggingFailureDoesNotAbortWorkAndTheLoggerRecoversWithoutLeakingErrors()
    {
        List<string> warnings = [];
        var log = new AppLog(state.FullName, "update", warnings.Add);
        Directory.CreateDirectory(log.FilePath);
        log.Write("First failure.");
        log.Write("Second failure.");
        Assert.AreEqual(42, await log.TraceAsync("Worker", () => Task.FromResult(42)));
        Assert.Contains("Cannot write application log", warnings.Single());
        Directory.Delete(log.FilePath);
        log.Write(new string('x', 17000) + "truncated-tail");
        var failure = new Win32Exception(5, "private command output");
        await Assert.ThrowsExactlyAsync<Win32Exception>(() => log.TraceAsync<int>("Worker", () => throw failure));
        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("Worker failed: Win32Exception (HRESULT 0x", text);
        Assert.Contains("Win32 5", text);
        Assert.DoesNotContain("private command output", text);
        Assert.DoesNotContain("truncated-tail", text);
        File.Delete(log.FilePath);
        Directory.CreateDirectory(log.FilePath);
        log.Write("Another failure after recovery.");
        Assert.HasCount(2, warnings);
    }

    [TestMethod]
    public void RotationAndLockingDoNotFollowLinksOrAppendToAPublicFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var outside = Path.Combine(state.FullName, "unrelated");
        File.WriteAllText(outside, "untouched");
        List<string> warnings = [];
        var log = new AppLog(state.FullName, "service", warnings.Add);
        var gate = log.FilePath + ".lock";
        File.CreateSymbolicLink(gate, outside);
        log.Write("Lost event.");
        Assert.IsFalse(File.Exists(log.FilePath));
        File.Delete(gate);
        log.Write("First event.");
        File.SetUnixFileMode(log.FilePath, (UnixFileMode)0x1a4);
        log.Write("Private event.");
        Assert.DoesNotContain("Private event", File.ReadAllText(log.FilePath));
        File.SetUnixFileMode(log.FilePath, (UnixFileMode)0x180);
        log.Write("Second event.");
        using (var fill = File.OpenWrite(log.FilePath)) fill.SetLength(256 * 1024);
        File.CreateSymbolicLink(log.FilePath + ".1", outside);
        log.Write("Would rotate.");
        Assert.HasCount(3, warnings);
        Assert.AreEqual("untouched", File.ReadAllText(outside));
        Assert.AreEqual(256 * 1024L, new FileInfo(log.FilePath).Length);
    }
}
