namespace DevTunnelSvc.Service;

internal static class StateFile
{
    internal const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal const UnixFileMode OwnerDirectory = OwnerFile | UnixFileMode.UserExecute;

    internal static void EnsureDirectory(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(path, OwnerDirectory);
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new UnauthorizedAccessException($"Remove group/other write permission from the DevTunnelSvc state directory: {path}");
        }
    }

    internal static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("Refusing to follow a link in the DevTunnelSvc state directory.");
    }

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        // The service lease serializes writers. Never follow a leftover staging file.
        var temp = path + ".pending";
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
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
