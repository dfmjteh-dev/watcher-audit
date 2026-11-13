#!/bin/sh
# H3: после установки/обновления пакета watcher-agent. Аргументы: deb —
# "configure <прежняя версия или пусто>", rpm — "1" (установка) / "2" (обновление).
set -e

upgrade=false
case "${1:-}" in
  configure) [ -n "${2:-}" ] && upgrade=true ;;
  2) upgrade=true ;;
esac

# Обёртку вывода консоли watcher-agent-setup копирует в /etc (её подключают
# shell'ы всех пользователей) — при обновлении пакета её тоже обновляем, если
# агент уже настроен, иначе на станции оставалась старая (до 0.12: без
# выгрузки идущих сессий кусками).
if [ "$upgrade" = true ] && [ -f /etc/watcher-term-wrap.sh ] && [ -f /usr/share/watcher-agent/watcher-term-wrap.sh ]; then
  cp /usr/share/watcher-agent/watcher-term-wrap.sh /etc/watcher-term-wrap.sh.new
  chmod 644 /etc/watcher-term-wrap.sh.new
  mv -f /etc/watcher-term-wrap.sh.new /etc/watcher-term-wrap.sh
  # 0.12.2: правило auditd для команд без терминала (ssh хост 'команда').
  bash /usr/share/watcher-agent/install-terminal-agent.sh --audit-rules || true
fi

if [ -d /run/systemd/system ]; then
  systemctl daemon-reload || true
  # Обновление: включённые службы перезапускаются на новые бинарники.
  # Экран (user-служба) подхватит новую версию со следующего входа пользователя.
  systemctl try-restart watcher-uploader.service watcher-terminal-agent.service || true
  systemctl try-restart 'watcher-capture-guard-*.service' || true
fi

if [ "$upgrade" = false ]; then
  echo "Watcher agent установлен, но не настроен. Настройка:"
  echo "  sudo watcher-agent-setup --server-url https://<сервер> --enroll-token <токен>"
fi
# Защита включена — новая версия GuardRepair в её каталог и лаунчер таймера
# (0.16: GuardRepair setup, бывший setup-guard.sh). Распаковка single-file —
# в закрытый каталог Guard, не в общий /tmp.
if [ -f /var/lib/watcher/guard/enabled ] && [ ! -L /var/lib/watcher/guard ]; then
  DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/watcher/guard/bundle /opt/watcher-guard/GuardRepair setup --refresh
fi
exit 0
