# Task p2-ci-asr: CI — real-model ASR tests and the "1 h audio -> text < 3 min" benchmark on Windows and Linux

## Scope
.github/workflows/**, scripts/**, docs/CI.md

## Spec
1. New workflow file `.github/workflows/asr.yml` (do NOT edit ci.yml — another task is changing it in parallel) with triggers push to night-run/master and workflow_dispatch, and a job `asr` (matrix: windows-latest, ubuntu-latest; builds itself with setup-dotnet + the same SDK pin/verify steps as ci.yml; timeout 45): cache the models dir (actions/cache keyed on 'parakeet-tdt-0.6b-v3-int8 + silero_vad v1') and run scripts/fetch-models.(ps1|sh) with MURMUR_MODELS_DIR=<workspace>/models; run `dotnet test tests/Murmur.Transcription.Tests -c Release` and `dotnet test tests/Murmur.Core.Tests -c Release` with MURMUR_MODELS_DIR set so the real-model tests run (they skip without models); AOT-publish the worker for the OS RID; run the worker bench: `Murmur.Transcription --bench --model-dir ... --vad ... --wav <the four test_wavs, comma-separated> --seconds 3600 --out artifacts/perf/asr-bench.json`; write a summary table (audio s, transcribe s, RTF, x-realtime, threads, CPU name) and FAIL the job if transcribe time > 180 s (budget from docs/PLAN.md); upload asr-bench.json.
2. Keep the existing jobs intact; do not run the 1-hour bench on every OS twice (the matrix above is the only place).
3. Update docs/CI.md.
Contract (implemented by the worker task): `Murmur.Transcription --bench --model-dir <dir> --vad <file> --wav <a.wav>[,<b.wav>...] --seconds <N> --out <json>` writes {audioSeconds, loadMs, transcribeMs, rtf, realtimeFactorX, segments, words, threads, batch, machine{os, cpu, cores}}. Check the merged worker source in src/Murmur.Transcription for the exact flags before writing the job.

## Acceptance
YAML parses (python yaml, actionlint if available); shell scripts pass bash -n; docs updated.
