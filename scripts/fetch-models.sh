#!/usr/bin/env bash
# Downloads the models Murmur needs into $MURMUR_MODELS_DIR (default: ~/.murmur/models).
#   - Parakeet TDT v3 int8 (sherpa-onnx)   -> <dir>/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/
#   - Silero VAD                            -> <dir>/silero_vad.onnx
# With --ai (phase 3, AI search and chat) additionally:
#   - llama.cpp b11716 server (CPU build)   -> <dir>/llama.cpp/   (llama-server + its shared libraries)
#   - Gemma 4 E4B instruct Q4_K_M, EmbeddingGemma 300M Q8_0 (GGUF, from Hugging Face) -> <dir>/gguf/
#     plus <dir>/gguf/models.json {chat, embed, repo, file}
# Idempotent: already present files are skipped. Retries on failure. Exits non-zero on any error.
# Environment: MURMUR_MODELS_DIR, MURMUR_FETCH_RETRIES, HF_TOKEN (optional, for gated Hugging Face repos).
set -euo pipefail

usage() { printf 'usage: %s [--ai]\n' "$0"; }

WITH_AI=0
for arg in "$@"; do
  case "$arg" in
    --ai) WITH_AI=1 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; printf 'unknown argument: %s\n' "$arg" >&2; exit 2 ;;
  esac
done

BASE_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models"
PARAKEET_NAME="sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"
PARAKEET_URL="$BASE_URL/$PARAKEET_NAME.tar.bz2"
VAD_URL="$BASE_URL/silero_vad.onnx"
RETRIES="${MURMUR_FETCH_RETRIES:-4}"

MODELS_DIR="${MURMUR_MODELS_DIR:-$HOME/.murmur/models}"

log() { printf '[fetch-models] %s\n' "$*"; }
die() { printf '[fetch-models] ERROR: %s\n' "$*" >&2; exit 1; }

command -v curl >/dev/null 2>&1 || die "curl is required"
command -v tar >/dev/null 2>&1 || die "tar is required"

# download <url> <dest>: resumable download to <dest>.part, atomically renamed on success.
download() {
  local url="$1" dest="$2" attempt=1 delay=2
  while true; do
    if curl --fail --location --silent --show-error --connect-timeout 20 \
        --continue-at - --output "$dest.part" "$url"; then
      mv -f "$dest.part" "$dest"
      return 0
    fi
    if [ "$attempt" -ge "$RETRIES" ]; then
      rm -f "$dest.part"
      die "download failed after $RETRIES attempts: $url"
    fi
    log "download failed (attempt $attempt/$RETRIES), retrying in ${delay}s: $url"
    sleep "$delay"
    attempt=$((attempt + 1))
    delay=$((delay * 2))
  done
}

mkdir -p "$MODELS_DIR"
log "models directory: $MODELS_DIR"

# --- Parakeet TDT v3 int8 ---------------------------------------------------
PARAKEET_DIR="$MODELS_DIR/$PARAKEET_NAME"
if [ -f "$PARAKEET_DIR/encoder.int8.onnx" ]; then
  log "skip: $PARAKEET_NAME already present"
else
  command -v bzip2 >/dev/null 2>&1 || die "bzip2 is required to extract the Parakeet archive"
  tmp="$(mktemp -d "$MODELS_DIR/.fetch.XXXXXX")"
  trap 'rm -rf "$tmp"' EXIT
  log "downloading $PARAKEET_URL"
  download "$PARAKEET_URL" "$tmp/$PARAKEET_NAME.tar.bz2"
  log "extracting"
  tar -xjf "$tmp/$PARAKEET_NAME.tar.bz2" -C "$tmp"
  [ -f "$tmp/$PARAKEET_NAME/encoder.int8.onnx" ] || die "archive does not contain $PARAKEET_NAME/encoder.int8.onnx"
  rm -rf "$PARAKEET_DIR"
  mv "$tmp/$PARAKEET_NAME" "$PARAKEET_DIR"
  rm -rf "$tmp"
  trap - EXIT
  log "installed $PARAKEET_DIR"
fi

# --- Silero VAD ---------------------------------------------------------------
VAD_FILE="$MODELS_DIR/silero_vad.onnx"
if [ -s "$VAD_FILE" ]; then
  log "skip: silero_vad.onnx already present"
else
  log "downloading $VAD_URL"
  download "$VAD_URL" "$VAD_FILE"
  [ -s "$VAD_FILE" ] || die "silero_vad.onnx is empty after download"
  log "installed $VAD_FILE"
fi

# --- AI (optional): llama.cpp server + GGUF models ----------------------------
LLAMA_TAG="b11716"
LLAMA_BASE_URL="https://github.com/ggml-org/llama.cpp/releases/download/$LLAMA_TAG"
HF_CHAT_REPOS=(ggml-org/gemma-4-E4B-it-GGUF unsloth/gemma-4-E4B-it-GGUF bartowski/google_gemma-4-E4B-it-GGUF)
HF_EMBED_REPOS=(ggml-org/embeddinggemma-300M-GGUF unsloth/embeddinggemma-300m-GGUF)

# Hugging Face requests; HF_TOKEN (if set) is sent for gated repos. Not used for GitHub URLs.
hf_curl() {
  if [ -n "${HF_TOKEN:-}" ]; then
    curl --fail --location --silent --show-error --connect-timeout 20 -H "Authorization: Bearer $HF_TOKEN" "$@"
  else
    curl --fail --location --silent --show-error --connect-timeout 20 "$@"
  fi
}

# hf_pick_file <repo> <quant>: prints the first matching *.gguf file name of the repo (not a projector, not a shard).
# Returns 1 when the repo is unreachable or has no such file.
hf_pick_file() {
  local repo="$1" quant="$2" json
  json="$(hf_curl "https://huggingface.co/api/models/$repo")" || return 1
  printf '%s' "$json" | grep -o '"rfilename" *: *"[^"]*"' | sed 's/^"rfilename" *: *"//; s/"$//' \
    | grep -i -F -- "$quant" | grep -i '\.gguf$' | grep -vi 'mmproj' | grep -vE -- '-[0-9]{5}-of-[0-9]{5}' | sort | head -n 1 | { grep . || return 1; }
}

# hf_download <repo> <file> <dest>: resumable download from Hugging Face with retries.
hf_download() {
  local repo="$1" file="$2" dest="$3" attempt=1 delay=2 url
  url="https://huggingface.co/$repo/resolve/main/$file"
  while true; do
    if hf_curl --continue-at - --output "$dest.part" "$url"; then
      mv -f "$dest.part" "$dest"
      return 0
    fi
    if [ "$attempt" -ge "$RETRIES" ]; then
      rm -f "$dest.part"
      die "download failed after $RETRIES attempts: $url"
    fi
    log "download failed (attempt $attempt/$RETRIES), retrying in ${delay}s: $url"
    sleep "$delay"
    attempt=$((attempt + 1))
    delay=$((delay * 2))
  done
}

# fetch_gguf <label> <quant> <repo>...: sets FETCHED_PATH, FETCHED_REPO, FETCHED_FILE; tries the repos in order.
fetch_gguf() {
  local label="$1" quant="$2" repo file dest
  shift 2
  for repo in "$@"; do
    if ! file="$(hf_pick_file "$repo" "$quant")"; then
      log "$label: no $quant GGUF in $repo (or repo unreachable), trying the next candidate"
      continue
    fi
    dest="$GGUF_DIR/$file"
    if [ -s "$dest" ]; then
      log "skip: $file already present"
    else
      log "downloading $repo/$file"
      hf_download "$repo" "$file" "$dest"
      [ -s "$dest" ] || die "$file is empty after download"
      log "installed $dest"
    fi
    FETCHED_PATH="$dest"; FETCHED_REPO="$repo"; FETCHED_FILE="$file"
    return 0
  done
  die "$label: none of the candidate repositories worked: $*"
}

json_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

# json_unescape: reverses json_escape (only \\ and \").
json_unescape() { sed 's/\\"/"/g; s/\\\\/\\/g'; }

# read_manifest_paths <models.json>: succeeds and sets MANIFEST_CHAT / MANIFEST_EMBED when the manifest exists and both
# recorded files exist and are non-empty; fails otherwise (missing manifest, unreadable fields, missing or empty file).
read_manifest_paths() {
  local manifest="$1" chat embed
  [ -s "$manifest" ] || return 1
  chat="$(sed -n 's/^  "chat": "\(.*\)",$/\1/p' "$manifest" | head -n 1 | json_unescape)"
  embed="$(sed -n 's/^  "embed": "\(.*\)",$/\1/p' "$manifest" | head -n 1 | json_unescape)"
  [ -n "$chat" ] && [ -n "$embed" ] && [ -s "$chat" ] && [ -s "$embed" ] || return 1
  MANIFEST_CHAT="$chat"; MANIFEST_EMBED="$embed"
}

if [ "$WITH_AI" -eq 1 ]; then
  command -v find >/dev/null 2>&1 || die "find is required"
  LLAMA_DIR="$MODELS_DIR/llama.cpp"
  GGUF_DIR="$MODELS_DIR/gguf"

  case "$(uname -s)" in
    Linux)
      LLAMA_ARCHIVE="llama-$LLAMA_TAG-bin-ubuntu-x64.tar.gz"
      LLAMA_SERVER="llama-server"
      ;;
    MINGW*|MSYS*|CYGWIN*)
      LLAMA_ARCHIVE="llama-$LLAMA_TAG-bin-win-cpu-x64.zip"
      LLAMA_SERVER="llama-server.exe"
      ;;
    *) die "--ai: unsupported OS $(uname -s) (llama.cpp $LLAMA_TAG server is fetched for Linux x64 and Windows x64 only)" ;;
  esac

  if [ -x "$LLAMA_DIR/$LLAMA_SERVER" ] || [ -f "$LLAMA_DIR/$LLAMA_SERVER" ]; then
    log "skip: llama.cpp $LLAMA_TAG server already present"
  else
    tmp="$(mktemp -d "$MODELS_DIR/.fetch.XXXXXX")"
    trap 'rm -rf "$tmp"' EXIT
    log "downloading $LLAMA_BASE_URL/$LLAMA_ARCHIVE"
    download "$LLAMA_BASE_URL/$LLAMA_ARCHIVE" "$tmp/$LLAMA_ARCHIVE"
    mkdir "$tmp/x"
    log "extracting"
    case "$LLAMA_ARCHIVE" in
      *.zip)
        command -v unzip >/dev/null 2>&1 || die "unzip is required to extract the llama.cpp archive"
        unzip -q "$tmp/$LLAMA_ARCHIVE" -d "$tmp/x" ;;
      *) tar -xzf "$tmp/$LLAMA_ARCHIVE" -C "$tmp/x" ;;
    esac
    # The archive layout differs between releases (flat, llama-<tag>/, build/bin/): locate the server and take its whole folder.
    server_path="$(find "$tmp/x" -type f -name "$LLAMA_SERVER" | head -n 1)"
    [ -n "$server_path" ] || die "archive does not contain $LLAMA_SERVER"
    rm -rf "$LLAMA_DIR.new"
    mkdir -p "$LLAMA_DIR.new"
    cp -a "$(dirname "$server_path")/." "$LLAMA_DIR.new/"
    chmod +x "$LLAMA_DIR.new/$LLAMA_SERVER"
    rm -rf "$LLAMA_DIR"
    mv "$LLAMA_DIR.new" "$LLAMA_DIR"
    rm -rf "$tmp"
    trap - EXIT
    log "installed $LLAMA_DIR"
  fi

  mkdir -p "$GGUF_DIR"
  # Idempotent: when models.json names two existing non-empty files, skip the Hugging Face lookup and the download
  # (a re-run works offline and never switches to a different file). The manifest is read only for its two paths.
  if read_manifest_paths "$GGUF_DIR/models.json"; then
    log "skip: chat and embedding models already present (see $GGUF_DIR/models.json)"
    CHAT_PATH="$MANIFEST_CHAT"; EMBED_PATH="$MANIFEST_EMBED"
  else
    fetch_gguf "chat model" "Q4_K_M" "${HF_CHAT_REPOS[@]}"
    CHAT_PATH="$FETCHED_PATH"; CHAT_REPO="$FETCHED_REPO"; CHAT_FILE="$FETCHED_FILE"
    fetch_gguf "embedding model" "Q8_0" "${HF_EMBED_REPOS[@]}"
    EMBED_PATH="$FETCHED_PATH"; EMBED_REPO="$FETCHED_REPO"; EMBED_FILE="$FETCHED_FILE"

    printf '{\n  "chat": "%s",\n  "embed": "%s",\n  "repo": {"chat": "%s", "embed": "%s"},\n  "file": {"chat": "%s", "embed": "%s"}\n}\n' \
      "$(json_escape "$CHAT_PATH")" "$(json_escape "$EMBED_PATH")" "$(json_escape "$CHAT_REPO")" "$(json_escape "$EMBED_REPO")" \
      "$(json_escape "$CHAT_FILE")" "$(json_escape "$EMBED_FILE")" > "$GGUF_DIR/models.json.part"
    mv -f "$GGUF_DIR/models.json.part" "$GGUF_DIR/models.json"
    log "wrote $GGUF_DIR/models.json"
  fi

  log "AI is ready. Use these environment variables:"
  printf 'export MURMUR_LLM_MODEL=%q\n' "$CHAT_PATH"
  printf 'export MURMUR_EMBED_MODEL=%q\n' "$EMBED_PATH"
  printf 'export MURMUR_LLAMA_SERVER=%q\n' "$LLAMA_DIR/$LLAMA_SERVER"
fi

log "done"
