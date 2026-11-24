namespace DevTunnelSvc.Platform;

internal static class InstanceLease
{
    internal static string DefaultDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("A per-user application data directory is required.");
            return Path.Combine(root, "DevTunnelSvc");
        }
    }

    public static FileStream Acquire(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { return File.Open(Path.Combine(directory, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex)
        {
            throw new IOException("Cannot lock DevTunnelSvc. Stop the service before updating, and run only one service per user.", ex);
        }
    }
}
