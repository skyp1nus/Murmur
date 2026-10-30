#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the Windows checks of the GitHub workflows on this PC and writes one Markdown report.

.DESCRIPTION
    The heavy GitHub workflows (Windows build and tests, startup, capture e2e, ASR bench, AI eval, long recordings) run
    only on demand; this script is the everyday way to run the same checks on a real Windows PC (real GPU, microphone
    and speakers). Pick the steps with switches; without any, it runs -Build -Test -Publish -Startup.

      -Build     SDK check (global.json) + dotnet build Murmur.sln -c Release (warnings are errors)
      -Test      dotnet test Murmur.sln -c Release (Windows tests run for real here: WASAPI, WGC, Media Foundation, playback)
      -Publish   Native AOT win-x64 of Murmur.Desktop + Murmur.Transcription into artifacts/publish/win-x64;
                 fails on any publish warning (same rule as ci.yml)
      -Startup   scripts/measure-startup.ps1, 7 runs, median budget 400 ms
      -Installer Inno Setup installer (scripts/build-installer.ps1) from the -Publish folder: silent per-user install into a
                 temp folder, Murmur.exe + Murmur.Transcription.exe + native libs present, the startup check of the
                 installed Murmur.exe (every run must render a frame; the median is only reported, -Startup
                 enforces the budget), silent uninstall, folder gone, user data kept.
                 Needs Inno Setup 6 (winget install JRSoftware.InnoSetup); skipped when Murmur is installed on this PC
      -Capture   60 s recording with audio + screen (Murmur.exe --autorecord) + scripts/check-recording.ps1
      -Perf      long recordings with the PLAN.md budgets: -PerfMinutes audio only (CPU < 3 %, RAM < 150 MB, no growth),
                 then 10 min audio + screen (CPU < 8 %, working set must not grow)
      -Asr       1 h of audio -> text with Murmur.Transcription --bench, budget 180 s (needs scripts/fetch-models.ps1)
      -AiEval    "AI answer leads to the right minute" with Gemma 4 (needs scripts/fetch-models.ps1 -Ai)
      -All       everything above

    -SetupExe <path> makes -Installer test an existing Murmur-Setup-*.exe instead of building one (CI builds it first).

    Recordings use the default devices. Play something (a video with speech) during -Capture and -Perf, or pass -Tone
    to play a 440 Hz test tone through the default output device (loud!) and also check that the tone was recorded
    without gaps, like CI does with its virtual sound card.

    Steps after a failed step still run when they can (like CI); steps that need the published app use
    artifacts/publish/win-x64 from an earlier -Publish when this run does not publish.

    Output: artifacts/local-verify/<yyyyMMdd-HHmmss>/report.md (plus JSON and logs). Exit code 1 when a step failed.

.EXAMPLE
    pwsh scripts/verify-local.ps1
.EXAMPLE
    pwsh scripts/verify-local.ps1 -Publish -Capture -Tone
.EXAMPLE
    pwsh scripts/verify-local.ps1 -All -PerfMinutes 20
#>
[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $Test,
    [switch] $Publish,
    [switch] $Startup,
    [switch] $Installer,
    [switch] $Capture,
    [switch] $Perf,
    [switch] $Asr,
    [switch] $AiEval,
    [switch] $All,
    [switch] $Tone,
    [ValidateRange(1, 240)] [int] $PerfMinutes = 60,
    [string] $ModelsDir = '',
    [string] $SetupExe = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
Set-Location -LiteralPath $repo

if ($All) { $Build = $Test = $Publish = $Startup = $Installer = $Capture = $Perf = $Asr = $AiEval = $true }
if (-not ($Build -or $Test -or $Publish -or $Startup -or $Installer -or $Capture -or $Perf -or $Asr -or $AiEval)) {
    $Build = $Test = $Publish = $Startup = $true
}

$onWindows = $IsWindows
$runDir = Join-Path $repo ("artifacts/local-verify/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$publishDir = Join-Path $repo 'artifacts/publish/win-x64'
$murmurExe = Join-Path $publishDir 'Murmur.exe'
$transcriptionExe = Join-Path $publishDir 'Murmur.Transcription.exe'

if ([string]::IsNullOrWhiteSpace($ModelsDir)) {
    $ModelsDir = if (-not [string]::IsNullOrWhiteSpace($env:MURMUR_MODELS_DIR)) { $env:MURMUR_MODELS_DIR }
                 elseif ($onWindows) { Join-Path $env:LOCALAPPDATA 'Murmur\models' }
                 else { Join-Path $HOME '.local/share/Murmur/models' }
}

$results = [System.Collections.Generic.List[object]]::new()
$sections = [System.Text.StringBuilder]::new()

function Add-Result([string] $Step, [string] $Status, [string] $Summary) {
    $results.Add([pscustomobject]@{ Step = $Step; Status = $Status; Summary = $Summary })
    $color = switch ($Status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("[{0}] {1}: {2}" -f $Status, $Step, $Summary) -ForegroundColor $color
}

function Add-Section([string] $Title, [string] $Body) {
    [void]$sections.AppendLine("## $Title").AppendLine().AppendLine($Body.TrimEnd()).AppendLine()
}

function Read-Text([string] $Path) {
    if (Test-Path -LiteralPath $Path) { return (Get-Content -LiteralPath $Path -Raw) }
    return ''
}

# Runs a native command, tees its output to a log, returns the exit code.
function Invoke-Logged([string] $Log, [scriptblock] $Command) {
    & $Command 2>&1 | Tee-Object -FilePath $Log | Out-Host
    return $LASTEXITCODE
}

function Test-Published([string] $Step, [string] $Exe) {
    if (Test-Path -LiteralPath $Exe) { return $true }
    Add-Result $Step 'SKIP' "$([System.IO.Path]::GetFileName($Exe)) not found in artifacts/publish/win-x64 (run with -Publish)"
    return $false
}

function Require-Windows([string] $Step) {
    if ($onWindows) { return $true }
    Add-Result $Step 'SKIP' 'Windows only'
    return $false
}

# --- Build -----------------------------------------------------------------------------------------------------------
$buildOk = $true
if ($Build) {
    $expected = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
    $actual = (dotnet --version).Trim()

    # global.json rolls forward (latestFeature, prereleases allowed): a newer SDK of the same major.minor is what dotnet itself
    # picks, so it builds; only an older one or another major.minor is a mismatch. CI installs exactly the pinned SDK.
    $sdkNote = ''
    $pinned = $null; $installed = $null
    $sdkOk = $actual -eq $expected
    if (-not $sdkOk -and [System.Management.Automation.SemanticVersion]::TryParse($expected, [ref]$pinned) `
            -and [System.Management.Automation.SemanticVersion]::TryParse($actual, [ref]$installed)) {
        $sdkOk = $installed.Major -eq $pinned.Major -and $installed.Minor -eq $pinned.Minor -and $installed -ge $pinned
        if ($sdkOk) { $sdkNote = " (global.json pins $expected and rolls forward to this SDK; CI builds with the pinned one)" }
    }

    if (-not $sdkOk) {
        Add-Result 'Build' 'FAIL' "SDK mismatch: dotnet --version is $actual, global.json pins $expected (a newer SDK of the same major.minor is fine, an older or another one is not)"
        $buildOk = $false
    }
    else {
        $log = Join-Path $runDir 'build.log'
        $code = Invoke-Logged $log { dotnet build Murmur.sln -c Release }
        $warnings = @(Select-String -LiteralPath $log -Pattern ': warning ' -SimpleMatch:$false).Count
        if ($code -eq 0) { Add-Result 'Build' 'PASS' "SDK $actual$sdkNote, 0 errors, $warnings warning line(s)" }
        else { Add-Result 'Build' 'FAIL' "dotnet build exit $code (see build.log)"; $buildOk = $false }
    }
}

# --- Test ------------------------------------------------------------------------------------------------------------
if ($Test) {
    $log = Join-Path $runDir 'test.log'
    $trx = Join-Path $runDir 'test-results'
    # @(...) keeps an array: `$x = if (...) { @('a') }` unrolls to the string 'a', and splatting a string passes its characters.
    $noBuild = @(if ($Build -and $buildOk) { '--no-build' })
    $code = Invoke-Logged $log { dotnet test Murmur.sln -c Release @noBuild --report-xunit-trx --results-directory $trx }
    $text = Read-Text $log
    $totals = [regex]::Matches($text, 'total:\s*(\d+)\s+failed:\s*(\d+)\s+succeeded:\s*(\d+)\s+skipped:\s*(\d+)')
    $sum = @(0, 0, 0, 0)
    foreach ($m in $totals) { for ($i = 0; $i -lt 4; $i++) { $sum[$i] += [int]$m.Groups[$i + 1].Value } }
    $summary = "total $($sum[0]), failed $($sum[1]), passed $($sum[2]), skipped $($sum[3])"
    if ($code -eq 0) { Add-Result 'Test' 'PASS' $summary } else { Add-Result 'Test' 'FAIL' "$summary (exit $code, see test.log)" }
    $failedLines = @(Select-String -LiteralPath $log -Pattern '^\s*failed ' | Select-Object -First 30 | ForEach-Object { $_.Line.Trim() })
    if ($failedLines.Count -gt 0) { Add-Section 'Failed tests' ("````text`n" + ($failedLines -join "`n") + "`n````") }
}

# --- Publish ---------------------------------------------------------------------------------------------------------
# Native AOT links with MSVC, which it finds through vswhere (Visual Studio or the Build Tools with the C++ workload).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if ($Publish -and $onWindows -and -not (Test-Path -LiteralPath $vswhere)) {
    Add-Result 'Publish' 'FAIL' ("Native AOT needs the MSVC linker: install the Visual Studio Build Tools with the C++ workload " +
        "(winget install Microsoft.VisualStudio.2022.BuildTools --override ""--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended""), " +
        "or put the CI artifact murmur-win-x64 into artifacts/publish/win-x64 and run without -Publish")
    $Publish = $false
}
if ($Publish -and (Require-Windows 'Publish')) {
    $log1 = Join-Path $runDir 'publish-desktop.log'
    $log2 = Join-Path $runDir 'publish-transcription.log'
    $code1 = Invoke-Logged $log1 { dotnet publish src/Murmur.Desktop -c Release -f net11.0-windows10.0.22621.0 -r win-x64 -o $publishDir }
    $code2 = Invoke-Logged $log2 { dotnet publish src/Murmur.Transcription -c Release -r win-x64 -o $publishDir }
    $hits = @(Select-String -LiteralPath $log1, $log2 -Pattern ': (warning|error) ')
    if ($code1 -ne 0 -or $code2 -ne 0) {
        Add-Result 'Publish' 'FAIL' "dotnet publish exit $code1 / $code2 (see publish-*.log)"
    }
    elseif ($hits.Count -gt 0) {
        Add-Result 'Publish' 'FAIL' "$($hits.Count) publish warning/error line(s) (see publish-*.log)"
        Add-Section 'Publish warnings' ("````text`n" + (($hits | Select-Object -First 30 | ForEach-Object Line) -join "`n") + "`n````")
    }
    else {
        $exeMb = [math]::Round((Get-Item -LiteralPath $murmurExe).Length / 1MB, 1)
        $dirMb = [math]::Round(((Get-ChildItem -LiteralPath $publishDir -File -Recurse | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
        Add-Result 'Publish' 'PASS' "no warnings; Murmur.exe $exeMb MB, folder $dirMb MB"
    }
}

# --- Startup (also used on the installed exe by -Installer) -----------------------------------------------------------
# -BudgetIsInfo: every run must still start and render a frame, but a median over the budget is only reported (INFO).
# The installed exe is the same binary the Startup step measures, so that step alone enforces the budget; a second
# measurement of a ~400 ms median on a shared runner would only add noise.
function Invoke-StartupCheck([string] $Step, [string] $Exe, [string] $Name, [switch] $BudgetIsInfo) {
    $out = Join-Path $runDir "$Name.json"
    $code = Invoke-Logged (Join-Path $runDir "$Name.log") { pwsh -NoProfile -File scripts/measure-startup.ps1 -Exe $Exe -Runs 7 -BudgetMs 400 -Out $out }
    $json = if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Raw | ConvertFrom-Json } else { $null }
    $summary = if ($json -and $json.stats) {
        "median $([math]::Round([double]$json.stats.median)) ms (budget 400), first run $([math]::Round([double]$json.cold.startupMs)) ms"
    } else { "see $Name.log" }
    $status = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
    if ($status -eq 'FAIL' -and $BudgetIsInfo -and $json -and $json.stats -and [int]$json.failedRuns -eq 0 -and $json.withinBudget -eq $false) {
        $status = 'INFO'
        $summary += '; over budget here, the budget is checked by the Startup step'
    }
    Add-Result $Step $status $summary
    $md = Read-Text ([System.IO.Path]::ChangeExtension($out, '.md'))
    if ($md) { Add-Section $Step $md }
}

if ($Startup -and (Require-Windows 'Startup') -and (Test-Published 'Startup' $murmurExe)) {
    Invoke-StartupCheck 'Startup' $murmurExe 'startup'
}

# --- Installer -------------------------------------------------------------------------------------------------------
# Builds the setup exe from the publish folder, installs it silently into a temp folder as the current user, starts the
# installed app, uninstalls silently and checks what is left. The installer keeps %LOCALAPPDATA%\Murmur (user data) on a
# silent uninstall; a sentinel file in it proves that.
function Wait-Until([scriptblock] $Condition, [int] $TimeoutS) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutS)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return [bool](& $Condition)
}

# Number of RT_GROUP_ICON resources of an exe (the icon set that ApplicationIcon embeds), -1 when it cannot be opened.
function Get-IconGroupCount([string] $Path) {
    if (-not ('Murmur.PeIcons' -as [type])) {
        Add-Type -Namespace Murmur -Name PeIcons -MemberDefinition @'
public delegate bool EnumProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumProc callback, IntPtr param);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool FreeLibrary(IntPtr module);
public static int CountGroupIcons(string path)
{
    IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x2); // LOAD_LIBRARY_AS_DATAFILE
    if (module == IntPtr.Zero) return -1;
    int count = 0;
    EnumResourceNames(module, (IntPtr)14, (m, t, n, p) => { count++; return true; }, IntPtr.Zero); // RT_GROUP_ICON
    FreeLibrary(module);
    return count;
}
'@
    }
    return [Murmur.PeIcons]::CountGroupIcons($Path)
}

function Invoke-InstallerCheck {
    $appId = [regex]::Match((Get-Content -LiteralPath 'installer/Murmur.iss' -Raw), 'AppId=\{\{([0-9A-Fa-f-]+)\}').Groups[1].Value
    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$appId}_is1"
    if (-not $appId) { Add-Result 'Installer' 'FAIL' 'AppId not found in installer/Murmur.iss'; return }
    if (Test-Path -LiteralPath $uninstallKey) {
        Add-Result 'Installer' 'SKIP' 'Murmur is installed for this user (same AppId): uninstall it first, the smoke test would replace its uninstaller entry'
        return
    }

    if ($SetupExe) {
        $setup = Get-Item -LiteralPath $SetupExe -ErrorAction SilentlyContinue
        if (-not $setup) { Add-Result 'Installer' 'FAIL' "-SetupExe not found: $SetupExe"; return }
    }
    else {
        $installerDir = Join-Path $runDir 'installer'
        $code = Invoke-Logged (Join-Path $runDir 'installer-build.log') { pwsh -NoProfile -File scripts/build-installer.ps1 -SkipPublish -PublishDir $publishDir -OutDir $installerDir }
        $setup = Get-ChildItem -LiteralPath $installerDir -Filter 'Murmur-Setup-*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($code -ne 0 -or -not $setup) { Add-Result 'Installer' 'FAIL' "build-installer.ps1 failed (exit $code, see installer-build.log)"; return }
        $payload = Join-Path $installerDir 'payload.md'
        if (Test-Path -LiteralPath $payload) { Add-Section 'Installer payload' (Get-Content -LiteralPath $payload -Raw) }
    }

    $installDir = Join-Path ([System.IO.Path]::GetTempPath()) ('murmur-installer-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $installLog = Join-Path $runDir 'installer-install.log'
    $uninstallLog = Join-Path $runDir 'installer-uninstall.log'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Murmur.lnk'
    $dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Murmur'
    $sentinel = Join-Path $dataDir 'installer-smoke.keep'
    $dataDirExisted = Test-Path -LiteralPath $dataDir
    $problems = [System.Collections.Generic.List[string]]::new()
    $installed = $false
    $keyExisted = Test-Path -LiteralPath $uninstallKey
    $version = [regex]::Match($setup.Name, '^Murmur-Setup-(.+)\.exe$').Groups[1].Value
    $facts = ''

    try {
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        Set-Content -LiteralPath $sentinel -Value 'created by verify-local.ps1 -Installer; must survive a silent uninstall' -Encoding utf8

        $p = Start-Process -FilePath $setup.FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$installDir`"", "/LOG=`"$installLog`"") -Wait -PassThru
        if ($p.ExitCode -ne 0) { $problems.Add("setup exit code $($p.ExitCode) (see installer-install.log)") }
        $installed = Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe')

        foreach ($f in 'Murmur.exe', 'Murmur.Transcription.exe', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll', 'unins000.exe') {
            if (-not (Test-Path -LiteralPath (Join-Path $installDir $f))) { $problems.Add("$f not installed") }
        }
        if (@(Get-ChildItem -LiteralPath $installDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue).Count -gt 0) { $problems.Add('*.pdb files were installed') }
        if (-not (Test-Path -LiteralPath $uninstallKey)) { $problems.Add('no uninstall entry in HKCU') }
        if (-not (Test-Path -LiteralPath $shortcut)) { $problems.Add('no Start-menu shortcut') }

        $installedExe = Join-Path $installDir 'Murmur.exe'
        if (Test-Path -LiteralPath $installedExe) {
            # The version and the icon of the installed exe: the version must be there (the Version property of
            # Directory.Build.props), a missing icon resource is only reported.
            $productVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
            if ([string]::IsNullOrWhiteSpace($productVersion)) { $problems.Add('Murmur.exe has no VersionInfo') }
            else { $facts += "; exe version $productVersion" }
            if ($productVersion -and $version -and -not $productVersion.StartsWith($version)) { Write-Host "Note: Murmur.exe ProductVersion '$productVersion' does not start with the setup version '$version'." -ForegroundColor Yellow }
            $icons = Get-IconGroupCount $installedExe
            $facts += $(if ($icons -gt 0) { '; exe has an icon resource' } else { '; exe has NO icon resource' })
            Write-Host "Installed exe: ProductVersion '$productVersion', $icons icon group resource(s)."
            Invoke-StartupCheck 'Installer startup' $installedExe 'installer-startup' -BudgetIsInfo
        }
    }
    finally {
        if ($installed) {
            $u = Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$uninstallLog`"") -Wait -PassThru
            if ($u.ExitCode -ne 0) { $problems.Add("uninstaller exit code $($u.ExitCode) (see installer-uninstall.log)") }
            # unins000.exe hands over to a copy of itself in %TEMP% and may return before the files are gone.
            if (-not (Wait-Until { -not (Test-Path -LiteralPath $installDir) } 60)) { $problems.Add('install folder still exists after the silent uninstall') }
            if (Test-Path -LiteralPath $uninstallKey) { $problems.Add('uninstall entry still in HKCU') }
            if (Test-Path -LiteralPath $shortcut) { $problems.Add('Start-menu shortcut still there') }
        }
        # A failed or partial install can leave its uninstall entry behind; remove it only when it points at our temp folder.
        if (-not $keyExisted -and (Test-Path -LiteralPath $uninstallKey)) {
            $entry = Get-ItemProperty -LiteralPath $uninstallKey
            $location = 'Inno Setup: App Path', 'InstallLocation' | ForEach-Object { if ($entry.PSObject.Properties[$_]) { $entry.$_ } } | Where-Object { $_ } | Select-Object -First 1
            if ($location -and $location.TrimEnd('\') -ieq $installDir.TrimEnd('\')) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue }
        }
        if (-not (Test-Path -LiteralPath $sentinel)) { $problems.Add('user data was deleted by a silent uninstall') }
        Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
        if (-not $dataDirExisted -and (Test-Path -LiteralPath $dataDir) -and -not (Get-ChildItem -LiteralPath $dataDir -Force)) {
            Remove-Item -LiteralPath $dataDir -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $sizeMb = [math]::Round($setup.Length / 1MB, 1)
    if ($problems.Count -eq 0) { Add-Result 'Installer' 'PASS' "$($setup.Name) $sizeMb MB; silent install, uninstall, user data kept$facts" }
    else { Add-Result 'Installer' 'FAIL' "$($setup.Name) $sizeMb MB: $($problems -join '; ')" }
}

if ($Installer -and (Require-Windows 'Installer') -and (Test-Published 'Installer' $murmurExe)) {
    Invoke-InstallerCheck
}

# --- Recordings (shared by -Capture and -Perf) -------------------------------------------------------------------------
$toneStarted = $false
function Start-TestTone([int] $Seconds) {
    if (-not $Tone) { return }
    Stop-TestTone
    $line = & ./scripts/start-tone.ps1 -DurationS $Seconds | Select-Object -Last 1
    Write-Host $line
    $script:toneStarted = $true
}

function Stop-TestTone {
    $pidFile = 'artifacts/tone/tone.pid'
    if ($script:toneStarted -and (Test-Path $pidFile)) {
        Stop-Process -Id ([int](Get-Content $pidFile -Raw).Trim()) -Force -ErrorAction SilentlyContinue
    }
    $script:toneStarted = $false
}

function Invoke-Recording([string] $Step, [int] $Seconds, [bool] $WithScreen, [string[]] $CheckArgs) {
    $name = $Step.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $perf = Join-Path $runDir "$name.json"
    $md = Join-Path $runDir "$name.md"
    $screenArg = @(if ($WithScreen) { '-Screen' })
    $code = Invoke-Logged (Join-Path $runDir "$name.log") { pwsh -NoProfile -File scripts/run-autorecord.ps1 -Exe $murmurExe -Seconds $Seconds @screenArg -PerfOut $perf }
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $perf)) {
        Add-Result $Step 'FAIL' "Murmur.exe --autorecord failed (exit $code, see $name.log)"
        return
    }

    $toneArg = @(if ($Tone) { '-ExpectTone' })
    $screenCheck = @(if ($WithScreen) { '-ExpectScreen' })
    $code = Invoke-Logged (Join-Path $runDir "$name-check.log") { pwsh -NoProfile -File scripts/check-recording.ps1 -PerfJson $perf @toneArg @screenCheck -Title $Step -MarkdownOut $md @CheckArgs }
    $json = Get-Content -LiteralPath $perf -Raw | ConvertFrom-Json
    $parts = @()
    foreach ($field in 'droppedFrames', 'cpuAvgPct', 'wsMaxMb') {
        if ($json.PSObject.Properties[$field]) { $parts += "$field $($json.$field)" }
    }
    Add-Result $Step ($(if ($code -eq 0) { 'PASS' } else { 'FAIL' })) ($parts -join ', ')
    $text = Read-Text $md
    if ($text) { Add-Section $Step $text }
}

try {
    if ($Capture -and (Require-Windows 'Capture') -and (Test-Published 'Capture' $murmurExe)) {
        Start-TestTone 300
        Invoke-Recording 'Capture 60 s audio + screen' 60 $true @()
    }

    if ($Perf -and (Require-Windows 'Perf') -and (Test-Published 'Perf' $murmurExe)) {
        Start-TestTone ($PerfMinutes * 60 + 600 + 1200)
        Invoke-Recording "Perf $PerfMinutes min audio only" ($PerfMinutes * 60) $false @('-IdleCpuBudgetPct', '1', '-CpuBudgetPct', '3', '-RamBudgetMb', '150', '-MaxWsSlopeMbPerMin', '1')
        Invoke-Recording 'Perf 10 min audio + screen' 600 $true @('-IdleCpuBudgetPct', '1', '-CpuBudgetPct', '8', '-MaxWsSlopeMbPerMin', '1')
    }
}
finally {
    Stop-TestTone
}

# --- ASR bench -------------------------------------------------------------------------------------------------------
if ($Asr -and (Require-Windows 'ASR') -and (Test-Published 'ASR' $transcriptionExe)) {
    $parakeet = Join-Path $ModelsDir 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8'
    $vad = Join-Path $ModelsDir 'silero_vad.onnx'
    if (-not (Test-Path -LiteralPath (Join-Path $parakeet 'encoder.int8.onnx')) -or -not (Test-Path -LiteralPath $vad)) {
        Add-Result 'ASR' 'SKIP' "models not found in $ModelsDir (run scripts/fetch-models.ps1)"
    }
    else {
        $wavs = (@('en', 'de', 'es', 'fr') | ForEach-Object { Join-Path $parakeet "test_wavs/$_.wav" }) -join ','
        $out = Join-Path $runDir 'asr-bench.json'
        $code = Invoke-Logged (Join-Path $runDir 'asr.log') { & $transcriptionExe --bench --model-dir $parakeet --vad $vad --wav $wavs --seconds 3600 --out $out }
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $out)) {
            Add-Result 'ASR' 'FAIL' "bench failed (exit $code, see asr.log)"
        }
        else {
            $b = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
            $seconds = [math]::Round($b.transcribeMs / 1000, 1)
            $ok = $b.audioSeconds -ge 3600 -and $b.transcribeMs -le 180000
            Add-Result 'ASR' ($(if ($ok) { 'PASS' } else { 'FAIL' })) "1 h audio -> text in $seconds s (budget 180 s), $($b.realtimeFactorX)x realtime, peak RSS $($b.peakRssMb) MB, $($b.machine.cpu)"
        }
    }
}

# --- AI eval ---------------------------------------------------------------------------------------------------------
if ($AiEval) {
    $manifest = Join-Path $ModelsDir 'gguf/models.json'
    $server = Join-Path $ModelsDir ($(if ($onWindows) { 'llama.cpp/llama-server.exe' } else { 'llama.cpp/llama-server' }))
    if (-not (Test-Path -LiteralPath $manifest) -or -not (Test-Path -LiteralPath $server)) {
        Add-Result 'AI eval' 'SKIP' "AI models not found in $ModelsDir (run scripts/fetch-models.ps1 -Ai)"
    }
    else {
        $m = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $env:MURMUR_MODELS_DIR = $ModelsDir
        $env:MURMUR_LLM_MODEL = $m.chat
        $env:MURMUR_EMBED_MODEL = $m.embed
        $env:MURMUR_LLAMA_SERVER = $server
        $out = Join-Path $runDir 'ai-eval.json'
        $md = Join-Path $runDir 'ai-eval.md'
        $code = Invoke-Logged (Join-Path $runDir 'ai-eval.log') {
            dotnet run -c Release --project tools/Murmur.AiEval/src -- --fixture tests/fixtures/meeting-60min.json --questions tests/fixtures/meeting-60min.questions.json --threads ([Environment]::ProcessorCount) --out $out --markdown $md
        }
        $summary = if (Test-Path -LiteralPath $out) {
            $r = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
            "$($r.passed)/$($r.total) ($([math]::Round($r.rate * 100)) %, threshold 80 %), avg answer $([math]::Round($r.timings.avgAnswerMs / 1000, 1)) s"
        } else { "no report (exit $code, see ai-eval.log)" }
        Add-Result 'AI eval' ($(if ($code -eq 0) { 'PASS' } elseif ($code -eq 2) { 'SKIP' } else { 'FAIL' })) $summary
        $text = Read-Text $md
        if ($text) { Add-Section 'AI eval' $text }
    }
}

# On GitHub Actions a requested -Installer that was skipped must not look green.
if ($env:GITHUB_ACTIONS -eq 'true' -and $PSBoundParameters.ContainsKey('Installer')) {
    foreach ($r in $results) {
        if ($r.Step -eq 'Installer' -and $r.Status -eq 'SKIP') { $r.Status = 'FAIL'; $r.Summary = "skipped on CI, which counts as a failure: $($r.Summary)" }
    }
}

# --- Report ----------------------------------------------------------------------------------------------------------
$commit = (git rev-parse --short HEAD 2>$null)
$branch = (git rev-parse --abbrev-ref HEAD 2>$null)
$dirty = if (git status --porcelain 2>$null) { ' (uncommitted changes)' } else { '' }
$machine = if ($onWindows) {
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
    $gpu = ((Get-CimInstance Win32_VideoController | ForEach-Object Name) -join ', ')
    $ram = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
    "$($os.Caption) $($os.Version), $cpu, $ram GB RAM, GPU: $gpu"
} else { [System.Runtime.InteropServices.RuntimeInformation]::OSDescription }

$report = [System.Text.StringBuilder]::new()
[void]$report.AppendLine('# Murmur local verification').AppendLine()
[void]$report.AppendLine("- Commit: ``$commit`` on ``$branch``$dirty")
[void]$report.AppendLine("- Machine: $machine")
[void]$report.AppendLine("- When: $(Get-Date -Format 'yyyy-MM-dd HH:mm') (local)")
[void]$report.AppendLine("- Test tone: $(if ($Tone) { 'on' } else { 'off' })").AppendLine()
[void]$report.AppendLine('| Step | Result | Summary |').AppendLine('|---|---|---|')
foreach ($r in $results) { [void]$report.AppendLine("| $($r.Step) | $($r.Status) | $($r.Summary -replace '\|', '\|') |") }
[void]$report.AppendLine().Append($sections.ToString())
$reportPath = Join-Path $runDir 'report.md'
Set-Content -LiteralPath $reportPath -Value $report.ToString() -Encoding utf8

Write-Host ''
Write-Host "Report: $reportPath"
$failed = @($results | Where-Object Status -eq 'FAIL').Count
exit ($(if ($failed -gt 0) { 1 } else { 0 }))
