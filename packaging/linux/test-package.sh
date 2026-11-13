#!/usr/bin/env bash
# H3: проверка пакета в контейнере (CI, job linux-packages): установка,
# watcher-agent-setup, удаление — хуки PAM/bashrc и конфиги с токенами
# должны уйти вместе с пакетом. systemd в контейнере нет — службы не
# включаются, это проверяется живьём на станции (docs/development.md).
#
#   docker run --rm -v "$PWD:/src" debian:12 /src/packaging/linux/test-package.sh deb /src/dist/<файл>.deb
set -euo pipefail

KIND="$1"
PKG="$2"

fail() { echo "FAIL: $*" >&2; exit 1; }
step() { echo; echo "=== $* ==="; }

step "установка $PKG"
if [ "$KIND" = deb ]; then
  apt-get update -qq
  apt-get install -y -qq "$PKG" >/dev/null
else
  dnf install -y -q "$PKG" >/dev/null
fi

for f in /opt/watcher-capture/Watcher.Capture.Linux /opt/watcher-uploader/Watcher.Uploader \
         /opt/watcher-terminal-agent/Watcher.TerminalAgent /usr/sbin/watcher-agent-setup \
         /usr/lib/systemd/user/watcher-capture.service /usr/lib/systemd/system/watcher-uploader.service; do
  [ -e "$f" ] || fail "нет $f"
done
grep -q "ExecStart=/opt/watcher-capture/Watcher.Capture.Linux" /usr/lib/systemd/user/watcher-capture.service \
  || fail "user-юнит экрана указывает не на /opt/watcher-capture"
[ -f /opt/watcher-uploader/Watcher.Uploader.json ] && fail "пакет не должен ничего настраивать сам"

step "watcher-agent-setup"
[ -f /etc/pam.d/sshd ] || touch /etc/pam.d/sshd
watcher-agent-setup --server-url https://watcher.example --enroll-token wenr_test
[ "$(stat -c %a /opt/watcher-uploader/Watcher.Uploader.json)" = 600 ] || fail "Watcher.Uploader.json не 600"
[ "$(stat -c %a /opt/watcher-terminal-agent/Watcher.TerminalAgent.json)" = 600 ] || fail "Watcher.TerminalAgent.json не 600"
grep -q "pam_tty_audit.so enable=\*" /etc/pam.d/sshd || fail "нет строки pam_tty_audit для всех"
[ "$(stat -c %a /var/lib/watcher/queue)" = 1733 ] || fail "очередь не 1733"
[ -f /etc/watcher-term-wrap.sh ] || fail "нет обёртки вывода"

step "повторный запуск — идемпотентно"
# --users от прежних версий принимается и игнорируется.
watcher-agent-setup --server-url https://watcher.example --users alice
[ "$(grep -c pam_tty_audit /etc/pam.d/sshd)" = 1 ] || fail "строка PAM задвоилась"

step "--no-console снимает хуки консоли"
watcher-agent-setup --server-url https://watcher.example --no-console
grep -q watcher /etc/pam.d/sshd && fail "хук PAM остался после --no-console"
watcher-agent-setup --server-url https://watcher.example

step "удаление пакета"
if [ "$KIND" = deb ]; then apt-get remove -y -qq watcher-agent >/dev/null; else dnf remove -y -q watcher-agent >/dev/null; fi
grep -q watcher /etc/pam.d/sshd && fail "хук PAM остался после удаления"
for f in /etc/bash.bashrc /etc/bashrc; do
  [ -f "$f" ] && grep -q watcher "$f" && fail "хук в $f остался после удаления"
done
[ -e /etc/watcher-term-wrap.sh ] && fail "/etc/watcher-term-wrap.sh остался"
[ -e /opt/watcher-uploader/Watcher.Uploader.json ] && fail "конфиг с токеном остался"
[ -e /opt/watcher-terminal-agent/Watcher.TerminalAgent.json ] && fail "конфиг консоли с токеном остался"
[ -e /opt/watcher-uploader/Watcher.Uploader ] && fail "бинарник остался"
[ -d /var/lib/watcher/queue ] || fail "очередь (невыгруженные записи) удалять нельзя"

if [ "$KIND" = deb ]; then
  step "purge"
  apt-get purge -y -qq watcher-agent >/dev/null
  [ -e /opt/watcher-uploader ] && fail "/opt/watcher-uploader остался после purge"
fi

echo
echo "OK: $PKG"
