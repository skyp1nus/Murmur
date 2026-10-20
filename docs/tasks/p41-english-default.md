# Task p41-english-default: English is the default UI language, Ukrainian the second

Branch `design/english-default` (based on `phase4`). Decision D-039 (owner, 2026-10-20). Another task builds the
design-parity test harness in parallel under `tests/Murmur.App.Tests/Design/`: stay out of that folder.

## Scope
src/Murmur.App/** (resources, `Localization/UiCulture`, the strings generator in `Murmur.App.csproj`), src/Murmur.Core/Settings
(the `AppSettings` default and a settings version — the architect approves this Core change: a new `SettingsVersion`
property and the `Language` default only), src/Murmur.Storage or wherever `settings.json` is read (the one-time
migration), tests, docs/design/README.md (the "Рядки UI" rule), CLAUDE.md is not touched.

## Spec
1. **Resources**: the neutral `Strings.resx` becomes English (today's `Strings.en.resx` content), Ukrainian moves to
   `Strings.uk.resx` (today's `Strings.resx` content), both embedded with `WithCulture=false` exactly as D-034 does now;
   `NeutralResourcesLanguage("en")`; the generated accessor keeps working (keys from the neutral file; a key missing
   in either language still fails the build). `git mv` so history follows the files.
2. **Default**: `AppSettings.Language` defaults to `"en"`; `UiCulture` defaults to English; an unknown or empty language
   falls back to English. Anything that compares with the Ukrainian text explicitly (e.g. `TrialSessionCleaner` matching
   the trial title in Ukrainian) keeps working for sessions created in either language.
3. **Migration**: add `AppSettings.SettingsVersion` (int, current = 2). A `settings.json` read without it (written
   before D-039, where `"Language": "uk"` was only the old default) is loaded with `Language = "en"` and saved back with
   version 2 on the next save; a file with version 2 keeps whatever the user chose. Test both.
4. **English strings follow the canvas**: where an English string has a counterpart in the canvas
   (`docs/design/source/*.dc.html`), use the canvas wording verbatim (nav "Library", "Ask AI", "Settings", "New recording",
   "Fully offline", "Recordings and models stay on this PC", "Recent", section titles and descriptions in Settings,
   "Start / stop recording", "Open Murmur", etc.). Do not restructure screens (later tasks do); only texts. List the keys
   whose English text changed in the report.
5. **Ukrainian stays complete**: every key exists in both files; the Ukrainian texts are unchanged except where the
   English source changed meaning (then translate the new English).
6. Tests: default language is English on a clean profile; switching to Ukrainian in Settings and restarting gives
   Ukrainian; the migration cases; existing localization tests updated for the new neutral language.
7. Update docs/design/README.md "Рядки UI" bullet: `Strings.resx` = English, `Strings.uk.resx` = Ukrainian.
