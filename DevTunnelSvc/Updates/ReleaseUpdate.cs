using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace DevTunnelSvc.Updates;

internal sealed record ReleaseInfo(string TagName, ReleaseAsset[] Assets);
internal sealed record ReleaseAsset(string Name, long Size, string? Digest);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ReleaseInfo))]
internal sealed partial class ReleaseJson : JsonSerializerContext;

internal sealed class ReleaseUpdate(HttpClient http, UpdateHandoff? force = null)
{
    private const string Repo = "VasiliyNovikov/DevTunnelSvc";
    private const int MaxBytes = 256 * 1024 * 1024;
    private static readonly Uri Latest = new($"https://api.github.com/repos/{Repo}/releases/latest");

    public async Task<string> RunAsync(string current, string executable, bool checkOnly, Func<IDisposable> acquireUpdateLease, CancellationToken ct)
    {
        var release = await FindNewerAsync(current, ct);
        if (release is null)
            return $"Already up to date ({current}).";
        if (checkOnly)
            return $"Available: {release.TagName} (installed: {current}).";
        if (force is not null)
            return await force.ApplyAsync(release.TagName, async token =>
            {
                using var lease = await UpdateHandoff.WaitForLeaseAsync(acquireUpdateLease, Task.Delay);
                token.ThrowIfCancellationRequested();
                await DownloadAndInstallAsync(release, executable, token);
            }, ct);
        await ApplyAsync(release, executable, acquireUpdateLease, ct);
        return $"Installed {release.TagName}. Run 'service start' to restart the service.";
    }

    internal async Task<ReleaseInfo?> FindNewerAsync(string current, CancellationToken ct)
    {
        var release = await http.GetFromJsonAsync(Latest, ReleaseJson.Default.ReleaseInfo, ct)
            ?? throw new InvalidDataException("GitHub returned no release.");
        return IsNewer(current, release.TagName) ? release : null;
    }

    internal async Task ApplyAsync(ReleaseInfo release, string executable, Func<IDisposable> acquireUpdateLease, CancellationToken ct)
    {
        using var lease = acquireUpdateLease();
        await DownloadAndInstallAsync(release, executable, ct);
    }

    private async Task DownloadAndInstallAsync(ReleaseInfo release, string executable, CancellationToken ct)
    {
        var name = AssetName(OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture);
        var asset = release.Assets.SingleOrDefault(a => a.Name == name)
            ?? throw new InvalidDataException($"Release {release.TagName} has no {name} asset.");
        using var response = await http.GetAsync(new Uri($"https://github.com/{Repo}/releases/download/{release.TagName}/{name}"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        await InstallAsync(executable, asset, stream, ct);
    }

    internal static string AssetName(bool windows, Architecture architecture)
    {
        var arch = architecture switch { Architecture.Arm64 => "arm64", Architecture.X64 => "x64", _ => throw new PlatformNotSupportedException() };
        return windows ? $"devtunnelsvc-win-{arch}.zip" : $"devtunnelsvc-linux-{arch}";
    }

    internal static bool IsNewer(string current, string tag)
    {
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var latest) || latest.Build < 0 ||
            latest.Revision >= 0 || latest.ToString() != tag[1..])
            throw new InvalidDataException("GitHub did not return a stable vMAJOR.MINOR.PATCH release.");
        var parts = current.Split('-', 2);
        var installed = Version.Parse(parts[0]);
        return latest > installed || (latest == installed && parts.Length > 1);
    }

    private static async Task InstallAsync(string executable, ReleaseAsset asset, Stream input, CancellationToken ct)
    {
        var download = $"{executable}.{Guid.NewGuid():N}.download";
        var extracted = download + ".exe";
        try
        {
            using (var file = new FileStream(download, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await CopySizedAsync(input, file, asset.Size, ct);
                file.Position = 0;
                var digest = $"sha256:{Convert.ToHexString(await SHA256.HashDataAsync(file, ct))}";
                if (!string.Equals(asset.Digest, digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Release asset SHA-256 digest is missing or incorrect.");
                if (OperatingSystem.IsWindows())
                {
                    file.Position = 0;
                    using var target = new FileStream(extracted, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await UnpackAsync(file, target, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            Replace(executable, OperatingSystem.IsWindows() ? extracted : download);
        }
        finally { File.Delete(extracted); File.Delete(download); }
    }

    internal static async Task UnpackAsync(Stream payload, Stream destination, CancellationToken ct)
    {
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries is not [var exe] || exe.FullName != "devtunnelsvc.exe")
            throw new InvalidDataException("Expected a single devtunnelsvc.exe in the release ZIP.");
        using var entry = exe.Open();
        await CopySizedAsync(entry, destination, exe.Length, ct);
    }

    internal static async Task CopySizedAsync(Stream stream, Stream destination, long length, CancellationToken ct)
    {
        if (length is < 1 or > MaxBytes)
            throw new InvalidDataException("Invalid release asset size.");
        var buffer = new byte[81920];
        for (long left = length; left > 0;)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), ct);
            if (read == 0) throw new EndOfStreamException("Truncated release asset.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            left -= read;
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
            throw new InvalidDataException("Release asset exceeds its declared size.");
    }

    private static void Replace(string path, string staged)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(staged, File.GetUnixFileMode(path) & (UnixFileMode)0x1ff);
            File.Move(staged, path, overwrite: true);
            return;
        }
        ReplaceWindowsImage(path, staged);
    }

    internal static void ReplaceWindowsImage(string path, string staged)
    {
        var old = path + ".previous";
        File.Move(path, old, overwrite: true);
        try { File.Move(staged, path); }
        catch { File.Move(old, path); throw; }
    }
}
