#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Measures Murmur cold-start time and enforces the startup budget.

.DESCRIPTION
    Launches the app N times with `--startup-probe <json path>`. The app writes
    {"startupMs": number, "utc": string} after its first rendered frame and exits
    (startupMs is measured in-process: process start -> first rendered frame).
    The script also records the external wall time (launch -> exit) of every run.

    Run 1 is the cold run (first launch after extraction) and is reported separately;
    the statistics (median/p95/min/max) cover all successful runs.

    Writes <Out> (JSON) and a markdown table next to it (<Out without extension>.md), and prints
    the table to stdout. Exit code 1 if the median startupMs exceeds -BudgetMs or any run failed.

.PARAMETER Exe
    Path to Murmur.exe.
.PARAMETER Runs
    Number of launches (default 7).
.PARAMETER BudgetMs
    Median startupMs budget in milliseconds (default 400).
.PARAMETER Out
    Output JSON path (default artifacts/perf/startup.json).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Exe,
    [ValidateRange(1, 1000)] [int] $Runs = 7,
    [double] $BudgetMs = 400,
    [string] $Out = 'artifacts/perf/startup.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TimeoutMs = 30000

function Get-Percentile {
    # Nearest-rank percentile of a pre-sorted array.
    param([double[]] $Sorted, [double] $P)
    $rank = [int][Math]::Ceiling($P / 100.0 * $Sorted.Length)
    if ($rank -lt 1) { $rank = 1 }
    return $Sorted[$rank - 1]
}

function Get-Median {
    param([double[]] $Sorted)
    $n = $Sorted.Length
    if ($n % 2 -eq 1) { return $Sorted[($n - 1) / 2] }
    return ($Sorted[$n / 2 - 1] + $Sorted[$n / 2]) / 2.0
}

function Get-MachineInfo {
    $os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    $cores = [Environment]::ProcessorCount
    $cpu = 'unknown'
    $ramMb = 0
    if ($IsWindows) {
        try {
            $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
            $ramMb = [int]((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB)
        }
        catch { Write-Warning "Could not query WMI: $_" }
    }
    elseif ($IsLinux) {
        try {
            $line = Get-Content /proc/cpuinfo | Where-Object { $_ -like 'model name*' } | Select-Object -First 1
            if ($line) { $cpu = ($line -split ':', 2)[1].Trim() }
            $mem = Get-Content /proc/meminfo | Where-Object { $_ -like 'MemTotal:*' } | Select-Object -First 1
            if ($mem) { $ramMb = [int]((($mem -split '\s+')[1]) / 1024) }
        }
        catch { Write-Warning "Could not read /proc: $_" }
    }
    return [ordered]@{
        os           = $os
        cpu          = $cpu
        logicalCores = $cores
        ramMb        = $ramMb
    }
}

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    throw "Executable not found: $Exe"
}
$exePath = (Resolve-Path -LiteralPath $Exe).Path

$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("murmur-startup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
try {
    for ($i = 1; $i -le $Runs; $i++) {
        $probe = Join-Path $tmpDir "probe-$i.json"
        $run = [ordered]@{
            run       = $i
            cold      = ($i -eq 1)
            ok        = $false
            startupMs = $null
            wallMs    = $null
            exitCode  = $null
            error     = $null
        }

        $psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WorkingDirectory = Split-Path -Parent $exePath
        $psi.ArgumentList.Add('--startup-probe')
        $psi.ArgumentList.Add($probe)

        $proc = $null
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $proc = [System.Diagnostics.Process]::Start($psi)
            if (-not $proc.WaitForExit($TimeoutMs)) {
                $sw.Stop()
                $run.error = "timeout after $($TimeoutMs / 1000) s"
                try { $proc.Kill($true) } catch { }
                $proc.WaitForExit()
            }
            else {
                $sw.Stop()
                $run.wallMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
                $run.exitCode = $proc.ExitCode
                if ($proc.ExitCode -ne 0) {
                    $run.error = "exit code $($proc.ExitCode)"
                }
                elseif (-not (Test-Path -LiteralPath $probe)) {
                    $run.error = 'probe file was not written'
                }
                else {
                    $json = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
                    if ($null -eq $json -or $null -eq $json.PSObject.Properties['startupMs'] -or $json.startupMs -isnot [ValueType]) {
                        $run.error = 'probe file has no numeric startupMs'
                    }
                    else {
                        $run.startupMs = [Math]::Round([double]$json.startupMs, 1)
                        $run.ok = $true
                    }
                }
            }
        }
        catch {
            $run.error = $_.Exception.Message
        }
        finally {
            if ($null -ne $proc) { $proc.Dispose() }
        }

        $results.Add([pscustomobject]$run)
        $state = if ($run.ok) { "startupMs=$($run.startupMs) wallMs=$($run.wallMs)" } else { "FAILED: $($run.error)" }
        Write-Host ("run {0}/{1}{2}: {3}" -f $i, $Runs, $(if ($run.cold) { ' (cold)' } else { '' }), $state)
    }
}
finally {
    Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

$okRuns = @($results | Where-Object { $_.ok })
$failed = $Runs - $okRuns.Count

$stats = $null
if ($okRuns.Count -gt 0) {
    $sorted = [double[]]@($okRuns | ForEach-Object { [double]$_.startupMs } | Sort-Object)
    $stats = [ordered]@{
        count  = $sorted.Length
        median = [Math]::Round((Get-Median $sorted), 1)
        p95    = [Math]::Round((Get-Percentile $sorted 95), 1)
        min    = $sorted[0]
        max    = $sorted[$sorted.Length - 1]
    }
}

$cold = $results[0]
$withinBudget = ($null -ne $stats) -and ($stats.median -le $BudgetMs)
$pass = $withinBudget -and ($failed -eq 0)

$report = [ordered]@{
    exe          = $exePath
    runs         = $results
    cold         = [ordered]@{ startupMs = $cold.startupMs; wallMs = $cold.wallMs; ok = $cold.ok }
    stats        = $stats
    budgetMs     = $BudgetMs
    failedRuns   = $failed
    withinBudget = $withinBudget
    pass         = $pass
    machine      = Get-MachineInfo
    utc          = [DateTime]::UtcNow.ToString('o')
}

$outFull = [System.IO.Path]::GetFullPath($Out, (Get-Location).Path)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outFull) | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outFull -Encoding utf8

# Markdown table.
function Format-Ms($v) { if ($null -eq $v) { 'n/a' } else { ('{0:N1}' -f $v) } }
$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine('### Startup (process start -> first rendered frame)')
[void]$md.AppendLine()
[void]$md.AppendLine('| Metric | startupMs |')
[void]$md.AppendLine('|---|---:|')
if ($null -ne $stats) {
    [void]$md.AppendLine("| Median | $(Format-Ms $stats.median) |")
    [void]$md.AppendLine("| p95 | $(Format-Ms $stats.p95) |")
    [void]$md.AppendLine("| Min | $(Format-Ms $stats.min) |")
    [void]$md.AppendLine("| Max | $(Format-Ms $stats.max) |")
}
[void]$md.AppendLine("| Cold (run 1) | $(Format-Ms $cold.startupMs) |")
[void]$md.AppendLine("| Budget | $(Format-Ms $BudgetMs) |")
$verdict = if ($pass) { 'PASS' } elseif ($failed -gt 0) { "FAIL ($failed run(s) failed)" } else { 'FAIL (median over budget)' }
[void]$md.AppendLine("| **Result** | **$verdict** |")
[void]$md.AppendLine()
[void]$md.AppendLine('| Run | startupMs | wall ms | Status |')
[void]$md.AppendLine('|---:|---:|---:|---|')
foreach ($r in $results) {
    $label = if ($r.cold) { "$($r.run) (cold)" } else { "$($r.run)" }
    $status = if ($r.ok) { 'ok' } else { "failed: $($r.error)" }
    [void]$md.AppendLine("| $label | $(Format-Ms $r.startupMs) | $(Format-Ms $r.wallMs) | $status |")
}
[void]$md.AppendLine()
$m = $report.machine
[void]$md.AppendLine("Machine: $($m.os), $($m.cpu), $($m.logicalCores) logical cores, $($m.ramMb) MB RAM")

$mdText = $md.ToString()
$mdPath = [System.IO.Path]::ChangeExtension($outFull, '.md')
Set-Content -LiteralPath $mdPath -Value $mdText -Encoding utf8
Write-Host $mdText

if (-not $pass) {
    if ($failed -gt 0) { Write-Error "$failed of $Runs run(s) failed." -ErrorAction Continue }
    if (-not $withinBudget) { Write-Error "Median startup exceeds budget of $BudgetMs ms." -ErrorAction Continue }
    exit 1
}
exit 0
