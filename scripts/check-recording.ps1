#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Validates a Murmur autorecord run: perf JSON + the recorded WAV/MP4 files. Exit code 1 on any violation.

.DESCRIPTION
    Input is the JSON written by `Murmur.exe --autorecord <s> [--screen] --perf-out <json>`:
      { durationS, idleCpuPct, cpuAvgPct, cpuP95Pct, cpuAvgPctOneCore, wsAvgMb, wsMaxMb, privateMaxMb,
        recorderStats{ microphoneFrames, systemFrames, droppedFrames, screenFramesDropped, screenFaults? },
        screenStartMs?, warnings?[], session{ id, folder, micSeconds, systemSeconds, screenSegments,
                                 screenSegmentTimes[]{ index, offsetS, durationS } } }
    durationS is the length of the session timeline (the recorder's Elapsed at stop; the audio starts at 0), and each
    screenSegmentTimes entry places screen-<index>.mp4 on it: offsetS = session time of the file's media time 0,
    durationS = the media duration the file must have. mic.wav / system.wav in session.folder are 16 kHz mono PCM16, screen segments are screen-NNN.mp4.

    Reported but never failing: recorderStats.screenFaults (faults the screen recorder raised: pool exhaustion, CPU readback fallback,
    device lost, encoder errors) and each of warnings[] (what the user was told, in order). A perf JSON without them
    (older build) is fine; with -FailOnScreenFaults it is then reported as INFO (no data), not PASS.

    Checks (always):
      - recorderStats.droppedFrames == 0 and systemFrames > 0
      - system.wav is 16 kHz mono PCM16 and its duration is within +/- -DurationToleranceS of durationS (the signed
        difference is reported as an INFO row, so a systematic offset shows up long before it fails)
      - mic.wav (when it contains data) has the same format and duration tolerance
    With -ExpectTone (a looping tone plays on the render device during the recording):
      - no run of digital silence longer than -MaxZeroRunMs in system.wav (the first -LeadGraceS and the
        last -TailGraceS seconds are ignored: the tone/recording start and stop are not synchronised)
      - at least -MinToneFraction of the 1-second windows contain the -ToneHz tone (Goertzel energy ratio
        >= 0.5) and the signal peak is not zero
    With -ExpectScreen:
      - at least one non-empty screen-NNN.mp4 and session.screenSegments matches the file count
      - every segment parses as an MP4 with video samples; its frame count and duration are reported and its
        frame rate is at least -MinScreenFps (variable frame rate: the recorder repeats the last frame once a
        second on a static screen, so ~1 fps is the floor; fewer frames mean the encoder stopped early)
      - every segment's MP4 media duration is within +/- -SegmentToleranceS of the durationS the session recorded
        for that segment (session.screenSegmentTimes, matched by the NNN of screen-NNN.mp4); the signed difference
        is reported as an INFO row. This is the audio/video sync check: a segment that starts late (the old bug:
        media time 0 taken after the encoder start-up, ~0.45 s on a GPU PC) is that much shorter than the session
        says. A perf JSON without screenSegmentTimes (older build) skips it; a file without an entry fails.
      - the summed segment duration is within +/- -ScreenToleranceS of durationS (a 12-frame, 0.4 s file for a
        10 minute recording fails). Duration = sum of the trun sample durations of the video track divided by the
        mdhd timescale (fragmented MP4, using tfhd/trex defaults), the stts table for a classic MP4, else the
        mehd/mvhd header value (then the frame count is unknown and the segment fails). A file cut short by a
        crash is read up to the last complete box.
    Budgets (a budget of 0 disables the check; metric names are the JSON field names):
      - -IdleCpuBudgetPct on idleCpuPct, -CpuBudgetPct on cpuAvgPct, -RamBudgetMb on wsMaxMb
      - -MaxWsSlopeMbPerMin on the working-set growth: the least-squares slope of memory[].wsMb over
        memory[].seconds, fitted only over the samples from -WsSlopeSkipS on (the first minute is warm-up: JIT,
        Media Foundation, encoder and sample pools; the app's own wsSlopeMbPerMin covers the whole run, warm-up
        included, and is only reported). Fewer than 3 samples after the warm-up fail the check (nothing to fit).

    The WAV scan is done by an inline C# helper (Add-Type), so a 1-hour 16 kHz file takes seconds.
    Prints a markdown report to stdout; -MarkdownOut also writes it to a file (for $GITHUB_STEP_SUMMARY).

.PARAMETER PerfJson
    Path to the perf JSON.
.PARAMETER ExpectTone
    A looping tone was playing: enable the silence-gap and tone-presence checks.
.PARAMETER ExpectScreen
    The run used --screen: require screen segments.
.PARAMETER FailOnScreenFaults
    Fail when screenFaults is greater than 0 (default: only reported).
.PARAMETER Title
    Heading of the markdown report.
.PARAMETER MarkdownOut
    Optional path to write the markdown report to.
.PARAMETER DurationToleranceS
    Allowed |wav duration - durationS| (default 1).
.PARAMETER ScreenToleranceS
    Allowed |summed screen video duration - durationS| with -ExpectScreen (default 2; the first segment starts a
    little after the audio and the last frame ends a frame before the stop).
.PARAMETER SegmentToleranceS
    Allowed |MP4 media duration - session.screenSegmentTimes[].durationS| per segment with -ExpectScreen (default
    0.25). The contract is exact (the file starts at the recorder's StartSegmentAsync call and ends at its
    StopSegmentAsync call, the session clock and the media clock are both QPC), so what remains is one frame of the
    sink (the software encoder re-times its output to whole frames: 33 ms at 30 fps), the perf JSON's millisecond
    rounding and timer slack of a loaded CI runner on the two clock reads (a few 10 ms). 0.25 s covers that and is
    still far below the 0.43-0.48 s the start-up bug cost, so it catches a regression.
.PARAMETER MinScreenFps
    Lowest plausible frames/duration of a screen segment with -ExpectScreen (default 0.5; 0 disables the check). The
    file has a variable frame rate: a static screen holds one repeated frame a second (~1 fps), moving content runs
    at up to 60 fps on the GPU path and 30 fps on CPU readback.
.PARAMETER MaxZeroRunMs
    Longest allowed run of silent samples while the tone plays (default 100).
.PARAMETER SilenceAbs
    A sample counts as silent when |sample| <= this value (int16 units, default 1).
.PARAMETER LeadGraceS
    Seconds at the start ignored by the silence/tone checks (default 5).
.PARAMETER TailGraceS
    Seconds at the end ignored by the silence/tone checks (default 2).
.PARAMETER ToneHz
    Expected tone frequency (default 440).
.PARAMETER MinToneFraction
    Required fraction of 1-second windows containing the tone (default 0.95).
.PARAMETER IdleCpuBudgetPct
    Budget for idleCpuPct (0 = not checked).
.PARAMETER CpuBudgetPct
    Budget for cpuAvgPct (0 = not checked).
.PARAMETER RamBudgetMb
    Budget for wsMaxMb (0 = not checked).
.PARAMETER MaxWsSlopeMbPerMin
    Largest allowed working-set slope after the warm-up, MB per minute (0 = not checked). With the screen there is no
    absolute RAM cap (the app may use what the quality needs), but memory must stay flat while recording.
.PARAMETER WsSlopeSkipS
    Seconds at the start of the recording left out of the slope fit (default 60).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PerfJson,
    [switch] $ExpectTone,
    [switch] $ExpectScreen,
    [switch] $FailOnScreenFaults,
    [string] $Title = 'Recording check',
    [string] $MarkdownOut = '',
    [double] $DurationToleranceS = 1.0,
    [double] $ScreenToleranceS = 2.0,
    [double] $SegmentToleranceS = 0.25,
    [double] $MinScreenFps = 0.5,
    [double] $MaxZeroRunMs = 100,
    [int] $SilenceAbs = 1,
    [double] $LeadGraceS = 5,
    [double] $TailGraceS = 2,
    [double] $ToneHz = 440,
    [double] $MinToneFraction = 0.95,
    [double] $IdleCpuBudgetPct = 0,
    [double] $CpuBudgetPct = 0,
    [double] $RamBudgetMb = 0,
    [double] $MaxWsSlopeMbPerMin = 0,
    [double] $WsSlopeSkipS = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('MurmurWavScan' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;

public sealed class MurmurWavScanResult
{
    public string Error;
    public int FormatTag;
    public int Channels;
    public int SampleRate;
    public int BitsPerSample;
    public long DataBytes;
    public long Frames;
    public bool HeaderSizeMatches;
    public double DurationS;
    public double Peak;
    public double Rms;
    public long ZeroRunsOverLimit;
    public double LongestZeroRunMs;
    public double LongestZeroRunAtS;
    public long ToneWindows;
    public long ToneWindowsHit;
}

public static class MurmurWavScan
{
    public static MurmurWavScanResult Scan(string path, bool analyze, double maxZeroRunMs, int silenceAbs,
        double leadGraceS, double tailGraceS, double toneHz)
    {
        var r = new MurmurWavScanResult();
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 12) { r.Error = "file shorter than a RIFF header"; return r; }
                string riff = new string(br.ReadChars(4));
                br.ReadUInt32();
                string wave = new string(br.ReadChars(4));
                if (riff != "RIFF" || wave != "WAVE") { r.Error = "not a RIFF/WAVE file"; return r; }

                bool haveFmt = false;
                bool haveData = false;
                long dataStart = 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = new string(br.ReadChars(4));
                    uint size = br.ReadUInt32();
                    if (id == "fmt ")
                    {
                        long next = fs.Position + size + (size & 1);
                        r.FormatTag = br.ReadUInt16();
                        r.Channels = br.ReadUInt16();
                        r.SampleRate = (int)br.ReadUInt32();
                        br.ReadUInt32();
                        br.ReadUInt16();
                        r.BitsPerSample = br.ReadUInt16();
                        haveFmt = true;
                        fs.Position = next;
                    }
                    else if (id == "data")
                    {
                        long actual = fs.Length - fs.Position;
                        // A crash-safe recorder may leave the data size as 0 or 0xFFFFFFFF until finalised.
                        if (size == 0 || size == 0xFFFFFFFFu || size > actual) { r.DataBytes = actual; r.HeaderSizeMatches = false; }
                        else { r.DataBytes = size; r.HeaderSizeMatches = (size == actual); }
                        dataStart = fs.Position;
                        haveData = true;
                        break;
                    }
                    else
                    {
                        fs.Position += size + (size & 1);
                    }
                }
                if (!haveFmt) { r.Error = "no fmt chunk"; return r; }
                if (!haveData) { r.Error = "no data chunk"; return r; }
                if (r.Channels < 1 || r.SampleRate < 1) { r.Error = "invalid fmt chunk"; return r; }

                int blockAlign = r.Channels * (r.BitsPerSample / 8);
                if (r.BitsPerSample != 16 || blockAlign < 2) { r.Frames = 0; r.Error = "not 16-bit PCM"; return r; }

                r.Frames = r.DataBytes / blockAlign;
                r.DurationS = (double)r.Frames / r.SampleRate;

                int framesPerBlock = 8192;
                byte[] buf = new byte[framesPerBlock * blockAlign];
                long frameIndex = 0;
                int sr = r.SampleRate;
                int ch = r.Channels;

                long leadFrames = (long)(leadGraceS * sr);
                long analysisEnd = r.Frames - (long)(tailGraceS * sr);
                long maxRunFrames = (long)(maxZeroRunMs * sr / 1000.0);
                long runStart = -1;

                double coeff = 2.0 * Math.Cos(2.0 * Math.PI * toneHz / sr);
                double s1 = 0, s2 = 0, winSumSq = 0;
                long winPos = 0;

                double peak = 0;
                double sumSq = 0;
                long remaining = r.Frames;

                fs.Position = dataStart;

                while (remaining > 0)
                {
                    int want = (int)Math.Min(framesPerBlock, remaining);
                    int bytes = want * blockAlign;
                    int got = 0;
                    while (got < bytes)
                    {
                        int n = fs.Read(buf, got, bytes - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    int frames = got / blockAlign;
                    if (frames == 0) break;

                    for (int f = 0; f < frames; f++)
                    {
                        int o = f * blockAlign;
                        bool silent = true;
                        int s0 = 0;
                        for (int c = 0; c < ch; c++)
                        {
                            int s = (short)(buf[o + 2 * c] | (buf[o + 2 * c + 1] << 8));
                            if (c == 0) s0 = s;
                            int a = s < 0 ? -s : s;
                            if (a > silenceAbs) silent = false;
                            if (a > peak) peak = a;
                            sumSq += (double)s * s;
                        }

                        if (analyze && frameIndex >= leadFrames)
                        {
                            // Silent run bookkeeping (runs are cut at the lead grace start and at the tail grace start).
                            if (silent)
                            {
                                if (runStart < 0) runStart = frameIndex;
                            }
                            else if (runStart >= 0)
                            {
                                FlushRun(r, runStart, frameIndex, analysisEnd, sr, maxRunFrames);
                                runStart = -1;
                            }

                            // Goertzel over consecutive 1-second windows starting at the lead grace boundary.
                            double x = s0;
                            double s = x + coeff * s1 - s2;
                            s2 = s1;
                            s1 = s;
                            winSumSq += x * x;
                            winPos++;
                            if (winPos == sr)
                            {
                                if (frameIndex + 1 <= analysisEnd)
                                {
                                    double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
                                    double ratio = winSumSq > 0 ? 2.0 * power / ((double)sr * winSumSq) : 0.0;
                                    r.ToneWindows++;
                                    if (ratio >= 0.5) r.ToneWindowsHit++;
                                }
                                s1 = 0; s2 = 0; winSumSq = 0; winPos = 0;
                            }
                        }
                        frameIndex++;
                    }
                    remaining -= frames;
                    if (frames < want) break;
                }

                if (analyze && runStart >= 0) FlushRun(r, runStart, frameIndex, analysisEnd, sr, maxRunFrames);

                r.Peak = peak;
                long samples = frameIndex * ch;
                r.Rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;
            }
        }
        catch (Exception ex)
        {
            r.Error = ex.GetType().Name + ": " + ex.Message;
        }
        return r;
    }

    private static void FlushRun(MurmurWavScanResult r, long start, long endExclusive, long analysisEnd, int sr, long maxRunFrames)
    {
        long end = Math.Min(endExclusive, analysisEnd);
        long len = end - start;
        if (len <= 0) return;
        double ms = len * 1000.0 / sr;
        if (ms > r.LongestZeroRunMs) { r.LongestZeroRunMs = ms; r.LongestZeroRunAtS = (double)start / sr; }
        if (len > maxRunFrames) r.ZeroRunsOverLimit++;
    }
}
'@
}

# The MP4 reader lives in its own file so that tests/Murmur.Scripts.Tests compiles and tests the very same code.
if (-not ('MurmurMp4Scan' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'MurmurMp4Scan.cs')
}

# ---------------------------------------------------------------- report plumbing

$rows = [System.Collections.Generic.List[object]]::new()
$violations = [System.Collections.Generic.List[string]]::new()

function Add-Row {
    param([string] $Check, [string] $Value, [string] $Limit, [ValidateSet('PASS', 'FAIL', 'INFO', 'SKIP')] [string] $Status)
    $rows.Add([pscustomobject]@{ Check = $Check; Value = $Value; Limit = $Limit; Status = $Status })
    if ($Status -eq 'FAIL') { $violations.Add("${Check}: $Value (limit $Limit)") }
}

function Get-Prop {
    # Null-safe property access (StrictMode throws on missing properties).
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Format-Num {
    param($Value, [string] $Fmt = 'N2')
    if ($null -eq $Value) { return 'n/a' }
    return ([double]$Value).ToString($Fmt, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Test-Budget {
    param([string] $Name, $Value, [double] $Budget, [string] $Unit)
    if ($Budget -le 0) {
        Add-Row $Name (Format-Num $Value) 'not checked' 'INFO'
        return
    }
    if ($null -eq $Value) { Add-Row $Name 'n/a' "< $Budget $Unit" 'FAIL'; return }
    $st = if ([double]$Value -lt $Budget) { 'PASS' } else { 'FAIL' }
    Add-Row $Name ("{0} {1}" -f (Format-Num $Value), $Unit) "< $Budget $Unit" $st
}

function Get-WsSlope {
    # Least-squares slope (MB per minute) of wsMb over seconds, from the memory samples at or after $SkipS.
    param($Memory, [double] $SkipS)
    $points = [System.Collections.Generic.List[double[]]]::new()
    foreach ($m in @($Memory)) {
        $t = Get-Prop $m 'seconds'
        $ws = Get-Prop $m 'wsMb'
        if ($null -ne $t -and $null -ne $ws -and [double]$t -ge $SkipS) { $points.Add([double[]]@([double]$t, [double]$ws)) }
    }
    $fit = [pscustomobject]@{ Samples = $points.Count; Slope = $null; FromS = $null; ToS = $null }
    if ($points.Count -lt 3) { return $fit }
    [double] $meanT = 0
    [double] $meanW = 0
    foreach ($p in $points) { $meanT += $p[0]; $meanW += $p[1] }
    $meanT /= $points.Count
    $meanW /= $points.Count
    [double] $cov = 0
    [double] $var = 0
    foreach ($p in $points) {
        $cov += ($p[0] - $meanT) * ($p[1] - $meanW)
        $var += ($p[0] - $meanT) * ($p[0] - $meanT)
    }
    $fit.FromS = $points[0][0]
    $fit.ToS = $points[$points.Count - 1][0]
    if ($var -gt 0) { $fit.Slope = $cov / $var * 60 }
    return $fit
}

function Test-WsSlope {
    param($Memory, [double] $Limit, [double] $SkipS)
    $fit = Get-WsSlope $Memory $SkipS
    $name = 'working-set slope after {0} s' -f (Format-Num $SkipS '0.##')
    $limitText = if ($Limit -le 0) { 'not checked' } else { '< {0} MB/min' -f (Format-Num $Limit '0.###') }
    if ($null -eq $fit.Slope) {
        $value = 'too few samples to fit ({0} after the warm-up, need 3)' -f $fit.Samples
        Add-Row $name $value $limitText $(if ($Limit -le 0) { 'INFO' } else { 'FAIL' })
        return
    }
    $value = '{0} MB/min over {1} samples ({2}..{3} s)' -f (Format-Num $fit.Slope '0.000'), $fit.Samples, (Format-Num $fit.FromS '0'), (Format-Num $fit.ToS '0')
    if ($Limit -le 0) { Add-Row $name $value $limitText 'INFO'; return }
    Add-Row $name $value $limitText $(if ($fit.Slope -lt $Limit) { 'PASS' } else { 'FAIL' })
}

function Test-Wav {
    param([string] $Label, [string] $Path, $DurationS, [bool] $Analyze, [bool] $Required)

    if (-not (Test-Path -LiteralPath $Path)) {
        if ($Required) { Add-Row "$Label file" 'missing' 'exists' 'FAIL' }
        else { Add-Row "$Label file" 'missing' 'optional' 'INFO' }
        return
    }
    $size = (Get-Item -LiteralPath $Path).Length
    $res = [MurmurWavScan]::Scan($Path, $Analyze, $MaxZeroRunMs, $SilenceAbs, $LeadGraceS, $TailGraceS, $ToneHz)
    if ($res.Error) {
        Add-Row "$Label parse" $res.Error 'valid PCM16 WAV' $(if ($Required) { 'FAIL' } else { 'INFO' })
        return
    }
    if ($res.Frames -eq 0 -and -not $Required) {
        Add-Row "$Label file" 'empty (no data)' 'optional' 'INFO'
        return
    }

    Add-Row "$Label size" ("{0:N1} MB" -f ($size / 1MB)) '-' 'INFO'
    $fmtOk = ($res.SampleRate -eq 16000 -and $res.Channels -eq 1 -and $res.BitsPerSample -eq 16)
    Add-Row "$Label format" ("{0} Hz, {1} ch, {2}-bit" -f $res.SampleRate, $res.Channels, $res.BitsPerSample) '16000 Hz, 1 ch, 16-bit' $(if ($fmtOk) { 'PASS' } else { 'FAIL' })

    if ($null -ne $DurationS) {
        $diff = [Math]::Abs($res.DurationS - [double]$DurationS)
        Add-Row "$Label duration - durationS (signed)" ("{0} s" -f (Format-Num ($res.DurationS - [double]$DurationS) '+0.000;-0.000;0.000')) '-' 'INFO'
        Add-Row "$Label duration vs durationS" ("{0} s (durationS {1} s, diff {2} s)" -f (Format-Num $res.DurationS), (Format-Num $DurationS), (Format-Num $diff '0.000')) ("+/- $DurationToleranceS s") $(if ($diff -le $DurationToleranceS) { 'PASS' } else { 'FAIL' })
    }
    if (-not $res.HeaderSizeMatches) {
        Add-Row "$Label header" 'data size in header does not match the file (not finalised?)' 'consistent' 'INFO'
    }

    if ($Analyze) {
        Add-Row "$Label peak / RMS" ("{0:N0} / {1:N1} (int16)" -f $res.Peak, $res.Rms) 'peak > 0' $(if ($res.Peak -gt 0) { 'PASS' } else { 'FAIL' })
        Add-Row "$Label longest silent run" ("{0} ms at {1} s" -f (Format-Num $res.LongestZeroRunMs '0.0'), (Format-Num $res.LongestZeroRunAtS '0.0')) ("<= $MaxZeroRunMs ms") $(if ($res.ZeroRunsOverLimit -eq 0) { 'PASS' } else { 'FAIL' })
        Add-Row "$Label silent runs over limit" ([string]$res.ZeroRunsOverLimit) '0' $(if ($res.ZeroRunsOverLimit -eq 0) { 'PASS' } else { 'FAIL' })
        if ($res.ToneWindows -gt 0) {
            $frac = $res.ToneWindowsHit / [double]$res.ToneWindows
            Add-Row "$Label ${ToneHz} Hz tone windows" ("{0}/{1} ({2:P1})" -f $res.ToneWindowsHit, $res.ToneWindows, $frac) (">= {0:P0}" -f $MinToneFraction) $(if ($frac -ge $MinToneFraction) { 'PASS' } else { 'FAIL' })
        }
        else {
            Add-Row "$Label tone windows" '0 analysable windows' '>= 1' 'FAIL'
        }
    }
}

function Test-SegmentSync {
    # Compares one segment's MP4 media duration with the duration the session recorded for it.
    param([string] $Label, [double] $MediaS, $SegmentTimes)
    $name = "$Label duration vs session segment"
    $limit = "+/- {0} s" -f (Format-Num $SegmentToleranceS '0.###')
    if ($null -eq $SegmentTimes) {
        Add-Row $name 'perf JSON has no session.screenSegmentTimes' $limit 'SKIP'
        return
    }
    $index = if ($Label -match '^screen-(\d+)\.mp4$') { [int]$Matches[1] } else { -1 }
    $entry = $null
    foreach ($candidate in @($SegmentTimes)) {
        $candidateIndex = Get-Prop $candidate 'index'
        if ($null -ne $candidateIndex -and [int]$candidateIndex -eq $index) { $entry = $candidate }
    }
    $expected = Get-Prop $entry 'durationS'
    if ($null -eq $expected) {
        Add-Row $name "no segment $index in session.screenSegmentTimes" $limit 'FAIL'
        return
    }
    $signed = $MediaS - [double]$expected
    Add-Row "$Label media - segment durationS (signed)" ("{0} s (offsetS {1})" -f (Format-Num $signed '+0.000;-0.000;0.000'), (Format-Num (Get-Prop $entry 'offsetS') '0.000')) '-' 'INFO'
    $value = "{0} s (segment durationS {1} s, diff {2} s)" -f (Format-Num $MediaS '0.000'), (Format-Num $expected '0.000'), (Format-Num ([Math]::Abs($signed)) '0.000')
    Add-Row $name $value $limit $(if ([Math]::Abs($signed) -le $SegmentToleranceS) { 'PASS' } else { 'FAIL' })
}

function Test-ScreenVideo {
    # Parses every screen-NNN.mp4 (frames + duration), compares each with its entry of session.screenSegmentTimes
    # (the sync check) and the summed duration with durationS. --autorecord never toggles the screen, so the segments
    # cover the whole run (the recorder opens the first one right after the audio starts); the sum may be short of
    # durationS only by that gap, which -ScreenToleranceS absorbs.
    param($Segments, $DurationS, $SegmentTimes)

    [double] $totalS = 0
    [long] $totalFrames = 0
    $allKnown = $true
    foreach ($segment in ($Segments | Sort-Object Name)) {
        $label = $segment.Name
        $res = [MurmurMp4Scan]::Scan($segment.FullName)
        if ($res.Error) {
            Add-Row "$label video" $res.Error 'valid MP4 with video samples' 'FAIL'
            $allKnown = $false
            continue
        }
        $totalS += $res.DurationS
        Test-SegmentSync $label $res.DurationS $SegmentTimes
        $kind = if ($res.Fragmented) { "fragmented, $($res.Fragments) fragment(s)" } else { 'not fragmented' }
        $notes = "$kind, from $($res.DurationSource)"
        if ($res.Truncated) { $notes += ', file truncated' }
        if (-not $res.FramesKnown) {
            $allKnown = $false
            Add-Row "$label video" ("no sample table or fragments, {0} s from the container header ({1})" -f (Format-Num $res.DurationS '0.000'), $notes) '>= 1 frame' 'FAIL'
            continue
        }
        $totalFrames += $res.Frames
        $fps = if ($res.DurationS -gt 0) { $res.Frames / $res.DurationS } else { 0.0 }
        $value = "{0} frames, {1} s, {2} fps ({3})" -f $res.Frames, (Format-Num $res.DurationS '0.000'), (Format-Num $fps '0.0'), $notes
        if ($MinScreenFps -le 0) {
            Add-Row "$label video" $value 'fps not checked' $(if ($res.Frames -gt 0) { 'PASS' } else { 'FAIL' })
        }
        else {
            Add-Row "$label video" $value (">= {0} fps" -f (Format-Num $MinScreenFps '0.##')) $(if ($res.Frames -gt 0 -and $fps -ge $MinScreenFps) { 'PASS' } else { 'FAIL' })
        }
    }

    Add-Row 'screen video total' ("{0} frames, {1} s" -f $totalFrames, (Format-Num $totalS '0.000')) '-' 'INFO'
    if ($null -eq $DurationS) { return }
    $diff = [Math]::Abs($totalS - [double]$DurationS)
    $value = "{0} s (durationS {1} s, diff {2} s)" -f (Format-Num $totalS '0.000'), (Format-Num $DurationS), (Format-Num $diff '0.000')
    Add-Row 'screen video duration vs durationS' $value ("+/- {0} s" -f (Format-Num $ScreenToleranceS '0.##')) $(if ($diff -le $ScreenToleranceS) { 'PASS' } else { 'FAIL' })
}

# ---------------------------------------------------------------- perf JSON

if (-not (Test-Path -LiteralPath $PerfJson)) {
    Add-Row 'perf JSON' "missing: $PerfJson" 'exists' 'FAIL'
    $perf = $null
}
else {
    try { $perf = Get-Content -LiteralPath $PerfJson -Raw | ConvertFrom-Json }
    catch {
        Add-Row 'perf JSON' "unparseable: $($_.Exception.Message)" 'valid JSON' 'FAIL'
        $perf = $null
    }
}

if ($null -ne $perf) {
    $durationS = Get-Prop $perf 'durationS'
    $stats = Get-Prop $perf 'recorderStats'
    $session = Get-Prop $perf 'session'

    Add-Row 'durationS' (Format-Num $durationS) '> 0' $(if ($null -ne $durationS -and [double]$durationS -gt 0) { 'PASS' } else { 'FAIL' })

    $dropped = Get-Prop $stats 'droppedFrames'
    Add-Row 'droppedFrames' $(if ($null -eq $dropped) { 'n/a' } else { [string]$dropped }) '== 0' $(if ($null -ne $dropped -and [long]$dropped -eq 0) { 'PASS' } else { 'FAIL' })
    $sysFrames = Get-Prop $stats 'systemFrames'
    Add-Row 'systemFrames' $(if ($null -eq $sysFrames) { 'n/a' } else { [string]$sysFrames }) '> 0' $(if ($null -ne $sysFrames -and [long]$sysFrames -gt 0) { 'PASS' } else { 'FAIL' })
    Add-Row 'microphoneFrames' ([string](Get-Prop $stats 'microphoneFrames')) '-' 'INFO'
    Add-Row 'screenFramesDropped' ([string](Get-Prop $stats 'screenFramesDropped')) '-' 'INFO'
    $screenFaults = Get-Prop $stats 'screenFaults'
    if ($FailOnScreenFaults -and $null -ne $screenFaults) {
        Add-Row 'screenFaults' ([string]$screenFaults) '== 0' $(if ([long]$screenFaults -eq 0) { 'PASS' } else { 'FAIL' })
    }
    elseif ($FailOnScreenFaults) {
        Add-Row 'screenFaults' 'n/a (older perf JSON)' '== 0' 'INFO'
    }
    elseif ($null -ne $screenFaults) {
        Add-Row 'screenFaults' ([string]$screenFaults) '-' 'INFO'
    }

    # Get-Prop would unroll a one-element array into a scalar; @() brings the list back.
    foreach ($warning in @(Get-Prop $perf 'warnings')) {
        if (-not [string]::IsNullOrWhiteSpace($warning)) { Add-Row 'warning' (([string]$warning) -replace '\|', '/') '-' 'INFO' }
    }
    $screenStart = Get-Prop $perf 'screenStartMs'
    if ($null -ne $screenStart) { Add-Row 'screenStartMs' (Format-Num $screenStart '0') '-' 'INFO' }

    Test-Budget 'idleCpuPct' (Get-Prop $perf 'idleCpuPct') $IdleCpuBudgetPct '%'
    Test-Budget 'cpuAvgPct' (Get-Prop $perf 'cpuAvgPct') $CpuBudgetPct '%'
    Add-Row 'cpuP95Pct' (Format-Num (Get-Prop $perf 'cpuP95Pct')) '-' 'INFO'
    Add-Row 'cpuAvgPctOneCore' (Format-Num (Get-Prop $perf 'cpuAvgPctOneCore')) '-' 'INFO'
    Add-Row 'wsAvgMb' (Format-Num (Get-Prop $perf 'wsAvgMb')) '-' 'INFO'
    Test-Budget 'wsMaxMb' (Get-Prop $perf 'wsMaxMb') $RamBudgetMb 'MB'
    Add-Row 'wsSlopeMbPerMin (app, whole run)' (Format-Num (Get-Prop $perf 'wsSlopeMbPerMin') '0.000') '-' 'INFO'
    Test-WsSlope (Get-Prop $perf 'memory') $MaxWsSlopeMbPerMin $WsSlopeSkipS
    Add-Row 'privateMaxMb' (Format-Num (Get-Prop $perf 'privateMaxMb')) '-' 'INFO'

    $folder = Get-Prop $session 'folder'
    if ([string]::IsNullOrWhiteSpace($folder)) {
        Add-Row 'session.folder' 'missing' 'set' 'FAIL'
    }
    elseif (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        Add-Row 'session.folder' "not found: $folder" 'exists' 'FAIL'
    }
    else {
        Add-Row 'session.folder' $folder '-' 'INFO'
        Test-Wav 'system.wav' (Join-Path $folder 'system.wav') $durationS $ExpectTone.IsPresent $true
        Test-Wav 'mic.wav' (Join-Path $folder 'mic.wav') $durationS $false $false

        $segments = @(Get-ChildItem -LiteralPath $folder -Filter 'screen-*.mp4' -File -ErrorAction SilentlyContinue)
        $declared = Get-Prop $session 'screenSegments'
        if ($ExpectScreen) {
            $nonEmpty = @($segments | Where-Object { $_.Length -gt 0 })
            # Measure-Object yields no Sum property for an empty input under StrictMode, so add up by hand.
            [long] $segmentBytes = 0
            foreach ($segment in $segments) { $segmentBytes += $segment.Length }
            Add-Row 'screen segments' ("{0} file(s), {1} non-empty, {2:N1} MB" -f $segments.Count, $nonEmpty.Count, ($segmentBytes / 1MB)) '>= 1 non-empty' $(if ($nonEmpty.Count -ge 1) { 'PASS' } else { 'FAIL' })
            Add-Row 'session.screenSegments' $(if ($null -eq $declared) { 'n/a' } else { [string]$declared }) ("== file count ({0})" -f $segments.Count) $(if ($null -ne $declared -and [int]$declared -eq $segments.Count) { 'PASS' } else { 'FAIL' })
            # Get-Prop would unroll an empty array into $null (= "older JSON, skip"); an empty list must stay a list,
            # so that every file then fails for having no entry.
            $timesProperty = if ($null -eq $session) { $null } else { $session.PSObject.Properties['screenSegmentTimes'] }
            $segmentTimes = if ($null -eq $timesProperty -or $null -eq $timesProperty.Value) { $null } else { , @($timesProperty.Value) } # the comma keeps an empty array from being unrolled to $null
            Test-ScreenVideo $segments $durationS $segmentTimes
        }
        else {
            Add-Row 'screen segments' ("{0} file(s)" -f $segments.Count) 'not expected' 'INFO'
        }
    }
}

# ---------------------------------------------------------------- report

$failed = $violations.Count -gt 0
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("### $Title")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Result: **$(if ($failed) { 'FAIL' } else { 'PASS' })**")
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Check | Value | Limit | Status |')
[void]$sb.AppendLine('|---|---|---|---|')
foreach ($row in $rows) {
    $v = ([string]$row.Value).Replace('|', '\|')
    [void]$sb.AppendLine("| $($row.Check) | $v | $($row.Limit) | $($row.Status) |")
}
if ($failed) {
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Violations:')
    foreach ($v in $violations) { [void]$sb.AppendLine("- $v") }
}
$report = $sb.ToString()
Write-Host $report

if ($MarkdownOut) {
    $mdFull = [System.IO.Path]::GetFullPath($MarkdownOut, (Get-Location).ProviderPath)
    $mdDir = [System.IO.Path]::GetDirectoryName($mdFull)
    if (-not (Test-Path -LiteralPath $mdDir)) { New-Item -ItemType Directory -Path $mdDir -Force | Out-Null }
    $report | Set-Content -LiteralPath $mdFull -Encoding utf8
}

if ($failed) {
    foreach ($v in $violations) { Write-Host "::error::$Title - $v" }
    exit 1
}
exit 0
