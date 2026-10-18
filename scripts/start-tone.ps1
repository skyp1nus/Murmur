#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Plays the generated test tone in a loop in a background process (Windows only).

.DESCRIPTION
    Makes sure the Windows Audio service is running, generates the tone WAV (scripts/gen-tone.ps1) if it
    does not exist, then starts a separate hidden pwsh process that calls
    System.Media.SoundPlayer.PlayLooping() on the default render device and stays alive for -DurationS seconds.
    The default render device is expected to be the virtual sound card (Scream) installed earlier in the job;
    WASAPI loopback capture in Murmur then records this tone.

    The PID of the player process is written to -PidFile so a later step can stop it (Stop-Process -Id).
    If the pwsh child dies right away (SoundPlayer unavailable in that host) the script falls back to
    Windows PowerShell 5.1 (powershell.exe).

    Prints one line "tone: <status>" at the end; exit code 1 when no player process could be kept alive.

.PARAMETER DurationS
    How long the player process stays alive, in seconds (default 4000).
.PARAMETER Wav
    Tone file (default artifacts/tone/tone-440hz.wav); generated when missing.
.PARAMETER PidFile
    Where to write the player PID (default artifacts/tone/tone.pid).
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 86400)] [int] $DurationS = 4000,
    [string] $Wav = 'artifacts/tone/tone-440hz.wav',
    [string] $PidFile = 'artifacts/tone/tone.pid'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) { throw 'start-tone.ps1 only works on Windows (System.Media.SoundPlayer + WASAPI).' }

$here = $PSScriptRoot
$wavFull = [System.IO.Path]::GetFullPath($Wav, (Get-Location).ProviderPath)
$pidFull = [System.IO.Path]::GetFullPath($PidFile, (Get-Location).ProviderPath)

try {
    $svc = Get-Service -Name audiosrv -ErrorAction Stop
    if ($svc.Status -ne 'Running') {
        Start-Service -Name audiosrv -ErrorAction Stop
        Write-Host 'Started Windows Audio service (audiosrv).'
    }
}
catch {
    Write-Host "::warning::Could not ensure the Windows Audio service is running: $($_.Exception.Message)"
}

if (-not (Test-Path -LiteralPath $wavFull)) {
    & (Join-Path $here 'gen-tone.ps1') -Out $wavFull
}

$dir = [System.IO.Path]::GetDirectoryName($pidFull)
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# The player script runs in its own process: SoundPlayer.PlayLooping() is asynchronous, so the process
# has to stay alive for as long as the tone must play.
# It logs to -Log itself (try/catch + Out-File) because it is started WITHOUT stdout/stderr redirection:
# Start-Process -RedirectStandard* takes the CreateProcess path with bInheritHandles=TRUE, so the hidden player
# would inherit the runner's step pipes, and the runner kills the step's process tree when those pipes do not
# close (the tone would die a few seconds after this step ends).
$playerScript = Join-Path $dir 'play-tone.ps1'
@'
param([string] $Wav, [int] $DurationS, [string] $Log)
$ErrorActionPreference = 'Stop'
function Write-Log([string] $Text) {
    ('{0:o} {1}' -f [DateTime]::UtcNow, $Text) | Out-File -LiteralPath $Log -Append -Encoding utf8
}
try {
    Write-Log "starting: PowerShell $($PSVersionTable.PSVersion), wav $Wav, duration $DurationS s"
    $player = [System.Media.SoundPlayer]::new($Wav)
    $player.Load()
    $player.PlayLooping()
    Write-Log 'PlayLooping() called'
    Start-Sleep -Seconds $DurationS
    $player.Stop()
    Write-Log 'finished'
}
catch {
    Write-Log ('FAILED: ' + ($_ | Out-String))
    exit 1
}
'@ | Set-Content -LiteralPath $playerScript -Encoding utf8

function Start-Player {
    param([string] $Host_, [string[]] $HostArgs)
    $log = Join-Path $dir ("play-tone-{0}.log" -f [System.IO.Path]::GetFileNameWithoutExtension($Host_))
    Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
    # File paths are quoted: Start-Process joins the argument array with spaces without quoting.
    $argList = $HostArgs + @('-File', "`"$playerScript`"", '-Wav', "`"$wavFull`"", '-DurationS', "$DurationS", '-Log', "`"$log`"")
    # No -RedirectStandard*: no handle inheritance (see above). -WindowStyle Hidden keeps the ShellExecute path.
    $p = Start-Process -FilePath $Host_ -ArgumentList $argList -WindowStyle Hidden -PassThru
    # Open the process handle right away so HasExited / ExitCode stay valid even if the player dies early.
    $null = $p.Handle
    Start-Sleep -Seconds 3
    if ($p.HasExited) {
        $code = try { $p.ExitCode } catch { 'unknown' }
        $logText = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Raw) } else { '(no log written)' }
        Write-Host "::warning::$Host_ tone player exited early (code $code): $logText"
        return $null
    }
    return $p
}

$proc = Start-Player -Host_ 'pwsh' -HostArgs @('-NoProfile', '-NonInteractive')
$hostName = 'pwsh'
if ($null -eq $proc) {
    $proc = Start-Player -Host_ 'powershell.exe' -HostArgs @('-NoProfile', '-NonInteractive')
    $hostName = 'powershell.exe'
}

if ($null -eq $proc) {
    Write-Output 'tone: FAILED (no player process stayed alive)'
    exit 1
}

Set-Content -LiteralPath $pidFull -Value $proc.Id -Encoding ascii
Write-Output ("tone: playing {0} in a loop via {1} (pid {2}) for {3} s" -f $wavFull, $hostName, $proc.Id, $DurationS)
exit 0
