#!/usr/bin/env bash
# The fast test set of the `ci` job on a PR into dev. The full set runs on the release PR (dev -> main) and on a manual
# run (ci.yml, `linux` job). Usage: bash scripts/ci/test-fast.sh [extra dotnet test args]. Expects a finished Release build.
#
# What is left out (measured on the ubuntu-latest runner: 4m 30s for the full set, about 50 s for this one):
#   Project Murmur.Scripts.Tests   pwsh scripts run as child processes, about 1m 45s. The Windows jobs and verify-local.ps1 cover them.
#   Tests with [Trait("Category", "Slow")]
#                                  a headless Avalonia window (screenshots, input, layout, motion), design parity, or a long real-time
#                                  wait. Only those tests are marked, on the method where a class mixes them with pure logic tests
#                                  and on the class where all of its tests are of that kind. Murmur.App.Tests alone is about 4m 20s
#                                  with them and about 10 s without (1893 tests -> about 1000). One Core test class carries the
#                                  trait too: OneHourNoLossTests, a simulated hour of recording, about 3 minutes.
#   Tests with [Trait("Category", "DesignParity")]
#                                  the design-parity suite (renders every artboard and compares pixels).
# Everything else runs: Core, Storage, Intelligence, Models, Transcription, Desktop, AiEval, Capture.Windows (platform tests skip
# on Linux) and the logic, view-model and dialog tests of App. A new test or class runs here by default. Mark it
# [Trait("Category", "Slow")] if it opens a window or takes more than a few seconds.
#
# An assembly that ends up with no tests at all fails the run (exit code 8), so a project cannot drop out silently.
set -euo pipefail

# Projects left out entirely. Every other project of Murmur.sln, including ones added later, is tested.
EXCLUDE_PROJECTS=(Murmur.Scripts.Tests)

mkdir -p artifacts/ci artifacts/test-results

# A solution filter (.slnf) listing every project of Murmur.sln except the excluded ones. Paths in it are relative to the solution.
filter=artifacts/ci/fast.slnf
{
  echo '{"solution":{"path":"..\\..\\Murmur.sln","projects":['
  dotnet sln Murmur.sln list | tail -n +3 | tr -d '\r' | tr '\\' '/' | {
    first=1
    while IFS= read -r project; do
      skip=0
      for name in "${EXCLUDE_PROJECTS[@]}"; do
        if [ "$(basename "$project" .csproj)" = "$name" ]; then skip=1; fi
      done
      if [ "$skip" -eq 1 ]; then continue; fi
      if [ "$first" -eq 0 ]; then echo ','; fi
      first=0
      printf '"%s"' "$(sed 's|/|\\\\|g' <<<"$project")"
    done
    echo
  }
  echo ']}}'
} > "$filter"

exec dotnet test "$filter" -c Release --no-build \
  --report-xunit-trx --results-directory artifacts/test-results \
  --filter-not-trait "Category=Slow" "Category=DesignParity" \
  "$@"
