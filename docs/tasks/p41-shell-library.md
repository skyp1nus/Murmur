# Task p41-shell-library: sidebar, top bar and Library exactly as the canvas

Branch `design/shell-library` (based on `phase4` after wave 1). Boards: `Main`, `MainDark` (Library) and the sidebar
strip (x < 241) of every main board (`Recording`, `Session`, `Chat`, `Settings` + dark). Truth: `docs/design/source/*.dc.html`
(markup, sizes, texts) and `docs/design/reference/*.png`; measure with `DesignParityTests` (`scripts/design/parity.sh`,
report in `artifacts/design-parity/`). Parallel tasks own the Recording screen (`p41-recording`) and onboarding
(`p41-onboarding`): this task owns `SidebarView`, the main window frame and top bar, and `LibraryView`.

## Scope
src/Murmur.App/** (views, view models, controls, styles, resources) and tests/Murmur.App.Tests/**. Core/Storage only
for what the spec names; anything else in Core → ask the architect first (report it, do not change it).

## Spec
1. **Sidebar** (240 px + 1 px border, padding 20/16, gap 24 — read the markup): wordmark (orb 22 + "Murmur" 17/600);
   primary button "New recording" (44 px pill, ink) — while a recording runs it becomes the "Recording" pill
   (surface fill, mini orb, label; clicking returns to the recording screen) as on `Recording.dc.html`; nav Library /
   Ask AI / Settings (40 px pills, icon 18, active = filled pill + 500 weight); a context list that depends on the
   screen: **Recent** (Library, Session, Recording: the 3 latest sessions, the open one highlighted, click opens it),
   **Chats** (Ask AI: provided by the chat screen through a view-model hook; until `p41-chat` lands it shows nothing),
   **Sections** (Settings: Models, Devices, Storage, Hotkeys, About — click scrolls the settings page to the section;
   expose the hook, `p41-settings` wires the scrolling); the "Fully offline" card at the bottom with the text
   "Recordings and models stay on this PC", or "No models are loaded while recording" while recording.
2. **Top bar** of every main screen (60 px, padding 12/32): left a breadcrumb ("Library", "Library / New recording",
   "Library / <session title>", "Settings"; the first part is a link back), right a hotkey hint ("Record" / "Stop"
   + the gesture chip in Geist Mono) or the screen's own buttons (Session: Export, "…" — owned by `p41-session`; leave a
   slot). The hint shows the gesture from settings.
3. **Library** (`Main.dc.html`), top to bottom:
   - "What are we recording today?" (display style per the markup).
   - Card **New recording**: big orb with the record glyph, title, description, source toggles Microphone / System audio /
     Screen (dashed when off), "Start recording" + the hotkey hint. Toggles set the options of the recording started
     from here (persist them as the existing recording defaults do).
   - Card **Ask your library**: description, a question box with the send button, suggestion chips ("My action items this
     week", "Who mentioned the budget?", "Summarize yesterday"). Sending or clicking a chip opens Ask AI with that
     question asked over the whole library.
   - **Recordings** header + search box "Search titles and text" (the existing title + transcript search).
   - Filter chips **All / Today / This week / This month / Pick dates**; "Pick dates" opens a flyout with a date-range
     calendar and filters by start date; the active chip has the dot.
   - Rows grouped by day: "Today", "Yesterday", then the date ("Mon, Oct 13"). Row = icon tile by source (screen,
     microphone only, system audio), title, meta "10:30 · English · Mic, system audio, screen", one line of text (the
     transcript's first line or the search hit) or, while transcribing, the progress bar + "Transcribing · 64%";
     duration in Geist Mono; chevron. Interrupted / failed sessions keep their state visible in the meta line in the
     same style. Language: the transcript's detected language if stored, else omit it (report if Core lacks it).
   - Keep everything the current Library does (delete, rename, open, empty states) reachable; restyle, don't drop.
4. **Responsive**: the canvas page is fluid (flex-wrap). At 1440 it must match; below ~1100 the two cards stack, and the
   sidebar keeps its width. No horizontal scrolling at the minimum window size.
5. **Parity**: bring `Main` and `MainDark` to the lowest mismatch you can (target ≤ 2 % of the content area and ≤ 1 % of
   the sidebar strip), set `MaxMismatchPercent` for both boards in the parity table so CI enforces them, and make the
   sidebar strip of the other main boards match too (set nothing for their content). Tokens only — no hex in views.
6. Tests: the sidebar states and context lists, filters (incl. date range), grouping, the Ask-your-library hand-off,
   the source toggles feeding the recording options; existing tests updated, none deleted without a replacement.
7. Report (Ukrainian): before/after mismatch numbers, side-by-side paths, what still differs and why, what you could not
   do without a Core change.
