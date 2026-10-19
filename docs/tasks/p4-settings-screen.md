# Task p4-settings-screen: the Settings screen

Branch `feat/settings-screen` (based on the integration branch `phase4`, which already has the dark theme, the
localization (`Resources/Strings*.resx`, D-032/D-034), `ISettingsStore`/`JsonSettingsStore` and `Murmur.Models`).
PLAN.md: "Налаштування — моделі (завантаження, розмір), пристрої, папка, хоткеї".

## Scope
src/Murmur.App/** (views, view models, resources), src/Murmur.Desktop/CompositionRoot.cs (wiring only — authorized for
this task), tests/Murmur.App.Tests/**. Not Core (architect-owned; `AppSettings` already got the new fields for this
task), not Storage, not Capture.

## Design
`docs/design/source/Settings.dc.html` and `SettingsDark.dc.html` (read `docs/design/README.md` first). The mockup has
the sections Моделі, Пристрої, Сховище, Гарячі клавіші, Про програму, and the left navigation of
`Main.dc.html` that links to Settings — follow the mockup structure; make the app's navigation match it if the app
does not have that nav yet (Library ⇄ Settings at least). Tokens only, DynamicResource for brushes, strings in both
resx files (uk verbatim from the mockup, natural short English), compiled bindings, no hex in views.

## Spec
1. **Моделі** — one row per item of `IModelManager.Catalog` (name, size from the catalog / real total, state):
   Install / progress bar with % and Cancel / Installed with Remove; the active chat model marked "За замовчуванням"
   (= `models.json` active, `RecommendedChatModelId` shown as the recommendation when none is installed). AI items are
   useful only together: installing a chat model also installs the runtime and the embedding model if missing
   (install them in sequence; show progress per row). Events come off the UI thread (marshal). Start installs with an
   app-lifetime token per item (cancel = that token), observe the returned task (a failure shows a short error text on
   the row; the details are in `ModelStatus.Error`). Free space of the models drive shown under the section. Wire
   `IModelManager` (the `Murmur.Models.ModelManager` over the models folder the app already uses — see
   `ModelLocator`/`AiModelPaths` in Desktop) into `AppServices` (new optional member, keep existing constructor callers
   compiling) in `CompositionRoot`. When a model gets installed or removed, the rest of the app must notice: the ASR
   "not installed" hint and the AI chat "not installed" state re-check on their next use — make sure nothing caches
   "not installed" forever (fix it in the App view models if they do; report Core/Desktop places that cache it).
   The mockup's "Прискорення" row is out of scope (no GPU offload yet) — leave it out.
2. **Пристрої** — microphone and system-audio device pickers (the recording screen already lists devices: reuse its
   source), saved to `AppSettings.MicrophoneDeviceId` / `SystemAudioDeviceId` (null = system default, shown as
   "За замовчуванням"); "Запис екрана" — display picker saved to `ScreenDisplayId` (the mockup's "Вибране вікно" is not
   supported: the app records a whole display) and the "screen on by default" toggle (`ScreenByDefault`). The recording
   screen must start with these defaults (device ids, screen toggle, display) — implement that in its view model.
3. **Сховище** — the recordings folder (`RecordingsDirectory`, null = default data folder; show the effective path),
   "Змінити" (folder picker via Avalonia's StorageProvider), "Відкрити в Провіднику" (open the folder with the OS shell —
   use a small platform-neutral launcher; on Windows `explorer.exe <path>`, elsewhere `xdg-open`/`open`). Note for the
   user under the row: new recordings go to the new folder. (Storage does not use the setting yet — a later backend task
   does; do not change Storage.)
4. **Гарячі клавіші** — rows for `RecordHotkey`, `ToggleScreenHotkey`, `ShowAppHotkey` (the mockup's "Пауза" is not
   supported — leave it out): show the gesture as key caps like the mockup, click → capture the next key combination
   (Esc cancels, Backspace clears = null), validate with Avalonia `KeyGesture.Parse` round-trip, refuse duplicates.
   Registering them with the OS is a later task.
5. **Вигляд** (not in the mockup — add a compact section in the mockup's style, before "Про програму"): theme
   Світла / Темна / Як у системі (applies at once through the existing ThemeApplier), language Українська / English
   ("applies after restart" note, D-032), "Пройти знайомство ще раз" (sets `OnboardingCompleted = false`; onboarding is
   a later task).
6. **Про програму** — app version (assembly informational version), project licence (read LICENSE at the repo root to
   name it), Parakeet attribution (NVIDIA, CC-BY-4.0 — required), Gemma 4 / EmbeddingGemma (Google; take the licence
   names from THIRD-PARTY-NOTICES.md), llama.cpp (MIT), sherpa-onnx (Apache-2.0).
7. Every change saves through `ISettingsStore.SaveAsync` immediately (no Save button), errors shown inline.
8. **Tests** (headless): the screen renders in Light/Dark and uk/en (use `UiCulture.Override`, never `Apply`), model
   rows react to fake `IModelManager` events (install → progress → installed; cancel; failure text; AI bundle installs
   runtime + embedding first), device/display/screen defaults saved and picked up by RecordingViewModel, folder change
   saved, hotkey capture (valid, Esc, Backspace, duplicate refused), theme change applied, language saved, onboarding
   reset, About shows the attributions. No real network.

## Acceptance
Linux: 0 warnings, App tests green (also with LANG=en_US.UTF-8), Desktop builds; a headless screenshot of the screen in
both themes saved to /tmp/claude-0/-home-user-Murmur/ffa700a1-1873-543f-83db-f4dfe7fce242/scratchpad/ for the
architect. Report any mockup elements you could not reproduce.
