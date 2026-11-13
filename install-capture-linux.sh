#!/usr/bin/env bash
# Установка Watcher agent на Linux-станцию: Capture.Linux (экран) — systemd
# --user служба пользователя рабочего стола, Uploader — системная служба (Задача
# G1c, roadmap-next.md). Аналог install.ps1 для Windows, Фаза 7 (roadmap.md).
#
# НЕ запускать от root/sudo — Capture должен завестись в systemd --user именно
# того пользователя, чей рабочий стол аудируется (watcher-capture.service
# привязан к graphical-session.target его сессии). Системную часть (Uploader,
# /var/lib/watcher) скрипт ставит сам через sudo.
#
# Почему Uploader системный (до G1 он тоже был systemd --user): с G1 у агента
# своя учётка на сервере, её секрет не должен лежать в домашнем каталоге
# записываемого пользователя. Очередь — общая /var/lib/watcher/queue с правами
# 1733: любой пользователь создаёт в ней файлы, но не видит и не удаляет чужие;
# Capture пишет с UMask=0077 (файлы 600), Uploader (root) читает всё.
set -euo pipefail

SERVER_URL=""
API_TOKEN=""
ENROLL_TOKEN=""
INSTALL_DIR="$HOME/.local/opt/watcher"
CA_CERT=""
UNINSTALL=false

SYS_DIR="/opt/watcher-uploader"
DATA_DIR="/var/lib/watcher"

while [ $# -gt 0 ]; do
  case "$1" in
    --server-url) SERVER_URL="$2"; shift 2 ;;
    --enroll-token) ENROLL_TOKEN="$2"; shift 2 ;;
    --api-token) API_TOKEN="$2"; shift 2 ;;
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    --ca-cert) CA_CERT="$2"; shift 2 ;;
    --uninstall) UNINSTALL=true; shift ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done

if [ "$(id -u)" = "0" ]; then
  echo "Не запускайте этот скрипт от root/sudo — служба Capture должна завестись в" >&2
  echo "systemd --user реального пользователя рабочего стола, см. комментарий выше." >&2
  exit 1
fi
if ! sudo -n true 2>/dev/null && ! sudo -v; then
  echo "Нужны sudo-права: выгрузка (Watcher.Uploader) — системная служба." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
UNIT_DIR="$HOME/.config/systemd/user"
OLD_DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/watcher"

# До G1c Uploader был systemd --user службой с конфигом (и ApiToken) в
# $INSTALL_DIR — снимаем его при любом запуске, установке или удалении.
remove_user_uploader() {
  if [ -f "$UNIT_DIR/watcher-uploader.service" ]; then
    echo "Снимаю пользовательский Uploader (до Задачи G1c)..."
    systemctl --user disable --now watcher-uploader.service 2>/dev/null || true
    rm -f "$UNIT_DIR/watcher-uploader.service"
  fi
  rm -f "$INSTALL_DIR/Watcher.Uploader" "$INSTALL_DIR/Watcher.Uploader.json"
}

if [ "$UNINSTALL" = true ]; then
  echo "Останавливаю и снимаю Capture пользователя $(whoami)..."
  systemctl --user stop watcher-capture.service 2>/dev/null || true
  systemctl --user disable watcher-capture.service 2>/dev/null || true
  rm -f "$UNIT_DIR/watcher-capture.service"
  remove_user_uploader
  systemctl --user daemon-reload
  rm -rf "$INSTALL_DIR"
  echo "Останавливаю и снимаю системный Uploader..."
  sudo systemctl disable --now watcher-uploader.service 2>/dev/null || true
  sudo rm -f /etc/systemd/system/watcher-uploader.service
  sudo systemctl daemon-reload
  sudo rm -rf "$SYS_DIR"
  echo "Удалено. Данные в $DATA_DIR (очередь, учётка агента) остаются — удалите"
  echo "вручную, если нужно; при переустановке учётка будет использована снова."
  echo "Если на станции Capture стоит и у других пользователей — их записи будут"
  echo "копиться в очереди, пока выгрузку не поставят заново."
  exit 0
fi

if [ -z "$SERVER_URL" ]; then
  echo "Укажите --server-url, например: --server-url https://watcher.internal" >&2
  exit 1
fi

# B1 (roadmap-next.md): HttpClient в Uploader доверяет системному хранилищу
# сертификатов — если сервер за Caddy с самоподписанным CA (tls internal),
# без этого шага запросы падали бы с ошибкой доверия сертификата. CA
# экспортируется на сервере через install-server.sh --export-ca.
if [ -n "$CA_CERT" ]; then
  echo "Устанавливаю корневой CA сервера в системное хранилище..."
  sudo cp "$CA_CERT" /usr/local/share/ca-certificates/watcher-server-ca.crt
  sudo update-ca-certificates
fi

case "$SERVER_URL" in
  http://*)
    host="${SERVER_URL#http://}"; host="${host%%[:/]*}"
    if [ "$host" != "localhost" ] && [ "$host" != "127.0.0.1" ]; then
      echo "ПРЕДУПРЕЖДЕНИЕ: --server-url использует http:// на не-localhost адрес" >&2
      echo "($host) — видео и токены агента пойдут по сети открытым" >&2
      echo "текстом. Рекомендуется https:// (см. docs/install.md, 'HTTPS для агентов')." >&2
    fi
    ;;
esac

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "ffmpeg не найден в PATH — установите пакет ffmpeg (apt; на RHEL-семействе — RPM Fusion)" >&2
  exit 1
fi

if [ -z "$ENROLL_TOKEN" ] && [ -z "$API_TOKEN" ] && ! sudo test -f "$DATA_DIR/agent/credential.json"; then
  echo "ПРЕДУПРЕЖДЕНИЕ: не задан ни --enroll-token, ни --api-token, и учётки агента" >&2
  echo "ещё нет — выгрузка на сервер работать не будет. Создайте токен регистрации" >&2
  echo "в веб-UI (Настройки → Агенты) и передайте --enroll-token." >&2
fi

echo "=== Системная часть: Uploader в $SYS_DIR, данные в $DATA_DIR ==="
sudo install -d -m 755 "$SYS_DIR" "$DATA_DIR" "$DATA_DIR/config"
sudo install -d -m 1733 "$DATA_DIR/queue"
sudo install -d -m 700 "$DATA_DIR/sent" "$DATA_DIR/logs"
sudo install -m 755 "$SCRIPT_DIR/Watcher.Uploader" "$SYS_DIR/Watcher.Uploader"
# 600 root — в файле токен регистрации/общий токен. Пишем через umask, а не
# chmod после: файл не должен ни мгновения существовать с правами по умолчанию.
sudo sh -c "umask 077; cat > '$SYS_DIR/Watcher.Uploader.json'" <<EOF
{
  "Uploader": {
    "ServerUrl": "$SERVER_URL",
    "EnrollToken": "$ENROLL_TOKEN",
    "ApiToken": "$API_TOKEN",
    "CredentialPath": "$DATA_DIR/agent/credential.json",
    "QueueDir": "$DATA_DIR/queue",
    "SentDir": "$DATA_DIR/sent",
    "LogDir": "$DATA_DIR/logs",
    "ConfigCachePath": "$DATA_DIR/config/capture-config.json",
    "FfmpegPath": "ffmpeg"
  }
}
EOF
sudo install -m 644 "$SCRIPT_DIR/systemd/watcher-uploader.service" /etc/systemd/system/watcher-uploader.service
sudo systemctl daemon-reload
sudo systemctl enable watcher-uploader.service >/dev/null 2>&1
sudo systemctl restart watcher-uploader.service

echo "=== Capture пользователя $(whoami) в $INSTALL_DIR ==="
systemctl --user stop watcher-capture.service 2>/dev/null || true
remove_user_uploader
mkdir -p "$INSTALL_DIR"
cp "$SCRIPT_DIR/Watcher.Capture.Linux" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/Watcher.Capture.Linux"
# Очередь и конфиг с сервера — общие (системные), логи — свои.
cat > "$INSTALL_DIR/Watcher.Capture.Linux.json" <<EOF
{
  "Capture": {
    "QueueDir": "$DATA_DIR/queue",
    "ConfigCachePath": "$DATA_DIR/config/capture-config.json"
  }
}
EOF

# Неотправленное из старой пользовательской очереди — в общую (до G1c).
if compgen -G "$OLD_DATA_DIR/queue/*.mp4" >/dev/null; then
  echo "Переношу неотправленные сегменты из $OLD_DATA_DIR/queue..."
  for f in "$OLD_DATA_DIR"/queue/*.mp4 "$OLD_DATA_DIR"/queue/*.mp4.json; do
    [ -e "$f" ] || continue
    mv -n "$f" "$DATA_DIR/queue/" && chmod 600 "$DATA_DIR/queue/$(basename "$f")"
  done
fi

mkdir -p "$UNIT_DIR"
cp "$SCRIPT_DIR/systemd/watcher-capture.service" "$UNIT_DIR/"
# Юнит в репозитории зашивает дефолтный путь (%h/.local/opt/watcher) —
# подставляем реальный INSTALL_DIR в копию в $UNIT_DIR, иначе нестандартный
# --install-dir даёт службу, которая падает с status=203/EXEC.
sed -i "s|%h/.local/opt/watcher|$INSTALL_DIR|g" "$UNIT_DIR/watcher-capture.service"
systemctl --user daemon-reload
systemctl --user enable --now watcher-capture.service

echo
echo "Готово. Проверить статус:"
echo "  systemctl --user status watcher-capture"
echo "  systemctl status watcher-uploader"
echo "  sudo journalctl -u watcher-uploader -f"
