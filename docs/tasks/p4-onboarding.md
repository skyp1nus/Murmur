# Task p4-onboarding: first-run onboarding (5 steps)

Branch `feat/onboarding` (based on `phase4`, which has the Settings screen, `ISettingsStore`, `IModelManager` in
`AppServices`, the theme and the localization). PLAN.md "Онбординг (first-run experience)" and the phase 4 task line
are the source of truth for behaviour; read both.

## Scope
src/Murmur.App/** (new views/view models, resources), tests/Murmur.App.Tests/**, src/Murmur.Desktop/CompositionRoot.cs
only if wiring is needed. Not Core, not Storage, not Capture. Another task will polish the Settings screen in parallel
(view models under `ViewModels/*Settings*`, `HotkeyGestures.cs`): reuse their public API, do not refactor their
internals; if you need a change there, keep it small and say so in the report.

## Design
`docs/design/source/Onb1Welcome … Onb5FirstRecording` + `*Dark`, `Onb3ModelsReady`, `OnbComponents(.Dark)`
(read `docs/design/README.md` first). The mockup copy is English: use it for `Strings.en.resx` and write natural short
Ukrainian for `Strings.resx` (the app's default language is uk). Tokens only, DynamicResource brushes, compiled
bindings, Violet/Ember only for the orb.

## Spec
1. **When**: on start, if `AppSettings.OnboardingCompleted` is false, the shell shows the onboarding instead of the
   library (full window, no sidebar). Finishing or skipping sets `OnboardingCompleted = true` (save immediately) and
   shows the library. The Settings screen's "Пройти знайомство ще раз" already sets it to false — make it take effect
   at once (show the onboarding now) instead of only on the next start, if that is simple; otherwise leave the
   existing "next start" note. Existing users: if the library already has sessions and the flag is false (an older
   settings.json), do not force the onboarding — set the flag silently.
2. **Shared layout**: step indicator `1 / 5` (mono) + 5 progress segments, headline, secondary line, content; bottom:
   Back, Skip (text buttons), Continue (primary). Skip on any step except 2 jumps to the end (sets the flag).
3. **Step 1 Welcome**: the animated orb (reuse the existing orb control), "Get started".
4. **Step 2 Audio**: microphone and system-audio device pickers (reuse the Devices settings source; selections save to
   `MicrophoneDeviceId` / `SystemAudioDeviceId` like the Settings screen) with **live level meters** for both while the
   step is visible: open `ICapture.CreateAudioSource` for each and show the peak level. Audio callbacks are a hot path
   (CLAUDE.md): no allocations, no LINQ, no async, no logging in the callback — compute the peak into a field the UI
   polls on its render tick (look at how the recording screen's level/orb works: `ILevelSource`). Stop the sources when
   leaving the step or the window. Microphone permission on Windows: there is no prompt API — if opening the mic fails
   with access denied (see D-023 and how the Recorder detects `E_ACCESSDENIED`), show the permission card ("Murmur
   needs access to your microphone") with a button that opens `ms-settings:privacy-microphone` (platform-neutral
   launcher like the folder launcher; on non-Windows hide the button) and a "Try again" that re-opens the source.
   Continue is enabled when the mic works; when there is no usable mic, offer a secondary "Continue without
   microphone" (D-023 allows recording without it). No Skip on this step (PLAN.md).
5. **Step 3 Models**: Parakeet (+ VAD — the catalog's required items) installs automatically when the step opens
   (required, progress shown); AI choice: E4B (badge "recommended for your PC" when it is
   `IModelManager.RecommendedChatModelId`), 26B, or "Skip AI for now". Choosing a chat model starts the same AI bundle
   install as the Settings screen (runtime → embedding → chat; reuse that logic, do not duplicate it — extract a shared
   helper only if the Settings code cannot be called as is, and keep the Settings behaviour identical). Free space shown;
   if the drive cannot hold the choice, say so before starting. "Downloads continue in the background" — Continue is
   always enabled; installs keep running after the onboarding (app-lifetime tokens), and the Settings screen shows their
   progress. The "Ready" variant (`Onb3ModelsReady`) when everything chosen is installed.
6. **Step 4 Preferences**: transcription language (only "Auto-detect" in MVP — a disabled/single-item picker with a
   short note, see the last line of PLAN.md's phase 4 section), recordings folder (path + Change + free space; same
   behaviour as Settings → Сховище), start/stop hotkey (the key-capture control from Settings), record screen by
   default (toggle). All save immediately.
7. **Step 5 First recording**: "Try it: say something." A 10-second recording through the normal `IRecorder` (mic +
   system audio, no screen) with the orb reacting to the level and a mono countdown; then wait for the transcript the
   normal processing pipeline produces (`ISessionProcessing` / the library's status source) and show the recognised
   text with a timestamp chip. If Parakeet is not installed yet, show "Waiting for the transcription model…" with its
   progress and continue automatically when it is ready; the user can still finish ("Open Murmur") without waiting.
   The trial session is a test: delete it from the library when the user leaves the step (after its processing
   finished or was cancelled — never delete a session the processor is still working on; check how deletion and the
   job queue interact). Consent note: "Always tell people when you're recording a meeting." Errors (no audio device,
   recording failed) are shown inline with a way to skip.
8. **Tests (headless)**: first start shows the onboarding, an existing library skips it; navigation Back/Continue/Skip
   and the flag; step 2 with a fake capture (levels move; access denied → permission card; continue without mic);
   step 3 with the fake model manager (required items start automatically; AI choice starts the bundle; skip AI;
   ready state); step 4 saves; step 5 with a fake recorder/processing (countdown, transcript shown, trial session
   deleted after processing, waiting-for-model path). Light/Dark × uk/en renders (use `UiCulture.Override`, never
   `Apply`). Screenshots of every step (light + dark) saved to
   /tmp/claude-0/-home-user-Murmur/ffa700a1-1873-543f-83db-f4dfe7fce242/scratchpad/onb/ for the architect (only when
   `MURMUR_SCREENSHOT_DIR` is set, like the Settings tests — no tracked files change).

## Acceptance
Linux: 0 warnings, App tests green (also with LANG=en_US.UTF-8), Desktop builds. Report mockup elements you could not
reproduce and every Ukrainian string you wrote that is not a translation of the mockup (for review).
