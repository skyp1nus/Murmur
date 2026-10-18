#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Generates a sine-tone WAV (16-bit PCM) used as a deterministic loopback source in capture CI.

.DESCRIPTION
    Default: 440 Hz, 48 kHz, stereo, 10 s, amplitude 0.5 of full scale. The file is short on purpose:
    the CI plays it in a loop (System.Media.SoundPlayer.PlayLooping). With the defaults the file holds
    exactly 4400 periods (10 s * 440 Hz), so the loop point is click-free.

.PARAMETER Out
    Output path (default artifacts/tone/tone-440hz.wav).
.PARAMETER Seconds
    Duration in seconds (default 10).
.PARAMETER Frequency
    Tone frequency in Hz (default 440).
.PARAMETER SampleRate
    Sample rate in Hz (default 48000).
.PARAMETER Channels
    Channel count (default 2).
.PARAMETER Amplitude
    Peak amplitude, 0..1 of full scale (default 0.5).
#>
[CmdletBinding()]
param(
    [string] $Out = 'artifacts/tone/tone-440hz.wav',
    [ValidateRange(1, 3600)] [int] $Seconds = 10,
    [ValidateRange(20, 20000)] [double] $Frequency = 440,
    [ValidateRange(8000, 192000)] [int] $SampleRate = 48000,
    [ValidateRange(1, 8)] [int] $Channels = 2,
    [ValidateRange(0.01, 1.0)] [double] $Amplitude = 0.5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$outFull = [System.IO.Path]::GetFullPath($Out, (Get-Location).ProviderPath)
$dir = [System.IO.Path]::GetDirectoryName($outFull)
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$frames = $SampleRate * $Seconds
$blockAlign = 2 * $Channels
$dataBytes = [long]$frames * $blockAlign
if ($dataBytes -gt [int]::MaxValue - 64) { throw "Tone too long: $dataBytes data bytes" }

$data = [byte[]]::new([int]$dataBytes)
$twoPiF = 2.0 * [Math]::PI * $Frequency / $SampleRate
$pos = 0
for ($i = 0; $i -lt $frames; $i++) {
    $v = [int][Math]::Round($Amplitude * 32767.0 * [Math]::Sin($twoPiF * $i))
    $lo = [byte]($v -band 0xFF)
    $hi = [byte](($v -shr 8) -band 0xFF)
    for ($c = 0; $c -lt $Channels; $c++) {
        $data[$pos] = $lo
        $data[$pos + 1] = $hi
        $pos += 2
    }
}

$fs = [System.IO.File]::Create($outFull)
try {
    $bw = [System.IO.BinaryWriter]::new($fs)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
    $bw.Write([int](36 + $dataBytes))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('fmt '))
    $bw.Write([int]16)                          # fmt chunk size
    $bw.Write([int16]1)                         # PCM
    $bw.Write([int16]$Channels)
    $bw.Write([int]$SampleRate)
    $bw.Write([int]($SampleRate * $blockAlign)) # byte rate
    $bw.Write([int16]$blockAlign)
    $bw.Write([int16]16)                        # bits per sample
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
    $bw.Write([int]$dataBytes)
    $bw.Write($data)
    $bw.Flush()
}
finally {
    $fs.Dispose()
}

Write-Host ("Wrote {0} ({1} Hz tone, {2} Hz, {3} ch, {4} s, {5:N0} bytes)" -f $outFull, $Frequency, $SampleRate, $Channels, $Seconds, (44 + $dataBytes))
