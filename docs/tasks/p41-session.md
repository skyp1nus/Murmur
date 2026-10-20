# Task p41-session: Recording details exactly as the canvas, with Summary and Export

Branch `design/session` (based on `phase4` after wave 2). Boards: `Session`, `SessionDark` (content area x ≥ 241; the
sidebar belongs to `p41-shell-library`, already merged). Truth: `docs/design/source/Session*.dc.html`,
`docs/design/reference/Session*.png`; measure with `DesignParityTests`.

## Core contract (approved by the architect — exactly this)
- `Murmur.Core.Intelligence`:
  - `SummaryItem(string Text, TimeSpan? At)`
  - `SessionSummary(SessionId Session, string Overview, IReadOnlyList<SummaryItem> KeyPoints, IReadOnlyList<SummaryItem> ActionItems, string ModelId, DateTimeOffset CreatedAt)`
  - `ISessionSummarizer.SummarizeAsync(SessionId, IProgress<double>?, CancellationToken) → ValueTask<SessionSummary>`:
    built on `ILlm` over the transcript (chunked map → reduce for long meetings), timestamps resolved to real transcript
    times like the assistant's citations; English or Ukrainian follows the transcript.
- `Murmur.Core.Sessions.ISummaryStore` (`GetSummaryAsync`, `SaveSummaryAsync`, `DeleteSummaryAsync`), part of `IStorage`;
  SQLite implementation in `Murmur.Storage` (migration, deleted with the session).
- `Murmur.Core.Sessions.TranscriptExport`: pure formatters `ToMarkdown(session, transcript, summary?)`, `ToPlainText`,
  `ToSrt`, `ToWebVtt` (speakers "You"/"Others" localized by the caller passing the labels).
AI rules stay: never while recording; the model loads lazily in the worker and unloads as today.

## Spec (UI)
1. Top bar: breadcrumb "Library / <title>"; right **Export** (flyout: Markdown, Text, SRT subtitles, WebVTT → save
   dialog; Markdown includes the summary when one exists) and **…** (Rename, Show in folder, Re-transcribe if it exists
   today, Delete — the existing actions).
2. Header: title (display 36/300), meta "Today, 10:30 · 47:12 · English · Parakeet TDT v3".
3. Left column: the video panel (rounded surface, the existing video surface; the "1920×1080 · H.264" chip from the
   segment's real resolution/codec; the "Screen off" placeholder between segments), then the player card: waveform of
   the session (peaks from the audio, ink before the playhead, light after; click/drag seeks), play/pause, time
   "12:21 / 47:12" in Geist Mono, track filters **You** / **Others** (mute that track in playback and dim its lines),
   speed chip **1×** (cycles 1, 1.25, 1.5, 2, 0.75).
4. Right column: tabs **Transcript / Summary / Ask AI** (pill style; the active one has the dot):
   - Transcript: "Search transcript" box (highlights hits like the canvas, Enter jumps to the next); rows with the
     avatar (Y / O), speaker, time (Geist Mono), text; the current segment is the filled row; click seeks.
   - Summary: empty state with **Summarize** (needs the AI models; explains when they are missing), progress while it
     runs, then Overview, Key points, Action items — each item with a time chip that seeks; **Regenerate**.
   - Ask AI: the existing per-session chat, restyled to the Ask AI look (scope fixed to this recording).
5. Keep every existing behaviour (playback sync D-035, warnings, interrupted sessions, missing files) — restyle.
6. Parity: content area of `Session` / `SessionDark` within the parity gates with the demo state (Transcript tab, 12:21, "timestamps"
   search hit); ignore rectangle only for the video frame and the waveform bars; set the three parity gates (below).
7. Tests: Core formatters (golden files), summarizer with a fake `ILlm` (chunking, citation resolution, cancel), storage
   migration and delete cascade; App: tabs, export flow, filters, speed, search navigation, summary states.
8. Report (Ukrainian): mismatch before/after, what still differs, test results.

## Parity gates (D-041)
`DesignParityTests` measure three things per board (report: `artifacts/design-parity/report.md`, per board `<Board>.side.png`,
`<Board>.shapes.side.png`, `<Board>.text.md`, `<Board>.text.png`):
- **Shape layer** (app rendered with every glyph transparent vs `reference/<Board>.notext.png`, symmetric, tolerance 5/255):
  `MaxShapeMismatchPercent` ≤ **4 %** of drawn pixels (onboarding steps 3 and 4: ≤ 10 % until measured lower).
- **Text matched** (reference texts from `reference/<Board>.text.json` found in the app by exact text): `MinTextMatchedPercent`
  ≥ **95 %**; every text still missing is either a documented deviation (`docs/design/DEVIATIONS.md`) or demo data the
  app cannot produce — list them in the report.
- **Text geometry** (matched texts: |Δx| and |Δ centre y| ≤ 2 px, width within 3 %, same line count, size, weight, family):
  `MaxTextGeometryErrors` ≤ **2**.
Set the three gates for your boards in `DesignBoards.cs` at the values you reach (not looser than above) so CI keeps them.
Pixel-level text differences (Chromium vs Skia rasterisation) are not a gate. Work from `<Board>.text.md` (sorted list of
text errors) and the side-by-sides; check visually at the end that nothing differs that the metrics miss (e.g. icon
shapes, orb look, hover/selected states drawn on the board).
