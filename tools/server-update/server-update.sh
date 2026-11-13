#!/usr/bin/env bash
# Помощник обновления сервера из веб-UI (src/Watcher.Server/ServerUpdate.cs).
# Запускается systemd-юнитом watcher-server-update.service от root, когда в
# $UPDATE_DIR/requests появляется заявка; ставит архив релиза тем же
# install-server.sh, что и при ручном обновлении.
#
# Сервер — непривилегированный (контейнер без docker.sock), поэтому всё, что
# идёт от него, здесь проверяется заново: id и пути, sha256 архива, что архив
# не пишет за пределы своей папки, версия в нём та же и не старше текущей.
# Подпись архива не проверяется (решение владельца) — права на кнопку только у admin.
#
# Автооткат: до установки — снимок БД (Watcher.Server --backup) и, для systemd,
# копия бинарника; после — проверка, что за VERIFY_SECONDS поднялась именно
# новая версия и отвечает /healthz. Не поднялась или install-server.sh упал —
# БД из снимка, прежний образ/бинарник, статус «не удалось, возвращена X».
set -uo pipefail

UPDATE_DIR="${UPDATE_DIR:-/var/lib/watcher-server-update}"
WORK_DIR="${WORK_DIR:-/var/tmp/watcher-server-update}"
STATUS="$UPDATE_DIR/status.json"
VERIFY_SECONDS="${VERIFY_SECONDS:-300}"
SYSTEMD_UNIT="${SYSTEMD_UNIT:-/etc/systemd/system/watcher-server.service}"
SYSTEMD_DIR=/opt/watcher
SYSTEMD_USER=watcher
KIT_DIR="${KIT_DIR:-/opt/watcher-kit}"

exec 9>"/run/watcher-server-update.lock"
flock -n 9 || exit 0

status() { # state message [exit_code]
  python3 - "$STATUS" "$ID" "$VERSION" "$1" "$2" "${3:-}" "$STARTED" <<'PY'
import json, os, sys, datetime
f, id_, ver, state, msg, code, started = sys.argv[1:]
now = datetime.datetime.now(datetime.timezone.utc).isoformat()
d = {"id": id_, "version": ver, "state": state, "message": msg, "startedAt": started,
     "updatedAt": now, "finishedAt": now if state in ("done", "failed") else None,
     "exitCode": int(code) if code else None}
tmp = f + ".tmp"
with open(tmp, "w") as fh: json.dump(d, fh, ensure_ascii=False)
os.chmod(tmp, 0o644)
os.replace(tmp, f)
PY
}

log() { echo "[$(date '+%F %T')] $*" >> "$LOG"; }

fail() { log "ОШИБКА: $1"; status failed "$1" "${2:-1}"; cleanup; }

cleanup() {
  rm -rf "${WORK_DIR:?}/$ID" "$UPDATE_DIR/incoming/$ID.tar.gz" "$UPDATE_DIR/incoming/$ID.tar.gz.part"
  rm -f "$REQ_PROCESSING"
}

# --- Как установлен сервер: как и install-server.sh — есть юнит → systemd. ----
detect_mode() {
  if [ -f "$SYSTEMD_UNIT" ]; then MODE=systemd; return; fi
  MODE=docker
  # Каталог compose — из меток работающего контейнера (из архива — /opt/watcher,
  # из репозитория — сам репозиторий).
  COMPOSE_DIR="$(docker ps -a --filter label=com.docker.compose.service=watcher-server \
    --format '{{.Label "com.docker.compose.project.working_dir"}}' | head -1)"
  [ -n "$COMPOSE_DIR" ] || COMPOSE_DIR=/opt/watcher
}

compose() { (cd "$COMPOSE_DIR" && docker compose "$@"); }

srv() { # аргументы Watcher.Server на работающем сервере
  if [ "$MODE" = docker ]; then compose exec -T watcher-server dotnet Watcher.Server.dll "$@"
  else (cd "$SYSTEMD_DIR" && runuser -u "$SYSTEMD_USER" -- env DOTNET_BUNDLE_EXTRACT_BASE_DIR="$SYSTEMD_DIR/.bundle" "$SYSTEMD_DIR/Watcher.Server" "$@")
  fi
}

# Снимок до установки. Без снимка не обновляем — откатиться было бы не на что.
snapshot() {
  RB_DIR="$UPDATE_DIR/rollback/$ID"
  # Держим только снимок последнего обновления — он же для отката вручную.
  rm -rf "$UPDATE_DIR/rollback" "${WORK_DIR:?}"/rollback-*; mkdir -p "$RB_DIR"
  if [ "$MODE" = docker ]; then
    chown -R 1654:1654 "$UPDATE_DIR/rollback"   # пишет пользователь app контейнера
    docker image inspect "watcher-server:$PREV" >/dev/null 2>&1 \
      || { log "Нет образа watcher-server:$PREV — откатиться будет не на что"; return 1; }
    srv --backup "/updates/rollback/$ID" >> "$LOG" 2>&1 || return 1
  else
    chown -R "$SYSTEMD_USER:$SYSTEMD_USER" "$UPDATE_DIR/rollback"
    mkdir -p "$WORK_DIR/rollback-$ID"
    cp -a "$SYSTEMD_DIR/Watcher.Server" "$SYSTEMD_DIR/wwwroot" "$SYSTEMD_DIR/appsettings.json" "$WORK_DIR/rollback-$ID/" || return 1
    srv --backup "$RB_DIR" >> "$LOG" 2>&1 || return 1
  fi
  # Комплект агентов: install-server.sh пересобирает его и при неудачной
  # установке — откат возвращает прежний (с правами: его читает сервер).
  if [ -d "$KIT_DIR" ]; then
    mkdir -p "$WORK_DIR/rollback-$ID"
    cp -a "$KIT_DIR" "$WORK_DIR/rollback-$ID/kit" || return 1
  fi
  RB_SNAPSHOT="$(find "$RB_DIR" -maxdepth 1 -type d -name 'watcher-backup-*' | head -1)"
  [ -n "$RB_SNAPSHOT" ] && [ -f "$RB_SNAPSHOT/manifest.json" ]
}

# Работает именно версия $1 и отвечает /healthz — ждём до VERIFY_SECONDS.
verify() {
  local want="$1" t=0 got
  while [ "$t" -lt "$VERIFY_SECONDS" ]; do
    got="$(srv --version 2>/dev/null | tail -1)"
    if [ "$got" = "$want" ] && srv --healthcheck >/dev/null 2>&1; then return 0; fi
    sleep 5; t=$((t + 5))
  done
  log "За ${VERIFY_SECONDS} с не поднялась версия $want (отвечает: «${got:-нет ответа}»)"
  return 1
}

restore_kit() {
  [ -d "$WORK_DIR/rollback-$ID/kit" ] || return 0
  # Тот же каталог, не замена: в Docker он смонтирован в контейнер (bind mount держит inode).
  find "$KIT_DIR" -mindepth 1 -delete && cp -a "$WORK_DIR/rollback-$ID/kit/." "$KIT_DIR/" \
    && chown --reference="$WORK_DIR/rollback-$ID/kit" "$KIT_DIR" && chmod --reference="$WORK_DIR/rollback-$ID/kit" "$KIT_DIR"
}

rollback() {
  log "Откат на $PREV: БД из снимка $(basename "$RB_SNAPSHOT"), комплект агентов — прежний"
  restore_kit || log "Не удалось вернуть комплект агентов — пересоберите: install-server.sh --rebuild-kit"
  if [ "$MODE" = docker ]; then
    compose stop watcher-server >> "$LOG" 2>&1
    local rel
    rel="/updates/rollback/$ID/$(basename "$RB_SNAPSHOT")"
    WATCHER_VERSION="$PREV" compose run --rm --no-deps watcher-server --restore "$rel" >> "$LOG" 2>&1 || return 1
    sed -i "s/^WATCHER_VERSION=.*/WATCHER_VERSION=$PREV/" "$COMPOSE_DIR/.env"
    WATCHER_VERSION="$PREV" compose up -d >> "$LOG" 2>&1 || return 1
  else
    systemctl stop watcher-server
    cp -a "$WORK_DIR/rollback-$ID/." "$SYSTEMD_DIR/" || return 1
    chown -R "$SYSTEMD_USER:$SYSTEMD_USER" "$SYSTEMD_DIR"
    srv --restore "$RB_SNAPSHOT" >> "$LOG" 2>&1 || return 1
    systemctl start watcher-server || return 1
  fi
  verify "$PREV"
}

process() { # путь к заявке
  local req="$1" archive sha version current
  ID="$(basename "$req" .json)"
  VERSION="?"; STARTED="$(date -u +%FT%TZ)"
  if ! [[ "$ID" =~ ^[a-z0-9]{1,64}$ ]]; then rm -f "$req"; return; fi
  mkdir -p "$UPDATE_DIR/logs" "$WORK_DIR"
  LOG="$UPDATE_DIR/logs/$ID.log"
  : > "$LOG"; chmod 644 "$LOG"
  # Заявку забираем сразу — повторный запуск юнита её больше не увидит.
  REQ_PROCESSING="$WORK_DIR/$ID.request.json"
  mv "$req" "$REQ_PROCESSING" || return

  read -r archive sha version current < <(python3 - "$REQ_PROCESSING" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
print(r.get("archive", ""), r.get("sha256", ""), r.get("version", ""), r.get("currentVersion", ""))
PY
  ) || { fail "заявка не читается"; return; }
  VERSION="$version"
  status running "проверка архива"
  log "Заявка $ID: версия $version (сейчас $current), архив $archive"

  [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { fail "неверная версия «$version»"; return; }
  [ "$archive" = "incoming/$ID.tar.gz" ] || { fail "неожиданный путь архива «$archive»"; return; }
  local file="$UPDATE_DIR/$archive"
  if [ ! -f "$file" ] || [ -L "$file" ]; then fail "архива нет"; return; fi
  [ "$(sha256sum "$file" | cut -d' ' -f1)" = "$sha" ] || { fail "sha256 архива не совпал с заявкой"; return; }
  if [[ "$current" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && [ "$(printf '%s\n%s\n' "$current" "$version" | sort -V | head -1)" != "$current" ]; then
    fail "версия $version старше текущей $current — откат не поддерживается"; return
  fi

  # Все записи — внутри watcher-server-$version/, без абсолютных путей и «..»,
  # только обычные файлы и каталоги (без ссылок и устройств).
  local top="watcher-server-$version"
  if ! python3 - "$file" "$top" <<'PY'
import sys, tarfile
f, top = sys.argv[1:]
with tarfile.open(f, "r:gz") as t:
    for m in t:
        n = m.name[2:] if m.name.startswith("./") else m.name
        if n in ("", "."):
            continue
        parts = n.split("/")
        if n.startswith("/") or ".." in parts or not (m.isfile() or m.isdir()) or parts[0] != top:
            sys.exit(1)
PY
  then
    fail "архив содержит пути вне $top/ или ссылки"; return
  fi

  status running "распаковка"
  local dest="$WORK_DIR/$ID"
  rm -rf "$dest"; mkdir -p "$dest"
  tar -xzf "$file" -C "$dest" --no-same-owner >> "$LOG" 2>&1 || { fail "не распаковался"; return; }
  [ "$(cat "$dest/$top/VERSION" 2>/dev/null)" = "$version" ] || { fail "VERSION в архиве не $version"; return; }
  [ -x "$dest/$top/install-server.sh" ] || chmod +x "$dest/$top/install-server.sh"

  detect_mode
  PREV="$current"
  status running "снимок БД для отката"
  log "Сервер: $MODE${COMPOSE_DIR:+ ($COMPOSE_DIR)}, снимок БД для отката на $PREV"
  if ! [[ "$PREV" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || ! snapshot; then
    fail "не удалось снять снимок БД для отката — обновление не начато"; return
  fi

  status running "установка (install-server.sh), сервер перезапустится"
  log "Запуск install-server.sh из $top"
  # Тот же способ установки, что уже стоит: install-server.sh определит сам.
  (cd "$dest/$top" && ./install-server.sh) >> "$LOG" 2>&1
  local rc=$? why="" code
  if [ $rc -ne 0 ]; then why="install-server.sh завершился с кодом $rc"
  else
    status running "проверка: поднялась ли $version"
    verify "$version" || why="версия $version не поднялась за ${VERIFY_SECONDS} с"
  fi
  if [ -n "$why" ]; then
    log "ОШИБКА: $why"
    code=$rc; [ "$code" -eq 0 ] && code=1
    status running "$why — откат на $PREV"
    if rollback; then
      fail "обновление до $version не удалось ($why), возвращена версия $PREV" "$code"
    else
      fail "обновление до $version не удалось ($why), и откат на $PREV ТОЖЕ не удался — нужен ручной разбор (docs/operations.md, «Откат вручную»)" "$code"
    fi
    return
  fi
  log "Готово: сервер $version"
  status "done" "сервер обновлён до $version" 0
  cleanup
  # Журналы — последние 10.
  find "$UPDATE_DIR/logs" -maxdepth 1 -name '*.log' -printf '%T@ %p\n' | sort -rn | tail -n +11 | cut -d' ' -f2- | xargs -r rm -f
}

shopt -s nullglob
# Сервер до 0.16 писал недописанную заявку прямо в requests/ (*.json.tmp) — юнит
# будится на ней раньше переименования. Ждём .json до 30 с, а не выходим сразу:
# иначе systemd перезапускал помощника по кругу и останавливал по лимиту.
for _ in $(seq 1 30); do
  reqs=("$UPDATE_DIR"/requests/*.json); tmps=("$UPDATE_DIR"/requests/*.tmp)
  [ ${#reqs[@]} -gt 0 ] || [ ${#tmps[@]} -eq 0 ] && break
  sleep 1
done
for req in "$UPDATE_DIR"/requests/*.json; do
  process "$req"
done
# Заявки, которые сервер не дописал (.tmp) или битые имена, не будят юнит вечно.
find "$UPDATE_DIR/requests" -maxdepth 1 -type f ! -name '*.json' -mmin +10 -delete 2>/dev/null || true
exit 0
