# Task p41-recording: the Recording screen exactly as the canvas, with pause / resume

Branch `design/recording` (based on `phase4` after wave 1). Boards: `Recording`, `RecordingDark` (content area,
x ≥ 241). Truth: `docs/design/source/Recording*.dc.html` and `docs/design/reference/Recording*.png`; measure with
`DesignParityTests`. `p41-shell-library` owns the sidebar and the top bar frame in parallel (the "Recording" pill, the
"No models are loaded while recording" card, the breadcrumb and the "Stop Ctrl Shift R" hint): do not edit
`SidebarView` or the main window frame; you own `RecordingView` and the recorder.

## Core contract (approved by the architect — implement exactly this, nothing else in Core's public surface)
- `RecorderState` gains `Paused` (between `Recording` and `Stopping`).
- `IRecorder.PauseAsync(CancellationToken)` / `IRecorder.ResumeAsync(CancellationToken)`:
  - Pause: nothing is written while paused; `Elapsed` stops; levels fall to 0; the screen segment in progress ends at the
    pause time (as a stop of that segment, D-033 timing).
  - Resume continues the same audio files with no gap and no silence: the paused time is left out of the session
    timeline. If the screen was on, a new segment starts on resume with its offset on that timeline.
  - Stop works while paused. Pause/Resume in the wrong state are no-ops (or `InvalidOperationException` — pick one,
    document it, test it).
  - The capture sources keep running while paused and their frames are discarded (no device re-open); the writer loop
    stays allocation-free (`WriterSteadyStateAllocatedBytes == 0` with pauses in the run — add that test).
  - Crash while paused: recovery sees a normal interrupted session; nothing extra.
- `HotkeyAction.TogglePause` in `Murmur.Core.Input`, `AppSettings.PauseHotkey` default `Ctrl+Shift+P`, registered like
  the other hotkeys; it pauses / resumes only while recording.

## Spec (UI, `Recording.dc.html`)
1. Centre column: "Title" label + the editable title (display weight 300; editing renames the session, Enter/blur
   saves, Esc cancels); the big orb with halo (static under reduced motion), the timer `00:24:13` in Geist Mono at the
   canvas size, "Auto-saved to disk every few seconds"; source chips Microphone / System audio / Screen ("Screen off"
   dashed when off; clicking toggles the screen as today); buttons **Pause** (↔ **Resume** while paused, the timer and
   orb show the paused state) and **Stop and transcribe**.
2. Right column: card **Tracks** — per track an icon, "You · microphone" / "Others · system audio", the level in dB
   (Geist Mono), a scrolling level history drawn as bars (the newest on the right; past bars ink, future bars light —
   match the canvas look) and the device name ("Microphone (USB Audio Device)", "Speakers (Realtek Audio) · loopback");
   a muted/denied/absent track shows its state in the same layout. Card **Let people know** — text and **Copy message**
   (copies a short English/Ukrainian notice to the clipboard; the button shows "Copied" for 2 s). The note under the cards.
3. Keep every existing behaviour (warnings, mic permission card, screen faults, device fallbacks) — restyle into the canvas
   layout, do not drop.
4. Parity: content area of `Recording` / `RecordingDark` ≤ 2 % (demo data: 00:24:13, the title, -18 / -24 dB, the two
   devices, screen off); set `MaxMismatchPercent` for the content area of both boards.
5. Tests: Core — pause/resume timing (elapsed, file length = recorded time, screen segments around a pause, stop while
   paused, allocation-free writer with pauses, crash recovery); App — pause button states, hotkey, copy message, title
   rename. Windows-only parts (WASAPI/WGC behaviour under pause) get Windows tests with `[Trait("Category","Windows")]`.
6. Report (Ukrainian): mismatch before/after, what still differs, test results.
