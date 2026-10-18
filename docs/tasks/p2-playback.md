# Task p2-playback: WASAPI playback of a session's tracks

## Scope
src/Murmur.Capture.Windows/** (playback files + IAudioPlayback on WindowsCapture or a separate class), tests/Murmur.Capture.Windows.Tests/**

## Spec
Implement `IAudioPlayback`/`IAudioPlayer` (src/Murmur.Core/Capture/IAudioPlayer.cs) for Windows, following the semantics documented on `Murmur.Core.Capture.Synthetic.ClockAudioPlayer` (Core, read it first).
1. `WasapiAudioPlayback : IAudioPlayback` and `WasapiAudioPlayer : IAudioPlayer`: default render endpoint, shared mode, event-driven (AUDCLNT_STREAMFLAGS_EVENTCALLBACK) with AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY so the engine converts our 16 kHz mono PCM16 to the mix format (no resampler of our own). Add IAudioRenderClient (+ IAudioClock if you use it) to the existing thin interop (reuse the capture task's COM definitions; verify vtables against audioclient.h and list them).
2. OpenAsync(trackPaths): open the session WAVs (16 kHz mono PCM16; 1–2 tracks), Duration = longest; render thread (MMCSS "Playback") mixes tracks sample-wise (sum, soft clip) from preallocated buffers read from the files in chunks (no full-file load, no allocations in the render loop). Play/Pause (stop the client, keep position), Seek(position) (clamp; flush: Stop + Reset, reposition readers), Position = frames submitted - padding (GetCurrentPadding) converted to time, accurate to ~20 ms; Ended when the data runs out (StateChanged raised once, off the render thread or marshalled — document). Device invalidated -> Paused + fault surfaced (StateChanged); Dispose stops and releases everything.
3. Expose playback from the Windows capture facade: `WindowsCapture` implements IAudioPlayback (or a separate `WindowsAudioPlayback` the composition root constructs — pick one, note it).
4. Tests: Linux-runnable unit tests for the pure parts (mixing with clipping, position math, seek clamping, WAV chunk reader); Windows-trait tests (skipped elsewhere): open a generated 2-track WAV pair, play 1 s, Position advances ~1 s (±150 ms), Pause holds, Seek works, Ended fires (requires a render endpoint — skip with a message when none).

## Acceptance
Linux: build 0 warnings, unit tests green, vtables checked. Windows behaviour UNVERIFIED until CI.
