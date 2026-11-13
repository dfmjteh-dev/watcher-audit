#!/usr/bin/env bash
# H2 (roadmap-next.md): подпись пакета обновления агента ключом обновлений
# (new-update-key.sh). Подписывается не файл, а сообщение
#   watcher-agent-update/1 \n <платформа> \n <версия> \n <sha256 файла> \n
# — станция не поставит пакет под чужой версией или для другой ОС.
#
#   ./tools/update-signing/sign-update.sh --key ~/watcher-update-key/update-key.pem \
#       --platform windows-msi --version 0.16.0 dist/watcher-agent-0.16.0.msi
#
# Пароль закрытого ключа спрашивается или берётся из WATCHER_UPDATE_KEY_PASSWORD.
# Печатает подпись (base64) — её вставляют в веб-UI при загрузке пакета;
# рядом с файлом кладёт <файл>.sig с тем же текстом.
set -euo pipefail

KEY="" PLATFORM="" VERSION="" FILE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --key) KEY="$2"; shift 2 ;;
    --platform) PLATFORM="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    -*) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
    *) FILE="$1"; shift ;;
  esac
done
case "$PLATFORM" in windows-msi|linux-deb|linux-rpm|android) ;; *) echo "--platform: windows-msi | linux-deb | linux-rpm | android" >&2; exit 1 ;; esac
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "--version: X.Y.Z" >&2; exit 1; }
if [ ! -f "$KEY" ] || [ ! -f "$FILE" ]; then
  echo "Нужны --key <update-key.pem> и файл пакета" >&2
  exit 1
fi

SHA="$(sha256sum "$FILE" | cut -d' ' -f1)"
# Пароль ключа — в терминале или из WATCHER_UPDATE_KEY_PASSWORD (без терминала).
PASSIN=()
if [ -n "${WATCHER_UPDATE_KEY_PASSWORD:-}" ]; then PASSIN=(-passin env:WATCHER_UPDATE_KEY_PASSWORD); fi
SIG="$(printf 'watcher-agent-update/1\n%s\n%s\n%s\n' "$PLATFORM" "$VERSION" "$SHA" \
  | openssl dgst -sha256 -sign "$KEY" "${PASSIN[@]}" | base64 -w0)"
printf '%s\n' "$SIG" > "$FILE.sig"
echo "sha256:  $SHA"
echo "подпись: $SIG"
echo "(записано в $FILE.sig)"
