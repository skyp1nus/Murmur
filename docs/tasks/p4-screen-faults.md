# Task p4-screen-faults: screen problems reach the session, the UI and the perf JSON

Branch `feat/screen-faults` (based on `phase4`). PLAN.md phase 4: "Помилки екрана (вичерпаний пул, перехід на CPU
readback) доходять до сесії й perf JSON: `Recorder` підписується на `IScreenRecorder.Faulted`, нефатальні — як
попередження сесії".

## Contract (architect, in the branch's first commit — do not change)
`src/Murmur.Core/Recording/IRecorder.cs`: `RecorderStats.ScreenFaults` (new last parameter, default 0),
`RecorderWarning(Source, Message, ScreenStopped)`, `IRecorder.IsScreenRecording`, `IRecorder.Warning` event. Read the
XML docs. `IScreenRecorder.Faulted` (`CaptureFault(Source, Message, IsFatal)`) already exists and the Windows recorder
raises it (look at `WgcScreenRecorder` for every place that reports a fault: sample-pool exhaustion, GPU → CPU readback
fallback, device lost, encoder errors, WGC session closed).

## Scope
src/Murmur.Core/Recording/** (Recorder), src/Murmur.App/** (recording screen + perf JSON in Diagnostics), tests
(Core, App), fakes implementing `IRecorder` (tests/Murmur.App.Tests/Fakes/FakeRecorder.cs,
tests/Murmur.Core.Tests/Fakes/ProcessingFakes.cs). Not Capture.Windows (only read it), not Storage.

## Spec
1. **Recorder** subscribes to `IScreenRecorder.Faulted` for every segment (and unsubscribes when the segment ends).
   Faults arrive on capture threads: no locks held across user callbacks, no allocation-heavy work on the capture
   thread beyond creating the warning once (faults are rare — a small allocation is fine, but never block).
   - Count every fault in `RecorderStats.ScreenFaults`.
   - Non-fatal fault: raise `Warning` once per distinct message per recording (`ScreenStopped = false`) and add a
     Ukrainian user text to the session's warnings (today warnings are joined into `SessionRecord.Error` via
     `active.Warning` / `JoinMessages` — follow that mechanism; do not invent a new field: that is a Core record change).
     Texts (short, user-facing; the technical detail goes to the console/log only):
     pool exhausted → "Запис екрана пропустив кадри: компʼютер не встигав."; CPU-readback fallback → "Екран
     записується без прискорення відеокарти — це більше навантажує процесор."; other non-fatal → "Під час запису
     екрана були збої."
   - Fatal fault: end the current segment cleanly (stop it as if the user switched the screen off, keep the file and
     its `ScreenSegment` with the real end on the timeline — D-033), `IsScreenRecording` → false, raise `Warning` with
     `ScreenStopped = true` ("Запис екрана зупинився через помилку; звук записується далі."), add it to the session
     warnings, and never stop or fail the audio. The user may switch the screen on again (a new segment).
   - `IsScreenRecording` reflects reality: true only while a segment runs (after `SetScreenEnabledAsync(true)`
     succeeded and until it is switched off, the recording stops, or a fatal fault ends it).
2. **Recording screen** (`RecordingViewModel` / `RecordingView`): the screen toggle follows `IsScreenRecording` (a fatal
   fault flips it off), and warnings appear as a small inline notice under the controls (token styles, both resx
   files; marshal `Warning` to the UI thread). Keep it unobtrusive — no modal dialogs.
3. **Perf JSON** (`src/Murmur.App/Diagnostics/PerfReport.cs`, `AutoRecordRunner`): add `screenFaults` (count) and
   `warnings` (the messages, in order) to the report; `scripts/check-recording.ps1` prints them (do not fail on
   warnings; do fail when `screenFaults` > 0 only if a new `-FailOnScreenFaults` switch is given). Update the script
   tests (tests/Murmur.Scripts.Tests).
4. **Tests**: Core — a fake screen recorder that raises non-fatal and fatal faults from another thread: counts,
   one warning per distinct message, session warnings text, fatal ends the segment with the right duration on the
   timeline and keeps the audio, screen can be re-enabled after a fatal fault, unsubscribe after the segment (a late
   fault from a finished segment is ignored), no deadlock when `Faulted` is raised while `SetScreenEnabledAsync` or
   `StopAsync` runs. App — the toggle flips off on a fatal fault, the notice shows, perf JSON contains the new fields.

## Acceptance
Linux: 0 warnings; Core, App and Scripts tests green (MURMUR_PWSH for Scripts:
/tmp/claude-0/-home-user-Murmur/ffa700a1-1873-543f-83db-f4dfe7fce242/scratchpad/pwsh/pwsh). The real WGC faults are
verified by Windows CI (capture e2e + the ScreenCrashRecovery tests).
