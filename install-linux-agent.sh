#!/usr/bin/env bash
# Единая точка входа для Linux-агента Watcher: экран (Фаза 7) + консоль/SSH
# (Фаза 8a+8b), см. docs/roadmap.md. Оборачивает install-capture-linux.sh
# (запускается как есть, от текущего пользователя) и install-terminal-agent.sh
# (требует root — сам делает `sudo` внутри, не нужно запускать этот скрипт
# через sudo целиком).
#
# Консоль/SSH записывается у всех пользователей станции; кого не писать —
# allowUsers/excludeUsers в конфиге группы на сервере (веб-UI).
set -euo pipefail

SERVER_URL=""
ENROLL_TOKEN=""
CA_CERT=""
UNINSTALL=false

while [ $# -gt 0 ]; do
  case "$1" in
    --server-url) SERVER_URL="$2"; shift 2 ;;
    --enroll-token) ENROLL_TOKEN="$2"; shift 2 ;;
    --ca-cert) CA_CERT="$2"; shift 2 ;;
    --users) echo "ВНИМАНИЕ: --users больше не нужен и игнорируется — консоль пишется у всех," >&2
             echo "кого не писать — конфиг группы на сервере." >&2
             shift 2 ;;
    --uninstall) UNINSTALL=true; shift ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done

if [ "$(id -u)" = "0" ]; then
  echo "Не запускайте этот скрипт от root/sudo целиком — часть экрана" >&2
  echo "(install-capture-linux.sh) должна ставиться от обычного desktop-" >&2
  echo "пользователя; часть консоли сама вызовет sudo внутри себя." >&2
  exit 1
fi

if ! sudo -n true 2>/dev/null && ! sudo -v; then
  echo "Нужны sudo-права для части консоли/SSH (install-terminal-agent.sh" >&2
  echo "правит /etc/pam.d, системный bashrc и ставит системную службу)." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

if [ "$UNINSTALL" = true ]; then
  echo "=== Удаляю Terminal Agent (консоль/SSH)... ==="
  sudo "$SCRIPT_DIR/install-terminal-agent.sh" --uninstall
  echo
  echo "=== Удаляю Capture (экран)... ==="
  "$SCRIPT_DIR/install-capture-linux.sh" --uninstall
  exit 0
fi

if [ -z "$SERVER_URL" ]; then
  echo "Укажите --server-url, например: --server-url http://192.0.2.10:5050" >&2
  exit 1
fi

echo "=== [1/2] Устанавливаю Capture (экран) для $(whoami)... ==="
"$SCRIPT_DIR/install-capture-linux.sh" --server-url "$SERVER_URL" --enroll-token "$ENROLL_TOKEN" --ca-cert "$CA_CERT"

echo
echo "=== [2/2] Устанавливаю Terminal Agent (ввод+вывод консоли/SSH, все пользователи)... ==="
sudo "$SCRIPT_DIR/install-terminal-agent.sh" --server-url "$SERVER_URL" --enroll-token "$ENROLL_TOKEN" --ca-cert "$CA_CERT"

echo
echo "Готово — оба механизма установлены. Экран: systemctl --user status"
echo "watcher-capture, выгрузка: systemctl status watcher-uploader. Консоль/SSH:"
echo "systemctl status watcher-terminal-agent. Оба агента регистрируются одним"
echo "токеном регистрации, каждый своей учёткой (экран и консоль)."
