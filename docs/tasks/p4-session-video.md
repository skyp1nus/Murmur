# Task p4-session-video: the screen recording in the session view

Branch `feat/session-video` (based on `phase4`, which has `IVideoPlayer`/`IVideoPlayback` (Core), `ScreenPlaybackSync`
(Core, `src/Murmur.Core/Playback`), the Windows Media Engine player (`WindowsCapture` implements `IVideoPlayback`,
D-035) and the session timeline of screen segments (D-033)). PLAN.md phase 4: "Плеєр екрана в «Перегляді запису»:
відео синхронно зі звуком і транскриптом, клік по слову перемотує й відео".

## Scope
src/Murmur.App/** (SessionView, SessionViewModel, a video control), src/Murmur.Desktop/CompositionRoot.cs (wiring
only), tests/Murmur.App.Tests/**. Not Core (read the XML docs of `IVideoPlayer`, `ScreenPlaybackSync`,
`VideoSyncTypes` — they are the contract and include a usage example), not Capture.

## Design
`docs/design/source/Session.dc.html` / `SessionDark.dc.html`: the player section has a 16:9 rounded (radius card)
panel above the waveform card, with a mono chip "1920×1080 · H.264" bottom-left. Tokens only, DynamicResource,
compiled bindings.

## Spec
1. **Wiring**: `AppServices` gets an optional `IVideoPlayback? VideoPlayback` (keep existing constructor callers
   compiling). Desktop passes the `WindowsCapture` instance on Windows (it already implements `IVideoPlayback`), null
   elsewhere. When null or `IsSupported == false`, or the session has no screen segments, the session view looks as
   today (no video panel).
2. **Session view model**: when a session with `ScreenSegments` opens, create one `IVideoPlayer` (dispose it when the
   session view closes or another session opens). Drive `ScreenPlaybackSync` from the existing playback tick
   (`SessionViewModel.Tick`, which only runs while playing — make sure the video also follows seeks while paused: a
   click on a word / waveform seek must show the right frame even when paused, so run one sync step on every seek and
   when the session opens). Apply each `VideoSyncCommand` in the documented order (Close, Open (async — report
   `VideoPlayerState.Opening(k)` until it completes), Seek, Play/Pause). `OpenAsync` failure or `Failed` →
   `MarkUnplayable(k)` and show the placeholder for that segment with a short localized note; never crash or stop the
   audio. Segment file path: `SessionFiles.ScreenSegment(index)`; a missing file → unplayable.
3. **Video control** (`Controls/VideoSurface` or similar): owns a `WriteableBitmap` (BGRA8888, premultiplied/opaque),
   sized to the control's current pixel size fitted to the video's aspect ratio (letterbox; use the render scaling),
   recreated only when that size changes. On each render tick (`TopLevel.RequestAnimationFrame` or the existing tick —
   only while the session view is visible and the video is open), call `IVideoPlayer.TryCopyFrame` into the locked
   framebuffer (`ILockedFramebuffer.Address` → `Span<byte>` with its RowBytes as stride) and invalidate only when it
   returns true. Per frame: zero allocations (CLAUDE.md hot path) — no LINQ, no closures, no string formatting; add an
   allocation test with a fake player (like the existing headless tests that measure allocations, if any; otherwise
   `GC.GetAllocatedBytesForCurrentThread` around N ticks after warm-up). Rounded corners via the panel's clip, the
   "screen off" placeholder (icon + text) in gaps between segments and while no segment is open, and the chip with the
   native size `VideoSize` + "H.264".
4. **Behaviour**: play/pause/seek of the audio drive the video through the sync (the audio stays the master clock;
   never seek the audio from the video). Changing playback speed (the "1×" control if the view has it) → the video
   follows by seeks (the sync handles drift) — do not implement rate on the video player. Leaving the session view
   stops ticking and closes the player.
5. **Tests (headless)**: fake `IVideoPlayback`/`IVideoPlayer` (records calls, controllable Position/IsPlaying, frames
   that fill the buffer with a colour): a session with two segments and a gap → opens segment 0 at start, placeholder
   in the gap, segment 1 after it; a word click while paused seeks the video; play/pause follow the audio; open
   failure → placeholder + note, audio keeps playing; disposing on session change; no video panel when playback is
   null/unsupported or the session has no segments; the surface copies frames only when TryCopyFrame returns true;
   allocation test for the per-frame path; Light/Dark × uk/en render (UiCulture.Override, never Apply). Screenshot
   of the session view with the video panel (fake coloured frame) in both themes to
   /tmp/claude-0/-home-user-Murmur/ffa700a1-1873-543f-83db-f4dfe7fce242/scratchpad/session-video/ (only with
   `MURMUR_SCREENSHOT_DIR`, no tracked files change).

## Acceptance
Linux: 0 warnings, App tests green (also LANG=en_US.UTF-8), Desktop builds for both TFMs. The real Media Engine path is
verified by the architect's Windows CI run (the Windows player tests already pass there) and by the owner on the final
build.
