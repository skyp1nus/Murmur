# Task p4-screen-player: play the screen recording in sync with the audio (engine part)

Branch `feat/screen-player`. PLAN.md phase 4: "Плеєр екрана в «Перегляді запису»: відео синхронно зі звуком і
транскриптом, клік по слову перемотує й відео (Media Foundation Media Engine, альтернатива з D-020)". This task builds
the Core sync logic and the Windows player; the session view UI is a later task (do not touch src/Murmur.App or
src/Murmur.Desktop).

## Contracts (architect-owned, already in the branch — do not change)
- `src/Murmur.Core/Capture/IVideoPlayer.cs`: `IVideoPlayer`, `IVideoPlayback`, `VideoSize`. Frame-server model: the UI
  pulls the current picture into its own bitmap with `TryCopyFrame` (BGRA32, caller-chosen size).
- Session timeline: `SessionRecord.ScreenSegments` (`ScreenSegment(Index, Offset, Duration)`, D-033): media time 0 of
  `screen-NNN.mp4` is session time `Offset`; the audio tracks start at session time 0 and are the master clock
  (`IAudioPlayer.Position`). Gaps between segments = the screen was off.

## Spec
1. **Core: `Murmur.Core.Playback.ScreenPlaybackSync`** (new folder `src/Murmur.Core/Playback`), pure and allocation-free
   per tick, fully unit-tested on Linux. Input each UI tick: audio position, audio playing?, the video player's state
   (open segment index or none, its position, playing?). Output: a small command struct telling the caller what to do
   with the video player — open segment k (and seek to t), close (show the "screen off" placeholder), seek to t, play,
   pause, or nothing. Rules:
   - The segment containing the audio position is the one to show; outside every segment → close/placeholder.
   - Target video time = audio position − segment.Offset, clamped to [0, segment.Duration].
   - Drift: when |video − target| > tolerance (default 150 ms) → seek; never seek more often than every 500 ms while
     playing (avoid seek storms on a slow decoder); a user seek (audio position jumps) → immediate seek.
   - Play/pause follows the audio; at the end of a segment while audio continues → close when the audio leaves it.
   - Opening is asynchronous: the caller reports "opening k" and the sync must not request it again meanwhile.
   Design the API so the session view model can drive it from its existing UI timer; document it with an example in
   the XML docs. Test: table-driven tests for every rule, a simulated 2-segment session with gaps, user seeks across
   segments, a decoder that lags (position not advancing) → bounded seeks.
2. **Windows: `WindowsVideoPlayback : IVideoPlayback` + `MediaEngineVideoPlayer : IVideoPlayer`** in
   `src/Murmur.Capture.Windows/Video/` using the Media Foundation Media Engine (`IMFMediaEngineClassFactory`,
   `IMFMediaEngine`/`IMFMediaEngineEx`) in **frame-server mode** (no HWND):
   - Own thin interop like the rest of the project (`[GeneratedComInterface]` / function-pointer vtables — look at
     `src/Murmur.Capture.Windows/Screen/Interop` and `Screen/GpuDevice.cs`, reuse what fits); the notify callback
     `IMFMediaEngineNotify` as a `[GeneratedComClass]`. Verify every vtable slot order against the Windows SDK headers
     (mfmediaengine.h) and list them in your notes. No Vortice, no new packages.
   - D3D11 device with video support + `IMFDXGIDeviceManager` (`MF_MEDIA_ENGINE_DXGI_MANAGER`), output format
     `DXGI_FORMAT_B8G8R8A8_UNORM`; when there is no hardware device (WARP on CI), fall back to whatever the Media
     Engine supports there (WARP device or software) — `IsSupported` is false only when nothing works.
   - Muted, no audio; `OpenAsync` = SetSource + Load, completes on LOADEDMETADATA (or fails on ERROR), paused at 0.
   - `TryCopyFrame`: `OnVideoStreamTick` → when a new frame is due, `TransferVideoFrame` into a reusable BGRA render
     target of the requested size → copy to a reusable staging texture → Map → copy rows into the span. Recreate the
     textures only when the requested size changes. Zero allocations per frame (add a BenchmarkDotNet-free allocation
     test like the existing Windows tests do, see PlaybackMixingTests).
   - `Seek` while paused must make the next `TryCopyFrame` return the frame at the new position.
   - Events from the Media Engine arrive on MF threads: keep the state thread-safe, raise `Failed` from there.
   - Expose it from the Windows composition point the same way audio playback is (look at how `IAudioPlayback` is
     provided by `WindowsCapture`), but do not wire it into Desktop/App (later task).
3. **Tests (Windows-trait, run in CI):** produce a short MP4 with the existing recorder in tests
   (`WgcScreenRecorder` + `AnimatedWindow` helpers, or the H264 sink writer directly with synthetic frames whose colour
   encodes the time — prefer the latter, it does not need a desktop): open → Duration/Size right; Play for ~1 s →
   Position advances ~1 s (±200 ms); Pause holds; Seek(t) then TryCopyFrame returns the frame for t (check the encoded
   colour); TryCopyFrame returns false when nothing changed; a missing file → OpenAsync throws, a corrupt file →
   Failed or throw; Dispose releases (open/dispose 20 times, no growth in handle count). Skip with a clear message
   when Media Foundation is absent.
4. **Docs.** docs/DECISIONS.md is the architect's; put anything decision-like in your notes.

## Acceptance
Linux: 0 warnings, Core tests green (new sync tests), Capture.Windows builds; Windows-trait tests written and
UNVERIFIED until the architect's CI run (the integration PR runs Windows CI on every push).
