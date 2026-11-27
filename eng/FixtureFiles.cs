using System.Security.Cryptography;

internal static class FixtureFiles
{
    internal static string InstalledCli(string home) => OperatingSystem.IsWindows()
        ? Path.Combine(home, "Programs", "DevTunnelSvc", "devtunnelsvc.exe")
        : Path.Combine(home, ".local", "bin", "devtunnelsvc");

    internal static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
