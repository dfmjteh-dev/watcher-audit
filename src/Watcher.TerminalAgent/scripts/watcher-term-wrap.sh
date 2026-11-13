#!/bin/sh
# Фаза 8b (см. docs/roadmap.md) — захват полного вывода консольной/SSH-
# сессии через стандартный script(1), без подмены login shell'а и без PAM.
#
# Разворачивается руками на целевом хосте (install-скрипт — следующий
# проход, см. roadmap.md) тремя шагами:
#   1. Этот файл -> /etc/watcher-term-wrap.sh
#   2. Одна строка в /etc/profile.d/watcher-term-wrap.sh:
#        [ -f /etc/watcher-term-wrap.sh ] && . /etc/watcher-term-wrap.sh
#      (покрывает login shell — SSH интерактивная сессия, локальная консоль)
#   3. Та же строка в конец /etc/bash.bashrc (Debian/Ubuntu/Astra) и/или
#      /etc/bashrc (RHEL-семейство, РЕД ОС, ALT) — какие есть, R11
#      (покрывает НЕ-login интерактивный shell — `su <user>` без дефиса;
#      проверено живьём — без этого шага такой su не попадает под запись)
#   4. mkdir -p /var/lib/watcher-terminal-agent/raw-output && chmod 1777 (как
#      /tmp — разные непривилегированные пользователи пишут свои файлы)
#
# Пишутся все пользователи; кого не сохранять — решает сервер по конфигу
# группы (excludeUsers/allowUsers), см. install-terminal-agent.sh.
#
# fail-open на каждом шаге: нет tty / не интерактивно / нет script(1) / нет
# прав на запись в raw-output / script(1) не запустился — молча пропускаем,
# пользователь получает обычный shell без записи. Файл подключается из
# profile/bashrc, поэтому выход отсюда закрывает сессию: exit — только после
# того, как script(1) реально отработал сессию (есть .out), см. _wta_record.
# R10 (docs/code-review-2026-10.md): строка для JSON — экранируем \ и ",
# управляющие символы выкидываем. Без этого доменный логин winbind
# (CORP\ivanov — разделитель по умолчанию «\») давал невалидный JSON, и
# запись навсегда застревала в очереди.
_wta_json_str() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\000-\037'
}
# Имя файлов записи — только безопасные символы: сервер не принимает имя с
# «\» или «/», а логин бывает доменным (см. выше).
_wta_name_part() {
    printf '%s' "$1" | tr -c 'A-Za-z0-9._@-' '_'
}

# 0.14.3: --log-out/--log-timing есть только с util-linux 2.35; на EL8 (2.32),
# Ubuntu 20.04 (2.34) script падал на разборе опций, и exit ниже закрывал
# вход по SSH и с консоли. Там — классический синтаксис: .out позиционно,
# тайминг «<задержка> <байт>» в stderr script'а (его понимает termplayer.js;
# stderr shell'а внутри — pty, в тайминг не попадает).
#
# 0.14.4: где есть, тайминг — в расширенном формате (--logging-format advanced):
# в нём размер терминала (H COLUMNS/LINES) и каждое изменение размера окна
# (S SIGWINCH ROWS=… COLS=…) — без этого плеер рисовал mc/htop развёрнутого
# окна в размере начала сессии. termplayer.js и сервер (ActiveTime) понимают оба.
_wta_script() { # out timing
    _wta_help=$(script --help 2>&1)
    if printf '%s' "$_wta_help" | grep -q -- --logging-format; then
        script -qfe --logging-format advanced --log-out "$1" --log-timing "$2"
    elif printf '%s' "$_wta_help" | grep -q -- --log-out; then
        script -qfe --log-out "$1" --log-timing "$2"
    else
        script -qfe -t "$1" 2>"$2"
    fi
}

# Запись сессии в каталог $1. Возвращается (не выходит) только если запись
# не началась — тогда пользователь остаётся в обычном shell.
_wta_record() {
    _wta_dir=$1
    _wta_id="$(_wta_name_part "$USER")-$(date +%s)-$$"
    _wta_started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    if [ -d "$_wta_dir" ] && [ -w "$_wta_dir" ]; then
        _wta_umask=$(umask)
        # umask 077 — script(1) создаёт файлы с обычным umask вызывающего
        # (было 022 -> 644, т.е. ЛЮБОЙ локальный пользователь хоста мог
        # прочитать чужую запись сессии, вплоть до паролей, случайно
        # введённых не в то окно — найдено на ревью, не было в спайке).
        # У script нет флага для прав доступа к своим выходным файлам,
        # только umask решает. Восстанавливаем только если записи не вышло —
        # иначе процесс всё равно завершится через exit ниже.
        umask 077
        # 0.12: номер audit-сессии (тот же ses=, что у ввода из pam_tty_audit) —
        # по нему сервер склеивает вывод с вводом в одну сессию. 4294967295 —
        # audit-сессии нет (не через PAM sshd).
        _wta_ses=$(cat /proc/self/sessionid 2>/dev/null)
        case "$_wta_ses" in ''|*[!0-9]*|4294967295) _wta_ses=null ;; esac
        # 0.14.4: размер терминала на старте — старый script(1) (util-linux
        # < 2.35) не пишет его ни в .out, ни в тайминг; плеер берёт отсюда.
        _wta_size=$(stty size 2>/dev/null)
        _wta_rows=${_wta_size% *}; _wta_cols=${_wta_size#* }
        case "$_wta_rows" in ''|*[!0-9]*) _wta_rows=null ;; esac
        case "$_wta_cols" in ''|*[!0-9]*) _wta_cols=null ;; esac
        # .start — сессия идёт: агент шлёт её на сервер кусками, не дожидаясь
        # конца, а если shell убит (обрыв SSH — SIGHUP, .json ниже не успеет
        # записаться), закрывает запись сам, увидев, что процесса $$ больше нет.
        printf '{"kind":"terminal-output","host":"%s","sessionUser":"%s","startedAt":"%s","outFile":"%s.out","timingFile":"%s.timing","source":"linux-script","auditSession":%s,"pid":%s,"cols":%s,"rows":%s}\n' \
            "$(_wta_json_str "$(hostname)")" "$(_wta_json_str "$USER")" "$_wta_started" "$_wta_id" "$_wta_id" "$_wta_ses" "$$" "$_wta_cols" "$_wta_rows" > "$_wta_dir/$_wta_id.start"
        # Без позиционного аргумента: у script(1) он означает legacy-имя
        # output-файла, НЕ команду для запуска (грабля спайка — "script:
        # unexpected number of arguments" при попытке передать $SHELL
        # позиционно вместе с --log-out). Без -c script сам стартует
        # интерактивный $SHELL — это и есть обёртка.
        _wta_script "$_wta_dir/$_wta_id.out" "$_wta_dir/$_wta_id.timing"
        _wta_rc=$?
        if [ ! -s "$_wta_dir/$_wta_id.out" ]; then
            # script(1) не запустился (нет .out) — не закрываем сессию.
            rm -f "$_wta_dir/$_wta_id.start" "$_wta_dir/$_wta_id.out" "$_wta_dir/$_wta_id.timing"
            umask "$_wta_umask"
            return 0
        fi
        _wta_ended=$(date -u +%Y-%m-%dT%H:%M:%SZ)
        printf '{"kind":"terminal-output","host":"%s","sessionUser":"%s","startedAt":"%s","endedAt":"%s","exitCode":%s,"outFile":"%s.out","timingFile":"%s.timing","source":"linux-script","auditSession":%s,"cols":%s,"rows":%s}\n' \
            "$(_wta_json_str "$(hostname)")" "$(_wta_json_str "$USER")" "$_wta_started" "$_wta_ended" "$_wta_rc" "$_wta_id" "$_wta_id" "$_wta_ses" "$_wta_cols" "$_wta_rows" > "$_wta_dir/$_wta_id.json"
        exit "$_wta_rc"
    fi
}

if [ -z "$WATCHER_WRAPPED" ] && [ -t 0 ] && [ -n "$PS1" ] && command -v script >/dev/null 2>&1; then
    export WATCHER_WRAPPED=1
    _wta_record /var/lib/watcher-terminal-agent/raw-output
    unset WATCHER_WRAPPED
fi
