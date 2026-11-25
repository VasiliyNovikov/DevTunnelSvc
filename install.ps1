#requires -Version 5.1
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Use install.sh on Linux; this installer requires Windows.'
    }
    if (![Environment]::Is64BitOperatingSystem) { throw 'A 64-bit Windows OS is required.' }
    # Query the machine rather than the shell, which can run under x86/x64 emulation.
    $arch = switch (Get-CimInstance -ClassName Win32_Processor -Property Architecture | Select-Object -First 1 -ExpandProperty Architecture) {
        9  { 'x64' }
        12 { 'arm64' }
        default { throw "Unsupported Windows architecture: $_. Expected x64 or Arm64." }
    }

    function Add-InstallDirectory([string] $PathValue, [string] $Directory) {
        foreach ($part in ($PathValue -split ';')) {
            if ([Environment]::ExpandEnvironmentVariables($part.Trim().Trim('"')).TrimEnd('\') -ieq $Directory) {
                return $PathValue
            }
        }
        if (!$PathValue) { return $Directory }
        return $PathValue.TrimEnd(';') + ';' + $Directory
    }

    function Set-InstallPath([string] $Directory) {
        $key = Get-Item -LiteralPath 'HKCU:\Environment'
        try {
            $raw = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $updated = Add-InstallDirectory $raw $Directory
            if ($updated -cne $raw) {
                $kind = if ($key.GetValueNames() -contains 'Path') { $key.GetValueKind('Path') } else { 'ExpandString' }
                New-ItemProperty -LiteralPath 'HKCU:\Environment' -Name Path -PropertyType $kind -Value $updated -Force | Out-Null
            }
        } finally { $key.Close() }
        $env:Path = Add-InstallDirectory $env:Path $Directory
    }

    $repo = 'VasiliyNovikov/DevTunnelSvc'
    $assetName = "devtunnelsvc-win-$arch.zip"
    $maxBytes = 256MB
    $tls = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = $tls -bor [Net.SecurityProtocolType]::Tls12
        $release = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$repo/releases/latest"
        $tag = $release.tag_name
        if ($release.draft -ne $false -or $release.prerelease -ne $false -or $tag -cnotmatch '\Av(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2}\z') {
            throw 'GitHub did not return a stable release.'
        }
        $assets = @($release.assets | Where-Object { $_.name -ceq $assetName })
        if ($assets.Count -ne 1 -or $assets[0].state -ne 'uploaded' -or
            $assets[0].digest -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or $assets[0].size -le 0 -or $assets[0].size -gt $maxBytes) {
            throw "No verified stable release metadata for $assetName."
        }
        $asset = $assets[0]
        if (!$env:LOCALAPPDATA -or ![IO.Path]::IsPathRooted($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA must be an absolute per-user path.' }
        $dir = [IO.Directory]::CreateDirectory((Join-Path $env:LOCALAPPDATA 'Programs\DevTunnelSvc')).FullName
        $target = Join-Path $dir 'devtunnelsvc.exe'
        $work = [IO.Directory]::CreateDirectory((Join-Path $dir ('.install-' + [guid]::NewGuid().ToString('N')))).FullName
        $download = Join-Path $work $assetName
        $staged = Join-Path $work 'devtunnelsvc.exe'
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/$repo/releases/download/$tag/$assetName" -OutFile $download
            if ([IO.FileInfo]::new($download).Length -ne $asset.size -or
                ('sha256:' + (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash) -ine $asset.digest) {
                throw "Downloaded $assetName failed the release size or SHA-256 check."
            }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead($download)
            try {
                if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne 'devtunnelsvc.exe' -or
                    $zip.Entries[0].Length -le 0 -or $zip.Entries[0].Length -gt $maxBytes) {
                    throw 'Expected a single devtunnelsvc.exe in the release ZIP.'
                }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.Entries[0], $staged)
            } finally { $zip.Dispose() }

            $version = & $staged --version
            if ($LASTEXITCODE -ne 0 -or "$version" -cne $tag.Substring(1)) { throw "Executable version does not match $tag." }
            if (Test-Path -LiteralPath $target) {
                $attrs = [IO.File]::GetAttributes($target)
                if (($attrs -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) -or
                    (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $staged).Hash) {
                    throw "$target differs from $tag; it was not changed. For a trusted existing installation, use 'devtunnelsvc service stop' and 'devtunnelsvc update'."
                }
            } else {
                [IO.File]::Move($staged, $target)
            }
        } finally {
            [IO.File]::Delete($download)
            [IO.File]::Delete($staged)
            [IO.Directory]::Delete($work)
        }
    } finally { [Net.ServicePointManager]::SecurityProtocol = $tls }

    Set-InstallPath $dir
    Write-Output "Installed devtunnelsvc $version at $target"
    & $target service install
    if ($LASTEXITCODE -ne 0) { throw 'Service registration failed.' }
    $status = @(& $target service start)
    if ($LASTEXITCODE -ne 0) { throw 'Service startup failed.' }
    if (!$status.Count) { throw 'The service did not report its sign-in state.' }
    $status | Write-Output
    switch -CaseSensitive ($status[0]) {
        'Service running; signed in.'  { }
        'Service running; signed out.' {
            & $target login
            if ($LASTEXITCODE -ne 0) { throw "Sign-in did not finish. Retry with: devtunnelsvc login" }
        }
        default { throw 'Unexpected service status. Run devtunnelsvc status to inspect it.' }
    }
}
