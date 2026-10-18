#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Runs `Murmur.exe --autorecord <seconds> [--screen] --perf-out <json>` and waits for it to finish.

.DESCRIPTION
    Contract with the app: the process records for -Seconds, writes the perf JSON to -PerfOut and exits.
    The script kills the process when it is still alive after -Seconds + -GraceS, and fails when the exit code
    is not 0 or the JSON was not written. Prints wall time and exit code.

.PARAMETER Exe
    Path to Murmur.exe.
.PARAMETER Seconds
    Recording length in seconds.
.PARAMETER Screen
    Also record the screen (--screen).
.PARAMETER PerfOut
    Output JSON path (parent directory is created).
.PARAMETER GraceS
    Extra seconds allowed for startup/shutdown before the process is killed (default 120).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Exe,
    [Parameter(Mandatory = $true)] [ValidateRange(1, 86400)] [int] $Seconds,
    [switch] $Screen,
    [Parameter(Mandatory = $true)] [string] $PerfOut,
    [ValidateRange(1, 3600)] [int] $GraceS = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$exeFull = [System.IO.Path]::GetFullPath($Exe, (Get-Location).ProviderPath)
$outFull = [System.IO.Path]::GetFullPath($PerfOut, (Get-Location).ProviderPath)
if (-not (Test-Path -LiteralPath $exeFull)) { throw "Murmur.exe not found: $exeFull" }

$outDir = [System.IO.Path]::GetDirectoryName($outFull)
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
if (Test-Path -LiteralPath $outFull) { Remove-Item -LiteralPath $outFull -Force }

$argList = [System.Collections.Generic.List[string]]::new()
$argList.Add('--autorecord')
$argList.Add([string]$Seconds)
if ($Screen) { $argList.Add('--screen') }
$argList.Add('--perf-out')
$argList.Add($outFull)

Write-Host ("Starting: {0} {1}" -f $exeFull, ($argList -join ' '))
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exeFull -ArgumentList $argList.ToArray() -PassThru
$limitMs = ($Seconds + $GraceS) * 1000
if (-not $proc.WaitForExit($limitMs)) {
    Write-Host "::error::Murmur.exe did not exit within $($Seconds + $GraceS) s, killing it."
    try { $proc.Kill($true) } catch { Write-Host "Kill failed: $($_.Exception.Message)" }
    exit 1
}
$sw.Stop()
Write-Host ("Murmur.exe exited with code {0} after {1:N1} s (requested {2} s)" -f $proc.ExitCode, $sw.Elapsed.TotalSeconds, $Seconds)

if ($proc.ExitCode -ne 0) {
    Write-Host "::error::Murmur.exe exit code $($proc.ExitCode)"
    exit 1
}
if (-not (Test-Path -LiteralPath $outFull)) {
    Write-Host "::error::Perf JSON was not written: $outFull"
    exit 1
}
Write-Host "Perf JSON: $outFull"
