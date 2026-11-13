#!/usr/bin/env bash
# The fast test set of the `ci` job on a PR into dev: everything except the tests that render headless windows,
# compare screenshots or run for minutes. The full set runs on the release PR (dev -> main) and on a manual run
# (ci.yml, `linux` job). Usage: bash scripts/ci/test-fast.sh [extra dotnet test args]. Expects a finished Release build.
#
# Left out, with the reasons (measured on the ubuntu-latest runner, 4m 30s for the full set):
#   Murmur.Scripts.Tests                      pwsh scripts run as child processes (~1m 45s); the Windows jobs and verify-local.ps1 cover them.
#   Murmur.App.Tests.Design                   design parity: renders every artboard and compares pixels.
#   Murmur.App.Tests *ScreenshotTests, *ViewTests and the other classes below
#                                             open headless Avalonia windows (the App.Tests assembly alone takes ~4m 20s).
#                                             The view-model, localization, token, call-detection and dialog-logic tests stay.
#   Murmur.Core.Tests OneHourNoLossTests      a simulated hour of recording, ~3 minutes of wall time (the 1-hour criterion of PLAN.md).
# A new test class runs here by default; if it renders a window or runs for more than a few seconds, add it below.
set -euo pipefail

mkdir -p artifacts/test-results

# --ignore-exit-code 8: an assembly whose tests are all filtered out (Murmur.Scripts.Tests) is not an error.
exec dotnet test Murmur.sln -c Release --no-build \
  --report-xunit-trx --results-directory artifacts/test-results \
  --ignore-exit-code 8 \
  --filter-not-namespace \
    Murmur.Scripts.Tests \
    Murmur.App.Tests.Design \
  --filter-not-class \
    Murmur.Core.Tests.Recording.OneHourNoLossTests \
    '*ScreenshotTests' \
    '*ViewTests' \
    Murmur.App.Tests.AppPolishTests \
    Murmur.App.Tests.AppTests \
    Murmur.App.Tests.AutoRecordTests \
    Murmur.App.Tests.BookmarkTests \
    Murmur.App.Tests.ChapterTests \
    Murmur.App.Tests.CompositorMotionTests \
    Murmur.App.Tests.DropToImportTests \
    Murmur.App.Tests.FollowAlongTests \
    Murmur.App.Tests.GlassOrbControlTests \
    Murmur.App.Tests.LevelBarsTests \
    Murmur.App.Tests.LibraryImportedTests \
    Murmur.App.Tests.MainWindowTests \
    Murmur.App.Tests.OnboardingMinimumSizeTests \
    Murmur.App.Tests.OpenSessionProbeTests \
    Murmur.App.Tests.OrbControlTests \
    Murmur.App.Tests.RecordingMarkTests \
    Murmur.App.Tests.ReducedMotionTests \
    Murmur.App.Tests.SessionShortcutsTests \
    Murmur.App.Tests.SettingsStartupTests \
    Murmur.App.Tests.ShellLibraryTests \
    Murmur.App.Tests.SpeakerNamesTests \
    Murmur.App.Tests.ThemeTests \
    Murmur.App.Tests.TranscriptMarkTests \
    Murmur.App.Tests.UncertainWordsTests \
    Murmur.App.Tests.VideoSurfaceTests \
  "$@"
