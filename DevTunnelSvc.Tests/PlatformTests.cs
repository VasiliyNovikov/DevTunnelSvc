using System.Diagnostics;
using DevTunnelSvc.Platform;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class PlatformTests
{
    [TestMethod]
    public void WindowsBrowserUsesTheRegisteredHttpsHandler()
    {
        var page = new Uri("https://github.com/login/device");
        var launcher = LoginBrowser.SelectLauncher(page, true, _ => throw new AssertFailedException());
        Assert.IsNotNull(launcher);
        Assert.IsTrue(launcher.UseShellExecute);
        Assert.AreEqual(page.AbsoluteUri, launcher.FileName);
        Assert.IsEmpty(launcher.ArgumentList);
    }

    [TestMethod]
    [DataRow(":0", null, null, "xdg-open")]
    [DataRow(null, "wayland-0", null, "xdg-open")]
    [DataRow(null, null, "/opt/browser bridge/open", "/opt/browser bridge/open")]
    [DataRow(":0", null, "/opt/browser", "/opt/browser")]
    [DataRow(null, null, null, null)]
    public void LinuxBrowserUsesAnAvailableHandlerOrLeavesHeadlessLoginManual(string? x11, string? wayland, string? browser, string? expected)
    {
        var page = new Uri("https://github.com/login/device");
        var launcher = LoginBrowser.SelectLauncher(page, false, key => key switch
        {
            "DISPLAY" => x11,
            "WAYLAND_DISPLAY" => wayland,
            "BROWSER" => browser,
            _ => null
        });
        if (expected is null) { Assert.IsNull(launcher); return; }
        Assert.IsNotNull(launcher);
        Assert.AreEqual(expected, launcher.FileName);
        Assert.AreEqual(page.AbsoluteUri, launcher.ArgumentList.Single());
        Assert.IsFalse(launcher.UseShellExecute);
    }

    [TestMethod]
    [DataRow(true, null, null, "clip.exe")]
    [DataRow(false, null, "wayland-0", "wl-copy --type text/plain")]
    [DataRow(false, ":0", null, "xclip -selection clipboard -in;xsel --clipboard --input")]
    [DataRow(false, ":0", "wayland-0", "wl-copy --type text/plain;xclip -selection clipboard -in;xsel --clipboard --input")]
    [DataRow(false, null, null, "")]
    public void ClipboardTargetsTheDesktopClipboardOrSkipsHeadlessSessions(bool windows, string? x11, string? wayland, string expected)
    {
        var writers = LoginClipboard.SelectWriters(windows, key => key switch
        {
            "DISPLAY" => x11,
            "WAYLAND_DISPLAY" => wayland,
            _ => null
        });
        var commands = writers.Select(w => string.Join(' ', w.ArgumentList.Prepend(Path.GetFileName(w.FileName))));
        Assert.AreEqual(expected, string.Join(';', commands));
    }

    [TestMethod]
    public async Task ClipboardFallsBackAndSendsOnlyTheUserCodeEvenToANoisyWriter()
    {
        var dir = Directory.CreateTempSubdirectory("devtunnel-clipboard-");
        try
        {
            var missing = new ProcessStartInfo(Path.Combine(dir.FullName, "missing"));
            var rejected = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", ["/d", "/c", "exit", "/b", "7"]) : new ProcessStartInfo("/bin/false");
            var capture = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::Out.Write('o'*262144); [Console]::Error.Write('e'*262144); " +
                    "$f=[IO.File]::Create('input'); try { [Console]::OpenStandardInput().CopyTo($f) } finally { $f.Dispose() }"])
                : new ProcessStartInfo("/bin/sh", ["-c", "head -c 262144 /dev/zero; head -c 262144 /dev/zero >&2; cat > input"]);
            capture.WorkingDirectory = dir.FullName;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.IsTrue(await LoginClipboard.TryCopyAsync("ABCD-1234", [missing, rejected, capture], TimeSpan.FromSeconds(8), limit.Token));
            CollectionAssert.AreEqual("ABCD-1234"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(dir.FullName, "input"), limit.Token));
        }
        finally { dir.Delete(true); }
    }

    [TestMethod]
    public async Task StalledClipboardDoesNotHoldUpSignInAndCallerCancellationIsPreserved()
    {
        var slow = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"])
            : new ProcessStartInfo("/bin/sleep", ["30"]);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.IsFalse(await LoginClipboard.TryCopyAsync("ABCD-1234", [slow], TimeSpan.FromMilliseconds(150), limit.Token));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<OperationCanceledException>(() => LoginClipboard.TryCopyAsync("ABCD-1234", [slow], TimeSpan.FromSeconds(5), cancel.Token));
    }

    [TestMethod]
    public async Task WindowsStartupUsesTheSignedInSessionAndLinuxUsesTheUserUnit()
    {
        const string exe = @"C:\User Programs\devtunnelsvc.exe";
        var ct = CancellationToken.None;
        var registration = "";
        var result = await ServiceLifetime.InstallAsync(exe, "unused", (name, value) => registration = $"{name}={value}",
            (_, _) => throw new AssertFailedException(), ct);
        Assert.AreEqual($"DevTunnelSvc=\"{exe}\" service start", registration);
        Assert.Contains("service start", result);
        ProcessStartInfo? launched = null;
        await ServiceLifetime.LaunchAsync(exe, i => launched = i, (_, _) => throw new AssertFailedException(), ct);
        Assert.IsNotNull(launched);
        Assert.AreEqual(exe, launched.FileName);
        Assert.AreEqual("service run", string.Join(' ', launched.ArgumentList));
        Assert.IsTrue(launched.UseShellExecute);
        Assert.AreEqual(ProcessWindowStyle.Hidden, launched.WindowStyle);
        var unit = "";
        await ServiceLifetime.LaunchAsync(null, _ => throw new AssertFailedException(), (args, _) =>
        {
            unit = string.Join(' ', args);
            return Task.CompletedTask;
        }, ct);
        Assert.AreEqual("start devtunnelsvc.service", unit);
    }

    [TestMethod]
    public async Task SystemctlFailureIsReportedWithItsOperationAndExitCode()
    {
        var exit = 0;
        Task<int> Run(ProcessStartInfo i, CancellationToken _)
        {
            Assert.AreEqual("systemctl", i.FileName);
            Assert.AreEqual("--user enable devtunnelsvc.service", string.Join(' ', i.ArgumentList));
            return Task.FromResult(exit);
        }
        await ServiceLifetime.SystemctlAsync(["enable", "devtunnelsvc.service"], Run, CancellationToken.None);
        exit = 17;
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ServiceLifetime.SystemctlAsync(["enable", "devtunnelsvc.service"], Run, CancellationToken.None));
        Assert.Contains("enable", failure.Message);
        Assert.Contains("17", failure.Message);
    }

    [TestMethod]
    public async Task AutomaticWorkerEscapesTheServiceLifetimeAndReportsLaunchFailure()
    {
        const string exe = "/home/a $user/dev tunnel";
        var linux = AutoUpdateLauncher.LinuxWorker(exe, "42", "/custom data", "/custom tmp");
        Assert.AreEqual("systemd-run", linux.FileName);
        CollectionAssert.AreEqual(new[] { "--user", "--quiet", "--collect", "--unit=devtunnelsvc-auto-update", "--setenv=XDG_DATA_HOME=/custom data",
            "--setenv=TMPDIR=/custom tmp", "--", exe, "__auto-update", "42" }, linux.ArgumentList.ToArray());
        var windows = AutoUpdateLauncher.WindowsWorker(exe, "42");
        Assert.AreEqual(exe, windows.FileName);
        Assert.IsTrue(windows.StartDetached);
        Assert.AreEqual("__auto-update,42", string.Join(',', windows.ArgumentList));

        var rejected = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", ["/d", "/c", "exit", "/b", "7"]) : new ProcessStartInfo("/bin/sh", ["-c", "exit 7"]);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => AutoUpdateLauncher.LaunchAsync(rejected, limit.Token));
        Assert.Contains("exit 7", error.Message);
        await limit.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => AutoUpdateLauncher.LaunchAsync(windows, limit.Token));
    }

    [TestMethod]
    public void RegistrationCannotAccidentallyTargetTheDotnetHost()
    {
        Assert.Contains("path", Assert.ThrowsExactly<InvalidOperationException>(() => ServiceLifetime.RequireExecutable(null)).Message);
        Assert.Contains("published", Assert.ThrowsExactly<InvalidOperationException>(() => ServiceLifetime.RequireExecutable("dotnet.exe")).Message);
        Assert.AreEqual("devtunnelsvc.exe", ServiceLifetime.RequireExecutable("devtunnelsvc.exe"));
    }
}
