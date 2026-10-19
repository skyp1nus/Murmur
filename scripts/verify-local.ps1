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
      -Capture   60 s recording with audio + screen (Murmur.exe --autorecord) + scripts/check-recording.ps1
      -Perf      long recordings with the PLAN.md budgets: -PerfMinutes audio only (CPU < 3 %, RAM < 150 MB, no growth),
                 then 10 min audio + screen (CPU < 8 %, working set must not grow)
      -Asr       1 h of audio -> text with Murmur.Transcription --bench, budget 180 s (needs scripts/fetch-models.ps1)
      -AiEval    "AI answer leads to the right minute" with Gemma 4 (needs scripts/fetch-models.ps1 -Ai)
      -All       everything above

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
    [switch] $Capture,
    [switch] $Perf,
    [switch] $Asr,
    [switch] $AiEval,
    [switch] $All,
    [switch] $Tone,
    [ValidateRange(1, 240)] [int] $PerfMinutes = 60,
    [string] $ModelsDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
Set-Location -LiteralPath $repo

if ($All) { $Build = $Test = $Publish = $Startup = $Capture = $Perf = $Asr = $AiEval = $true }
if (-not ($Build -or $Test -or $Publish -or $Startup -or $Capture -or $Perf -or $Asr -or $AiEval)) {
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
    if ($actual -ne $expected) {
        Add-Result 'Build' 'FAIL' "SDK mismatch: dotnet --version is $actual, global.json pins $expected"
        $buildOk = $false
    }
    else {
        $log = Join-Path $runDir 'build.log'
        $code = Invoke-Logged $log { dotnet build Murmur.sln -c Release }
        $warnings = @(Select-String -LiteralPath $log -Pattern ': warning ' -SimpleMatch:$false).Count
        if ($code -eq 0) { Add-Result 'Build' 'PASS' "SDK $actual, 0 errors, $warnings warning line(s)" }
        else { Add-Result 'Build' 'FAIL' "dotnet build exit $code (see build.log)"; $buildOk = $false }
    }
}

# --- Test ------------------------------------------------------------------------------------------------------------
if ($Test) {
    $log = Join-Path $runDir 'test.log'
    $trx = Join-Path $runDir 'test-results'
    $noBuild = if ($Build -and $buildOk) { @('--no-build') } else { @() }
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

# --- Startup ---------------------------------------------------------------------------------------------------------
if ($Startup -and (Require-Windows 'Startup') -and (Test-Published 'Startup' $murmurExe)) {
    $out = Join-Path $runDir 'startup.json'
    $code = Invoke-Logged (Join-Path $runDir 'startup.log') { pwsh -NoProfile -File scripts/measure-startup.ps1 -Exe $murmurExe -Runs 7 -BudgetMs 400 -Out $out }
    $json = if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Raw | ConvertFrom-Json } else { $null }
    $summary = if ($json -and $json.stats) {
        "median $([math]::Round([double]$json.stats.median)) ms (budget 400), first run $([math]::Round([double]$json.cold.startupMs)) ms"
    } else { 'see startup.log' }
    Add-Result 'Startup' ($(if ($code -eq 0) { 'PASS' } else { 'FAIL' })) $summary
    $md = Read-Text ([System.IO.Path]::ChangeExtension($out, '.md'))
    if ($md) { Add-Section 'Startup' $md }
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
    $screenArg = if ($WithScreen) { @('-Screen') } else { @() }
    $code = Invoke-Logged (Join-Path $runDir "$name.log") { pwsh -NoProfile -File scripts/run-autorecord.ps1 -Exe $murmurExe -Seconds $Seconds @screenArg -PerfOut $perf }
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $perf)) {
        Add-Result $Step 'FAIL' "Murmur.exe --autorecord failed (exit $code, see $name.log)"
        return
    }

    $toneArg = if ($Tone) { @('-ExpectTone') } else { @() }
    $screenCheck = if ($WithScreen) { @('-ExpectScreen') } else { @() }
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
