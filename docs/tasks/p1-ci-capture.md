# Task p1-ci-capture: CI: Windows capture e2e with a virtual audio device; 1-hour perf-record workflow

## Scope
.github/workflows/**, scripts/**, docs/CI.md

## Spec
1. ci.yml: new job capture-e2e (windows-latest, needs: windows, timeout 30): download murmur-win-x64; give WASAPI a render endpoint by installing a virtual sound card with `LABSN/sound-ci-helpers@v1` (installs Scream on Windows) — continue-on-error, and record 'no audio device' in the summary if it fails; ensure Media Foundation exists (Get-WindowsFeature Server-Media-Foundation -> Install-WindowsFeature if missing, continue-on-error, report); generate a tone WAV (scripts/gen-tone.ps1: 16-bit 48 kHz stereo 440 Hz, 10 s) and play it in a loop in a background job (System.Media.SoundPlayer.PlayLooping in a separate pwsh process); run `dotnet test tests/Murmur.Capture.Windows.Tests -c Release` (Windows-trait tests with the device present); run `Murmur.exe --autorecord 60 --screen --perf-out artifacts/perf/rec60.json`; validate with scripts/check-recording.ps1; table to $GITHUB_STEP_SUMMARY; upload artifact capture-e2e (perf JSON + session folder).
2. .github/workflows/perf-record.yml: workflow_dispatch with inputs minutes (default 60) and screen (bool, default true). Same setup (virtual audio, Media Foundation, looping tone). Run A: `--autorecord <minutes*60>` audio only. Run B (if screen): `--autorecord 600 --screen`. Validate both with check-recording.ps1 (no losses: system.wav duration within +/-1 s of durationS, no zero runs > 100 ms while the tone plays, droppedFrames == 0). Summary table vs budgets from docs/PLAN.md: idle CPU < 1%, audio-only CPU < 3%, audio+screen CPU < 8%, RAM during recording < 150 MB. Upload JSON. timeout-minutes = 120.
3. scripts/check-recording.ps1 (pwsh 7): params -PerfJson, -ExpectTone; reads the JSON and the WAVs from session.folder (parse WAV headers + samples in PowerShell/.NET), prints a markdown table, exit 1 on violations. scripts/gen-tone.ps1.
Contract with the app (implemented by another task): `Murmur.exe --autorecord <seconds> [--screen] --perf-out <json>` writing {durationS, idleCpuPct, cpuAvgPct, cpuP95Pct, cpuAvgPctOneCore, wsAvgMb, wsMaxMb, privateMaxMb, recorderStats{microphoneFrames, systemFrames, droppedFrames, screenFramesDropped}, session{id, folder, micSeconds, systemSeconds, screenSegments}}; mic.wav/system.wav are 16 kHz mono PCM16 in session.folder, screen segments screen-NNN.mp4.
4. Update docs/CI.md.

## Acceptance
YAML parses (python yaml + actionlint if available); pwsh scripts parse (install pwsh via the Microsoft packages apt repo if reachable, else careful review); docs updated.
