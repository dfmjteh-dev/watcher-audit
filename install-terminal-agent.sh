#!/usr/bin/env bash
# Установка Watcher Terminal Agent (аудит ввода/вывода консольных и SSH-сессий,
# Фаза 8a+8b, roadmap.md) на Linux-станцию. Дополняет install-capture-linux.sh
# (захват экрана) — вместе они составляют единый Linux-агент, см.
# install-linux-agent.sh.
#
# В ОТЛИЧИЕ от install-capture-linux.sh — запускать ИМЕННО от root/sudo: этот
# скрипт правит системные файлы (/etc/pam.d/sshd, /etc/bash.bashrc или /etc/bashrc,
# /etc/profile.d/) и ставит системную (не per-user) systemd-службу, читающую
# /var/log/audit/audit.log (root/adm-only).
#
# Записываются все пользователи станции. Кого не писать — списки
# allowUsers/excludeUsers в конфиге группы/хоста на сервере (веб-UI), их
# применяет сервер при приёме записи (TerminalUserFilter); переустанавливать
# агента ради списка не нужно.
#
# Оба механизма fail-open по конструкции (см. docs/roadmap.md, Фаза 8/8b) —
# ни один баг/отсутствие зависимости здесь не может заблокировать login
# пользователя. Безопасно запускать повторно (идемпотентно, маркеры ниже).
set -euo pipefail

INSTALL_DIR="/opt/watcher-terminal-agent"
UNINSTALL=false
AUDIT_RULES_ONLY=false
SERVER_URL=""
ENROLL_TOKEN=""
CA_CERT=""
# H3: бинарник, appsettings.json и юнит уже разложил пакет (.deb/.rpm) —
# вызывается из watcher-agent-setup, сам ничего не копирует в $INSTALL_DIR и
# в /etc/systemd/system, при --uninstall не удаляет файлы пакета.
PACKAGED=false
MARKER_BEGIN="# BEGIN watcher-terminal-agent (managed by install-terminal-agent.sh, не редактировать руками)"
MARKER_END="# END watcher-terminal-agent"

while [ $# -gt 0 ]; do
  case "$1" in
    # До 0.12 — кого записывать; теперь пишутся все, фильтр — на сервере.
    # Принимается и игнорируется, чтобы старые команды установки не падали.
    --users) echo "ВНИМАНИЕ: --users больше не нужен и игнорируется — записываются все пользователи," >&2
             echo "кого не писать — excludeUsers/allowUsers в конфиге группы на сервере." >&2
             shift 2 ;;
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    --server-url) SERVER_URL="$2"; shift 2 ;;
    --enroll-token) ENROLL_TOKEN="$2"; shift 2 ;;
    --ca-cert) CA_CERT="$2"; shift 2 ;;
    --uninstall) UNINSTALL=true; shift ;;
    --packaged) PACKAGED=true; shift ;;
    # 0.12.2: только правило auditd для команд без терминала — из postinstall
    # пакета при обновлении уже настроенного агента.
    --audit-rules) AUDIT_RULES_ONLY=true; shift ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done

if [ "$(id -u)" != "0" ]; then
  echo "Запускайте от root/sudo — этот скрипт правит системные PAM/shell-" >&2
  echo "startup файлы и ставит системную службу, см. комментарий выше." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# R11 (docs/code-review-2026-10.md): общесистемный bashrc для не-login shell'а
# (`su user` без дефиса) называется по-разному: /etc/bash.bashrc — Debian,
# Ubuntu, Astra; /etc/bashrc — RHEL/Alma/Rocky, РЕД ОС, ALT. Правим все, что
# есть: строка-подключение безопасна при повторном чтении (WATCHER_WRAPPED в
# самой обёртке), а на RHEL /etc/bashrc читается из ~/.bashrc пользователя.
SYSTEM_BASHRCS=""
for f in /etc/bash.bashrc /etc/bashrc; do
  [ -f "$f" ] && SYSTEM_BASHRCS="$SYSTEM_BASHRCS $f"
done

# 0.12.2: запуск процессов (execve) для всех вошедших в систему — у сессии без
# терминала (ssh хост 'команда', scp, автоматизация) иначе не видно ничего:
# pam_tty_audit пишет только ввод с терминала. auid!=4294967295 — у процесса
# есть вошедший пользователь (демоны и службы без него не пишутся); сессии cron
# агент отбрасывает сам.
EXEC_RULES=/etc/audit/rules.d/watcher-exec.rules
EXEC_RULE_LINES="-a always,exit -F arch=b64 -S execve -F auid!=4294967295 -k watcher-exec
-a always,exit -F arch=b32 -S execve -F auid!=4294967295 -k watcher-exec"

load_audit_rules() {
  if command -v augenrules >/dev/null 2>&1; then augenrules --load >/dev/null 2>&1 && return 0; fi
  [ -f "$EXEC_RULES" ] && auditctl -R "$EXEC_RULES" >/dev/null 2>&1 && return 0
  return 1
}

install_exec_rules() {
  mkdir -p /etc/audit/rules.d
  {
    echo "# watcher-terminal-agent: запуск процессов в сессиях пользователей (install-terminal-agent.sh)"
    echo "$EXEC_RULE_LINES"
  } > "$EXEC_RULES"
  chmod 640 "$EXEC_RULES"
  if ! load_audit_rules; then
    echo "ВНИМАНИЕ: правила auditd не загрузились (правила заблокированы «-e 2»?) —" >&2
    echo "  команды без терминала начнут писаться после перезагрузки." >&2
  fi
}

remove_exec_rules() {
  [ -f "$EXEC_RULES" ] || return 0
  rm -f "$EXEC_RULES"
  if command -v augenrules >/dev/null 2>&1; then
    augenrules --load >/dev/null 2>&1 || true
  else
    echo "$EXEC_RULE_LINES" | while read -r rule; do
      # shellcheck disable=SC2086 # правило — несколько аргументов auditctl
      auditctl ${rule/-a /-d } >/dev/null 2>&1 || true
    done
  fi
}

remove_managed_block() {
  # Убирает ранее добавленный блок (идемпотентность при повторном запуске
  # и основа --uninstall) — работает, даже если блока там никогда не было.
  local file="$1"
  [ -f "$file" ] || return 0
  sed -i "/^${MARKER_BEGIN//\//\\/}\$/,/^${MARKER_END//\//\\/}\$/d" "$file"
}

if [ "$AUDIT_RULES_ONLY" = true ]; then
  install_exec_rules
  exit 0
fi

if [ "$UNINSTALL" = true ]; then
  echo "Останавливаю и снимаю службу..."
  systemctl stop watcher-terminal-agent.service 2>/dev/null || true
  systemctl disable watcher-terminal-agent.service 2>/dev/null || true
  rm -f /etc/systemd/system/watcher-terminal-agent.service
  systemctl daemon-reload 2>/dev/null || true
  remove_managed_block /etc/pam.d/sshd
  for f in /etc/bash.bashrc /etc/bashrc; do remove_managed_block "$f"; done
  remove_exec_rules
  rm -f /etc/profile.d/watcher-term-wrap.sh /etc/watcher-term-wrap.sh
  rm -rf /etc/watcher-term-wrap.d
  if [ "$PACKAGED" = true ]; then
    # Файлы пакета удаляет менеджер пакетов; конфиг с токенами — не его файл.
    rm -f "$INSTALL_DIR/Watcher.TerminalAgent.json"
  else
    rm -rf "$INSTALL_DIR"
  fi
  echo "Удалено. Данные в /var/lib/watcher-terminal-agent/ остаются — удалите вручную, если нужно."
  exit 0
fi

if ! command -v script >/dev/null 2>&1; then
  echo "script(1) не найден — установите util-linux (apt/dnf install util-linux)" >&2
  exit 1
fi

# R11: раньше — dpkg -l + apt-get, и на RHEL-семействе (set -e) установщик
# падал прямо здесь. Там auditd стоит по умолчанию (пакет audit).
if ! command -v auditd >/dev/null 2>&1 && [ ! -x /usr/sbin/auditd ] && [ ! -x /sbin/auditd ]; then
  echo "Устанавливаю auditd..."
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update -qq && apt-get install -y auditd >/dev/null
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y audit >/dev/null
  elif command -v yum >/dev/null 2>&1; then
    yum install -y audit >/dev/null
  else
    echo "auditd не найден, и нет ни apt-get, ни dnf/yum — установите auditd вручную." >&2
    exit 1
  fi
fi
systemctl enable --now auditd >/dev/null 2>&1 || true

# Останавливаем ДО копирования — найдено живым повторным запуском этого
# установщика: `cp` над бинарником работающей службы падает с "Text file
# busy" (ETXTBSY, ядро защищает исполняемый файл от truncate, пока он
# замаплен для исполнения). Служба поднимется заново в конце скрипта.
systemctl stop watcher-terminal-agent.service 2>/dev/null || true

if [ "$PACKAGED" = false ]; then
  echo "Копирую файлы в $INSTALL_DIR..."
  mkdir -p "$INSTALL_DIR"
  cp "$SCRIPT_DIR/Watcher.TerminalAgent" "$SCRIPT_DIR/appsettings.json" "$INSTALL_DIR/"
  chmod +x "$INSTALL_DIR/Watcher.TerminalAgent"
fi

if [ -n "$SERVER_URL" ]; then
  # B1 (roadmap-next.md): этот процесс сам себе Uploader (см. комментарий в
  # Program.cs про Watcher.TerminalAgent) — свой HttpClient, то же системное
  # хранилище сертификатов. CA экспортируется на сервере install-server.sh
  # --export-ca. Скрипт уже от root — sudo не нужен, в отличие от
  # install-capture-linux.sh.
  # H3: в пакетном режиме CA ставит сам watcher-agent-setup (один раз на оба агента).
  if [ -n "$CA_CERT" ] && [ "$PACKAGED" = false ]; then
    echo "Устанавливаю корневой CA сервера в системное хранилище..."
    cp "$CA_CERT" /usr/local/share/ca-certificates/watcher-server-ca.crt
    update-ca-certificates
  fi
  case "$SERVER_URL" in
    http://*)
      host="${SERVER_URL#http://}"; host="${host%%[:/]*}"
      if [ "$host" != "localhost" ] && [ "$host" != "127.0.0.1" ]; then
        echo "ПРЕДУПРЕЖДЕНИЕ: --server-url использует http:// на не-localhost адрес" >&2
        echo "($host) — консольный ввод/вывод и токены пойдут по сети открытым" >&2
        echo "текстом. Рекомендуется https:// (см. docs/install.md, 'HTTPS для агентов')." >&2
      fi
      ;;
  esac
  if [ -z "$ENROLL_TOKEN" ] && [ ! -f /var/lib/watcher-terminal-agent/agent/credential.json ]; then
    echo "ПРЕДУПРЕЖДЕНИЕ: не задан --enroll-token, и учётки агента" >&2
    echo "ещё нет — выгрузка на сервер работать не будет. Создайте токен регистрации" >&2
    echo "в веб-UI (Настройки → Агенты) и передайте --enroll-token." >&2
  fi
  echo "Настраиваю выгрузку на сервер ($SERVER_URL)..."
  # Задача G1c: EnrollToken — агент сам зарегистрируется (учётка вида terminal,
  # секрет в /var/lib/watcher-terminal-agent/agent/, 600 root). Файл пишется
  # под umask 077 — не существует ни мгновения с правами по умолчанию.
  ( umask 077; cat > "$INSTALL_DIR/Watcher.TerminalAgent.json" <<EOF
{
  "TerminalAgent": {
    "ServerUrl": "$SERVER_URL",
    "EnrollToken": "$ENROLL_TOKEN"
  }
}
EOF
  )
  # 600 — файл содержит токен регистрации; каталог root-owned, но без этого он был бы
  # читаем любым локальным пользователем хоста по дефолтному umask (та же
  # грабля, что нашли и закрыли для raw-output/*.out, см. roadmap.md).
  chmod 600 "$INSTALL_DIR/Watcher.TerminalAgent.json"
else
  # Идемпотентность: повторный запуск без --server-url должен выключать
  # выгрузку, а не оставлять конфиг от предыдущего запуска висеть молча.
  rm -f "$INSTALL_DIR/Watcher.TerminalAgent.json"
fi

echo "Настраиваю PAM (ввод, Фаза 8a)..."
remove_managed_block /etc/pam.d/sshd
cat >> /etc/pam.d/sshd <<EOF
$MARKER_BEGIN
session optional pam_tty_audit.so enable=*
$MARKER_END
EOF

echo "Настраиваю аудит запуска процессов (команды без терминала)..."
install_exec_rules

echo "Настраиваю shell-обёртку (вывод, Фаза 8b)..."
cp "$SCRIPT_DIR/watcher-term-wrap.sh" /etc/watcher-term-wrap.sh
chmod 644 /etc/watcher-term-wrap.sh
echo '[ -f /etc/watcher-term-wrap.sh ] && . /etc/watcher-term-wrap.sh' > /etc/profile.d/watcher-term-wrap.sh
chmod 644 /etc/profile.d/watcher-term-wrap.sh

if [ -z "$SYSTEM_BASHRCS" ]; then
  echo "ВНИМАНИЕ: нет ни /etc/bash.bashrc, ни /etc/bashrc — вывод «su <user>» без" >&2
  echo "дефиса (не-login shell) записываться не будет; SSH и консоль — будут." >&2
fi
for f in $SYSTEM_BASHRCS; do
  remove_managed_block "$f"
  cat >> "$f" <<EOF
$MARKER_BEGIN
[ -f /etc/watcher-term-wrap.sh ] && . /etc/watcher-term-wrap.sh
$MARKER_END
EOF
  echo "  подключено в $f"
done

# До 0.12 обёртка писала только пользователей из этого файла.
rm -rf /etc/watcher-term-wrap.d

echo "Создаю каталоги данных..."
mkdir -p /var/lib/watcher-terminal-agent/queue /var/lib/watcher-terminal-agent/raw-output /var/lib/watcher-terminal-agent/logs
# raw-output/sent — внутри 1777-каталога: его мог заранее создать (или
# подложить симлинком) любой пользователь, а агент переносит туда чужие
# записи сессий. Не root-овый — откладываем в сторону и создаём заново,
# агент без root-ового sent/ выгрузку вывода не делает (R5,
# docs/code-review-2026-10.md).
SENT_DIR=/var/lib/watcher-terminal-agent/raw-output/sent
if [ -L "$SENT_DIR" ] || { [ -e "$SENT_DIR" ] && [ "$(stat -c %u "$SENT_DIR")" != 0 ]; }; then
  echo "ВНИМАНИЕ: $SENT_DIR не принадлежит root — переименовываю в $SENT_DIR.untrusted.$(date +%s)"
  mv "$SENT_DIR" "$SENT_DIR.untrusted.$(date +%s)"
fi
mkdir -p "$SENT_DIR"
chmod 700 "$SENT_DIR"
chmod 1777 /var/lib/watcher-terminal-agent/raw-output
chmod 755 /var/lib/watcher-terminal-agent/queue /var/lib/watcher-terminal-agent/logs

echo "Устанавливаю systemd-службу коллектора..."
if [ "$PACKAGED" = true ]; then
  # Юнит пакета — в /usr/lib/systemd/system; копия от прежней установки
  # скриптом в /etc перекрывала бы его.
  rm -f /etc/systemd/system/watcher-terminal-agent.service
else
  cp "$SCRIPT_DIR/systemd/watcher-terminal-agent.service" /etc/systemd/system/
  # Юнит в репозитории зашивает дефолтный путь (/opt/watcher-terminal-agent) —
  # подставляем реальный INSTALL_DIR в копию в /etc/systemd/system/, иначе
  # нестандартный --install-dir даёт службу, которая падает с status=203/EXEC.
  sed -i "s|/opt/watcher-terminal-agent|$INSTALL_DIR|g" /etc/systemd/system/watcher-terminal-agent.service
fi
# H3: без запущенного systemd (контейнер, chroot сборки образа) — всё
# остальное уже настроено, службу включит первый загрузившийся systemd.
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  systemctl enable --now watcher-terminal-agent.service
else
  echo "systemd не запущен — служба watcher-terminal-agent не включена"
fi

echo
echo "Готово. Записываются все пользователи (кого не писать — конфиг группы на сервере)."
if [ -n "$SERVER_URL" ]; then
  echo "Выгрузка на сервер: включена ($SERVER_URL)"
else
  echo "Выгрузка на сервер: выключена (--server-url не задан) — данные только локально"
fi
echo "Проверить:"
echo "  systemctl status watcher-terminal-agent"
echo "  journalctl -u watcher-terminal-agent -f"
echo "  ls /var/lib/watcher-terminal-agent/queue/       # ввод, ещё не выгружен"
echo "  ls /var/lib/watcher-terminal-agent/queue/sent/  # ввод, выгружен"
echo "  ls /var/lib/watcher-terminal-agent/raw-output/       # вывод, ещё не выгружен"
echo "  ls /var/lib/watcher-terminal-agent/raw-output/sent/  # вывод, выгружен"
