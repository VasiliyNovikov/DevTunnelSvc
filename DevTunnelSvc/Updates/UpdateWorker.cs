using System.Diagnostics;
using System.Security.Cryptography;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal static class UpdateWorker
{
    internal const string TagOption = "--update-worker-tag";
    private const string Prefix = ".devtunnelsvc-update-";

    // The .NET bundle loader can reopen its executable to load another assembly at any time.
    // Keep the worker's own image unchanged until all install and restart work has finished.
    internal static async Task<string> RunAsync(string installed, string[] arguments, string tag, AppLog log, CancellationToken ct) =>
        (await RunAsync(installed, arguments, tag, log, handledExitCode: null, ct)).Output;

    internal static async Task<(int ExitCode, string Output)> RunAsync(string installed, string[] arguments, string tag, AppLog log,
        int? handledExitCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetDirectoryName(installed)!, Prefix + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(directory, Path.GetFileName(installed));
        try
        {
            StateFile.EnsureDirectory(directory);
            try
            {
                File.Copy(installed, worker);
                log.Write("Launching stable update worker.");
                var result = await RunProcessAsync(new(worker, [.. arguments, TagOption, tag]), handledExitCode, ct);
                log.Write(result.ExitCode == 0 ? "Update worker completed." : $"Update worker exit code: {result.ExitCode}.");
                return result;
            }
            finally { File.Delete(worker); Directory.Delete(directory); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Write($"Update failed: {AppLog.ErrorCode(ex)}");
            throw new InvalidOperationException($"{AppLog.Detail(ex)}\nApplication log: {log.FilePath}", ex);
        }
    }

    internal static string InstalledPath(string worker)
    {
        var directory = new FileInfo(worker).Directory!;
        if (!directory.Name.StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(directory.Name.AsSpan(Prefix.Length), "N", out _) || directory.Parent is null)
            throw new InvalidOperationException("The update worker must run from its private copy.");
        StateFile.RejectLink(directory.FullName);
        var installed = Path.Combine(directory.Parent.FullName, Path.GetFileName(worker));
        VerifyImage(worker, installed);
        return installed;
    }

    internal static void VerifyImage(string worker, string installed)
    {
        StateFile.RejectLink(installed);
        using var original = File.OpenRead(installed);
        using var copy = File.OpenRead(worker);
        if (original.Length != copy.Length || !SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(copy)))
            throw new InvalidOperationException("The installed executable changed. Check for updates again.");
    }

    internal static IDisposable AcquireLease(string worker, string installed, string stateDirectory)
    {
        var lease = Platform.InstanceLease.Acquire(stateDirectory);
        try { VerifyImage(worker, installed); return lease; }
        catch { lease.Dispose(); throw; }
    }

    internal static void RequireTag(string expected, string actual)
    {
        if (expected != actual) throw new InvalidOperationException("The latest release changed. Check for updates again.");
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(ProcessStartInfo info, int? handledExitCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        info.CreateNoWindow = true;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Could not launch the update worker.");
        using var cancel = ct.Register(child.StandardInput.Close);
        var output = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = child.StandardError.ReadToEndAsync(CancellationToken.None);
        // The caller's bundle is about to be replaced. A synchronous OS wait needs no
        // managed process-exit callback that could lazy-load assemblies from the new image.
        // Cancellation signals the worker via stdin; still wait for its owed restart.
        child.WaitForExit();
        var message = (await error).TrimEnd();
        if (child.ExitCode != 0 && child.ExitCode != handledExitCode)
            throw new InvalidOperationException(message.Length != 0 ? message : $"The update worker exited with code {child.ExitCode}.");
        if (message.Length != 0) Console.Error.WriteLine(message);
        return (child.ExitCode, (await output).TrimEnd());
    }

    internal static async Task<T> UntilParentClosesAsync<T>(Task parentClosed, Func<CancellationToken, Task<T>> run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (parentClosed.IsCompleted)
        {
            await parentClosed;
            throw new OperationCanceledException("The update caller disconnected.");
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = run(lifetime.Token);
        if (await Task.WhenAny(work, parentClosed) == parentClosed)
        {
            try { await parentClosed; }
            finally { await lifetime.CancelAsync(); await work; }
        }
        return await work;
    }

    // Only the dedicated worker reads stdin. EOF means cancellation or a lost parent. On success
    // the worker exits without waiting for EOF (the parent is waiting for its exit code).
    internal static Task ParentClosed() => Task.Run(Console.In.ReadLine);
}
