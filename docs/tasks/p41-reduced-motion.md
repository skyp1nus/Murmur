# Task p41-reduced-motion: honour the system "reduce motion" setting

Branch `feat/reduced-motion` (based on `phase4.1` after wave 2). `tokens.json` says the orb's breathing is disabled under
reduced motion; Avalonia 12 has no API for it, so nothing in the app follows it today.

## Core contract (approved by the architect)
`Murmur.Core.Platform.IMotionPreference { bool ReduceMotion { get; } event Action? Changed; }` — `Changed` raised off the
UI thread (subscribers marshal). Windows implementation in `Murmur.Capture.Windows`: `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)`
(Settings → Accessibility → Visual effects → Animation effects), refreshed on `WM_SETTINGCHANGE` (the existing message-only
window thread of the hotkeys can carry it, or its own); a constant `false` elsewhere. Desktop composition passes it to the App.

## Spec
1. Every animated orb (`OrbControl`, `GlassOrbControl`, `OrbDisc` if animated) and any other decorative animation (halo,
   breathing, level-reactive pulsing beyond the meter itself, page transitions if any) stays static under reduced motion;
   level meters and progress bars keep updating (they carry information).
2. Timers of static orbs are stopped (no CPU while reduced motion is on).
3. The parity tests already freeze orbs; add unit tests: preference on → no timer, static frame; toggling at runtime
   starts/stops correctly; Windows-trait test for the SPI read.
4. Report (Ukrainian): what animates and what stops under the setting.
