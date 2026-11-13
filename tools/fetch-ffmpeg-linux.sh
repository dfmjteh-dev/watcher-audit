#!/usr/bin/env bash
# ffmpeg для пережатия записей на сервере (Transcode.cs): статическая сборка
# FFmpeg-Builds/FFmpeg-Builds (прежний BtbN, GPL, libsvtav1) — SVT-AV1 из пакета Debian 12 (1.4.1) в
# ~3 раза медленнее (замер на сегментах прода, 2026-10-19). Сборка
# версионированная, проверяется по SHA-256 архива и самого ffmpeg.
#
#   ./tools/fetch-ffmpeg-linux.sh <каталог>   → <каталог>/ffmpeg
#
# Новая версия — поменять переменные ниже (SHA-256 архива — поле digest ассета
# в релизе GitHub) и проверить замером (docs/configuration.md, «Пережатие»).
# Брать только сборку последнего дня месяца: ежедневные autobuild-* BtbN удаляет
# примерно через две недели (на 0.14.2 сборка 2026-10-05 уже отдавала 404 и
# ломала и образ, и установочный архив), месячные хранятся.
set -euo pipefail
BUILD_TAG="autobuild-2026-09-30-13-08"
ARCHIVE="ffmpeg-n9.0.2-17-g2a571b6068-linux64-gpl-9.0.tar.xz"
ARCHIVE_SHA256="68ee646831adaae2495618346f3bba94ff207ff83bbd34d643e7004730d66269"
FFMPEG_SHA256="0d709a0bed1a43f24cd2c420878c63afea4580671b6949ad19ed124572f3bc3d"
ASSET_ID="600951759"   # тот же архив в GitHub API; digest совпадает с ARCHIVE_SHA256
DEST="${1:?каталог назначения}"
mkdir -p "$DEST"
if [ -f "$DEST/ffmpeg" ] && echo "$FFMPEG_SHA256  $DEST/ffmpeg" | sha256sum -c --status; then
  echo "ffmpeg ($ARCHIVE) уже на месте"
  exit 0
fi
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
if ! curl -fsSL --retry 3 --retry-all-errors --max-time 300 -o "$TMP/ff.tar.xz" "https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/$BUILD_TAG/$ARCHIVE"; then
  # Обходим сбой release redirect через официальный API того же ассета.
  # Никаких токенов в образе; SHA-256 архива и бинарника проверяются ниже.
  curl -fsSL --retry 3 --retry-all-errors --max-time 300 -H 'Accept: application/octet-stream' \
    -o "$TMP/ff.tar.xz" "https://api.github.com/repos/FFmpeg-Builds/FFmpeg-Builds/releases/assets/$ASSET_ID"
fi
echo "$ARCHIVE_SHA256  $TMP/ff.tar.xz" | sha256sum -c --quiet
tar -xJf "$TMP/ff.tar.xz" -C "$TMP" --wildcards '*/bin/ffmpeg' --strip-components=2
echo "$FFMPEG_SHA256  $TMP/ffmpeg" | sha256sum -c --quiet
install -m 755 "$TMP/ffmpeg" "$DEST/ffmpeg"
echo "ffmpeg → $DEST/ffmpeg"
