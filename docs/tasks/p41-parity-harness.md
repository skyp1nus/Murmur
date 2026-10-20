# Task p41-parity-harness: render every artboard with the app and compare it with the reference

Branch `design/parity-harness` (based on `phase4`). Phase 4.1 makes every screen match the Claude Design canvas 1:1
(D-041). This task builds the measuring tool only; the screens are fixed by later tasks. Another task (`p41-english-default`)
switches the neutral resources to English in parallel: render in English through the existing language switch
(`UiCulture`), and touch `Strings*.resx` only if you must.

## Inputs
- `docs/design/source/*.dc.html` + `canvas.json`: the artboards (markup, inline styles, demo texts). All in English.
- `docs/design/reference/<Board>.png`: Chromium renders of every artboard at scale 1, canvas width, full page height
  (Main 1440x1406, Recording 1440x1000, Session 1440x1100, Chat 1440x1080, Settings 1440x1819, onboarding 1280x800,
  OnbComponents 1280x1600; `*Dark` the same sizes). These are the truth.

## Scope
tests/Murmur.App.Tests/** (new `Design/` folder), test-only fakes, `scripts/design/` (a runner script), `.github/workflows/ci.yml`
(upload the report from the Linux job), docs/design/README.md (how to run). No product code changes, except a test seam
that is missing and cannot be avoided (say so in the report).

## Spec
1. **Boards.** A table of the 22 screen artboards (all except `OnbComponents*`, which is a component sheet: list it as
   excluded) → how to put the app into that exact state: screen, theme (light/dark), English, window size = the
   reference PNG size. Main = Library; Recording = an active recording; Session = the "Client demo — Q4 roadmap" session
   at 12:21 with the 12:21 segment current and "timestamps" as a search hit; Chat = Ask AI with the two exchanges;
   Settings = the settings page scrolled to the top and rendered at full height; Onb1–Onb5 and Onb3ModelsReady = the
   onboarding steps in the drawn state.
2. **Demo data = the artboard's content**, exactly: titles, times ("Today, 10:30", "47:12"), languages, sources,
   snippets, the "Transcribing · 64%" row, sidebar Recent entries, transcript lines and speakers, chat messages,
   sources and timestamps, model rows and their states (Parakeet installed 0.67 GB, Silero VAD installed, Gemma 4 E4B
   downloading 42% with "Default", Gemma 4 26B A4B not installed, EmbeddingGemma installed), device names, the storage
   path `C:\Users\[USER]\Murmur`, hotkeys, recording timer 00:24:13, title "Untitled — Oct 18, 01:12", levels -18 / -24 dB.
   Put it in one `DesignDemoData` class that later tasks extend. Where the app has no way to show something yet
   (e.g. pause, summary tab, chats list), leave it out and list it in the report: the screen tasks add them.
3. **Render** each board headlessly (the existing `UiHarness` / Avalonia.Headless + Skia, scale 1, fonts embedded),
   theme and size set before the first frame, animations settled (orb static), caret and focus visuals off.
4. **Compare** with the reference PNG (SkiaSharp is available; no new packages): per pixel, mismatch when the channel
   difference exceeds a tolerance after allowing a 1 px shift (compare with the min over the reference's 3x3
   neighbourhood) so anti-aliasing and sub-pixel text differences do not count. Report the mismatch percentage for the
   whole board, for the sidebar strip (x < 241 on the main screens) and for the content area separately. Per-board
   ignore rectangles are allowed only for content the app cannot reproduce (the video frame placeholder, randomly
   drawn waveform bars, `[VERSION]`/`[LICENSE]` placeholders): declare them in the board table with a reason.
5. **Output** to `artifacts/design-parity/`: `<Board>.app.png`, `<Board>.diff.png` (reference greyed, mismatches in
   red), `<Board>.side.png` (reference | app | diff) and `report.md` (table: board, size, mismatch % total / sidebar /
   content, ignored area). Tests are `[Trait("Category", "DesignParity")]`, report-only for now (a board passes unless it
   cannot be rendered); each board has a `MaxMismatchPercent` that starts as `null` (not enforced) — screen tasks set it.
6. `scripts/design/parity.sh` (and `.ps1`): run just these tests and print the report. CI Linux job uploads
   `artifacts/design-parity` as an artifact (keep the job's time reasonable).
7. Tests for the comparer itself (identical images → 0 %, a 1 px shifted image → ~0 %, a moved block → mismatch).
8. Report: the current mismatch numbers for every board, with the three worst-looking side-by-sides described in a
   few lines each (what differs: structure, spacing, fonts, colours), so the screen tasks start from facts.
