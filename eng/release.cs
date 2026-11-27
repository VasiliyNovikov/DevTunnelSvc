#!/usr/bin/env dotnet
#:property PublishAot=false
#:include Repository.cs
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

var version = args.Length == 0 ? "0.0.0-dev" : args[0];
if (args.Length > 1 || !IsReleaseVersion(version))
{
    Console.Error.WriteLine("Usage: dotnet eng/release.cs [MAJOR.MINOR.PATCH[-PRERELEASE]]");
    return 2;
}

var rid = GetReleaseRid();
if (rid is null)
{
    Console.Error.WriteLine($"Packaging requires Windows or glibc-based Linux on x64 or Arm64 (host: {RuntimeInformation.RuntimeIdentifier}).");
    return 2;
}

var root = Repository.Root;
var staging = Directory.CreateTempSubdirectory("devtunnelsvc-release-");
try
{
    var publishDir = Path.Combine(staging.FullName, "publish");
    string[] publishArgs = ["publish", Path.Combine(root, "DevTunnelSvc", "DevTunnelSvc.csproj"),
        "-c", "Release", "-r", rid, "--self-contained", "true", $"-p:Version={version}", "-o", publishDir];
    var publish = Process.Run(new ProcessStartInfo("dotnet", publishArgs) { WorkingDirectory = root });
    if (publish.ExitCode != 0)
        return publish.ExitCode;

    var asset = Path.Combine(staging.FullName, $"devtunnelsvc-{rid}");
    var executable = asset;
    if (OperatingSystem.IsWindows())
    {
        const string exeName = "devtunnelsvc.exe";
        asset += ".zip";
        using (var zip = ZipFile.Open(asset, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(Path.Combine(publishDir, exeName), exeName);
        var unpacked = Path.Combine(staging.FullName, "unpacked");
        ZipFile.ExtractToDirectory(asset, unpacked);
        executable = Path.Combine(unpacked, exeName);
    }
    else
    {
        File.Copy(Path.Combine(publishDir, "devtunnelsvc"), asset);
    }

    var actual = RunCli(executable, "--version");
    if (actual != version)
        throw new InvalidDataException($"Packaged CLI reported '{actual}', expected '{version}'.");
    if (!RunCli(executable, "--help").StartsWith("Usage: devtunnelsvc ", StringComparison.Ordinal))
        throw new InvalidDataException("Packaged CLI did not print its usage.");

    var destination = Directory.CreateDirectory(Path.Combine(root, "artifacts", "packages", rid));
    var path = Path.Combine(destination.FullName, Path.GetFileName(asset));
    File.Copy(asset, path, overwrite: true);
    Console.WriteLine($"Packaged DevTunnelSvc {version}: {path}");
    return 0;
}
finally
{
    staging.Delete(recursive: true);
}

static string? GetReleaseRid()
{
    var arch = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };
    if (arch is null)
        return null;
    if (OperatingSystem.IsWindows())
        return $"win-{arch}";

    // Distro-built SDKs can report nonportable RIDs. Check the libc actually loaded by this process.
    if (OperatingSystem.IsLinux() && NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "gnu_get_libc_version", out _))
        return $"linux-{arch}";
    return null;
}

static string RunCli(string executable, string option)
{
    var result = Process.RunAndCaptureText(new ProcessStartInfo(executable, [option])
    {
        WorkingDirectory = Path.GetDirectoryName(executable)!,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    });
    Console.Error.Write(result.StandardError);
    if (result.ExitStatus.ExitCode != 0)
        throw new InvalidOperationException($"{option} exited with code {result.ExitStatus.ExitCode}.");
    return result.StandardOutput.TrimEnd('\r', '\n');
}

static bool IsReleaseVersion(string value)
{
    var parts = value.Split('-', 2);
    if (!Version.TryParse(parts[0], out var core) || core.Build < 0 || core.Revision >= 0 || core.ToString() != parts[0])
        return false;

    return parts.Length == 1 || parts[1].Split('.').All(id =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        (id.Length == 1 || id[0] != '0' || !id.All(char.IsAsciiDigit)));
}
