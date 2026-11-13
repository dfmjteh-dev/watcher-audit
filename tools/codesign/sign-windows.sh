#!/usr/bin/env bash
# H3 (roadmap-next.md): Authenticode-подпись файлов агента Windows (exe, msi)
# на Linux — osslsigncode. Любым PFX: самоподписанным
# (new-codesign-cert.sh), корпоративного CA или купленным — для станций
# разница только в том, кому они уже доверяют. Подписывает на месте.
#
#   WATCHER_SIGN_PFX=~/watcher-codesign/watcher-codesign.pfx WATCHER_SIGN_PASSWORD=... \
#     ./tools/codesign/sign-windows.sh dist/stage/*.exe dist/watcher-agent.msi
#
# WATCHER_SIGN_TIMESTAMP — сервер меток времени RFC 3161 (по умолчанию
# DigiCert): с меткой подпись остаётся действительной и после истечения
# сертификата. Пусто — без метки (сборка без интернета; подпись перестанет
# проверяться в день истечения сертификата). TSA получает только хеш подписи.
set -euo pipefail

PFX="${WATCHER_SIGN_PFX:?WATCHER_SIGN_PFX — путь к PFX}"
PASSWORD="${WATCHER_SIGN_PASSWORD:?WATCHER_SIGN_PASSWORD — пароль PFX}"
TSA="${WATCHER_SIGN_TIMESTAMP-http://timestamp.digicert.com}"
[ $# -gt 0 ] || { echo "Укажите файлы для подписи" >&2; exit 1; }
command -v osslsigncode >/dev/null || { echo "Нужен osslsigncode (apt install osslsigncode)" >&2; exit 1; }

# Пароль — через файл с правами 600, не аргументом (виден в ps).
PASSFILE="$(mktemp)"
trap 'rm -f "$PASSFILE"' EXIT
printf '%s' "$PASSWORD" > "$PASSFILE"

for f in "$@"; do
  args=(sign -pkcs12 "$PFX" -readpass "$PASSFILE" -h sha256 -n "Watcher agent"
        -i "https://github.com/dfmjteh-dev/watcher-audit" -in "$f" -out "$f.signed")
  [ -n "$TSA" ] && args+=(-ts "$TSA")
  # Уже подписанный (повторная сборка) — подпись заменяется, не вкладывается.
  # Через sh: osslsigncode 2.7 на неподписанном PE падает (segfault) — это и
  # значит «подписи нет»; о падении сообщает sh в свой stderr, а он заглушён.
  # «; exit» — чтобы sh не заменил себя osslsigncode и сообщал bash уже он.
  # shellcheck disable=SC2016 # $@ раскрывает sh, не bash
  if sh -c 'osslsigncode remove-signature -in "$1" -out "$2"; exit $?' sh "$f" "$f.unsigned" >/dev/null 2>&1; then
    mv "$f.unsigned" "$f"
  else
    rm -f "$f.unsigned"
  fi
  if ! osslsigncode "${args[@]}" > "$f.log" 2>&1; then
    cat "$f.log" >&2
    rm -f "$f.log" "$f.signed"
    echo "не подписан: $f" >&2
    exit 1
  fi
  rm -f "$f.log"
  mv "$f.signed" "$f"
  echo "подписан: $f"
done
