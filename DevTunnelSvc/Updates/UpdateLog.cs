using System.ComponentModel;
using System.Globalization;
using System.Text;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed class UpdateLog(string directory, Action<string> warn)
{
    private const int MaxBytes = 16 * 1024;
    internal string FilePath { get; } = Path.Combine(directory, "update.log");
    private bool unavailable;

    internal void Begin(string version, string tag) => Write($"Updating {version} to {tag}.", reset: true);
    internal void Step(string step) => Write(step, reset: false);

    internal async Task<T> TraceAsync<T>(Func<Task<T>> run)
    {
        try { return await run(); }
        catch (Exception ex) { Step($"Worker failed: {ErrorCode(ex)}"); throw; }
    }

    private void Write(string step, bool reset)
    {
        if (unavailable) return;
        try
        {
            StateFile.EnsureDirectory(directory);
            StateFile.RejectLink(FilePath);
            var bytes = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} [{Environment.ProcessId}] {step}\n"));
            if (reset) File.Delete(FilePath);
            var options = new FileStreamOptions { Mode = reset ? FileMode.CreateNew : FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = StateFile.OwnerFile;
            using var file = new FileStream(FilePath, options);
            if (file.Length + bytes.Length <= MaxBytes) file.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unavailable = true;
            warn($"Cannot write update log {FilePath}: {ErrorCode(ex)}");
        }
    }

    // Never persist exception messages, command output, tunnel names or credentials.
    internal static string ErrorCode(Exception ex)
    {
        var code = $"{ex.GetType().Name} (HRESULT 0x{ex.HResult:X8})";
        return ex is Win32Exception native ? $"{code}, Win32 {native.NativeErrorCode}" : code;
    }

    internal static string Detail(Exception ex)
    {
        if (!string.IsNullOrWhiteSpace(ex.Message)) return ex.Message;
        var file = ex switch { FileNotFoundException missing => missing.FileName, FileLoadException load => load.FileName, _ => null };
        return file is null ? ErrorCode(ex) : $"{ErrorCode(ex)}: {file}";
    }
}
