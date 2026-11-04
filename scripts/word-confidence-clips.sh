#!/usr/bin/env bash
# Spike #53 (word confidence): prepares evaluation clips with known transcripts and writes
# <out-dir>/clips.tsv (language<TAB>wav<TAB>reference) for `Murmur.Transcription --confidence-eval`.
#
# Source: Google FLEURS test split (CC BY 4.0, real read speech, 16 kHz WAV) from Hugging Face, first N
# utterances of every language. When a language cannot be downloaded, falls back to espeak-ng speech
# synthesised from built-in sentences (marked "<lang>-tts" in the manifest), so the run still produces numbers.
#
# usage: scripts/word-confidence-clips.sh <out-dir> [clips-per-language=150]
# env:   FLEURS_LANGS  space-separated fleurs_config:language pairs (default "en_us:en uk_ua:uk")
#        REQUIRE_FLEURS=1  fail when FLEURS cannot be downloaded instead of falling back to espeak-ng
#        CI            when set, a missing espeak-ng is installed with apt-get (otherwise: fail with a hint)
#        FLEURS_BASE   base URL of the FLEURS data folder (default: Hugging Face; tests use file://)
#        HF_TOKEN      optional Hugging Face token
set -euo pipefail

OUT="${1:?usage: $0 <out-dir> [clips-per-language]}"
N="${2:-150}"
LANGS="${FLEURS_LANGS:-en_us:en uk_ua:uk}"
BASE="${FLEURS_BASE:-https://huggingface.co/datasets/google/fleurs/resolve/main/data}"

log() { printf '[word-confidence-clips] %s\n' "$*" >&2; }

case "$N" in
  ''|*[!0-9]*) log "clips-per-language must be a non-negative integer, got '$N'"; exit 2 ;;
esac

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
MANIFEST="$OUT/clips.tsv"
printf '# language\twav\treference\n' > "$MANIFEST"

fetch() {
  local url="$1" dest="$2"
  local auth=()
  if [ -n "${HF_TOKEN:-}" ]; then auth=(-H "Authorization: Bearer $HF_TOKEN"); fi
  curl --fail --location --silent --show-error --retry 4 --retry-delay 5 --connect-timeout 20 "${auth[@]}" -o "$dest" "$url"
}

fleurs() {
  local config="$1" lang="$2" dir="$OUT/$1"
  mkdir -p "$dir"
  fetch "$BASE/$config/test.tsv" "$dir/test.tsv" || return 1
  fetch "$BASE/$config/audio/test.tar.gz" "$dir/test.tar.gz" || return 1
  tar -xzf "$dir/test.tar.gz" -C "$dir"
  rm -f "$dir/test.tar.gz"
  local added=0 file wav ref
  # FLEURS tsv: id, file_name, raw_transcription, transcription, phonemes, num_samples, gender
  while IFS=$'\t' read -r _ file ref _; do
    [ "$added" -ge "$N" ] && break
    wav="$(find "$dir" -name "$file" -print -quit)"
    if [ -z "$wav" ] || [ -z "$ref" ]; then continue; fi
    printf '%s\t%s\t%s\n' "$lang" "$wav" "$ref" >> "$MANIFEST"
    added=$((added + 1))
  done < "$dir/test.tsv"
  log "$config: $added clip(s)"
  [ "$added" -gt 0 ]
}

tts() {
  local lang="$1" dir="$OUT/tts-$1" i=0 sentence
  if ! command -v espeak-ng >/dev/null 2>&1; then
    if [ -n "${CI:-}" ]; then
      sudo apt-get update -qq && sudo apt-get install -y -qq espeak-ng >/dev/null
    else
      log "espeak-ng is not installed; install it (e.g. 'sudo apt-get install espeak-ng') or set REQUIRE_FLEURS=1"
      exit 1
    fi
  fi
  mkdir -p "$dir"
  case "$lang" in
    uk) sentences=(
          "Сьогодні ми обговорили план робіт на наступний квартал."
          "Бюджет проєкту треба погодити до кінця місяця."
          "Олена надішле протокол зустрічі всім учасникам."
          "Нам потрібно перевірити звіт про продажі за вересень."
          "Чи можемо ми перенести дзвінок на четвер о десятій?"
          "Команда дизайну показала нові макети застосунку."
          "Тестування на Windows займе ще два або три дні."
          "Клієнт попросив додати пошук по всіх записах."
          "Давайте зафіксуємо рішення і призначимо відповідальних."
          "Сервер у Львові перезавантажили вчора ввечері."
        ) ;;
    *) sentences=(
          "Today we discussed the roadmap for the next quarter."
          "The project budget has to be approved by the end of the month."
          "Helen will send the meeting notes to everyone who attended."
          "We need to double check the September sales report."
          "Can we move the call to Thursday at ten in the morning?"
          "The design team presented new mockups of the application."
          "Testing on Windows will take another two or three days."
          "The customer asked us to add search across all recordings."
          "Let's write down the decision and assign the owners."
          "The server in Lisbon was restarted last night."
        ) ;;
  esac
  for sentence in "${sentences[@]}"; do
    i=$((i + 1))
    espeak-ng -v "$lang" -s 150 -w "$dir/$i.wav" "$sentence"
    printf '%s\t%s\t%s\n' "$lang-tts" "$dir/$i.wav" "$sentence" >> "$MANIFEST"
  done
  log "espeak-ng $lang: $i synthetic clip(s)"
}

for pair in $LANGS; do
  config="${pair%%:*}"
  lang="${pair##*:}"
  if ! fleurs "$config" "$lang"; then
    if [ "${REQUIRE_FLEURS:-0}" = "1" ]; then
      log "FLEURS $config unavailable and REQUIRE_FLEURS=1: failing instead of falling back to espeak-ng"
      echo "::error::FLEURS $config could not be downloaded (REQUIRE_FLEURS=1)"
      exit 1
    fi
    log "FLEURS $config unavailable, falling back to espeak-ng (synthetic speech: much weaker numbers)"
    echo "::warning::FLEURS $config unavailable; $lang clips are espeak-ng synthetic speech ($lang-tts)"
    tts "$lang"
  fi
done

log "manifest: $MANIFEST ($(grep -vc '^#' "$MANIFEST") clips)"
