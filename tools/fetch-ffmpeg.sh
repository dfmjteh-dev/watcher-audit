#!/usr/bin/env bash
# ffmpeg.exe для агента Windows — в git не хранится (~100 МБ). Берётся из
# версионированного релиза сборок gyan.dev (GitHub GyanD/codexffmpeg, сборка
# essentials: GPL, libx264), проверяется по SHA-256 архива и кладётся в
# tools/ffmpeg.exe. Уже лежит нужный — ничего не качает.
#
#   ./tools/fetch-ffmpeg.sh
#
# Новая версия ffmpeg — поменять три переменные ниже (SHA-256 архива — в
# релизе GitHub, поле digest ассета). Хеш ffmpeg.exe меняется вместе с ним —
# исключения AV/EDR по хешу (docs/av-edr.md) обновить в той же раскатке.
set -euo pipefail

FFMPEG_VERSION="9.0.2"
FFMPEG_ZIP_SHA256="60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba"
FFMPEG_EXE_SHA256="3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec"
URL="https://github.com/GyanD/codexffmpeg/releases/download/$FFMPEG_VERSION/ffmpeg-$FFMPEG_VERSION-essentials_build.zip"

DEST="$(cd "$(dirname "$0")" && pwd)/ffmpeg.exe"
if [ -f "$DEST" ] && echo "$FFMPEG_EXE_SHA256  $DEST" | sha256sum -c --status; then
  echo "ffmpeg.exe $FFMPEG_VERSION уже на месте"
  exit 0
fi
command -v unzip >/dev/null || { echo "Нужен unzip" >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
echo "Качаю ffmpeg $FFMPEG_VERSION (essentials, gyan.dev)..."
curl -fsSL --retry 3 -o "$TMP/ffmpeg.zip" "$URL"
echo "$FFMPEG_ZIP_SHA256  $TMP/ffmpeg.zip" | sha256sum -c --quiet
unzip -q -j "$TMP/ffmpeg.zip" "*/bin/ffmpeg.exe" -d "$TMP"
echo "$FFMPEG_EXE_SHA256  $TMP/ffmpeg.exe" | sha256sum -c --quiet
mv "$TMP/ffmpeg.exe" "$DEST"
echo "ffmpeg.exe $FFMPEG_VERSION → $DEST"
