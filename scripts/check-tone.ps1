#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Reports whether the looping-tone player started by scripts/start-tone.ps1 is still alive.

.DESCRIPTION
    Reads the player PID from -PidFile and checks it with Get-Process. Writes "tone: alive" or "tone: DEAD"
    to $GITHUB_STEP_SUMMARY (when set) and emits a ::warning annotation for a dead player, so that a dead
    player is reported as such and not as recording silence. Includes the tail of the player log when dead.

    Never fails the step: the recording checks stay the source of truth; exit code is always 0.

.PARAMETER Label
    Where the check runs, used in the message (for example "before Autorecord 60 s").
.PARAMETER PidFile
    PID file written by start-tone.ps1 (default artifacts/tone/tone.pid).
#>
[CmdletBinding()]
param(
    [string] $Label = 'check',
    [string] $PidFile = 'artifacts/tone/tone.pid'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$alive = $false
$detail = ''
if (-not (Test-Path -LiteralPath $PidFile)) {
    $detail = "no PID file at $PidFile"
}
else {
    $raw = (Get-Content -LiteralPath $PidFile -Raw).Trim()
    $toneId = 0
    if (-not [int]::TryParse($raw, [ref] $toneId)) {
        $detail = "PID file content is not a number: '$raw'"
    }
    else {
        $proc = Get-Process -Id $toneId -ErrorAction SilentlyContinue
        if ($null -ne $proc) { $alive = $true; $detail = "pid $toneId" }
        else { $detail = "pid $toneId is not running" }
    }
}

$status = if ($alive) { 'tone: alive' } else { 'tone: DEAD' }
$line = "$status ($Label; $detail)"
Write-Host $line
if ($env:GITHUB_STEP_SUMMARY) { "- $line" | Out-File -LiteralPath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }

if (-not $alive) {
    Write-Host "::warning::Tone player is dead ($Label; $detail): a silent recording is a consequence, not the cause."
    $dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($PidFile, (Get-Location).ProviderPath))
    foreach ($log in @(Get-ChildItem -LiteralPath $dir -Filter 'play-tone-*.log' -ErrorAction SilentlyContinue)) {
        Write-Host "--- $($log.Name) (tail) ---"
        Get-Content -LiteralPath $log.FullName -Tail 20 | Write-Host
    }
}

exit 0
