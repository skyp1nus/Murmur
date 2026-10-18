#!/usr/bin/env bash
# Renders the worker bench JSON (Murmur.Transcription --bench --out ...) as a markdown table and
# enforces the PLAN.md budget "1 h audio -> text < 3 min on CPU".
#   usage: asr-bench-summary.sh <asr-bench.json> [budget-seconds=180] [label]
# The table goes to stdout (CI appends it to $GITHUB_STEP_SUMMARY). Exit 1 when transcribeMs exceeds
# the budget or when the audio is shorter than 3600 s (a shorter run would not prove the budget).
set -euo pipefail

json="${1:?usage: asr-bench-summary.sh <asr-bench.json> [budget-seconds] [label]}"
budget="${2:-180}"
label="${3:-}"

command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 2; }
[ -f "$json" ] || { echo "bench result not found: $json" >&2; exit 2; }

jq -r --argjson budget "$budget" --arg label "$label" '
  def f1: . * 10 | round / 10;
  def f3: . * 1000 | round / 1000;
  ((.transcribeMs / 1000) as $t
   | ((.audioSeconds >= 3600) and ($t <= $budget)) as $ok
   | "### ASR bench" + (if $label != "" then " (" + $label + ")" else "" end),
     "",
     "| Metric | Value |",
     "|---|---:|",
     "| Audio, s | \(.audioSeconds | f1) |",
     "| Transcribe, s | \($t | f1) |",
     "| Model load, s | \(.loadMs / 1000 | f1) |",
     "| RTF | \(.rtf | f3) |",
     "| x-realtime | \(.realtimeFactorX | f1) |",
     "| Segments / words | \(.segments) / \(.words) |",
     "| Threads / batch | \(.threads) / \(.batch) |",
     "| CPU | \(.machine.cpu) (\(.machine.cores) cores) |",
     "| OS | \(.machine.os) |",
     "| Budget, s | \($budget) |",
     "| Result | \(if $ok then "PASS" else "FAIL" end) |")
' "$json"

jq -e --argjson budget "$budget" '(.audioSeconds >= 3600) and (.transcribeMs / 1000 <= $budget)' "$json" >/dev/null || {
  echo "ASR budget FAILED: transcribe time must be <= ${budget}s for >= 3600 s of audio" >&2
  exit 1
}
