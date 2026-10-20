# Task p4-recordings-folder: new recordings go to the folder from the settings

Branch `feat/recordings-folder` (based on `phase4`). PLAN.md phase 4: "Папка записів з налаштувань
(`AppSettings.RecordingsDirectory`) у `Murmur.Storage`". The Settings screen (another task, running now) lets the user
pick the folder; this task makes Storage use it.

## Scope
src/Murmur.Storage/**, tests/Murmur.Storage.Tests/**, and in src/Murmur.Desktop/CompositionRoot.cs only the lines that
build the storage and the settings store (another task edits other parts of that file — keep the diff there minimal).
Not Core (if you need a Core change, stop and report), not App, not Capture.

## Spec
1. `SqliteStorage` takes an optional recordings-folder provider (e.g. `Func<string?>`; `null` provider or a
   `null`/blank result = the default `<data root>/Recordings`). It is asked at each `CreateSessionAsync`: a changed
   setting applies to the next recording without a restart. Old sessions stay where they are (no moving).
2. The `sessions.folder` column today stores a path relative to the data root (`Recordings/<name>`). Keep that for
   folders under the data root (no migration of existing rows); for a folder outside the data root store the absolute
   path. `GetFiles`/`ResolveFolder` accept both. The current "points outside the data root" check must stay for relative
   values (a `..` in a relative value is still refused).
3. **Safety of delete**: `DeleteSessionAsync` deletes a session folder recursively today. For an absolute folder from
   the database (outside the data root) it must never delete more than that session's own folder: refuse (leave it,
   delete the row? — no: keep the current order "folder first, row second" and its failure semantics) unless the folder
   name has the shape `ReserveFolder` produces (`yyyy-MM-dd_HH-mm-ss_<8 hex>[_N]`) and the path is not a drive root.
   Document the rule in the code; test a tampered absolute value (e.g. the user's home, a drive root) is refused with an
   error and nothing is deleted.
4. **Unusable folder**: when the configured folder cannot be created or written (missing drive, no permission, a file
   with that name), the recording must still start: fall back to the default folder for that session and report it —
   add `bool UsedFallbackFolder`-style information where it fits without a Core change (e.g. a `RecordingsFolderFallback`
   event or a last-fallback property on `SqliteStorage` the composition root can log) and say in your notes how the UI
   could surface it later (that needs a Core change — the architect decides).
   Check writability cheaply once per session start (create the session folder; if that throws
   `IOException`/`UnauthorizedAccessException`, fall back).
5. Relative configured paths are resolved against the data root? No — the Settings screen only stores absolute paths
   from a folder picker; treat a non-rooted configured value as unusable (fallback + report).
6. Composition root: build `StoragePaths` first (`StoragePaths.FromEnvironment()`), then `JsonSettingsStore(paths)`,
   then `SqliteStorage(paths, () => settings.Current.RecordingsDirectory)`; keep `CreateDefault()` working for other
   callers (tools, tests).
7. Anything else that assumes session folders live under `<root>/Recordings` (RecoveryService uses `GetFiles` — check;
   tools under `tools/`, scripts under `scripts/` such as `check-recording.ps1`/`verify-local.ps1` and perf harnesses
   that look for the newest session folder): list them in your notes; fix the C# ones in your scope, report the rest.
8. Tests: new session goes to the configured folder (absolute path in the row), changing the setting between two
   sessions, default when null/blank, relative-under-root rows from an older database still resolve, fallback on an
   unusable folder (a file in the way; a non-rooted value), delete of an outside session works and deletes only that
   folder, tampered rows refused, the `..` check still holds, list/get/delete round-trip after reopening the database.

## Acceptance
Linux: 0 warnings, Storage tests green, the whole solution builds (`dotnet build Murmur.sln -c Release`).
Conventional Commits, no trailers.
