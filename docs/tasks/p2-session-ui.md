# Task p2-session-ui: Recording view — player + synchronized transcript

## Scope
src/Murmur.App/**, src/Murmur.Desktop/**, tests/Murmur.App.Tests/**, docs/screenshots/**

## Spec
PLAN.md 'Екрани MVP' #3: player on the left, transcript on the right with the current word highlighted; tabs Транскрипт / Резюме / Запитати AI.
1. Navigation: Library item click -> SessionView(session). Back to Library. Library rows show processing state (Транскрибується 42 % / Готово / Помилка) from SessionProcessor progress events.
2. Player (left column, card): play/pause pill (achromatic), waveform from `peaks.bin` (PeaksFile in Core) drawn as one cached geometry (StreamGeometry rebuilt only on resize), playhead line + elapsed/total in Geist Mono; click/drag on the waveform seeks. Position polled from IAudioPlayer at ~30 Hz only while playing (DispatcherTimer), idle = no timer.
3. Transcript (right column): virtualized list of segments (ItemsRepeater or ListBox with VirtualizingStackPanel); each segment: speaker label (Я / Інші, Inter 500, Smoke) + time chip (Geist Mono, e.g. 12:34) + text built from words; the current word (binary search over word start times at the player position) is highlighted with an achromatic token (e.g. Stone background, Ink text — never Violet/Ember); click on a word seeks the player to its start and plays; auto-scroll keeps the current segment visible unless the user scrolled in the last 3 s. Must stay smooth for a 1-hour transcript (~10k words): no per-word controls if that is too heavy — use TextBlock inlines/Runs or a custom text control with hit testing; measure.
4. Tabs: Транскрипт (above); Резюме and Запитати AI — placeholders ('Зʼявиться у фазі 3' style empty states) — phase 3 fills Запитати AI.
5. Status states: no transcript yet + ASR not installed -> card explaining how to install Parakeet (scripts/fetch-models.ps1); transcription running -> progress; failed -> error text + 'Спробувати ще' (re-enqueue).
6. Composition root (Murmur.Desktop): WorkerAsrClient (Core), JobQueue, SessionProcessor (no indexer yet) with ResumePendingAsync at startup; playback = WindowsCapture/WasapiAudioPlayback on Windows, ClockAudioPlayback on net11.0 Linux; pause the queue while recording (SessionProcessor/JobQueue API). Murmur.App still references Murmur.Core only.
7. Screenshots (Avalonia.Headless, Skia): docs/screenshots/phase2-session.png (fake 1-hour transcript, player mid-way, a highlighted word), phase2-library-processing.png.
8. Tests: view-model tests with fakes (current-word lookup at boundaries, seek on word click, auto-scroll suppression, status states, retry), plus a performance test building the view model for a 10k-word transcript (< 200 ms).

## Acceptance
Build 0 warnings; App tests green; ArchitectureTests green; linux AOT publish of Murmur.Desktop 0 warnings; screenshots committed.
