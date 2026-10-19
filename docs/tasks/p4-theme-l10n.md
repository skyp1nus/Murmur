# Task p4-theme-l10n: dark theme, runtime theme switching, localized strings, settings store

Branch `feat/theme-l10n`. PLAN.md phase 4 (onboarding/settings need these first). Rules: docs/design/README.md
("Правила для агентів") and CLAUDE.md design rules.

## Scope
tools/Murmur.TokenGen/**, src/Murmur.App/** except src/Murmur.App/Diagnostics/** (another agent works there now — do
not touch it; its strings are English CI output and stay as they are), src/Murmur.Storage/** (settings file only),
src/Murmur.Desktop/** (wiring), tests for all of them. Core: `Murmur.Core.Settings` (src/Murmur.Core/Settings/AppSettings.cs) is
architect-owned and already in your branch — implement it, do not change it.

## Spec
1. **Tokens → two themes.** `docs/design/tokens.json` now has `color` (light) and `colorDark` (dark), see
   `$description`. TokenGen emits `Styles/Tokens.axaml` with the colour/brush pairs inside
   `ResourceDictionary.ThemeDictionaries` (`Light` from `color`, `Dark` from `colorDark`; same keys `Color.*`/`Brush.*`
   in both), everything theme-independent (fonts, sizes, spacing, radii, shadows, hairline width) stays outside.
   Keep the generator deterministic and the byte-for-byte sync test (TokensTests) green; extend the generator tests
   for the dark dictionary and for "every colour key exists in both themes". `orbGradient` in tokens.json is a design
   note for the orb, not a resource — ignore it in the generator unless the orb needs it (the orb is drawn in code).
2. **Views use the themes.** Every `{StaticResource Brush.*}`/`Color.*` lookup that must follow the theme becomes
   `{DynamicResource ...}` (StaticResource does not update on a runtime theme change). Code that resolves brushes
   (controls in src/Murmur.App/Controls, e.g. the orb, waveform) must re-resolve on `ActualThemeVariantChanged`.
   Add a test (like the existing "no literal hex" guard in TokensTests) that fails when a view/style uses
   `StaticResource Brush.` or `StaticResource Color.`. The accent guard (Violet/Ember only in orb/icons) stays.
   Check the dark mockups in docs/design/source/*Dark.dc.html for anything that is not just a colour swap (e.g. a
   different brush used for a surface in dark) and report it; do not redesign screens.
3. **Theme switching.** `App` applies `AppSettings.Theme`: System → `ThemeVariant.Default` (follows the OS), Light,
   Dark — at start-up and whenever `ISettingsStore.Changed` reports a new theme (marshal to the UI thread). Remove the
   hard-coded `RequestedThemeVariant="Light"` from App.axaml. No settings UI in this task: a hidden developer toggle is
   not needed; tests drive it through the store.
4. **Settings store.** `JsonSettingsStore : ISettingsStore` in Murmur.Storage: `<data dir>/settings.json` (data dir =
   the folder `StoragePaths` already uses), System.Text.Json source-gen (enum as string), missing properties take
   the record defaults, unknown ones ignored; atomic write (temp file + replace); a corrupt file is renamed to
   `settings.json.bad` and defaults are used; `Changed` raised after a successful save. Thread-safe. Wire it in
   Murmur.Desktop's composition root and make it available to the App (AppServices) for the later settings and
   onboarding screens.
5. **Localization.** All user-facing strings of src/Murmur.App (views and view models; not Diagnostics) move to
   resources: `Resources/Strings.resx` (Ukrainian, the neutral culture — the current texts verbatim) and
   `Resources/Strings.en.resx` (English translations; natural, short UI English). Use a strongly typed accessor
   that is AOT/trim-safe (generated at build time by MSBuild — e.g. `EmbeddedResource` metadata
   `StronglyTypedLanguage`/`StronglyTypedClassName`/`StronglyTypedNamespace` — or a small hand-written static class over
   `ResourceManager`; no reflection-based binding, no runtime XAML loading). XAML uses `{x:Static}` to the accessor.
   Formatted strings (counts, durations, "N %") keep their format placeholders in the resources and are formatted in
   the view models with the UI culture. The UI culture is set once at start-up from `AppSettings.Language` (`uk`
   default, `en`); a language change applies on the next start (decision D-032 — just document it on the property
   usage, the architect records the decision). Add tests: every key in Strings.resx exists in Strings.en.resx and vice
   versa, no empty values, placeholders (`{0}`…) match between languages, and a guard test that views contain no
   Cyrillic literals any more.
6. **Desktop/AOT.** The published AOT app must still contain the resources (satellite assembly for `en` — check that
   `dotnet publish -r linux-x64` (Native AOT) here produces it and that the app picks English when Language=en; on
   Windows this is UNVERIFIED until CI). No new NuGet packages.

## Acceptance
Linux: 0 warnings, TokenGen/App/Storage/Desktop tests green; headless UI tests render the main views in Light and
Dark without missing-resource errors (look at how existing App tests start Avalonia.Headless). Screenshots are not
required. Report the dark-mockup differences you found (step 2) and the list of views touched.
