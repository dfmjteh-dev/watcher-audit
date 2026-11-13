#!/usr/bin/env bash
# Ключ подписи APK Android-агента этого сервера. install-server.sh выпускает его
# один раз (рядом с ключом обновлений) и подписывает им APK при каждой сборке
# комплекта. Android ставит обновление приложения только с тем же сертификатом,
# поэтому ключ живёт между обновлениями сервера: потеря ключа = переустановка
# агента на телефонах (сброс устройства). Резервная копия — вместе с остальными
# ключами из /root/watcher-keys.
#
#   ./tools/android-signing/new-apk-key.sh --out /root/watcher-keys [--name "Watcher (host)"]
#
# В --out: android-apk.p12 (PKCS12, ключ RSA 3072 + самоподписанный сертификат
# на 30 лет — срок действия Android при обновлении не проверяет) и
# android-apk.pass (пароль). Уже есть — ничего не меняет.
set -euo pipefail
NAME="Watcher"
OUT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --name) NAME="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done
[ -n "$OUT" ] || { echo "Нужен --out" >&2; exit 1; }
[ -f "$OUT/android-apk.p12" ] && exit 0

install -d -m 700 "$OUT"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp:?}"' EXIT
( umask 077
  openssl rand -hex 24 > "$tmp/pass"
  openssl req -x509 -newkey rsa:3072 -nodes -keyout "$tmp/key.pem" -out "$tmp/cert.pem" \
    -days 10950 -subj "/CN=${NAME//\//-} Android agent" 2>/dev/null
  openssl pkcs12 -export -inkey "$tmp/key.pem" -in "$tmp/cert.pem" -name watcher \
    -out "$tmp/android-apk.p12" -passout "file:$tmp/pass"
  install -m 600 "$tmp/pass" "$OUT/android-apk.pass"
  install -m 600 "$tmp/android-apk.p12" "$OUT/android-apk.p12" )
