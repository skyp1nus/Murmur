#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Makes sure Media Foundation is available on the runner (Windows Server feature Server-Media-Foundation).

.DESCRIPTION
    GitHub Windows runners are Windows Server images where Media Foundation (needed by the H.264 screen
    encoder) is an optional feature. The script checks the feature (Get-WindowsFeature) and installs it when
    missing (Install-WindowsFeature). Never throws: a failure is reported in the output line and the exit code
    stays 0, so the job can continue and record the problem in the summary.

    Prints one markdown-friendly line "media-foundation: <status>".
    Statuses: present | installed | installed-restart-needed | install-failed | unsupported-sku.
    Also checks that mfplat.dll exists in System32.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

if (-not $IsWindows) {
    Write-Output 'media-foundation: unsupported-sku (not Windows)'
    exit 0
}

$status = 'unsupported-sku'
$detail = ''

if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    try {
        $feature = Get-WindowsFeature -Name Server-Media-Foundation -ErrorAction Stop
        if ($null -eq $feature) {
            $detail = 'feature Server-Media-Foundation not found'
        }
        elseif ($feature.Installed) {
            $status = 'present'
        }
        else {
            Write-Host 'Server-Media-Foundation is missing, installing...'
            $result = Install-WindowsFeature -Name Server-Media-Foundation -ErrorAction Stop
            if ($result.Success) {
                $status = if ($result.RestartNeeded -eq 'Yes') { 'installed-restart-needed' } else { 'installed' }
            }
            else {
                $status = 'install-failed'
                $detail = "exit code $($result.ExitCode)"
            }
        }
    }
    catch {
        $status = 'install-failed'
        $detail = $_.Exception.Message
    }
}
else {
    # Client SKU: Media Foundation ships with the OS.
    $detail = 'Get-WindowsFeature is not available (client SKU?)'
    $status = if (Test-Path -LiteralPath (Join-Path $env:SystemRoot 'System32\mfplat.dll')) { 'present' } else { 'unsupported-sku' }
}

$mfplat = Test-Path -LiteralPath (Join-Path $env:SystemRoot 'System32\mfplat.dll')
$line = "media-foundation: $status (mfplat.dll: $(if ($mfplat) { 'found' } else { 'missing' }))"
if ($detail) { $line += " - $detail" }
Write-Output $line
exit 0
