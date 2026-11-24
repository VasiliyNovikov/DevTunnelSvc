using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DevTunnelSvc.Platform;

internal static class LoginClipboard
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static Task<bool> TryCopyAsync(string userCode, CancellationToken ct) =>
        TryCopyAsync(userCode, SelectWriters(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable), TimeSpan.FromSeconds(2), ct);

    internal static IEnumerable<ProcessStartInfo> SelectWriters(bool windows, Func<string, string?> environment)
    {
        if (windows)
        {
            yield return new(Path.Combine(Environment.SystemDirectory, "clip.exe"));
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(environment("WAYLAND_DISPLAY")))
            yield return new("wl-copy", ["--type", "text/plain"]);
        if (!string.IsNullOrWhiteSpace(environment("DISPLAY")))
        {
            yield return new("xclip", ["-selection", "clipboard", "-in"]);
            yield return new("xsel", ["--clipboard", "--input"]);
        }
    }

    internal static async Task<bool> TryCopyAsync(string userCode, IEnumerable<ProcessStartInfo> writers, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var writer in writers)
        {
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                if (await WriteAsync(writer, userCode, deadline.Token)) return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }

    private static async Task<bool> WriteAsync(ProcessStartInfo writer, string userCode, CancellationToken ct)
    {
        writer.UseShellExecute = false;
        writer.CreateNoWindow = true;
        writer.RedirectStandardInput = true;
        writer.StandardInputEncoding = Utf8WithoutBom;
        // A Linux clipboard owner can outlive login. Don't let it inherit the CLI's output pipes.
        writer.RedirectStandardOutput = true;
        writer.RedirectStandardError = true;
        using var helper = Process.Start(writer)!;
        try
        {
            using (var input = helper.StandardInput)
                await input.WriteAsync(userCode.AsMemory(), ct);
            await helper.WaitForExitAsync(ct);
            return helper.ExitCode == 0;
        }
        finally
        {
            if (!helper.HasExited)
            {
                try { helper.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (helper.HasExited) { }
            }
        }
    }
}
