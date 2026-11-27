#!/usr/bin/env dotnet
#:property PublishAot=false
#:include FixtureFiles.cs
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static FixtureFiles;

if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
{
    Console.Error.WriteLine("The installer tests require Windows or Linux on x64 or Arm64.");
    return 2;
}

var eng = (string)AppContext.GetData("EntryPointFileDirectoryPath")!;
var work = Directory.CreateTempSubdirectory("devtunnel-install-test-");
try
{
    var windows = OperatingSystem.IsWindows();
    var installer = Path.Combine(Directory.GetParent(eng)!.FullName, windows ? "install.ps1" : "install.sh");
    var tools = work.CreateSubdirectory("tools").FullName;
    var cli = await PrepareToolsAsync(eng, tools);
    foreach (var arch in new[] { "x64", "arm64" })
    {
        await TestInstallAsync(work.CreateSubdirectory(arch), installer, cli, tools);
        Console.WriteLine($"Streamed {Path.GetFileName(installer)}: {arch} passed.");
    }
    return 0;
}
finally { work.Delete(recursive: true); }

static async Task<string> PrepareToolsAsync(string eng, string tools)
{
    // Execute a host-native stub in both cases; only the installer's architecture detection is faked.
    var windows = OperatingSystem.IsWindows();
    var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    var rid = $"{(windows ? "win" : "linux")}-{arch}";
    await RunAsync(new("dotnet", ["publish", Path.Combine(eng, "install-fixture.cs"), "-c", "Release", "-r", rid,
        "--self-contained", "false", "-o", tools, "--verbosity", "quiet"])
    { WorkingDirectory = eng });
    var cli = Path.Combine(tools, windows ? "install-fixture.exe" : "install-fixture");
    if (windows)
    {
        var compile = WindowsPowerShell("""
            Add-Type -Path $env:INSTALL_TEST_SOURCE -OutputAssembly $env:INSTALL_TEST_MODULE -ReferencedAssemblies ([System.Management.Automation.Cmdlet].Assembly.Location)
            """);
        compile.Environment["INSTALL_TEST_SOURCE"] = Path.Combine(eng, "install-test-cmdlets.cs");
        compile.Environment["INSTALL_TEST_MODULE"] = Path.Combine(tools, "InstallerMocks.dll");
        await RunAsync(compile);
    }
    // Fail closed if an installer tries to run dotnet or a user's CLI from PATH instead of its verified download.
    string[] helpers = windows ? ["dotnet.exe", "devtunnelsvc.exe"] : ["dotnet", "devtunnelsvc", "uname", "curl", "systemctl", "loginctl", "id", "sudo"];
    foreach (var helper in helpers) File.Copy(cli, Path.Combine(tools, helper));
    return cli;
}

static async Task TestInstallAsync(DirectoryInfo fixture, string installer, string cli, string tools)
{
    var windows = OperatingSystem.IsWindows();
    var root = fixture.FullName;
    var home = fixture.CreateSubdirectory("user home").FullName;
    var target = InstalledCli(home);
    var payload = Path.Combine(root, "payload");
    const string initialPath = @"%SystemRoot%\existing";
    if (windows)
    {
        using var zip = ZipFile.Open(payload, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(cli, "devtunnelsvc.exe");
        File.WriteAllLines(Path.Combine(root, "path-history"), [initialPath]);
    }
    else File.Copy(cli, payload);
    var asset = windows ? $"devtunnelsvc-win-{fixture.Name}.zip" : $"devtunnelsvc-linux-{fixture.Name}";
    WriteMetadata(root, payload, asset);
    var host = InstallerHost(root, home, tools);
    Task Install(string? error = null) => RunAsync(host, installer, error);

    if (!windows)
    {
        var denied = Path.Combine(root, "refuse-linger");
        File.WriteAllText(denied, "");
        await Install("Ask an administrator to run 'loginctl enable-linger 1001'");
        ExpectCalls(root, "--version", "enable-linger");
        if (File.Exists(target)) throw new InvalidDataException("Installed the CLI despite the lingering policy denial.");
        File.Delete(denied);
    }
    await Install();
    string[] linger = windows ? [] : ["enable-linger"];
    ExpectCalls(root, ["--version", .. linger, "service install", "service start", "login"]);
    if (HashFile(cli) != HashFile(target)) throw new InvalidDataException("Installed the wrong CLI.");
    if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    await Install();
    ExpectCalls(root, "--version", "service install", "service start");
    if (windows && !File.ReadAllLines(Path.Combine(root, "path-history")).SequenceEqual([initialPath, $"{initialPath};{Path.GetDirectoryName(target)}"]))
        throw new InvalidDataException("User PATH was not updated idempotently.");

    File.Delete(Path.Combine(root, "signed"));
    File.WriteAllText(Path.Combine(root, "refuse-start"), "");
    await Install(windows ? "Service startup failed" : "Fixture startup failed");
    ExpectCalls(root, "--version", "service install", "service start");

    File.Delete(target);
    File.WriteAllText(target, "existing install");
    await Install("was not changed");
    if (File.ReadAllText(target) != "existing install") throw new InvalidDataException("Overwrote an existing binary.");
    // Windows checks the staged EXE's version first; Linux verifies the existing file's digest before executing it.
    ExpectCalls(root, windows ? ["--version"] : []);
    File.Delete(target);
    WriteMetadata(root, payload, asset, badDigest: true);
    await Install("SHA-256");
    ExpectNoInstall(root, target);
    if (windows)
    {
        using (var zip = ZipFile.Open(payload, ZipArchiveMode.Update)) zip.CreateEntry("../unexpected.dll");
        WriteMetadata(root, payload, asset);
        await Install("single devtunnelsvc.exe");
        ExpectNoInstall(root, target);
    }
}

static ProcessStartInfo InstallerHost(string root, string home, string tools)
{
    ProcessStartInfo host;
    if (OperatingSystem.IsWindows())
    {
        host = WindowsPowerShell("""
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            $installer = [Console]::In.ReadToEnd()
            $module = Import-Module -Name $env:INSTALL_TEST_MODULE -PassThru
            foreach ($name in 'Get-CimInstance','Get-Item','New-ItemProperty','Invoke-RestMethod','Invoke-WebRequest') {
                if ((Get-Command $name).ResolvedCommand.ModuleName -ne $module.Name) { throw "Missing installer test alias: $name" }
            }
            $installer | Invoke-Expression
            """);
        host.Environment["INSTALL_TEST_MODULE"] = Path.Combine(tools, "InstallerMocks.dll");
        host.Environment["LOCALAPPDATA"] = home;
    }
    else
    {
        host = new("/bin/sh");
        host.Environment["HOME"] = home;
        host.Environment.Remove("SUDO_USER");
    }
    host.Environment["PATH"] = tools + Path.PathSeparator + host.Environment["PATH"];
    host.Environment["DEVTUNNELSVC_INSTALL_FIXTURE"] = root;
    host.WorkingDirectory = home;
    return host;
}

static ProcessStartInfo WindowsPowerShell(string command)
{
    var psHome = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
    var host = new ProcessStartInfo(Path.Combine(psHome, "powershell.exe"), ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", $$"""
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.ToString(2) -ne '5.1') { throw 'Expected Windows PowerShell 5.1.' }
        {{command}}
        """]);
    // A driver launched from pwsh must not pass PowerShell 7 modules to the 5.1 child.
    host.Environment["PSModulePath"] = Path.Combine(psHome, "Modules");
    return host;
}

static void WriteMetadata(string root, string payload, string assetName, bool badDigest = false)
{
    var digest = "sha256:" + (badDigest ? new string('0', 64) : HashFile(payload));
    var asset = new { name = assetName, state = "uploaded", size = new FileInfo(payload).Length, digest };
    var release = new { tag_name = "v1.2.3", draft = false, prerelease = false, assets = new[] { asset } };
    File.WriteAllBytes(Path.Combine(root, "release.json"), JsonSerializer.SerializeToUtf8Bytes(release));
}

static void ExpectCalls(string root, params string[] expected)
{
    var file = Path.Combine(root, "calls");
    string[] calls = File.Exists(file) ? File.ReadAllLines(file) : [];
    if (!calls.SequenceEqual(expected))
        throw new InvalidDataException($"Expected CLI calls [{string.Join(", ", expected)}], got [{string.Join(", ", calls)}].");
    File.Delete(file);
}

static void ExpectNoInstall(string root, string target)
{
    ExpectCalls(root);
    if (File.Exists(target)) throw new InvalidDataException("Installed an invalid release.");
}

static async Task RunAsync(ProcessStartInfo info, string? inputFile = null, string? expectedError = null)
{
    info.UseShellExecute = false;
    info.CreateNoWindow = true;
    info.RedirectStandardOutput = info.RedirectStandardError = true;
    info.RedirectStandardInput = inputFile is not null;
    if (inputFile is not null) info.StandardInputEncoding = new UTF8Encoding(false);
    using var child = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {info.FileName}.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(inputFile is null ? 5 : 1));
    var capture = child.ReadAllTextAsync(timeout.Token);
    try
    {
        if (inputFile is not null)
        {
            using var source = File.OpenRead(inputFile);
            using var stdin = child.StandardInput;
            await source.CopyToAsync(stdin.BaseStream, timeout.Token);
        }
        var status = await child.WaitForExitStatusAsync(timeout.Token);
        var (output, error) = await capture;
        var text = output + error;
        if (expectedError is null ? status.ExitCode != 0 : status.ExitCode == 0 || !text.Contains(expectedError, StringComparison.Ordinal))
            throw new InvalidOperationException($"{info.FileName} exited with {status.ExitCode}, expected {expectedError ?? "success"}: {text}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
            if (!child.WaitForExit(10_000)) throw new TimeoutException($"Could not terminate fixture process {child.Id}.", ex);
        }
        if (ex is OperationCanceledException && timeout.IsCancellationRequested) throw new TimeoutException($"Timed out running {info.FileName}.", ex);
        throw;
    }
}
