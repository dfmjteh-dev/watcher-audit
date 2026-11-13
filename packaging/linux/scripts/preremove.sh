#!/bin/sh
# H3: перед удалением пакета watcher-agent. deb — "remove" / "upgrade",
# rpm — "0" (удаление) / "1" (обновление). Только при удалении: хуки
# PAM/bashrc ссылаются на файлы, которых после удаления не будет, — снимаем
# их, пока watcher-agent-setup ещё на месте.
set -e

case "${1:-}" in
  remove|0)
    /usr/sbin/watcher-agent-setup --unconfigure || true
    ;;
esac
exit 0
