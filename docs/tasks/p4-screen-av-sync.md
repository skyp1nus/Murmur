# Task p4-screen-av-sync: screen segments start where the session timeline says

Branch `fix/screen-av-sync-start`. PLAN.md, phase 4, task "Синхрон звуку й відео".

## Problem
On the owner's PC (RX 9070 XT, GPU path) the screen recording is out of step with the audio: with the screen on, the
audio is 0.43–0.48 s longer than the recording; without the screen it is exact. Cause found by the architect:

- `Recorder.StartScreenSegmentAsync` (src/Murmur.Core/Recording/Recorder.cs) takes `ScreenSegment.Offset` on the
  session timeline right before it calls `IScreenRecorder.StartSegmentAsync`.
- `WgcScreenRecorder` (src/Murmur.Capture.Windows/Screen/WgcScreenRecorder.cs) sets media time 0 of the file with
  `segment.Timeline.Reset(MediaTime.Now())` only at the end of `StartCore`, after the encoder (Media Foundation), the
  frame pool and the capture session are created — about 0.45 s later on that PC. Everything in the MP4 is therefore
  ~0.45 s late against `Offset`, and the file is ~0.45 s shorter than `ScreenSegment.Duration`.
- `StopCore` takes `segment.StopTicks = MediaTime.Now()` after the gate, `Task.Run` scheduling and a spin; the
  `Recorder` takes the segment end before calling `StopSegmentAsync`. Smaller, same kind of error.
- The autorecord harness (src/Murmur.App/Diagnostics/AutoRecordRunner.cs) measures `durationS` from after
  `StartRecordingAsync()` returns, which includes the screen start-up, while the audio started before it. So the
  "audio longer than the recording" number is partly a harness artefact of the same start-up time.

Contract (already documented in this branch on `IScreenRecorder.StartSegmentAsync`/`StopSegmentAsync` and
`ScreenSegment`): media time 0 of a segment file is the moment `StartSegmentAsync` is called; the file ends at the
moment `StopSegmentAsync` is called; `ScreenSegment.Offset` is the session time of media time 0 and
`ScreenSegment.Duration` equals the media duration.

## Spec
1. `WgcScreenRecorder`: take the start time (`MediaTime.Now()`) as the first thing in `StartSegmentAsync`, before the
   gate, and use it for `segment.Timeline.Reset(...)`. The first frame already stands for the picture since the
   segment start (`HeldFrameTiming.Advance`: first held frame at 0), so the first frame now covers start-up as well.
   Same for stop: take `MediaTime.Now()` first thing in `StopSegmentAsync` and use it as `segment.StopTicks` (and
   wherever else the segment end is decided, e.g. the placeholder path and `CloseTimeline`). Keep the fault path
   (`FaultTicks`) as is. Check every other place that measures "segment start/end" and make them consistent.
2. `Recorder`: keep taking `Offset` immediately before `StartSegmentAsync` and the end immediately before
   `StopSegmentAsync` (both on the session clock `_time`, which is QPC-based like `MediaTime`); make sure nothing
   awaitable sits between taking the time and the call. If you change any of it, keep the existing Recorder tests green.
3. Harness: `durationS` in the perf JSON must be the length of the session timeline the files are on: use
   `IRecorder.Elapsed` (session time since the audio started) read right before stopping, instead of a stopwatch started
   after `StartRecordingAsync()`. Keep the per-second CPU/RAM sampling loop as it is. Add to the perf JSON (PerfReport):
   `screenStartMs` (how long starting the screen took when the recording starts with the screen on: time between the
   recorder's session time 0 and `StartAsync` returning, or better measured inside the runner around the start call —
   document which) and the session's `screenSegments` (index, offsetS, durationS) so CI and the owner can compare them
   with the MP4 files.
4. `scripts/check-recording.ps1` (it already measures MP4 media duration via scripts/MurmurMp4Scan.cs): for every
   segment in the perf JSON compare the MP4 media duration with the segment `durationS` — limit ±0.25 s (one GOP-free
   frame plus timer slack; explain the number in the script). Keep the existing `system.wav` vs `durationS` check (±1 s)
   and add an INFO row with the signed difference so the start-up effect is visible. Update
   tests/Murmur.Scripts.Tests (CheckRecordingScriptTests) accordingly.
5. Tests:
   - Linux: unit tests for whatever pure timing logic you add/touch (e.g. timeline start/stop math), Recorder test that
     `ScreenSegment.Offset`/`Duration` are taken around the recorder calls with a fake screen recorder that takes a
     long time to start and to stop (Core has fakes for capture: look at tests/Murmur.Core.Tests and
     `Murmur.Core.Capture.Synthetic`), App test (AutoRecordTests) that `durationS` follows the recorder timeline.
   - Windows-trait test in tests/Murmur.Capture.Windows.Tests: a segment whose start is artificially slow (inject a
     delay before the encoder opens if there is a test seam; otherwise measure the real start-up) has MP4 media
     duration == wall time between the StartSegmentAsync and StopSegmentAsync calls ± 50 ms. Reuse Mp4Probe /
     Mp4Fragments helpers.
6. Docs: a short note in docs/CI.md (check-recording section) about the new checks. Do not edit PLAN.md (the architect does).

## Out of scope
The duration-proportional drift seen on CI with VB-Cable (+1.03 s per 10 min with the screen on) — do not try to fix
it; if your new perf JSON fields make it easier to explain, say so in the notes.

## Acceptance
Linux: 0 warnings, tests of Core, App, Capture.Windows (Linux-runnable part), Scripts green. Windows behaviour
UNVERIFIED until the architect's CI run: capture-e2e must show |MP4 − segment durationS| ≤ 0.25 s.
