# Task p41-onboarding: onboarding exactly as the canvas

Branch `design/onboarding` (based on `phase4` after wave 1). Boards: `Onb1Welcome`, `Onb2Audio`, `Onb3Models`,
`Onb3ModelsReady`, `Onb4Preferences`, `Onb5FirstRecording` and their `*Dark` twins (1280x800); `OnbComponents*` is the
component sheet (states: hover, focus, disabled, selected, error) — use it as the reference for control states, it is
not scored. Truth: `docs/design/source/Onb*.dc.html` and `docs/design/reference/Onb*.png`; measure with
`DesignParityTests`. Parallel tasks own the sidebar/Library (`p41-shell-library`) and the Recording screen
(`p41-recording`): you own `OnboardingView` and the step views/view models.

## Scope
src/Murmur.App/** (onboarding views, view models, shared controls only when the component sheet demands it — then
check the other screens still render), tests/Murmur.App.Tests/**. No Core changes.

## Spec
1. Each step matches its board at 1280x800 in both themes: top bar (wordmark, "n / 5" + 5 progress segments), content,
   bottom bar (Skip/Back text buttons, primary button). Texts verbatim from the canvas.
2. Control states follow `OnbComponents.dc.html` (pointer-over, pressed, focus ring, disabled, selected cards, radio
   rows, toggles, level meters, progress rows).
3. The steps are fluid: at the minimum window size nothing overlaps or clips (the canvas is fixed 1280x800; keep the
   layout centred and scale gaps sensibly — describe what you chose).
4. Parity: every onboarding board ≤ 2 % mismatch (demo states as drawn: devices, levels, model choice and download
   progress, preferences, the first-recording transcript); set `MaxMismatchPercent` for all 12.
5. Keep every behaviour of the current onboarding (permissions, downloads, trial recording, skip paths) and its tests.
6. Report (Ukrainian): mismatch before/after per board, what still differs and why.
