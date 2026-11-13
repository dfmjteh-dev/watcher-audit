#!/usr/bin/env bash
# Подпись APK Android-агента ключом сервера (new-apk-key.sh): zipalign + apksigner.
# Релиз приносит APK без подписи; уже подписанный (свой ключ организации) —
# копируется как есть.
#
#   ./tools/android-signing/sign-apk.sh IN.apk OUT.apk KEYS_DIR
#
# Нужны apksigner и zipalign (Debian/Ubuntu: apt install apksigner zipalign).
set -euo pipefail
[ $# -eq 3 ] || { echo "Использование: $0 IN.apk OUT.apk KEYS_DIR" >&2; exit 2; }
IN="$1" OUT="$2" KEYS="$3"
for tool in apksigner zipalign; do
  command -v "$tool" >/dev/null || { echo "Нужен $tool (apt install apksigner zipalign)" >&2; exit 1; }
done

if apksigner verify "$IN" >/dev/null 2>&1; then
  cp "$IN" "$OUT"
  echo "APK уже подписан — без изменений: $OUT"
  exit 0
fi
[[ -f "$KEYS/android-apk.p12" && -f "$KEYS/android-apk.pass" ]] \
  || { echo "Нет ключа подписи APK в $KEYS (tools/android-signing/new-apk-key.sh)" >&2; exit 1; }

tmp="$(mktemp -d)"
trap 'rm -rf "${tmp:?}"' EXIT
zipalign -f -p 4 "$IN" "$tmp/aligned.apk"
apksigner sign --ks "$KEYS/android-apk.p12" --ks-type PKCS12 --ks-pass "file:$KEYS/android-apk.pass" \
  --out "$tmp/signed.apk" "$tmp/aligned.apk"
apksigner verify "$tmp/signed.apk" >/dev/null
mv "$tmp/signed.apk" "$OUT"
echo "APK подписан ключом сервера: $OUT"
