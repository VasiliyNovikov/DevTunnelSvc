using System.Diagnostics;

namespace DevTunnelSvc.Platform;

internal static class LoginBrowser
{
    public static Task OpenAsync(Uri page, CancellationToken ct) =>
        LaunchAsync(SelectLauncher(page, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable), ct);

    internal static ProcessStartInfo? SelectLauncher(Uri page, bool windows, Func<string, string?> environment)
    {
        if (windows)
            return new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true };

        var browser = environment("BROWSER");
        if (!string.IsNullOrWhiteSpace(browser))
            return new ProcessStartInfo(browser, [page.AbsoluteUri]);

        if (string.IsNullOrWhiteSpace(environment("DISPLAY")) && string.IsNullOrWhiteSpace(environment("WAYLAND_DISPLAY")))
            return null;

        return new ProcessStartInfo("xdg-open", [page.AbsoluteUri]);
    }

    internal static async Task LaunchAsync(ProcessStartInfo? launcher, CancellationToken ct)
    {
        if (launcher is null) return;
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(launcher);
        // Windows can dispatch the URL to an existing browser without returning a process.
        if (process is null) return;
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new IOException($"The browser launcher exited with code {process.ExitCode}.");
    }
}
