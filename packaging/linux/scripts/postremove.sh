#!/bin/sh
# H3: после удаления пакета watcher-agent. deb "purge" — убрать и
# сгенерированные конфиги с токенами (их пакет не знает). Очереди и учётки
# агента в /var/lib — не трогаем: там могут быть невыгруженные записи.
set -e

case "${1:-}" in
  purge)
    rm -f /opt/watcher-uploader/Watcher.Uploader.json /opt/watcher-terminal-agent/Watcher.TerminalAgent.json
    rmdir /opt/watcher-uploader /opt/watcher-terminal-agent /opt/watcher-capture 2>/dev/null || true
    echo "Данные Watcher остаются в /var/lib/watcher и /var/lib/watcher-terminal-agent — удалите вручную, если нужно."
    ;;
esac
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload || true
fi
exit 0
