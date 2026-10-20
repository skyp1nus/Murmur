#!/usr/bin/env bash
# Design parity: renders every artboard of docs/design/source with the app (headless, English, the demo data of the canvas),
# compares it with docs/design/reference/<Board>.png and prints the report.
#
# Usage: scripts/design/parity.sh [--no-build] [BoardName ...]
#   --no-build   reuse the last Release build of tests/Murmur.App.Tests
#   BoardName    only these boards (e.g. Main SessionDark); default: all 22
#
# Output: artifacts/design-parity/ (<Board>.app.png, .diff.png, .side.png and report.md). Set MURMUR_PARITY_DIR to write elsewhere.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

nobuild=0
args=()
for arg in "$@"; do
  case "$arg" in
    --no-build) nobuild=1 ;;
    *) args+=("$arg") ;;
  esac
done

if [ "${#args[@]}" -gt 0 ]; then
  MURMUR_PARITY_BOARDS="$(IFS=,; echo "${args[*]}")"
  export MURMUR_PARITY_BOARDS
fi
filter=(--filter-trait "Category=DesignParity")

if [ "$nobuild" = "1" ]; then
  dotnet test --project tests/Murmur.App.Tests -c Release --no-build "${filter[@]}"
else
  dotnet test --project tests/Murmur.App.Tests -c Release "${filter[@]}"
fi

out="${MURMUR_PARITY_DIR:-$root/artifacts/design-parity}"
echo
echo "Report: $out/report.md"
echo
cat "$out/report.md"
