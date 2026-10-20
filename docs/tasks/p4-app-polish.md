# Task p4-app-polish: Settings, Library and About polish from the reviews

Branch `feat/app-polish` (based on `phase4`). Follow-ups collected from the reviews of the Settings screen, the
recordings-folder backend, crash recovery and the installer. Another task wires the global hotkeys in parallel (it owns
the Settings hotkey rows, `ShellViewModel` hotkey actions, `Program.cs`): stay out of those.

## Scope
src/Murmur.App/** (Settings view models except the hotkey rows, Library, About, resources), tests/Murmur.App.Tests/**,
src/Murmur.App/Murmur.App.csproj and src/Murmur.Desktop/Murmur.Desktop.csproj only for the version constant / window
icon resources. Not Core.

## Spec
1. **Models section** (`ModelsSettingsViewModel`, `ModelRowViewModel`):
   - Remove is hidden/disabled for a row that is part of a running AI bundle; removing a model fails with a localized
     generic text on the row (details only in the log), never the raw English exception.
   - Starting a second chat bundle while one runs must not take over the shared runtime/embedding rows' tokens:
     refuse the second chat install while another bundle runs (the other chat row shows "Зачекайте, поки завершиться
     інше завантаження" or similar), simplest correct option.
   - The error text returned by `SettingsWriter.UpdateAsync` when saving `ChatModelId` goes to that row's error.
   - `Models_InstallFailed` text is neutral (no "check the connection" — a checksum or disk-full failure is not a
     network problem): «Не вдалося встановити. Спробуйте ще раз.» / "Install failed. Try again." and a separate text
     when free space is insufficient if that is detectable from the error/status cheaply.
   - Check `_disposed` at the top of the bundle loop.
2. **Start-up budget**: `SettingsViewModel` (and its section view models: device enumeration, model status for every
   catalog item, models.json, free space, subscriptions) is created lazily on the first `ShowSettings`, not in
   `ShellViewModel.Create`; the sidebar only needs the section ids. Dispose it on app exit. Coordinate with how the
   onboarding obtains the shared section view models (it reuses `Devices`, `Models`, `Storage`, `Hotkeys` from the
   Settings view model — keep that sharing working: the first of the two that needs them creates them).
3. **Devices**: a saved device that is no longer present falls back to the system default (`null`), not to the last
   manual pick (`RecordingViewModel.cs` ~333: `Saved(list, id) ?? Saved(list, null)`), so Settings and the recording
   screen agree.
4. **Settings follow the store**: `SettingsViewModel` subscribes to `ISettingsStore.Changed` (marshal to UI), refreshes,
   and unsubscribes on Dispose (onboarding and the hotkey task also write settings).
5. **Storage**: require `Path.IsPathFullyQualified` (and normalize with `Path.GetFullPath`) before creating/opening the
   recordings folder; dispose the `IStorageFolder` objects from the folder picker.
6. **About**: the version comes from a build-time constant (an MSBuild-generated `BuildInfo.Version` from `$(Version)`,
   like the existing generated Strings accessor) — no reflection; shows "0.4.0" (no `+commit` suffix; you may show the
   short commit in a tooltip if it is available at build time).
7. **Hotkey display only** (`HotkeyGestures.Display`): show digit and OEM keys as their characters ("Ctrl Shift 1",
   "Ctrl ,") — display only, the stored value stays canonical. (The hotkey task does not touch `Display`.)
8. **English copy**: "Like the system" → "Match system".
9. **Library sees background recovery**: a session recovered later by `RecoveryService.RetryBusyInBackgroundAsync`
   (Desktop) still shows "Триває запис" in the library until a reload. Fix in App without a Core change: when the
   library holds sessions in `Recording` state that are not the recorder's current session, re-read those rows from
   the store every 30 s (and when the window is activated) until they change; update the rows in place.
10. **Window icon**: the main window uses the app icon (installer/murmur.ico, linked into Murmur.App as an
    AvaloniaResource; `Window.Icon`).
11. **Progress bars**: confirm the Settings model rows render Ink on Stone (the onboarding task added a global
    `ProgressBar` style in Controls.axaml) — screenshot it.

## Tests
Each item above with a headless/view-model test where it is behaviour (1–7, 9); screenshots of Settings in both
themes (uk/en) to /tmp/claude-0/-home-user-Murmur/ffa700a1-1873-543f-83db-f4dfe7fce242/scratchpad/polish/ (only with
`MURMUR_SCREENSHOT_DIR`).

## Acceptance
Linux: 0 warnings, App tests green (also LANG=en_US.UTF-8), Desktop builds.
