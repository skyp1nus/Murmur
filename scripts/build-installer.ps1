#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the per-user Windows installer (Inno Setup 6, installer/Murmur.iss): Murmur-Setup-<version>.exe + .sha256.

.DESCRIPTION
    1. Publishes Murmur.Desktop (Native AOT, win-x64) into -PublishDir unless -SkipPublish. The publish already puts the
       AOT-published ASR worker (Murmur.Transcription.exe) and its native libraries (sherpa-onnx, onnxruntime) next to
       Murmur.exe (target MurmurPublishWorker in Murmur.Desktop.csproj); when the worker is missing it is published
       explicitly. Any publish warning is a failure, as in ci.yml.
    2. Checks the publish folder: Murmur.exe, Murmur.Transcription.exe, onnxruntime.dll and sherpa-onnx-c-api.dll must be
       there, and writes <OutDir>/payload.md: every file with its size and whether the installer takes it.
    3. Finds ISCC.exe (PATH, %ProgramFiles(x86)%\Inno Setup 6, %ProgramFiles%\Inno Setup 6,
       %LOCALAPPDATA%\Programs\Inno Setup 6) and compiles installer/Murmur.iss with /DAppVersion, /DAppVersionNumeric,
       /DMinWindowsVersion and /DPublishDir. Without Inno Setup it prints how to install it and fails.
    4. Writes <OutDir>/Murmur-Setup-<version>.exe and Murmur-Setup-<version>.exe.sha256 (sha256sum format).

    What the installer leaves out of the publish folder is the ExcludeMasks define at the top of installer/Murmur.iss
    (debug symbols and linker leftovers: *.pdb, *.dbg, *.exp, *.ilk, *.lib - none of them is loaded at run time, and the
    Native AOT .pdb files alone are tens of MB). Everything else in the folder is installed.

    The last line written to the output stream is the full path of the setup exe, so callers can capture it with
    `| Select-Object -Last 1`.

.PARAMETER Version
    x.y.z or x.y.z-suffix. Default: <Version> in src/Murmur.Desktop/Murmur.Desktop.csproj. Also passed to the publish as
    /p:Version.
.PARAMETER SkipPublish
    Use the existing -PublishDir as is.
.PARAMETER PublishDir
    Default: artifacts/publish/win-x64.
.PARAMETER OutDir
    Default: artifacts/installer.
.PARAMETER DryRun
    Resolve the version and check the publish folder, write payload.md and print the plan, but neither publish nor run
    ISCC (no Windows or Inno Setup needed; used by the script tests).

.EXAMPLE
    pwsh scripts/build-installer.ps1
.EXAMPLE
    pwsh scripts/build-installer.ps1 -Version 0.2.0 -SkipPublish
#>
[CmdletBinding()]
param(
    [string] $Version = '',
    [switch] $SkipPublish,
    [string] $PublishDir = '',
    [string] $OutDir = 'artifacts/installer',
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
$iss = Join-Path $repo 'installer/Murmur.iss'
$csproj = Join-Path $repo 'src/Murmur.Desktop/Murmur.Desktop.csproj'

function Resolve-RepoPath([string] $Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $repo $Path))
}

function Fail([string] $Message) {
    Write-Host "build-installer: $Message" -ForegroundColor Red
    exit 1
}

# --- Version ---------------------------------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    $m = [regex]::Match((Get-Content -LiteralPath $csproj -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { Fail "no <Version> in $csproj; pass -Version" }
    $Version = $m.Groups[1].Value
}
$vm = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z][0-9A-Za-z.\-]*)?$')
if (-not $vm.Success) { Fail "version '$Version' is not x.y.z or x.y.z-suffix" }
$versionNumeric = "$($vm.Groups[1].Value).$($vm.Groups[2].Value).$($vm.Groups[3].Value).0"

# Windows minimum: $(MurmurWindowsMinVersion) of Directory.Build.props (a.b.c.d), Inno wants a.b.c.
$mm = [regex]::Match((Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw), '<MurmurWindowsMinVersion>\s*(\d+\.\d+\.\d+)(?:\.\d+)?\s*</MurmurWindowsMinVersion>')
if (-not $mm.Success) { Fail 'MurmurWindowsMinVersion not found in Directory.Build.props' }
$minWindows = $mm.Groups[1].Value

$excludeMasks = [regex]::Match((Get-Content -LiteralPath $iss -Raw), '#define\s+ExcludeMasks\s+"([^"]*)"')
if (-not $excludeMasks.Success) { Fail 'ExcludeMasks define not found in installer/Murmur.iss' }
$excludePatterns = @($excludeMasks.Groups[1].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if ([string]::IsNullOrWhiteSpace($PublishDir)) { $PublishDir = 'artifacts/publish/win-x64' }
$PublishDir = Resolve-RepoPath $PublishDir
$OutDir = Resolve-RepoPath $OutDir
$setupName = "Murmur-Setup-$Version.exe"
$setupPath = Join-Path $OutDir $setupName

Write-Host "Murmur installer $Version ($versionNumeric), Windows $minWindows or newer"
Write-Host "Publish folder: $PublishDir"
Write-Host "Output: $setupPath"

# --- Publish ---------------------------------------------------------------------------------------------------------
function Test-WorkerPublished { Test-Path -LiteralPath (Join-Path $PublishDir 'Murmur.Transcription.exe') }

if (-not $SkipPublish -and -not $DryRun) {
    if (-not $IsWindows) { Fail 'the Native AOT win-x64 publish needs Windows (or use -SkipPublish with a downloaded publish folder)' }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $logs = @()
    $log = Join-Path $OutDir 'publish-desktop.log'
    $logs += $log
    dotnet publish (Join-Path $repo 'src/Murmur.Desktop') -c Release -f net11.0-windows10.0.22621.0 -r win-x64 "-p:Version=$Version" -o $PublishDir 2>&1 | Tee-Object -FilePath $log | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish Murmur.Desktop failed (exit $LASTEXITCODE)" }
    if (-not (Test-WorkerPublished)) {
        Write-Host 'The Desktop publish did not bring Murmur.Transcription.exe; publishing the worker explicitly.'
        $log = Join-Path $OutDir 'publish-transcription.log'
        $logs += $log
        dotnet publish (Join-Path $repo 'src/Murmur.Transcription') -c Release -r win-x64 -o $PublishDir 2>&1 | Tee-Object -FilePath $log | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "dotnet publish Murmur.Transcription failed (exit $LASTEXITCODE)" }
    }
    $hits = @(Select-String -LiteralPath $logs -Pattern ': (warning|error) ')
    if ($hits.Count -gt 0) {
        $hits | Select-Object -First 30 | ForEach-Object { Write-Host $_.Line }
        Fail "$($hits.Count) publish warning/error line(s)"
    }
}

# --- Check the publish folder and describe the payload -----------------------------------------------------------------
if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    Fail "publish folder not found: $PublishDir (run without -SkipPublish, or pass -PublishDir)"
}
$missing = @(@('Murmur.exe', 'Murmur.Transcription.exe', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll') |
    Where-Object { -not (Test-Path -LiteralPath (Join-Path $PublishDir $_) -PathType Leaf) })
if ($missing.Count -gt 0) { Fail "missing in the publish folder: $($missing -join ', ')" }

$rows = foreach ($f in (Get-ChildItem -LiteralPath $PublishDir -File -Recurse | Sort-Object FullName)) {
    $rel = [System.IO.Path]::GetRelativePath($PublishDir, $f.FullName) -replace '\\', '/'
    $excluded = $false
    foreach ($p in $excludePatterns) { if ($f.Name -like $p) { $excluded = $true; break } }
    [pscustomobject]@{ Name = $rel; Bytes = $f.Length; Excluded = $excluded }
}
$rows = @($rows)
$included = @($rows | Where-Object { -not $_.Excluded })
$left = @($rows | Where-Object { $_.Excluded })
$mb = { param($list) $sum = [long]0; foreach ($i in $list) { $sum += $i.Bytes }; [math]::Round($sum / 1MB, 1) }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$payload = [System.Text.StringBuilder]::new()
[void]$payload.AppendLine("### Installer payload ($($included.Count) files, $(& $mb $included) MB uncompressed)").AppendLine()
$maskList = ($excludePatterns | ForEach-Object { '`' + $_ + '`' }) -join ', '
[void]$payload.AppendLine("Left out ($($left.Count) files, $(& $mb $left) MB): $maskList").AppendLine()
[void]$payload.AppendLine('| File | Size, MB | In installer |').AppendLine('|---|---:|---|')
foreach ($r in ($rows | Sort-Object Bytes -Descending)) {
    [void]$payload.AppendLine(('| {0} | {1:N2} | {2} |' -f $r.Name, ($r.Bytes / 1MB), $(if ($r.Excluded) { 'no' } else { 'yes' })))
}
Set-Content -LiteralPath (Join-Path $OutDir 'payload.md') -Value $payload.ToString() -Encoding utf8
Write-Host "Payload: $($included.Count) files, $(& $mb $included) MB (left out: $($left.Count) files, $(& $mb $left) MB: $($excludePatterns -join ', '))"

if ($DryRun) {
    Write-Host "plan: version=$Version numeric=$versionNumeric minWindows=$minWindows included=$($included.Count) excluded=$($left.Count) out=$setupName"
    Write-Output $setupPath
    exit 0
}

# --- ISCC --------------------------------------------------------------------------------------------------------------
function Find-Iscc {
    $onPath = Get-Command 'ISCC.exe', 'ISCC' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs' })) | Where-Object { $_ }
    foreach ($root in $roots) {
        $candidate = Join-Path $root 'Inno Setup 6/ISCC.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host 'Inno Setup 6 (ISCC.exe) not found. Install it with:' -ForegroundColor Red
    Write-Host '    winget install JRSoftware.InnoSetup' -ForegroundColor Red
    Write-Host '  (or: choco install innosetup -y), then open a new terminal so that PATH is refreshed.' -ForegroundColor Red
    exit 1
}
Write-Host "ISCC: $iscc"

if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }
& $iscc /Q "/O$OutDir" "/DAppVersion=$Version" "/DAppVersionNumeric=$versionNumeric" "/DMinWindowsVersion=$minWindows" "/DPublishDir=$PublishDir" $iss
if ($LASTEXITCODE -ne 0) { Fail "ISCC failed (exit $LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath $setupPath)) { Fail "ISCC succeeded but $setupName was not written to $OutDir" }

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$setupPath.sha256" -Value "$hash *$setupName" -Encoding ascii
$sizeMb = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 1)
Write-Host "Built $setupName ($sizeMb MB), sha256 $hash"
Write-Output $setupPath
