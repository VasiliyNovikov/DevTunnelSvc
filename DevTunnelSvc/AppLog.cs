using System.ComponentModel;
using System.Globalization;
using System.Text;
using DevTunnelSvc.Service;

namespace DevTunnelSvc;

internal sealed class AppLog(string directory, string source, Action<string> warn)
{
    private const int MaxBytes = 256 * 1024;
    private static readonly Lock LocalWriters = new();
    internal string FilePath { get; } = Path.Combine(directory, "app.log");
    private int warned;

    internal async Task<T> TraceAsync<T>(string operation, Func<Task<T>> run)
    {
        try { return await run(); }
        catch (Exception ex) { Write($"{operation} failed: {ErrorCode(ex)}"); throw; }
    }

    internal void Write(string message)
    {
        try
        {
            lock (LocalWriters) Append(message);
            Volatile.Write(ref warned, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Exchange(ref warned, 1) == 0) warn($"Cannot write application log {FilePath}: {ErrorCode(ex)}");
        }
    }

    private void Append(string message)
    {
        StateFile.EnsureDirectory(directory);
        using var writer = AcquireWriter();
        StateFile.RejectLink(FilePath);
        using var file = Open(FilePath, FileShare.Read | FileShare.Delete);
        StateFile.RequirePrivateFile(file, "The application log must have mode 0600.");
        var text = message[..Math.Min(message.Length, 1024)].ReplaceLineEndings(" ");
        var bytes = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} [{Environment.ProcessId}] [{source}] {text}\n"));
        if (file.Length + bytes.Length > MaxBytes) Rotate(file);
        file.Position = file.Length;
        file.Write(bytes);
    }

    private FileStream AcquireWriter()
    {
        // The service and both updater processes can append or rotate at the same time.
        // Lock a separate file so readers can keep app.log open throughout rotation.
        var path = FilePath + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                StateFile.RejectLink(path);
                return Open(path, FileShare.None);
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(10); }
        }
    }

    private static FileStream Open(string path, FileShare share)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = StateFile.OwnerFile;
        return new FileStream(path, options);
    }

    private void Rotate(FileStream file)
    {
        var previous = FilePath + ".1";
        StateFile.RejectLink(previous);
        var tail = new byte[Math.Min(file.Length, MaxBytes)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        StateFile.WriteAtomically(previous, tail);
        file.SetLength(0);
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
