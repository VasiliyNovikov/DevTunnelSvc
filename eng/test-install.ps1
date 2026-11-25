#requires -Version 5.1
# Use the inbox .NET Framework compiler so the fixture works with both PowerShell editions.
$ErrorActionPreference = 'Stop'
$installer = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\install.ps1'))
$fixture = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('devtunnel-install-' + [guid]::NewGuid().ToString('N'))))
$oldLocal = $env:LOCALAPPDATA
$oldPath = $env:Path
$state = @{ Path = '%SystemRoot%\existing'; Writes = 0; Cpu = 9 }

# The installer must never touch GitHub, a real user PATH, service registration or credentials here.
function Get-CimInstance($ClassName, $Property) {
    if ($ClassName -ne 'Win32_Processor' -or $Property -ne 'Architecture') { throw 'Unexpected hardware query' }
    [pscustomobject]@{ Architecture = $state.Cpu }
}
function Get-Item($LiteralPath) {
    if ($LiteralPath -ne 'HKCU:\Environment') { throw "Unexpected registry access: $LiteralPath" }
    $key = [pscustomobject]@{}
    $key | Add-Member ScriptMethod GetValue { param($name, $default, $options); $state.Path }
    $key | Add-Member ScriptMethod GetValueNames { 'Path' }
    $key | Add-Member ScriptMethod GetValueKind { 'ExpandString' }
    $key | Add-Member ScriptMethod Close {}
    $key
}
function New-ItemProperty($LiteralPath, $Name, $PropertyType, $Value, [switch]$Force) {
    if ($LiteralPath -ne 'HKCU:\Environment' -or $Name -ne 'Path' -or $PropertyType -ne 'ExpandString') { throw 'Unexpected registry write' }
    $state.Path = $Value
    $state.Writes++
}
function Invoke-RestMethod($Uri, [switch]$UseBasicParsing) {
    if ($Uri -cne 'https://api.github.com/repos/VasiliyNovikov/DevTunnelSvc/releases/latest') { throw 'Unexpected metadata URL' }
    $state.Release
}
function Invoke-WebRequest($Uri, $OutFile, [switch]$UseBasicParsing) {
    if ($Uri -cne "https://github.com/VasiliyNovikov/DevTunnelSvc/releases/download/v1.2.3/$($state.Asset)") { throw 'Unexpected package URL' }
    [IO.File]::Copy($state.Zip, $OutFile)
}
function Set-ReleaseMetadata {
    $state.Release = [pscustomobject]@{ tag_name = 'v1.2.3'; draft = $false; prerelease = $false; assets = @(
        [pscustomobject]@{ name = $state.Asset; state = 'uploaded'; size = [IO.FileInfo]::new($state.Zip).Length
            digest = 'sha256:' + (Get-FileHash -LiteralPath $state.Zip).Hash.ToLowerInvariant() }
    ) }
}
function Expect-Failure([string] $Reason) {
    $message = ''
    try { $installer | Invoke-Expression | Out-Null } catch { $message = $_.Exception.Message }
    if (!$message.Contains($Reason)) { throw "Expected '$Reason', got '$message'." }
}

try {
    $stub = Join-Path $fixture.FullName 'cli.exe'
    $source = Join-Path $fixture.FullName 'cli.cs'
    [IO.File]::WriteAllText($source, @'
using System;
using System.IO;
public class InstallerTestCli {
    public static int Main(string[] args) {
        var home = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var command = String.Join(" ", args);
        File.AppendAllText(Path.Combine(home, "calls"), command + "\n");
        if (command == "--version") { Console.WriteLine("1.2.3"); return 0; }
        var target = Path.Combine(home, @"Programs\DevTunnelSvc\devtunnelsvc.exe");
        if (!String.Equals(Environment.GetCommandLineArgs()[0], target, StringComparison.OrdinalIgnoreCase)) return 91;
        var signed = Path.Combine(home, "signed");
        switch (command) {
            case "service install": return 0;
            case "service start":
                if (File.Exists(Path.Combine(home, "refuse-start"))) return 7;
                Console.WriteLine(File.Exists(signed) ? "Service running; signed in." : "Service running; signed out."); return 0;
            case "login": File.WriteAllText(signed, "test"); return 0;
            default: return 92;
        }
    }
}
'@)
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    & $csc /nologo /target:exe "/out:$stub" $source
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the offline installer fixture' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $state.Zip = Join-Path $fixture.FullName 'release.zip'
    $zip = [IO.Compression.ZipFile]::Open($state.Zip, 'Create')
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $stub, 'devtunnelsvc.exe') } finally { $zip.Dispose() }

    foreach ($cpu in @(9, 12)) {
        $state.Cpu = $cpu
        $arch = if ($cpu -eq 9) { 'x64' } else { 'arm64' }
        $state.Asset = "devtunnelsvc-win-$arch.zip"
        $env:LOCALAPPDATA = [IO.Directory]::CreateDirectory((Join-Path $fixture.FullName "$arch home")).FullName
        $state.Path = '%SystemRoot%\existing'
        $state.Writes = 0
        Set-ReleaseMetadata
        $calls = Join-Path $env:LOCALAPPDATA 'calls'
        $targetDir = Join-Path $env:LOCALAPPDATA 'Programs\DevTunnelSvc'
        $installer | Invoke-Expression | Out-Null
        if (([IO.File]::ReadAllLines($calls) -join ',') -cne '--version,service install,service start,login') { throw 'Incorrect fresh install sequence' }
        [IO.File]::Delete($calls)
        $installer | Invoke-Expression | Out-Null
        if (([IO.File]::ReadAllLines($calls) -join ',') -cne '--version,service install,service start') { throw 'Reinstallation did not preserve sign-in' }
        if ($state.Writes -ne 1 -or $state.Path -cne "%SystemRoot%\existing;$targetDir") { throw 'User PATH was not updated idempotently' }
    }
    $target = Join-Path $targetDir 'devtunnelsvc.exe'
    [IO.File]::Delete((Join-Path $env:LOCALAPPDATA 'signed'))
    [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'refuse-start'), '')
    [IO.File]::Delete($calls)
    Expect-Failure 'Service startup failed'
    if (([IO.File]::ReadAllLines($calls) -join ',') -cne '--version,service install,service start') { throw 'Tried to log in after failed startup' }
    [IO.File]::WriteAllText($target, 'existing install')
    Expect-Failure 'was not changed'
    if ([IO.File]::ReadAllText($target) -cne 'existing install') { throw 'Overwrote an existing binary' }
    [IO.File]::Delete($target)
    [IO.File]::Delete($calls)
    $state.Release.assets[0].digest = 'sha256:' + ('0' * 64)
    Expect-Failure 'SHA-256'
    $zip = [IO.Compression.ZipFile]::Open($state.Zip, 'Update')
    try { [void]$zip.CreateEntry('../unexpected.dll') } finally { $zip.Dispose() }
    Set-ReleaseMetadata
    Expect-Failure 'single devtunnelsvc.exe'
    if ([IO.File]::Exists($calls) -or [IO.File]::Exists($target)) { throw 'Ran or installed an invalid release' }
    Write-Output 'Windows streamed installer checks passed.'
} finally {
    $env:LOCALAPPDATA = $oldLocal
    $env:Path = $oldPath
    $fixture.Delete($true)
}
