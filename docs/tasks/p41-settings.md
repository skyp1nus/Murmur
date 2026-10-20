# Task p41-settings: Settings exactly as the canvas, with Acceleration

Branch `design/settings` (based on `phase4` after wave 2 and `p41-gpu-acceleration`). Boards: `Settings`, `SettingsDark`
(1440x1819, content area x ≥ 241) and the **Sections** list in the sidebar (the hook `p41-shell-library` left). Truth:
`docs/design/source/Settings*.dc.html`, `docs/design/reference/Settings*.png`; measure with `DesignParityTests`.

## Scope
src/Murmur.App/** (Settings views and view models), tests/Murmur.App.Tests/**. The backend for acceleration exists
(`p41-gpu-acceleration`, D-042): use its probe, setting and model-manager items. No Core changes.

## Spec (`Settings.dc.html`)
1. Page: breadcrumb "Settings", title (display), then sections as two columns — left the section title and description
   (verbatim), right the controls — separated by hairlines: **Models**, **Devices**, **Storage**, **Hotkeys**, **About**.
   The sidebar **Sections** list scrolls to them and highlights the section in view.
2. **Models**: one bordered list — Parakeet TDT v3 · int8 (size in Geist Mono, "Installed" chip), Silero VAD +
   diarization (installed with the ASR bundle), Gemma 4 E4B ("Default" badge on the default chat model; downloading shows
   the progress bar + %, **Cancel**), Gemma 4 26B A4B (**Download**), EmbeddingGemma; every existing model action
   (install bundle order, remove, errors, disk space) stays. Below it the **Acceleration** card: description and the
   segmented control **CPU / Vulkan / CUDA** (D-042: "Vulkan" instead of the canvas's "DirectML"); an option this PC
   cannot use is disabled with the reason in a tooltip; choosing one whose runtime is missing downloads it with progress
   in the card; a GPU fallback reason from the backend shows under the control.
3. **Devices**: Microphone, System audio, Screen recording — full-width selects as drawn (labels above).
4. **Storage**: path field (Geist Mono, folder icon), **Change**, **Show in Explorer**.
5. **Hotkeys**: one bordered list — Start / stop recording, Pause (from `p41-recording`), Toggle screen recording, Open
   Murmur — gesture chips in Geist Mono; capture/clear/conflict behaviour as today.
6. **About**: the orb card — "Murmur" + version (Geist Mono), project licence line, Parakeet / Gemma attributions; the
   other existing settings (theme, language, run onboarding again, transcription language) go where the canvas has room
   without changing the drawn sections — propose a placement consistent with the canvas (e.g. an **Appearance** section
   before About) and describe it in the report; they are not on the board, so the board's parity is measured with them
   placed below About or collapsed — say which.
7. Parity: content area of `Settings` / `SettingsDark` within the parity gates in the drawn state (E4B at 42 %, CPU selected, the drawn
   devices and path, `[VERSION]`/`[LICENSE]` ignored); set the three parity gates (below).
8. Tests: sections navigation, acceleration states (supported/unsupported/missing runtime/downloading/fallback), the rest
   as today.
9. Report (Ukrainian): mismatch before/after, what still differs, test results.

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
