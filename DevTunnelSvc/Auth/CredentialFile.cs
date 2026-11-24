using System.Security.Cryptography;
using System.Text.Json;

namespace DevTunnelSvc.Auth;

internal sealed class CredentialFile(string stateDirectory)
{
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectory = OwnerFile | UnixFileMode.UserExecute;
    private const int MaxSize = 64 * 1024;
    private readonly string directory = Path.Combine(stateDirectory, "auth");
    internal string FilePath => Path.Combine(directory, OperatingSystem.IsWindows() ? "github.dpapi" : "github.json");

    internal GitHubCredential? Read()
    {
        EnsureDirectory();
        RejectLink(FilePath);
        try
        {
            using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(file.SafeFileHandle) != OwnerFile)
                throw new UnauthorizedAccessException("Saved credential file must have mode 0600. Fix its permissions or run 'logout'.");
            var length = file.Length;
            if (length is <= 0 or > MaxSize)
                throw new InvalidDataException("Invalid saved credential size. Run 'logout' and sign in again.");
            var bytes = new byte[(int)length];
            try { file.ReadExactly(bytes); return Decode(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (FileNotFoundException) { return null; }
    }

    internal void Write(GitHubCredential credential)
    {
        credential.Validate();
        EnsureDirectory();
        RejectLink(FilePath);
        var json = JsonSerializer.SerializeToUtf8Bytes(credential, LoginJson.Default.GitHubCredential);
        try
        {
            var bytes = OperatingSystem.IsWindows() ? ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser) : json;
            WriteAtomically(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    internal void Delete()
    {
        EnsureDirectory();
        // Deleting the directory entry also permits recovery from a corrupt file or dangling link.
        try { File.Delete(FilePath); }
        finally { File.Delete(FilePath + ".pending"); }
    }

    private static GitHubCredential Decode(byte[] stored)
    {
        try
        {
            var json = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser) : stored;
            try
            {
                var result = JsonSerializer.Deserialize(json, LoginJson.Default.GitHubCredential) ?? throw new JsonException();
                result.Validate();
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(json); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidDataException("Cannot read the saved GitHub credential. Run 'logout' and sign in again.");
        }
    }

    private void WriteAtomically(byte[] bytes)
    {
        if (bytes.Length > MaxSize) throw new InvalidDataException("Credential exceeds the storage limit.");
        // The per-user service lease and credential lock serialize writers. A fixed staging name
        // lets the next save or logout remove an incomplete write left by an interrupted service.
        var temp = FilePath + ".pending";
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerFile;
        var file = new FileStream(temp, options);
        try
        {
            using (file)
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file.SafeFileHandle, OwnerFile);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private void EnsureDirectory()
    {
        RejectLink(stateDirectory);
        RejectLink(directory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        Directory.CreateDirectory(stateDirectory, OwnerDirectory);
        if ((File.GetUnixFileMode(stateDirectory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new UnauthorizedAccessException($"Remove group/other write permission from the DevTunnelSvc state directory: {stateDirectory}");
        Directory.CreateDirectory(directory, OwnerDirectory);
        if (File.GetUnixFileMode(directory) != OwnerDirectory)
            throw new UnauthorizedAccessException("The DevTunnelSvc auth directory must have mode 0700.");
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("Refusing to follow a link in the DevTunnelSvc credential store.");
    }
}
