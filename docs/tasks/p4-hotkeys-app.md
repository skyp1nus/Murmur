# Task p4-hotkeys-app: global hotkeys in the app, single instance

Branch `feat/hotkeys-app` (based on `phase4`, which has `IGlobalHotkeys`/`HotkeyGesture` in Core,
`WindowsGlobalHotkeys` + `WindowsCapture.CreateGlobalHotkeys()` in Capture.Windows, `UnsupportedGlobalHotkeys` in Core,
the Settings hotkey rows validated through `HotkeyGesture.TryParse`, and the onboarding).

## Scope
src/Murmur.App/**, src/Murmur.Desktop/** (Program.cs, CompositionRoot.cs), installer/Murmur.iss (AppMutex only),
tests (App, Desktop). Not Core, not Capture. Another task polishes the Settings screen in parallel (view models under
`ViewModels/*Settings*` except the hotkey rows, Library, About): keep your changes there to the hotkey rows.

## Spec
1. **Wiring**: `AppServices` gets an optional `IGlobalHotkeys? Hotkeys` (keep existing constructor callers compiling).
   Desktop: `WindowsCapture.CreateGlobalHotkeys()` on Windows, `UnsupportedGlobalHotkeys` elsewhere; disposed on exit
   (CompositionRoot.Dispose).
2. **Registration** (a small App service, e.g. `HotkeyCoordinator`): at start-up — after the first frame, not before
   (CI start-up budget) — register `RecordHotkey` → `ToggleRecording`, `ToggleScreenHotkey` → `ToggleScreen`,
   `ShowAppHotkey` → `ShowApp` from `ISettingsStore.Current`; on `ISettingsStore.Changed` re-register only the actions
   whose gesture changed. Keep the latest `HotkeyRegistration` per action and expose it to the Settings hotkey rows: a
   short note under a row when it is `Taken` ("Зайнято іншою програмою" / "Used by another app") or `Unsupported`
   (hide the note for Registered/Cleared). `Register` is synchronous and may block briefly (it marshals to the hotkey
   thread): never call it on the UI thread in a way that can deadlock with a `Pressed` handler — `Pressed` handlers
   only post to the UI thread and return.
3. **Actions** (`Pressed` arrives off the UI thread → post to the UI thread):
   - `ToggleRecording`: idle → start a recording with the saved defaults exactly as the recording screen's start does
     (devices, screen-by-default, display) and show the recording screen; recording → stop it (same path as the stop
     button). Ignored while the onboarding is shown, while the recorder is starting/stopping, and on step 5 of the
     onboarding (the trial recording).
   - `ToggleScreen`: only while recording — toggle the screen like the pill on the recording screen (respect a pending
     toggle; `IRecorder.IsScreenRecording` is the truth).
   - `ShowApp`: restore and activate the main window (un-minimize, show if hidden, bring to front; Windows'
     foreground-lock may refuse — `Window.Activate()` is enough, do not hack around it).
4. **Single instance** (Desktop, Windows only): a named mutex `MurmurSingleInstance` created in `Program.Main` and held
   for the whole run; a second start signals the first through a named `EventWaitHandle` (`MurmurShowApp`) and exits
   with code 0 before any UI; the first instance waits on that event on a background thread and runs the ShowApp action
   (post to the UI thread). `--autorecord` / diagnostic modes and the CI `startup` job must keep working (check how
   scripts/verify-local.ps1 and ci.yml start the exe: a leftover instance from a previous step must not make the next
   start exit silently — if needed, skip the single-instance check when a diagnostic switch is present, and say so).
   Add `AppMutex=MurmurSingleInstance` to installer/Murmur.iss (setup and uninstall then refuse while Murmur runs).
5. **Tests**: coordinator with a fake `IGlobalHotkeys` (records Register calls, raises Pressed from another thread):
   initial registration after start, re-registration of only the changed action, Taken/Unsupported notes on the rows,
   ToggleRecording start/stop through a fake recorder, ignored during the onboarding, ToggleScreen only while
   recording, ShowApp activates the window (headless). Single-instance logic behind a small seam that is testable on
   Linux (the Windows primitives can be faked).

## Acceptance
Linux: 0 warnings, App + Desktop tests green (also LANG=en_US.UTF-8). Windows behaviour (real RegisterHotKey presses,
foreground activation, the second-instance hand-off) is verified by the owner on the final build; the existing
Windows hotkey tests keep passing in CI.
