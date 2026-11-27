using System.Collections.Concurrent;
using System.Text;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class AutoUpdateTests : IDisposable
{
    private readonly DirectoryInfo state = Directory.CreateTempSubdirectory("devtunnel-auto-update-");
    private readonly CancellationTokenSource limit = new(TimeSpan.FromSeconds(8));
    private readonly ConcurrentQueue<string> events = new();
    private static readonly ReleaseInfo NewRelease = new("v2.0.0", []);

    public void Dispose() { limit.Dispose(); state.Delete(true); }

    private AutoUpdates Create(AutoUpdateActions actions) => new(state.FullName, actions, events.Enqueue, limit.Token);

    private Task<string> Send(AutoUpdates updates, string command) => updates.DispatchAsync(new(command),
        (_, _) => throw new AssertFailedException("Unexpected tunnel command."), limit.Token);

    [TestMethod]
    public async Task OptInChecksImmediatelyAndEveryFiveMinutesAcrossCurrentOfflineAndNewReleases()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = new List<TimeSpan>();
        var launches = new List<int>();
        var polls = 0;
        var actions = new AutoUpdateActions(() => { }, _ => ++polls switch
        {
            1 => Task.FromResult<ReleaseInfo?>(null),
            2 => throw new HttpRequestException("private HTTP details"),
            _ => Task.FromResult<ReleaseInfo?>(NewRelease)
        }, (pid, _) => { launches.Add(pid); return Task.CompletedTask; }, (interval, ct) =>
        {
            delays.Add(interval);
            if (delays.Count < 3) return Task.CompletedTask;
            idle.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await using var updates = Create(actions);
        Assert.AreEqual(AutoUpdateConsent.Disabled, await Send(updates, AutoUpdateConsent.Status));
        Assert.IsEmpty(delays);
        Assert.AreEqual(0, polls);
        Assert.AreEqual(AutoUpdateConsent.Enabled, await Send(updates, AutoUpdateConsent.Enable));
        await idle.Task.WaitAsync(limit.Token);
        Assert.AreEqual(AutoUpdateConsent.Enabled, await Send(updates, AutoUpdateConsent.Enable));
        Assert.AreEqual(AutoUpdateConsent.Disabled, await Send(updates, AutoUpdateConsent.Disable));
        Assert.AreEqual(3, polls);
        CollectionAssert.AreEqual(Enumerable.Repeat(TimeSpan.FromMinutes(5), 3).ToArray(), delays);
        CollectionAssert.AreEqual(new[] { Environment.ProcessId }, launches);
        var log = string.Join('\n', events);
        Assert.Contains("HttpRequestException", log);
        Assert.DoesNotContain("private HTTP details", log);
        await using var restarted = Create(Cli.NoBackgroundUpdates);
        Assert.AreEqual(AutoUpdateConsent.Disabled, await Send(restarted, AutoUpdateConsent.Status));
    }

    [TestMethod]
    public async Task OptInSurvivesRestartButAnAttemptedInstallDoesNotCauseARestartLoop()
    {
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Cli.NoBackgroundUpdates with { RequireSupported = () => { }, FindNewer = _ => { polled.TrySetResult(); return Task.FromResult<ReleaseInfo?>(null); } };
        await using (var original = Create(actions))
        {
            await Send(original, AutoUpdateConsent.Enable);
            await polled.Task.WaitAsync(limit.Token);
        }
        polled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var restored = Create(actions))
        {
            Assert.AreEqual(AutoUpdateConsent.Enabled, await Send(restored, AutoUpdateConsent.Status));
            await polled.Task.WaitAsync(limit.Token);
            var request = new Request(AutoUpdates.Shutdown, ServicePid: Environment.ProcessId);
            Assert.AreEqual("stopping", await restored.DispatchAsync(request, (forwarded, _) =>
            {
                Assert.AreEqual(request with { Command = "shutdown-for-update" }, forwarded);
                return Task.FromResult("stopping");
            }, limit.Token));
        }
        var cooldown = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var afterAttempt = Create(Cli.NoBackgroundUpdates with
        {
            Delay = (span, ct) => { cooldown.SetResult(span); return Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        });
        var remaining = await cooldown.Task.WaitAsync(limit.Token);
        Assert.IsGreaterThan(TimeSpan.FromMinutes(4), remaining);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(5), remaining);
        Assert.IsTrue(afterAttempt.Enabled);
    }

    [TestMethod]
    public async Task OptOutCancelsAnInFlightCheckAndDeniesDetachedWorkersWithoutStoppingTheService()
    {
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = false;
        async Task<ReleaseInfo?> LateRelease(CancellationToken ct)
        {
            checking.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            return NewRelease;
        }
        var pipe = $"devtunnel-auto-{Guid.NewGuid():N}";
        var actions = new AutoUpdateActions(() => { }, LateRelease, (_, _) => { launched = true; return Task.CompletedTask; }, Task.Delay);
        var service = ServiceLifetime.RunAsync(TextWriter.Null, pipe, state.FullName, actions, limit.Token);
        Task<string> Request(Request request, CancellationToken ct) => Control.SendAsync(request, pipe, ct);
        Task<string> Apply(int pid) => new UpdateHandoff(Request, () => throw new AssertFailedException("Unexpected service start."), AutoServicePid: pid)
            .ApplyAsync(NewRelease.TagName, _ => throw new AssertFailedException("Unexpected install."), limit.Token);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Request(new(AutoUpdateConsent.Enable, Version: 1), limit.Token));
            await Request(new(AutoUpdateConsent.Enable), limit.Token);
            await checking.Task.WaitAsync(limit.Token);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Apply(Environment.ProcessId + 1));
            Assert.AreEqual(AutoUpdateConsent.Disabled, await Request(new(AutoUpdateConsent.Disable), limit.Token));
            Assert.IsFalse(launched);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Apply(Environment.ProcessId));
            Assert.Contains("Service running", await Request(new("status"), limit.Token));
        }
        finally { await limit.CancelAsync(); await service; }
    }

    [TestMethod]
    [DataRow("""{"version":2,"enabled":true}""")]
    [DataRow("""{"version":1}""")]
    [DataRow("invalid private data")]
    public async Task InvalidPersistedConsentFailsClosedAndDoesNotBlockControl(string json)
    {
        StateFile.WriteAtomically(Path.Combine(state.FullName, "auto-update.json"), Encoding.UTF8.GetBytes(json));
        await using var updates = Create(Cli.NoBackgroundUpdates);
        Assert.AreEqual(AutoUpdateConsent.Disabled, await Send(updates, AutoUpdateConsent.Status));
        Assert.Contains("automatic updates are off", events.Single());
        Assert.DoesNotContain(json, events.Single());
        await Send(updates, AutoUpdateConsent.Disable);
        Assert.IsNull(new AutoUpdateFile(state.FullName).Read());
    }
}
