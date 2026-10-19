# Task p4-hotkeys: global hotkeys (engine part)

Branch `feat/global-hotkeys` (based on `phase4`). PLAN.md phase 4: "Глобальний хоткей старт/стоп: `RegisterHotKey` у
`Murmur.Capture.Windows` за інтерфейсом у Core". This task builds the Core gesture parser and the Windows
implementation. Wiring into the app (settings → Register, Pressed → start/stop, screen toggle, show window) is a later
task, because the Settings screen task is editing App/Desktop right now: do not touch src/Murmur.App or
src/Murmur.Desktop.

## Contract (architect-owned, already in the branch — do not change)
`src/Murmur.Core/Input/IGlobalHotkeys.cs`: `HotkeyAction` {ToggleRecording, ToggleScreen, ShowApp}, `HotkeyModifiers`
flags {Ctrl, Alt, Shift, Meta}, `HotkeyRegistration` {Registered, Cleared, Invalid, Taken, Unsupported},
`IGlobalHotkeys` {IsSupported, Register(action, gesture), Pressed}. Read the XML docs: a failed Register leaves the
action with no hotkey; Pressed fires once per press, off the UI thread.

## Spec
1. **Core: `Murmur.Core.Input.HotkeyGesture`** (new file next to the contract): `readonly record struct
   HotkeyGesture(HotkeyModifiers Modifiers, HotkeyKey Key)` + `public static bool TryParse(string? text, out
   HotkeyGesture gesture)` + `ToString()` in the same syntax, and `enum HotkeyKey` for the keys a global hotkey may
   use: A–Z, digits 0–9, F1–F24, Space, Insert, Delete, Home, End, PageUp, PageDown, Up/Down/Left/Right, NumPad0–9,
   Pause, PrintScreen, and the OEM punctuation keys Avalonia names (OemPlus, OemMinus, OemComma, OemPeriod, ...).
   - Syntax = what Avalonia's `KeyGesture.ToString()` produces and `KeyGesture.Parse` accepts, because the Settings
     screen stores that string (e.g. `Ctrl+Shift+R`, `Ctrl+Alt+F9`, `Ctrl+D1` for the digit key — check Avalonia 12's
     `KeyGesture`/`Key` source in the NuGet cache (~/.nuget/packages/avalonia/) for the exact key names and the
     modifier names/aliases it accepts, e.g. `Control`, `Cmd`, `Win`/`Meta`; accept those aliases case-insensitively,
     format with the canonical names).
   - Rules: at least one of Ctrl/Alt/Meta (Shift alone or no modifier is refused: it would steal normal typing), exactly
     one non-modifier key, no duplicates, whitespace around `+` tolerated, empty/null → false.
   - Allocation does not matter here (settings path), AOT does: no `Enum.Parse` over reflection-heavy paths — a
     generated/hand table or `Enum.TryParse<T>` on the generic overload is fine (it is AOT-safe); check trimming warnings.
   - Tests: table-driven valid/invalid cases, round-trip `Parse(ToString(g)) == g` for every `HotkeyKey`, and a test in
     `tests/Murmur.App.Tests` (App tests reference Avalonia) that for every `HotkeyKey` the Core string round-trips
     through Avalonia `KeyGesture.Parse(...).ToString()` unchanged — so the Settings screen's strings and Core agree.
     (Adding a test file in App.Tests is allowed; do not change App sources.)
2. **Windows: `Murmur.Capture.Windows.Input.WindowsGlobalHotkeys : IGlobalHotkeys`**:
   - A dedicated background thread (name it, `IsBackground = true`) that owns a message-only window
     (`CreateWindowExW` with `HWND_MESSAGE`, own registered window class with an `[UnmanagedCallersOnly]` WndProc) and
     runs `GetMessageW` / `DispatchMessageW`. `RegisterHotKey`/`UnregisterHotKey` must be called on that thread
     (they bind to the calling thread's window): marshal `Register` calls to it (post a private message + wait on a
     small completion object, or a lock-protected request slot — no Avalonia, no WinForms, no SynchronizationContext
     dependency) and return the result synchronously.
   - Map `HotkeyGesture` to `MOD_*` (+ `MOD_NOREPEAT` so holding the key fires once) and the virtual-key code table.
     `RegisterHotKey` failing with `ERROR_HOTKEY_ALREADY_REGISTERED` → `Taken`; another failure → `Taken` too, with the
     Win32 error kept for logs (property `LastError` or similar on the implementation, not in Core).
   - Ids: one per action. Registering the same gesture for two actions → the second gets `Taken` (Windows itself
     refuses a duplicate within the same thread? verify: it does not refuse for different ids — so check duplicates
     yourself across actions).
   - `WM_HOTKEY` → `Pressed(action)` raised on the hotkey thread (catch and swallow handler exceptions so a bad handler
     cannot kill the message loop; keep them for logs the same way other Windows parts do).
   - `Dispose`: unregister everything, destroy the window, post `WM_QUIT`, join the thread (with a timeout);
     idempotent; Register after Dispose → `ObjectDisposedException`.
   - The thread starts lazily on the first Register (constructing the object touches nothing native).
   - Interop: `LibraryImport` (source-generated) like the rest of `Murmur.Capture.Windows`; no new packages.
3. **Composition point**: expose a factory the same way playback is exposed by `WindowsCapture` (e.g.
   `WindowsCapture.CreateGlobalHotkeys()` or a public constructor the Desktop root can call later). On non-Windows
   builds nothing is needed now (the later App task will use a null/unsupported implementation — add
   `Murmur.Core.Input.UnsupportedGlobalHotkeys` in Core: `IsSupported = false`, Register → `Unsupported`/`Cleared`
   for null).
4. **Tests (Windows-trait, run in CI)**: register a gesture → `Registered`; same gesture for a second action →
   `Taken`; re-register replaces (the old gesture is free again: register it from a second `WindowsGlobalHotkeys`
   instance → `Registered`); null → `Cleared`; a press simulated with `SendInput` (Ctrl+Alt+<an unlikely key>, e.g.
   Ctrl+Alt+Shift+F24) raises `Pressed` once — mark it to skip with a clear message if the CI session has no input
   desktop (SendInput returns 0 or the event does not come within 2 s on a non-interactive runner: skip, do not fail);
   Dispose joins the thread and releases the gestures (a second instance can register them); 20× create/register/
   dispose → no thread or handle growth. Linux unit tests for everything pure (mapping table, duplicate check).

## Acceptance
Linux: 0 warnings, Core + App + Capture.Windows tests green; Windows-trait tests written and UNVERIFIED until the
architect's CI run. Commit message per Conventional Commits, no trailers.
