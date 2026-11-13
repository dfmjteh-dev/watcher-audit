#!/usr/bin/env bash
# H2 (roadmap-next.md): ключ обновлений агентов — ECDSA P-256. Закрытым
# подписываются пакеты (sign-update.sh), открытый — на сервер
# (Server__UpdatePublicKey) и на каждую станцию при установке
# (install.ps1 -UpdateKey / msiexec UPDATEKEY= / watcher-agent-setup --update-key).
# Станция ставит только пакет, подписанный этим ключом, — взломанный сервер
# Watcher без закрытого ключа ничего запустить на парке не может.
#
#   ./tools/update-signing/new-update-key.sh --out ~/watcher-update-key
#
# Сразу прописать открытый ключ в конфиг сервера:
#   --env .env                               (Docker Compose: Server__UpdatePublicKey=…)
#   --appsettings /opt/watcher/appsettings.json   (systemd; так делает install-server.sh)
#
# Закрытый ключ шифруется паролем — спрашивается в терминале или берётся из
# WATCHER_UPDATE_KEY_PASSWORD. Хранить вне сервера Watcher и вне репозитория —
# там же, где ключ подписи кода (docs/codesign.md).
#
# Уже есть ключ в --out — новый не создаётся (станции с прежним открытым
# ключом перестали бы принимать обновления), открытый ключ берётся оттуда.
set -euo pipefail

OUT="" ENV_FILE="" APPSETTINGS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --env) ENV_FILE="$2"; shift 2 ;;
    --appsettings) APPSETTINGS="$2"; shift 2 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done
[ -n "$OUT" ] || { echo "Укажите --out <каталог вне репозитория>" >&2; exit 1; }

if [ -e "$OUT/update-key.pem" ]; then
  [ -f "$OUT/update-key.pub.txt" ] || { echo "$OUT/update-key.pem есть, а update-key.pub.txt нет — разберитесь вручную" >&2; exit 1; }
  echo "Ключ в $OUT уже есть — новый не создаю (станции с прежним открытым ключом"
  echo "перестали бы принимать обновления), беру открытый оттуда."
else
  if [ -z "${WATCHER_UPDATE_KEY_PASSWORD:-}" ] && [ ! -t 0 ]; then
    echo "Нет терминала для пароля закрытого ключа — задайте WATCHER_UPDATE_KEY_PASSWORD" >&2
    exit 1
  fi
  install -d -m 700 "$OUT"
  umask 077
  # Ключ — только в переменной, на диск попадает уже зашифрованным; открытый
  # берётся до шифрования, чтобы пароль спрашивался один раз (ввод + повтор).
  KEY_PEM="$(openssl ecparam -name prime256v1 -genkey -noout)"
  printf '%s\n' "$KEY_PEM" | openssl pkey -pubout -out "$OUT/update-key.pub.pem"
  if [ -n "${WATCHER_UPDATE_KEY_PASSWORD:-}" ]; then
    printf '%s\n' "$KEY_PEM" | openssl pkcs8 -topk8 -v2 aes-256-cbc -passout env:WATCHER_UPDATE_KEY_PASSWORD -out "$OUT/update-key.pem"
  else
    printf '%s\n' "$KEY_PEM" | openssl pkcs8 -topk8 -v2 aes-256-cbc -out "$OUT/update-key.pem"
  fi
  unset KEY_PEM
  chmod 644 "$OUT/update-key.pub.pem"
  grep -v -- '-----' "$OUT/update-key.pub.pem" | tr -d '\n' > "$OUT/update-key.pub.txt"
  echo >> "$OUT/update-key.pub.txt"
  chmod 644 "$OUT/update-key.pub.txt"
fi
PUB_B64="$(tr -d '\n' < "$OUT/update-key.pub.txt")"

if [ -n "$ENV_FILE" ]; then
  touch "$ENV_FILE"
  if grep -qE '^#? *Server__UpdatePublicKey=' "$ENV_FILE"; then
    sed -i -E "s|^#? *Server__UpdatePublicKey=.*|Server__UpdatePublicKey=$PUB_B64|" "$ENV_FILE"
  else
    printf '\n# Ключ обновлений агентов (H2) — открытый; закрытый у администратора.\nServer__UpdatePublicKey=%s\n' "$PUB_B64" >> "$ENV_FILE"
  fi
  echo "Открытый ключ записан в $ENV_FILE (Server__UpdatePublicKey)."
fi
if [ -n "$APPSETTINGS" ]; then
  python3 - "$APPSETTINGS" "$PUB_B64" <<'PY'
import json, sys
path, key = sys.argv[1:]
with open(path) as f: cfg = json.load(f)
cfg.setdefault("Server", {})["UpdatePublicKey"] = key
with open(path, "w") as f: json.dump(cfg, f, indent=2)
PY
  echo "Открытый ключ записан в $APPSETTINGS (Server.UpdatePublicKey)."
fi

echo "Ключ обновлений: $OUT"
echo "  update-key.pem     — ЗАКРЫТЫЙ ключ (зашифрован паролем). Заберите его на свою"
echo "                       машину, где будете подписывать пакеты, и удалите отсюда,"
echo "                       если это сервер Watcher (shred -u $OUT/update-key.pem)."
echo "  update-key.pub.txt — открытый ключ одной строкой:"
echo "    $PUB_B64"
echo "  станции: install.ps1 -UpdateKey <строка>, msiexec ... UPDATEKEY=<строка>,"
echo "           watcher-agent-setup --update-key <строка>"
