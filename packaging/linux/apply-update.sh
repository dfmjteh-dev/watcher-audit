#!/bin/sh
# H2 (roadmap-next.md): установка обновления агента из пакета — запускает
# watcher-uploader через systemd-run отдельным юнитом (LinuxPackageInstaller.cs),
# подпись пакета к этому моменту уже проверена. Итог — JSON в result-файл,
# uploader отправит его серверу следующим heartbeat.
#   apply-update.sh deb|rpm <пакет> <версия> <result.json>
set -u

kind="$1" pkg="$2" version="$3" result="$4"
log="$(dirname "$pkg")/apply.log"

case "$kind" in
  # apt-get, а не dpkg -i: ждёт блокировку (unattended-upgrades) и
  # доставляет новые зависимости; откат на старую версию разрешён.
  deb) DEBIAN_FRONTEND=noninteractive apt-get install -y -o DPkg::Lock::Timeout=300 --allow-downgrades "$pkg" >"$log" 2>&1 ;;
  rpm) rpm -U --oldpackage --replacepkgs "$pkg" >"$log" 2>&1 ;;
  *) echo "unknown package kind: $kind" >"$log"; false ;;
esac
rc=$?

if [ "$rc" -eq 0 ]; then
  ok=true message="installed"
else
  # Последняя строка лога — без кавычек, обратных слешей и управляющих символов.
  # \134 — обратный слеш.
  last="$(tail -n 1 "$log" | tr -d '"\134' | tr -c '[:print:]' ' ' | cut -c1-200)"
  ok=false message="$kind install exit $rc: $last"
fi
printf '{"version":"%s","ok":%s,"message":"%s","at":"%s"}\n' \
  "$version" "$ok" "$message" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$result"
exit "$rc"
