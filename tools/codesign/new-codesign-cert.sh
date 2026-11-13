#!/usr/bin/env bash
# H3 (roadmap-next.md): самоподписанный сертификат издателя для Authenticode-
# подписи агента Windows (exe, msi). Подписывает sign-windows.sh,
# на станции раздаётся GPO в «Доверенные корневые» и «Доверенные издатели» —
# docs/codesign.md.
#
# Почему его безопасно класть в Trusted Root: basicConstraints CA:FALSE
# (критичное) — Windows не построит через него цепочку ни для какого другого
# сертификата, а EKU только codeSigning — им нельзя подписать TLS-сервер. То
# есть утечка ключа = возможность подписать код от имени Watcher (отзывается
# удалением сертификата из GPO), но не MITM HTTPS на станциях.
#
#   ./tools/codesign/new-codesign-cert.sh --name "ООО Ромашка — Watcher" --out ~/watcher-codesign
#
# В --out: watcher-codesign.pfx (ключ + сертификат, пароль спрашивается или
# берётся из WATCHER_SIGN_PASSWORD), watcher-codesign.cer (DER — для GPO) и
# .pem. Ключ НЕ класть в репозиторий; копия — туда же, где хранятся другие
# секреты организации.
set -euo pipefail

NAME="Watcher"
OUT=""
YEARS=5

while [ $# -gt 0 ]; do
  case "$1" in
    --name) NAME="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --years) YEARS="$2"; shift 2 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done

if [ -z "$OUT" ]; then
  echo "Укажите --out <каталог вне репозитория>" >&2
  exit 1
fi
if [ -e "$OUT/watcher-codesign.pfx" ]; then
  echo "$OUT/watcher-codesign.pfx уже есть — не перезаписываю (подписи старым сертификатом" >&2
  echo "перестанут проверяться на станциях, где раздан только новый)." >&2
  exit 1
fi

PASSWORD="${WATCHER_SIGN_PASSWORD:-}"
if [ -z "$PASSWORD" ]; then
  read -r -s -p "Пароль для PFX: " PASSWORD; echo
  read -r -s -p "Ещё раз: " again; echo
  [ "$PASSWORD" = "$again" ] || { echo "Пароли не совпали" >&2; exit 1; }
fi
[ ${#PASSWORD} -ge 12 ] || { echo "Пароль PFX — не короче 12 символов" >&2; exit 1; }

install -d -m 700 "$OUT"
umask 077
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

cat > "$TMP/ext.cnf" <<'EOF'
[req]
distinguished_name = dn
prompt = no
[dn]
[ext]
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
subjectKeyIdentifier = hash
EOF

openssl req -x509 -newkey rsa:3072 -sha256 -nodes \
  -keyout "$TMP/key.pem" -out "$OUT/watcher-codesign.pem" \
  -days $((YEARS * 365)) -subj "/CN=$NAME" -config "$TMP/ext.cnf" -extensions ext 2>/dev/null
openssl x509 -in "$OUT/watcher-codesign.pem" -outform der -out "$OUT/watcher-codesign.cer"
PASS="$PASSWORD" openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$OUT/watcher-codesign.pem" \
  -name "$NAME" -out "$OUT/watcher-codesign.pfx" -passout env:PASS
chmod 644 "$OUT/watcher-codesign.cer" "$OUT/watcher-codesign.pem"

echo "Готово: $OUT"
echo "  watcher-codesign.pfx — ключ, хранить как секрет; подпись: WATCHER_SIGN_PFX=$OUT/watcher-codesign.pfx"
echo "  watcher-codesign.cer — раздать станциям (GPO), docs/codesign.md"
openssl x509 -in "$OUT/watcher-codesign.pem" -noout -subject -enddate -fingerprint -sha1 | sed 's/^/  /'
