#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads the ASR models Murmur needs into $env:MURMUR_MODELS_DIR.

.DESCRIPTION
    Default directory: $env:LOCALAPPDATA\Murmur\models on Windows, ~/.murmur/models elsewhere.
      - Parakeet TDT v3 int8 (sherpa-onnx) -> <dir>/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/
      - Silero VAD                         -> <dir>/silero_vad.onnx
    Idempotent (existing files are skipped), retries with backoff, throws on any error.
    Extraction uses tar (bsdtar ships with Windows 10 1803+).
#>
[CmdletBinding()]
param(
    [int] $Retries = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest progress rendering is extremely slow

$baseUrl = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
$parakeetName = 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8'

$modelsDir = $env:MURMUR_MODELS_DIR
if ([string]::IsNullOrWhiteSpace($modelsDir)) {
    $onWindows = $PSVersionTable.PSVersion.Major -lt 6 -or $IsWindows
    if ($onWindows) {
        $modelsDir = Join-Path $env:LOCALAPPDATA 'Murmur\models'
    }
    else {
        $modelsDir = Join-Path $HOME '.murmur/models'
    }
}

function Write-Log([string] $Message) { Write-Host "[fetch-models] $Message" }

function Save-Download([string] $Url, [string] $Dest) {
    $part = "$Dest.part"
    $delay = 2
    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        try {
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
            Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing
            Move-Item -LiteralPath $part -Destination $Dest -Force
            return
        }
        catch {
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue }
            if ($attempt -ge $Retries) {
                throw "Download failed after $Retries attempts: $Url ($($_.Exception.Message))"
            }
            Write-Log "download failed (attempt $attempt/$Retries), retrying in ${delay}s: $Url"
            Start-Sleep -Seconds $delay
            $delay *= 2
        }
    }
}

New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
Write-Log "models directory: $modelsDir"

# --- Parakeet TDT v3 int8 ---------------------------------------------------
$parakeetDir = Join-Path $modelsDir $parakeetName
if (Test-Path -LiteralPath (Join-Path $parakeetDir 'encoder.int8.onnx') -PathType Leaf) {
    Write-Log "skip: $parakeetName already present"
}
else {
    $tmp = Join-Path $modelsDir (".fetch." + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $archive = Join-Path $tmp "$parakeetName.tar.bz2"
        Write-Log "downloading $baseUrl/$parakeetName.tar.bz2"
        Save-Download "$baseUrl/$parakeetName.tar.bz2" $archive
        Write-Log 'extracting'
        & tar -xjf $archive -C $tmp
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
        $extracted = Join-Path $tmp $parakeetName
        if (-not (Test-Path -LiteralPath (Join-Path $extracted 'encoder.int8.onnx') -PathType Leaf)) {
            throw "Archive does not contain $parakeetName/encoder.int8.onnx"
        }
        if (Test-Path -LiteralPath $parakeetDir) { Remove-Item -LiteralPath $parakeetDir -Recurse -Force }
        Move-Item -LiteralPath $extracted -Destination $parakeetDir
        Write-Log "installed $parakeetDir"
    }
    finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- Silero VAD ---------------------------------------------------------------
$vadFile = Join-Path $modelsDir 'silero_vad.onnx'
if ((Test-Path -LiteralPath $vadFile -PathType Leaf) -and (Get-Item -LiteralPath $vadFile).Length -gt 0) {
    Write-Log 'skip: silero_vad.onnx already present'
}
else {
    Write-Log "downloading $baseUrl/silero_vad.onnx"
    Save-Download "$baseUrl/silero_vad.onnx" $vadFile
    if ((Get-Item -LiteralPath $vadFile).Length -le 0) { throw 'silero_vad.onnx is empty after download' }
    Write-Log "installed $vadFile"
}

Write-Log 'done'
