#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
mkdir -p tmp runtime/media
if command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null; then
  ln -sf "$(command -v ffmpeg)" runtime/media/ffmpeg
  ln -sf "$(command -v ffprobe)" runtime/media/ffprobe
else
  [[ $(uname -m) == aarch64 ]] || { echo 'Install ffmpeg and ffprobe for your architecture first.' >&2; exit 1; }
  URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-19-13-06/ffmpeg-N-127697-g3ebed8c087-linuxarm64-gpl.tar.xz
  curl -fL --connect-timeout 20 --max-time 300 "$URL" -o tmp/ffmpeg.tar.xz
  # The archive filename differs between the daily and latest releases, so verify installed executables below.
  tar -xJf tmp/ffmpeg.tar.xz -C tmp
  SOURCE=$(find tmp -maxdepth 3 -path '*/bin/ffmpeg' -type f -print -quit)
  [[ -n "$SOURCE" ]]
  cp "$SOURCE" "$(dirname "$SOURCE")/ffprobe" runtime/media/
  echo 'f00758a6d586e5aa56ecb72327fa24b2c51eda1fe24c0491d8141c0412128597  runtime/media/ffmpeg' | sha256sum -c -
  echo '4d73dad7ffcc654655c4f71fb85a8c91cf57f279ce68c079fe1f1c0560d34fce  runtime/media/ffprobe' | sha256sum -c -
  cp "$(dirname "$(dirname "$SOURCE")")/LICENSE.txt" runtime/media/
fi
runtime/media/ffmpeg -version | head -1
