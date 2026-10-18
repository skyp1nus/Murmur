#!/usr/bin/env bash
# Downloads the ASR models Murmur needs into $MURMUR_MODELS_DIR (default: ~/.murmur/models).
#   - Parakeet TDT v3 int8 (sherpa-onnx)   -> <dir>/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/
#   - Silero VAD                            -> <dir>/silero_vad.onnx
# Idempotent: already present files are skipped. Retries on failure. Exits non-zero on any error.
set -euo pipefail

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

log "done"
