#requires -PSEdition Core
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This smoke test exercises a published Windows updater.' }
$rid = 'win-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$work = [IO.Directory]::CreateTempSubdirectory('devtunnel-update-smoke-')
$project = Join-Path $PSScriptRoot 'UpdateSmoke\DevTunnelSvc.UpdateSmoke.csproj'

function Invoke-Cli([string] $Image, [string] $State, [string[]] $Arguments, [int[]] $Expected = @(0)) {
    $info = [Diagnostics.ProcessStartInfo]::new($Image)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $info.RedirectStandardError = $true
    $info.Environment['DEVTUNNELSVC_SMOKE_STATE'] = $State
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) {
            $process.Kill($true)
            throw "Timed out: $Arguments"
        }
        if (!$stdout.Wait(5000) -or !$stderr.Wait(5000)) { throw "Child held the CLI streams open: $Arguments" }
        $text = ($stdout.Result + $stderr.Result).Trim()
        if ($process.ExitCode -notin $Expected) { throw "$Arguments returned $($process.ExitCode): $text" }
        return $text
    } finally { $process.Dispose() }
}

function Test-Update([string] $Case, [string] $Old, [string] $New, [string] $Archive) {
    $state = $work.CreateSubdirectory([guid]::NewGuid().ToString('N')).FullName
    $install = $work.CreateSubdirectory($Case).FullName
    $exe = Join-Path $install 'devtunnelsvc.exe'
    [IO.File]::Copy($Old, $exe)
    [IO.File]::Copy($Archive, (Join-Path $state 'asset.zip'))
    $digest = if ($Case -eq 'bad-digest') { '0' * 64 } else { (Get-FileHash -LiteralPath $Archive).Hash }
    @{ tag_name = 'v2.0.0'; assets = @(@{ name = "devtunnelsvc-$rid.zip"; size = ([IO.FileInfo]::new($Archive)).Length; digest = "sha256:$digest" }) } |
        ConvertTo-Json -Compress -Depth 4 | Set-Content -LiteralPath (Join-Path $state 'release.json')
    try {
        $null = Invoke-Cli $exe $state @('service', 'start')
        $before = Invoke-Cli $exe $state @('smoke-pid')
        if ($Case -eq 'start-failure') { [IO.File]::WriteAllText((Join-Path $state 'reject-start'), '') }
        $exitCode = if ($Case -eq 'success') { 0 } else { 1 }
        $reply = Invoke-Cli $exe $state @('update', '--force') $exitCode
        $log = Get-Content -Raw -LiteralPath (Join-Path $state 'app.log')
        if ($log -notmatch "\[$before\] \[service\] Control pipe ready\." -or $log -notmatch '\[update\] Launching stable update worker\.') {
            throw "Service and updater did not preserve their shared event history: $log"
        }
        if ($log -notmatch "Stopping service PID $before\.") { throw "Wrong service stopped: $log" }
        $expectedImage = if ($Case -eq 'bad-digest') { $Old } else { $New }
        if ((Get-FileHash -LiteralPath $exe).Hash -ne (Get-FileHash -LiteralPath $expectedImage).Hash) { throw "Wrong image after $Case" }
        if ([IO.Directory]::GetDirectories($install).Length) { throw "Update helper left behind: $Case" }
        if ($Case -eq 'start-failure') {
            if ($reply -notmatch 'exited with code 37' -or $reply -notmatch 'Application log:' -or $log -notmatch 'service start exit code: 37') {
                throw "Restart failure lost: $reply; $log"
            }
        } else {
            $after = Invoke-Cli $exe $state @('smoke-pid')
            if ($after -eq $before -or (Invoke-Cli $exe $state @('status')) -notmatch 'Service running; signed out') { throw 'Service was not restarted.' }
            if ($log -notmatch "\[$after\] \[service\] Control pipe ready\.") { throw "Restart missing from the application log: $log" }
            if ($log -notmatch 'status exit code: 0') { throw "Missing independent readiness result: $log" }
            if ($Case -eq 'bad-digest' -and $reply -notmatch 'SHA-256') { throw "Lost install failure: $reply" }
            if ($Case -eq 'success') {
                $again = Invoke-Cli $exe $state @('update', '--force')
                if ($again -notmatch 'Already up to date' -or (Invoke-Cli $exe $state @('smoke-pid')) -ne $after) { throw 'Reinstalled or restarted a current service.' }
            }
        }
        Write-Output "Published update: $Case passed."
    } finally {
        $servicePid = Invoke-Cli $exe $state @('smoke-pid') @(0, 1)
        if ($servicePid) {
            $service = [Diagnostics.Process]::GetProcessById([int]$servicePid)
            try {
                $null = $service.Handle
                $null = Invoke-Cli $exe $state @('service', 'stop')
                if (!$service.WaitForExit(10000)) { $service.Kill(); throw 'Isolated service did not stop.' }
            } finally { $service.Dispose() }
        }
    }
}

try {
    foreach ($major in 1, 2) {
        $dir = $work.CreateSubdirectory("v$major").FullName
        dotnet publish $project -c Release -r $rid --self-contained true "-p:Version=$major.0.0" "-p:UpdateSmokePayload=$($major -eq 2)" -o $dir --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Could not publish fixture v$major." }
    }
    $old = Join-Path $work.FullName 'v1\DevTunnelSvc.UpdateSmoke.exe'
    $new = Join-Path $work.FullName 'v2\DevTunnelSvc.UpdateSmoke.exe'
    $archive = Join-Path $work.FullName 'payload.zip'
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $new, 'devtunnelsvc.exe') }
    finally { $zip.Dispose() }
    foreach ($case in 'success', 'bad-digest', 'start-failure') { Test-Update $case $old $new $archive }
} finally { $work.Delete($true) }
