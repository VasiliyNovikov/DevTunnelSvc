using System.Security.Cryptography;
using System.Text.Json;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Auth;

internal sealed class CredentialFile(string stateDirectory)
{
    private const int MaxSize = 64 * 1024;
    private readonly string directory = Path.Combine(stateDirectory, "auth");
    internal string FilePath => Path.Combine(directory, OperatingSystem.IsWindows() ? "github.dpapi" : "github.json");

    internal GitHubCredential? Read()
    {
        EnsureDirectory();
        StateFile.RejectLink(FilePath);
        try
        {
            using var file = StateFile.OpenSnapshot(FilePath);
            StateFile.RequirePrivateFile(file, "Saved credential file must have mode 0600. Fix its permissions or run 'logout'.");
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
        StateFile.RejectLink(FilePath);
        var json = JsonSerializer.SerializeToUtf8Bytes(credential, LoginJson.Default.GitHubCredential);
        try
        {
            var bytes = OperatingSystem.IsWindows() ? ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser) : json;
            if (bytes.Length > MaxSize) throw new InvalidDataException("Credential exceeds the storage limit.");
            StateFile.WriteAtomically(FilePath, bytes);
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

    private void EnsureDirectory()
    {
        StateFile.EnsureDirectory(stateDirectory);
        StateFile.RejectLink(directory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        Directory.CreateDirectory(directory, StateFile.OwnerDirectory);
        if (File.GetUnixFileMode(directory) != StateFile.OwnerDirectory)
            throw new UnauthorizedAccessException("The DevTunnelSvc auth directory must have mode 0700.");
    }
}
