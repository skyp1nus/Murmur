#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Design parity: renders every artboard of docs/design/source with the app (headless, English, the canvas demo data),
    compares it with docs/design/reference/<Board>.png and prints the report.

.DESCRIPTION
    Output: artifacts/design-parity/ (<Board>.app.png, .diff.png, .side.png and report.md). Set MURMUR_PARITY_DIR to write elsewhere.

.PARAMETER NoBuild
    Reuse the last Release build of tests/Murmur.App.Tests.

.PARAMETER Board
    Only these boards (for example Main, SessionDark); default: all 22.

.EXAMPLE
    scripts/design/parity.ps1
    scripts/design/parity.ps1 -NoBuild -Board Main, SettingsDark
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [string[]]$Board = @()
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
Push-Location $root
try {
    $out = if ($env:MURMUR_PARITY_DIR) { $env:MURMUR_PARITY_DIR } else { Join-Path $root 'artifacts' 'design-parity' }
    if ($Board.Count -eq 0 -and (Test-Path $out)) {
        # A full run starts clean, so no result of an earlier run can pass for a current one (single boards keep the rest).
        Remove-Item -Recurse -Force $out
    }

    $testArgs = @('test', '--project', 'tests/Murmur.App.Tests', '-c', 'Release')
    if ($NoBuild) { $testArgs += '--no-build' }
    if ($Board.Count -gt 0) { $env:MURMUR_PARITY_BOARDS = $Board -join ',' }
    $testArgs += @('--filter-trait', 'Category=DesignParity')

    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }

    Write-Host ''
    Write-Host "Report: $(Join-Path $out 'report.md')"
    Write-Host ''
    Get-Content (Join-Path $out 'report.md')
}
finally {
    Pop-Location
}
