#!/usr/bin/env bash
# Установка сервера Watcher в один шаг — сервер, HTTPS, ключи и комплект
# установки агентов (MSI, zip, .deb/.rpm, сертификаты, README с командами).
#
#   sudo ./install-server.sh [--domain watcher.corp] — Docker Compose (по умолчанию);
#     нет Docker — предложит поставить его из официального репозитория Docker
#   sudo ./install-server.sh --install-docker [...]   — то же без вопроса (для автоматизации)
#   sudo ./install-server.sh --systemd [--domain ...] — без Docker: systemd + Caddy ≥ 2.7
#     (из apt, а если там старее — из официального репозитория Caddy)
#   sudo ./install-server.sh --rebuild-kit [--new-token] — пересобрать комплект агентов
#   sudo ./install-server.sh --uninstall              — systemd: снять службу (данные остаются)
#   ./install-server.sh --export-ca [file]            — корневой CA сервера (для станций)
#   --trusted-proxy 10.20.30.5/32 (можно несколько) — внешний обратный прокси
#     (NPM) перед Watcher: адрес клиента берётся из его X-Forwarded-For
#
# Откуда: установочный архив релиза (watcher-server-X.Y.Z.tar.gz — готовые
# образы и агенты, конфигурация Docker в /opt/watcher) или копия репозитория
# (сборка из исходников, конфигурация в каталоге репозитория).
#
# Повторный запуск безопасен: существующие .env/appsettings.json, ключи и
# данные не перезаписываются, сервер обновляется до версии из этой копии
# репозитория, комплект агентов собирается заново.
#
# Всё самоподписанное выпускается автоматически и лежит в $KEYS_DIR (только root):
#   watcher-ca.crt, caddy-root.key     корневой CA HTTPS (его выпускает Caddy;
#                                       ключ — резервная копия на случай потери
#                                       тома caddy-data, docs/install.md)
#   update-key.pem, update-key.pass     ключ подписи пакетов автообновления агентов
#   watcher-codesign.pfx, codesign.pass самоподписанный сертификат подписи кода Windows
#   enroll-token                        токен регистрации, зашитый в комплект
# Веб-сервер (контейнер или служба) этого каталога не видит: взлом
# веб-приложения не даёт подписать пакет обновления для всего парка. Унести
# ключи офлайн — docs/install.md, «Ключи».
#
# Переменные окружения (необязательно):
#   KEYS_DIR     /root/watcher-keys
#   KIT_DIR      /opt/watcher-kit — комплект установки агентов
#   INSTALL_DIR  /opt/watcher (systemd), DATA_DIR $INSTALL_DIR/data,
#   SERVICE_USER www-data, SERVICE_PORT 5050, DOTNET /root/.dotnet/dotnet
#   EXPOSE_HTTP  false — true: systemd-сервер слушает 0.0.0.0 без TLS, без Caddy
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
KEYS_DIR="${KEYS_DIR:-/root/watcher-keys}"
KIT_DIR="${KIT_DIR:-/opt/watcher-kit}"
INSTALL_DIR="${INSTALL_DIR:-/opt/watcher}"
DATA_DIR="${DATA_DIR:-$INSTALL_DIR/data}"
SERVICE_USER="${SERVICE_USER:-www-data}"
SERVICE_PORT="${SERVICE_PORT:-5050}"
EXPOSE_HTTP="${EXPOSE_HTTP:-false}"
DOTNET="${DOTNET:-/root/.dotnet/dotnet}"
SERVICE_NAME="watcher-server"
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
CADDY_CA=/var/lib/caddy/.local/share/caddy/pki/authorities/local
# Установочный архив релиза (make-server-bundle.sh): готовые образы, агенты и
# бинарь сервера, исходников нет — ничего не компилируется, интернет не нужен
# (кроме установки Docker или Caddy, если их нет). Из репозитория — сборка из исходников.
BUNDLE=false
if [ -f "$SCRIPT_DIR/VERSION" ] && [ -d "$SCRIPT_DIR/images" ]; then BUNDLE=true; fi
if [ "$BUNDLE" = true ]; then
  VERSION="$(cat "$SCRIPT_DIR/VERSION")"
  # Архив каждой версии распаковывается в свой каталог, а Docker Compose
  # называет тома по каталогу проекта — compose, Caddyfile и .env живут в
  # постоянном месте, иначе обновление получило бы новые пустые тома.
  COMPOSE_DIR="${COMPOSE_DIR:-/opt/watcher}"
else
  VERSION="$(grep -oP '<ServerVersion>\K[^<]+' "$SCRIPT_DIR/Directory.Build.props")"
  COMPOSE_DIR="$SCRIPT_DIR"
fi

MODE="" DOMAIN="" TRUSTED="" ACTION=install NEW_TOKEN=false NO_KIT=false EXPORT_OUT="" INSTALL_DOCKER=false
while [ $# -gt 0 ]; do
  case "$1" in
    --systemd) MODE=systemd; shift ;;
    --docker) MODE=docker; shift ;;
    --install-docker) MODE=docker; INSTALL_DOCKER=true; shift ;;
    # Caddy разделяет адреса сайта запятой С ПРОБЕЛОМ («a.ru,10.0.0.5» — ошибка
    # конфига, Caddy не стартует) — нормализуем.
    --domain) DOMAIN="$(printf '%s' "$2" | sed -e 's/[[:space:]]*,[[:space:]]*/, /g' -e 's/^[[:space:]]*//;s/[[:space:]]*$//')"; shift 2 ;;
    # 0.14.4: адрес (CIDR) внешнего обратного прокси перед Watcher (NPM) — ему
    # верим X-Forwarded-For; можно несколько раз. Без флага — прежний список.
    --trusted-proxy) TRUSTED="${TRUSTED:+$TRUSTED }$2"; shift 2 ;;
    --rebuild-kit) ACTION=kit; shift ;;
    --new-token) NEW_TOKEN=true; shift ;;
    --no-kit) NO_KIT=true; shift ;;
    --uninstall) ACTION=uninstall; shift ;;
    --export-ca) ACTION=export-ca; EXPORT_OUT="${2:-./watcher-ca.crt}"; [ $# -ge 2 ] && shift; shift ;;
    -h|--help) sed -n '2,32p' "$0"; exit 0 ;;
    *) echo "Неизвестный аргумент: $1 (--help)" >&2; exit 1 ;;
  esac
done
# Уже стоит как служба — тот же режим; иначе Docker. Docker нет — не уходим
# молча в systemd (там другие требования, и человек не узнал бы, что выбран
# другой режим): ensure_docker объяснит и предложит поставить Docker.
if [ -z "$MODE" ]; then
  if [ -f "$UNIT_FILE" ]; then MODE=systemd; else MODE=docker; fi
fi

say() { printf '\n=== %s ===\n' "$*"; }
rand() { openssl rand -hex "${1:-16}"; }

compose() { (cd "$COMPOSE_DIR" && WATCHER_VERSION="$VERSION" docker compose "$@"); }

# Команда CLI сервера (Watcher.Server --create-enroll-token и т.п.) в его же
# окружении: в контейнере или от пользователя службы.
server_cli() {
  if [ "$MODE" = docker ]; then
    compose exec -T watcher-server dotnet Watcher.Server.dll "$@"
  else
    (cd "$INSTALL_DIR" && runuser -u "$SERVICE_USER" -- env DOTNET_BUNDLE_EXTRACT_BASE_DIR="$INSTALL_DIR/.bundle" \
      "$INSTALL_DIR/Watcher.Server" "$@")
  fi
}

# --- .env (Docker) и appsettings.json (systemd) ---------------------------------
# IANA-имя пояса хоста (Europe/Moscow); не узнать — UTC.
host_tz() {
  local z
  z="$(timedatectl show -p Timezone --value 2>/dev/null || true)"
  [ -n "$z" ] || z="$(cat /etc/timezone 2>/dev/null || true)"
  [ -n "$z" ] || z="$(readlink -f /etc/localtime 2>/dev/null | sed -n 's|.*/zoneinfo/||p')"
  [[ "$z" =~ ^[A-Za-z0-9_+/-]+$ ]] || z=UTC
  echo "$z"
}
env_get() { grep -E "^$1=" "$COMPOSE_DIR/.env" 2>/dev/null | tail -1 | cut -d= -f2- || true; }
env_set() {
  local f="$COMPOSE_DIR/.env"
  if grep -qE "^$1=" "$f"; then
    python3 - "$f" "$1" "$2" <<'PY'
import sys
f, k, v = sys.argv[1:]
lines = open(f).read().splitlines()
open(f, "w").write("\n".join(f"{k}={v}" if l.startswith(k + "=") else l for l in lines) + "\n")
PY
  else
    printf '%s=%s\n' "$1" "$2" >> "$f"
  fi
}
json_get() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("Server",{}).get(sys.argv[2],""))' "$INSTALL_DIR/appsettings.json" "$1"; }
json_set() {
  python3 - "$INSTALL_DIR/appsettings.json" "$1" "$2" <<'PY'
import json, sys
f, k, v = sys.argv[1:]
cfg = json.load(open(f))
cfg.setdefault("Server", {})[k] = {"true": True, "false": False}.get(v, v)
json.dump(cfg, open(f, "w"), indent=2)
PY
}

primary_ip() { ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") print $(i + 1)}' | head -1; }

# --- Ключи ----------------------------------------------------------------------
ensure_keys() {
  install -d -m 700 "$KEYS_DIR"
  if [ ! -f "$KEYS_DIR/update-key.pem" ]; then
    say "Ключ обновлений агентов"
    ( umask 077; rand 24 > "$KEYS_DIR/update-key.pass" )
    WATCHER_UPDATE_KEY_PASSWORD="$(cat "$KEYS_DIR/update-key.pass")" \
      "$SCRIPT_DIR/tools/update-signing/new-update-key.sh" --out "$KEYS_DIR" >/dev/null
  fi
  bash "$SCRIPT_DIR/tools/update-signing/ensure-maintenance-key.sh" "$KEYS_DIR" >/dev/null
  if [ ! -f "$KEYS_DIR/watcher-codesign.pfx" ]; then
    say "Сертификат подписи кода Windows (самоподписанный)"
    ( umask 077; rand 24 > "$KEYS_DIR/codesign.pass" )
    WATCHER_SIGN_PASSWORD="$(cat "$KEYS_DIR/codesign.pass")" \
      "$SCRIPT_DIR/tools/codesign/new-codesign-cert.sh" --name "Watcher ($(hostname -s))" --out "$KEYS_DIR" >/dev/null
  fi
  if [ ! -f "$KEYS_DIR/android-apk.p12" ]; then
    say "Ключ подписи APK Android-агента"
    "$SCRIPT_DIR/tools/android-signing/new-apk-key.sh" --name "Watcher ($(hostname -s))" --out "$KEYS_DIR"
  fi
  chmod 600 "$KEYS_DIR"/*.pem "$KEYS_DIR"/*.pass "$KEYS_DIR"/*.pfx "$KEYS_DIR"/*.p12 2>/dev/null || true
}

# Серверу доступны только отдельная пара обслуживания и пароль, read-only.
# Ключ издателя обновлений в этот каталог и контейнер не копируется.
MAINTENANCE_DIR=/var/lib/watcher-server-maintenance
install_maintenance_key() {
  local group="$1"
  install -d -m 750 -o root -g "$group" "$MAINTENANCE_DIR"
  install -m 640 -o root -g "$group" "$KEYS_DIR/maintenance-key.pem" "$MAINTENANCE_DIR/maintenance-key.pem"
  install -m 640 -o root -g "$group" "$KEYS_DIR/maintenance-key.pass" "$MAINTENANCE_DIR/maintenance-key.pass"
}

# Открытый ключ на сервере задан раньше вручную и не совпадает с ключом в
# $KEYS_DIR — сервер не меняем (станции доверяют прежнему), пакеты в комплекте
# этим ключом не подписать.
check_update_key() {
  local configured="$1" ours
  ours="$(tr -d '\n' < "$KEYS_DIR/update-key.pub.txt")"
  if [ -n "$configured" ] && [ "$configured" != "$ours" ]; then
    echo "ВНИМАНИЕ: на сервере другой ключ обновлений (выпущен до установки в один шаг)." >&2
    echo "  Пакеты автообновления в комплекте не будут загружены на сервер. Чтобы подписывать" >&2
    echo "  их автоматически, положите тот ключ как $KEYS_DIR/update-key.pem (+ пароль в" >&2
    echo "  update-key.pass, update-key.pub.txt) — docs/install.md, «Ключи»." >&2
    return 1
  fi
}

# --- Обновление из веб-UI (tools/server-update) ---------------------------------
# Помощник на хосте от root: сервер кладёт в $UPDATE_DIR архив и заявку,
# systemd-юнит будит server-update.sh, тот запускает install-server.sh из архива.
UPDATE_DIR=/var/lib/watcher-server-update
install_update_helper() { # владелец каталога заявок: uid контейнера или пользователь службы
  local owner="$1"
  command -v systemctl >/dev/null || { echo "Нет systemd — обновление из веб-интерфейса не настроено." >&2; return 0; }
  install -d -m 755 /opt/watcher-server-update
  install -m 755 "$SCRIPT_DIR/tools/server-update/server-update.sh" /opt/watcher-server-update/server-update.sh
  install -m 644 "$SCRIPT_DIR/tools/server-update/watcher-server-update.path" \
    "$SCRIPT_DIR/tools/server-update/watcher-server-update.service" /etc/systemd/system/
  install -d -m 750 -o "$owner" -g "$owner" "$UPDATE_DIR" "$UPDATE_DIR/requests" "$UPDATE_DIR/incoming"
  install -d -m 755 "$UPDATE_DIR/logs"
  systemctl daemon-reload
  systemctl enable --now watcher-server-update.path >/dev/null 2>&1 \
    || echo "Не удалось включить watcher-server-update.path — обновление из веб-интерфейса работать не будет." >&2
}

# --- Docker Engine и Caddy из официальных репозиториев ----------------------------
# Ключи репозиториев сверяются по отпечатку: подменённый ключ (зеркало, MITM)
# не попадёт в доверенные apt.
DOCKER_GPG_FPR=9DC858229FC7DD38854AE2D88D81803C0EBFCD88
CADDY_GPG_FPR=65760C51EDEA2017CEA2CA15155B6D79CA56EA34

# Поле /etc/os-release (ID, VERSION_CODENAME…) без исполнения файла (source/eval).
os_release() { sed -n "s/^$1=//p" /etc/os-release 2>/dev/null | head -1 | tr -d "\"'"; }

# Ключ репозитория: скачать, сверить отпечаток, положить в /etc/apt/keyrings.
fetch_repo_key() { # url fingerprint dest
  local tmp fpr
  command -v gpg >/dev/null || DEBIAN_FRONTEND=noninteractive apt-get install -y -qq gnupg >/dev/null
  tmp="$(mktemp)"
  curl -fsSL "$1" -o "$tmp" || { rm -f "$tmp"; echo "Не скачался ключ $1" >&2; return 1; }
  fpr="$(gpg --show-keys --with-colons "$tmp" 2>/dev/null | awk -F: '/^fpr/{print $10; exit}')"
  if [ "$fpr" != "$2" ]; then
    rm -f "$tmp"; echo "Отпечаток ключа $1 не совпал: $fpr вместо $2 — не продолжаю." >&2; return 1
  fi
  install -d -m 755 /etc/apt/keyrings
  install -m 644 "$tmp" "$3"; rm -f "$tmp"
}

# Docker Engine + плагин compose из download.docker.com (Debian, Ubuntu и
# производные). Пакет docker.io из Debian не годится: в нём нет плагина compose v2.
install_docker_engine() {
  local id like codename distro
  id="$(os_release ID)"; like="$(os_release ID_LIKE)"
  case " $id $like " in
    *" ubuntu "*) distro=ubuntu; codename="$(os_release UBUNTU_CODENAME)"; codename="${codename:-$(os_release VERSION_CODENAME)}" ;;
    *" debian "*) distro=debian; codename="$(os_release VERSION_CODENAME)" ;;
    *)
      echo "Автоустановка Docker умеет только Debian/Ubuntu (здесь: ${id:-?})." >&2
      echo "Поставьте Docker Engine с плагином compose по https://docs.docker.com/engine/install/" >&2
      echo "и запустите установщик снова." >&2
      return 1 ;;
  esac
  [ -n "$codename" ] || { echo "Не удалось определить версию дистрибутива для репозитория Docker" >&2; return 1; }
  say "Docker Engine из download.docker.com ($distro $codename)"
  DEBIAN_FRONTEND=noninteractive apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ca-certificates curl >/dev/null
  fetch_repo_key "https://download.docker.com/linux/$distro/gpg" "$DOCKER_GPG_FPR" /etc/apt/keyrings/docker.asc
  cat > /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/$distro
Suites: $codename
Components: stable
Signed-By: /etc/apt/keyrings/docker.asc
EOF
  DEBIAN_FRONTEND=noninteractive apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
  docker compose version
}

# Режим Docker, а Docker нет: объяснить и предложить поставить. Без терминала
# (автоматизация) — только с --install-docker, иначе понятная ошибка.
ensure_docker() {
  command -v docker >/dev/null && return 0
  if [ "$INSTALL_DOCKER" = false ]; then
    cat >&2 <<EOF

Docker не найден. Watcher по умолчанию работает в Docker Compose: сервер и
HTTPS-прокси — готовые образы из этого архива, агенты собираются в контейнере.

Варианты:
  1) поставить Docker Engine из официального репозитория Docker (нужен интернет):
       sudo $0 --install-docker ${DOMAIN:+--domain "$DOMAIN"}
  2) без Docker — systemd-служба и Caddy (Debian/Ubuntu, подробности в docs/install.md):
       sudo $0 --systemd ${DOMAIN:+--domain "$DOMAIN"}
EOF
    if [ -t 0 ]; then
      local answer
      read -r -p $'\nПоставить Docker сейчас (вариант 1)? [Y/n] ' answer
      case "${answer:-y}" in [YyДд]*) INSTALL_DOCKER=true ;; esac
    fi
    [ "$INSTALL_DOCKER" = true ] || exit 1
  fi
  install_docker_engine || exit 1
}

# Caddy для режима --systemd: Caddyfile пишет trusted_proxies/client_ip_headers
# (Caddy 2.7+); в Debian 12 пакет — 2.6.2, на нём Caddy не стартует. Если apt
# даёт старее 2.7 — официальный репозиторий Caddy (cloudsmith).
caddy_version_ok() { caddy version 2>/dev/null | awk '{sub(/^v/, "", $1); split($1, v, "."); exit !(v[1] > 2 || (v[1] == 2 && v[2] >= 7))}'; }
ensure_caddy() {
  caddy_version_ok && return 0
  local candidate
  candidate="$(apt-cache policy caddy 2>/dev/null | awk '/Candidate:/{print $2}')"
  if ! printf '%s\n' "$candidate" | awk -F'[.-]' '{exit !($1 > 2 || ($1 == 2 && $2 >= 7))}'; then
    say "Caddy из официального репозитория (в apt — ${candidate:-нет}, нужен 2.7+)"
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ca-certificates curl >/dev/null
    fetch_repo_key https://dl.cloudsmith.io/public/caddy/stable/gpg.key "$CADDY_GPG_FPR" /etc/apt/keyrings/caddy-stable.asc
    echo "deb [signed-by=/etc/apt/keyrings/caddy-stable.asc] https://dl.cloudsmith.io/public/caddy/stable/deb/debian any-version main" \
      > /etc/apt/sources.list.d/caddy-stable.list
    DEBIAN_FRONTEND=noninteractive apt-get update -qq
  fi
  DEBIAN_FRONTEND=noninteractive apt-get install -y caddy
  caddy_version_ok || { echo "Caddy $(caddy version 2>/dev/null) — нужен 2.7 или новее" >&2; return 1; }
}

# --- Docker Compose -------------------------------------------------------------
install_docker() {
  ensure_docker
  if ! { command -v docker >/dev/null && docker compose version >/dev/null 2>&1; }; then
    echo "Нужен Docker с плагином compose — команда «docker compose» (через пробел), либо --systemd." >&2
    if command -v docker-compose >/dev/null; then
      echo "Найден старый docker-compose (v1) — он не подходит. Плагин v2 без переустановки Docker:" >&2
      echo "  mkdir -p /usr/local/lib/docker/cli-plugins && cd /usr/local/lib/docker/cli-plugins" >&2
      echo "  curl -fSL -o docker-compose https://github.com/docker/compose/releases/download/v2.29.7/docker-compose-linux-x86_64" >&2
      echo "  chmod +x docker-compose && docker compose version" >&2
    fi
    echo "Подробно — docs/install.md, «Сервер»." >&2
    exit 1
  fi
  ensure_keys
  if [ "$BUNDLE" = true ]; then
    say "Образы из архива ($VERSION) → $COMPOSE_DIR"
    install -d -m 755 "$COMPOSE_DIR"
    local f
    for f in docker-compose.yml Caddyfile .env.example; do
      # Свой Caddyfile (свой сертификат и т.п.) не теряется молча — копия рядом.
      if [ -f "$COMPOSE_DIR/$f" ] && ! cmp -s "$SCRIPT_DIR/$f" "$COMPOSE_DIR/$f" && [ "$f" = Caddyfile ]; then
        cp "$COMPOSE_DIR/$f" "$COMPOSE_DIR/$f.bak-$(date +%F-%H%M)"
        echo "Caddyfile заменён версией из архива, прежний — $COMPOSE_DIR/$f.bak-…"
      fi
      cp "$SCRIPT_DIR/$f" "$COMPOSE_DIR/$f"
    done
    for f in "$SCRIPT_DIR"/images/*.tar.gz; do docker load -q -i "$f"; done
  fi
  # uid 1654 — пользователь app в образе mcr.microsoft.com/dotnet/aspnet.
  install_update_helper 1654
  local env="$COMPOSE_DIR/.env" first=false
  if [ ! -f "$env" ]; then
    say "Конфигурация (.env)"
    first=true
    ( umask 077; cp "$COMPOSE_DIR/.env.example" "$env" )
    ADMIN_PASSWORD="$(rand 12)"
    env_set Server__AdminPassword "$ADMIN_PASSWORD"
    env_set WATCHER_DOMAIN "${DOMAIN:-$(primary_ip)}"
    env_set Server__UpdatePublicKey "$(tr -d '\n' < "$KEYS_DIR/update-key.pub.txt")"
  else
    [ -z "$DOMAIN" ] || env_set WATCHER_DOMAIN "$DOMAIN"
    if [ -z "$(env_get Server__UpdatePublicKey)" ]; then
      env_set Server__UpdatePublicKey "$(tr -d '\n' < "$KEYS_DIR/update-key.pub.txt")"
    fi
  fi
  local maintenance_directory
  maintenance_directory="$(env_get WATCHER_MAINTENANCE_DIR)"
  MAINTENANCE_DIR="${maintenance_directory:-$MAINTENANCE_DIR}"
  if [ -z "$(env_get Server__MaintenanceSigningKeyPath)" ]; then
    install_maintenance_key 1654
    env_set Server__MaintenanceSigningKeyPath /maintenance/maintenance-key.pem
    env_set Server__MaintenanceKeyPasswordFile /maintenance/maintenance-key.pass
  fi
  env_set WATCHER_MAINTENANCE_DIR "$MAINTENANCE_DIR"
  env_set WATCHER_DEFAULT_SNI "$(env_get WATCHER_DOMAIN | cut -d, -f1 | tr -d ' ')"
  [ -z "$TRUSTED" ] || env_set WATCHER_TRUSTED_PROXIES "$TRUSTED"
  env_set WATCHER_KIT_DIR "$KIT_DIR"
  # 0.14.4: часовой пояс хоста — в контейнер (время в UI и отчётах — сервера).
  # Каждый запуск: сменили пояс хоста — переустановка/обновление подхватит.
  env_set WATCHER_TZ "$(host_tz)"
  chmod 600 "$env"

  env_set WATCHER_VERSION "$VERSION"
  if [ "$BUNDLE" = true ]; then
    say "Сервер $VERSION (docker compose up)"
    compose up -d --no-build
  else
    say "Сервер $VERSION (docker compose up --build)"
    compose up -d --build
  fi

  say "Жду готовности сервера и HTTPS"
  for _ in $(seq 1 60); do
    compose exec -T watcher-server dotnet Watcher.Server.dll --healthcheck >/dev/null 2>&1 && break
    sleep 2
  done
  for _ in $(seq 1 60); do
    compose exec -T watcher-proxy test -f /data/caddy/pki/authorities/local/root.crt 2>/dev/null && break
    sleep 2
  done
  compose exec -T watcher-proxy cat /data/caddy/pki/authorities/local/root.crt > "$KEYS_DIR/watcher-ca.crt"
  ( umask 077; compose exec -T watcher-proxy cat /data/caddy/pki/authorities/local/root.key > "$KEYS_DIR/caddy-root.key" )
  [ -s "$KEYS_DIR/watcher-ca.crt" ] || { echo "Caddy не выпустил корневой CA — docker compose logs watcher-proxy" >&2; exit 1; }

  local first_domain
  first_domain="$(env_get WATCHER_DOMAIN | cut -d, -f1 | tr -d ' ')"
  SERVER_URL="https://$first_domain"
  UPDATE_KEY_OK=true
  check_update_key "$(env_get Server__UpdatePublicKey)" || UPDATE_KEY_OK=false
  FIRST_INSTALL=$first
}

# --- systemd --------------------------------------------------------------------
install_kit_deps() {
  local missing=()
  for t in wixl:wixl msibuild:msitools osslsigncode:osslsigncode apksigner:apksigner zipalign:zipalign zip:zip unzip:unzip python3:python3; do
    command -v "${t%%:*}" >/dev/null || missing+=("${t#*:}")
  done
  if [ ${#missing[@]} -gt 0 ]; then
    command -v apt-get >/dev/null || { echo "Нужны пакеты: ${missing[*]}" >&2; return 1; }
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${missing[@]}"
  fi
  # Из архива .deb/.rpm уже собраны — nfpm не нужен.
  if [ "$BUNDLE" = false ] && ! command -v nfpm >/dev/null; then
    local v sha
    v="$(grep -oP 'ARG NFPM_VERSION=\K.*' "$SCRIPT_DIR/tools/agent-kit/Dockerfile")"
    sha="$(grep -oP 'ARG NFPM_SHA256=\K.*' "$SCRIPT_DIR/tools/agent-kit/Dockerfile")"
    curl -fsSLo /tmp/nfpm.tgz "https://github.com/goreleaser/nfpm/releases/download/v${v}/nfpm_${v}_Linux_x86_64.tar.gz"
    echo "$sha  /tmp/nfpm.tgz" | sha256sum -c --quiet
    tar -xzf /tmp/nfpm.tgz -C /usr/local/bin nfpm && rm /tmp/nfpm.tgz
  fi
}

install_systemd() {
  id "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
  ensure_keys

  local pub
  if [ "$BUNDLE" = true ]; then
    say "Сервер $VERSION (из архива)"
    pub="$SCRIPT_DIR/server"
  else
    say "Сервер $VERSION (dotnet publish)"
    "$DOTNET" publish "$SCRIPT_DIR/src/Watcher.Server/Watcher.Server.csproj" -c Release -r linux-x64 --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v quiet --nologo
    pub="$SCRIPT_DIR/src/Watcher.Server/bin/Release/net8.0/linux-x64/publish"
  fi

  # B3: по умолчанию только localhost — снаружи HTTPS через Caddy. Адрес — в
  # appsettings.json: Kestrel:Endpoints побеждает ASPNETCORE_URLS.
  local bind=127.0.0.1
  [ "$EXPOSE_HTTP" = true ] && bind=0.0.0.0
  mkdir -p "$INSTALL_DIR" "$DATA_DIR" "$INSTALL_DIR/wwwroot" "$INSTALL_DIR/.bundle"
  systemctl stop "$SERVICE_NAME" 2>/dev/null || true
  install -m 755 "$pub/Watcher.Server" "$INSTALL_DIR/Watcher.Server"
  # ffmpeg для пережатия записей (Server:Transcode:FfmpegPath) — закреплённая
  # статическая сборка: из архива или скачивается (tools/fetch-ffmpeg-linux.sh).
  if [ -f "$pub/ffmpeg" ]; then install -m 755 "$pub/ffmpeg" "$INSTALL_DIR/ffmpeg"
  else "$SCRIPT_DIR/tools/fetch-ffmpeg-linux.sh" "$INSTALL_DIR" || echo "ffmpeg не скачан — пережатие записей работать не будет" >&2; fi
  cp -r "$pub/wwwroot/." "$INSTALL_DIR/wwwroot/"
  FIRST_INSTALL=false
  if [ ! -f "$INSTALL_DIR/appsettings.json" ]; then
    FIRST_INSTALL=true
    ADMIN_PASSWORD="$(rand 12)"
    python3 - "$pub/appsettings.json" "$INSTALL_DIR/appsettings.json" "$DATA_DIR" "$ADMIN_PASSWORD" "$bind" "$SERVICE_PORT" <<'PY'
import json, sys
src, dst, datadir, pw, bind_addr, port = sys.argv[1:]
cfg = json.load(open(src))
s = cfg.setdefault("Server", {})
s.update(DataDir=datadir, DbPath=datadir + "/watcher.db", AdminUser="admin", AdminPassword=pw)
cfg.setdefault("Kestrel", {}).setdefault("Endpoints", {}).setdefault("Http", {})["Url"] = f"http://{bind_addr}:{port}"
json.dump(cfg, open(dst, "w"), indent=2)
PY
  fi
  [ -n "$(json_get UpdatePublicKey)" ] || json_set UpdatePublicKey "$(tr -d '\n' < "$KEYS_DIR/update-key.pub.txt")"
  if [ -z "$(json_get MaintenanceSigningKeyPath)" ]; then
    install_maintenance_key "$SERVICE_USER"
    json_set MaintenanceSigningKeyPath "$MAINTENANCE_DIR/maintenance-key.pem"
    json_set MaintenanceKeyPasswordFile "$MAINTENANCE_DIR/maintenance-key.pass"
  fi
  [ -n "$(json_get AgentKitDir)" ] || json_set AgentKitDir "$KIT_DIR"
  install_update_helper "$SERVICE_USER"
  python3 - "$INSTALL_DIR/appsettings.json" "$UPDATE_DIR" <<'PY'
import json, sys
f, d = sys.argv[1:]
cfg = json.load(open(f))
cfg.setdefault("Server", {}).setdefault("ServerUpdate", {}).setdefault("Dir", d)
cfg["Server"].setdefault("Transcode", {}).setdefault("FfmpegPath", "/opt/watcher/ffmpeg")
json.dump(cfg, open(f, "w"), indent=2)
PY
  chmod 640 "$INSTALL_DIR/appsettings.json"
  chown -R "$SERVICE_USER":"$SERVICE_USER" "$INSTALL_DIR" "$DATA_DIR"

  cat > "$UNIT_FILE" <<UNIT
[Unit]
Description=Watcher Server
After=network.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/Watcher.Server
Restart=on-failure
RestartSec=5
StandardOutput=append:/var/log/watcher-server.log
StandardError=append:/var/log/watcher-server.log
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://$bind:$SERVICE_PORT
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$INSTALL_DIR/.bundle
ProtectSystem=full
ProtectHome=true
NoNewPrivileges=true
CapabilityBoundingSet=

[Install]
WantedBy=multi-user.target
UNIT
  touch /var/log/watcher-server.log
  chown "$SERVICE_USER":"$SERVICE_USER" /var/log/watcher-server.log
  # copytruncate: StandardOutput=append держит дескриптор через rename.
  cat > /etc/logrotate.d/watcher-server <<'ROTATE'
/var/log/watcher-server.log {
    weekly
    rotate 8
    maxsize 50M
    missingok
    notifempty
    compress
    delaycompress
    copytruncate
}
ROTATE
  systemctl daemon-reload
  systemctl enable --now "$SERVICE_NAME"
  systemctl restart "$SERVICE_NAME"

  if [ "$EXPOSE_HTTP" = true ]; then
    echo "EXPOSE_HTTP=true — сервер слушает 0.0.0.0:$SERVICE_PORT БЕЗ TLS, Caddy не настраивается," >&2
    echo "комплект агентов не собирается (агенты без HTTPS — docs/install.md)." >&2
    SERVER_URL="" NO_KIT=true
    return
  fi

  say "HTTPS: Caddy (tls internal)"
  if command -v apt-get >/dev/null; then
    ensure_caddy || exit 1
  elif ! caddy_version_ok; then
    echo "Нужен Caddy 2.7 или новее: https://caddyserver.com/docs/install" >&2; exit 1
  fi
  local domain="${DOMAIN:-}"
  if [ -z "$domain" ] && [ -f /etc/caddy/Caddyfile ] && grep -q "watcher-server: install-server.sh" /etc/caddy/Caddyfile; then
    domain="$(sed -n 's/^# domain: //p' /etc/caddy/Caddyfile)"
  fi
  domain="${domain:-$(primary_ip)}"
  local trusted="${TRUSTED:-}"
  if [ -z "$trusted" ] && [ -f /etc/caddy/Caddyfile ] && grep -q "watcher-server: install-server.sh" /etc/caddy/Caddyfile; then
    trusted="$(sed -n 's/^# trusted: //p' /etc/caddy/Caddyfile)"
  fi
  trusted="${trusted:-127.0.0.1/32}"
  # Чужой Caddyfile (не пакетный по умолчанию и не наш) не трогаем.
  if [ -f /etc/caddy/Caddyfile ] && ! grep -q "watcher-server: install-server.sh" /etc/caddy/Caddyfile \
     && ! grep -q "file_server" /etc/caddy/Caddyfile; then
    echo "В /etc/caddy/Caddyfile чужая конфигурация — не трогаю. Добавьте сайт Watcher по" >&2
    echo "Caddyfile.systemd-example и перезапустите: sudo ./install-server.sh --rebuild-kit" >&2
  else
    {
      echo "# watcher-server: install-server.sh — перезаписывается при переустановке"
      echo "# domain: $domain"
      echo "# trusted: $trusted"
      grep -v '^[[:space:]]*#' "$SCRIPT_DIR/Caddyfile.systemd-example" \
        | sed -e "s|{\$WATCHER_DOMAIN:localhost}|$domain|" -e "s|{\$WATCHER_DEFAULT_SNI:localhost}|${domain%%,*}|" \
              -e "s|{\$WATCHER_TRUSTED_PROXIES:127.0.0.1/32}|$trusted|"
    } > /etc/caddy/Caddyfile
    systemctl enable caddy >/dev/null 2>&1 || true
    systemctl restart caddy
  fi
  for _ in $(seq 1 30); do [ -f "$CADDY_CA/root.crt" ] && break; sleep 2; done
  [ -f "$CADDY_CA/root.crt" ] || { echo "Caddy не выпустил корневой CA ($CADDY_CA) — journalctl -u caddy" >&2; exit 1; }
  cp "$CADDY_CA/root.crt" "$KEYS_DIR/watcher-ca.crt"
  install -m 600 "$CADDY_CA/root.key" "$KEYS_DIR/caddy-root.key"
  SERVER_URL="https://$(echo "$domain" | cut -d, -f1 | tr -d ' ')"
  UPDATE_KEY_OK=true
  check_update_key "$(json_get UpdatePublicKey)" || UPDATE_KEY_OK=false
}

# --- Комплект агентов -------------------------------------------------------------
enroll_token() {
  local f="$KEYS_DIR/enroll-token" token expires
  if [ "$NEW_TOKEN" = false ] && [ -f "$f" ]; then
    read -r token expires < "$f"
    # Ещё месяц действует — оставляем: станции из прежнего комплекта тот же токен.
    if [ "$(date -d "$expires" +%s)" -gt "$(date -d '+30 days' +%s)" ]; then
      echo "$token"
      return
    fi
  fi
  token="$(server_cli --create-enroll-token "install-kit-$(date +%F-%H%M)" --days 365 | tail -1)"
  case "$token" in wenr_*) ;; *) echo "Не удалось выпустить токен регистрации" >&2; exit 1 ;; esac
  ( umask 077; printf '%s %s\n' "$token" "$(date -d '+365 days' +%F)" > "$f" )
  echo "$token"
}

# 2 — пакет этой версии на сервере уже есть, но собран раньше (zip и подписи
# от сборки к сборке побайтно разные): прежний подписан тем же ключом и
# годится, его и оставляем.
import_failed() {
  if [ "$3" = 2 ]; then
    echo "Пакет $1 $2 уже на сервере (прежняя сборка той же версии) — оставляю его."
  else
    echo "ВНИМАНИЕ: пакет $1 $2 не загружен на сервер — загрузите вручную (Настройки → Обновления)." >&2
  fi
}

build_kit() {
  if [ -z "${SERVER_URL:-}" ] || [[ "$SERVER_URL" == https://localhost* ]]; then
    echo "Адрес сервера для станций не задан (WATCHER_DOMAIN=localhost) — укажите --domain." >&2
    return 1
  fi
  say "Комплект агентов для $SERVER_URL (несколько минут)"
  if [ "${UPDATE_KEY_OK:-false}" != true ]; then echo 'Комплект не заменён: нужен прежний ключ издателя, совпадающий с сервером' >&2; return 1; fi
  local token tmp maintenance_key
  maintenance_key="$(server_cli --maintenance-public-key)" || { echo 'Не удалось прочитать активный ключ обслуживания. Сначала обновите сервер обычным запуском install-server.sh; прежний комплект сохранён.' >&2; return 1; }
  [[ "$maintenance_key" =~ ^[A-Za-z0-9+/=]+$ ]] || { echo 'Invalid maintenance public key' >&2; return 1; }
  token="$(enroll_token)" || return 1
  tmp="$(mktemp -d /var/tmp/watcher-kit.XXXXXX)"
  # build_kit зовётся через «|| …» — set -e внутри не действует, ошибки
  # проверяются явно: прежний комплект остаётся, если новый не собрался.
  if command -v docker >/dev/null && [ "$BUNDLE" = true ]; then
    # Готовые агенты из архива: только подпись, MSI и сборка комплекта.
    docker image inspect "watcher-kit-tools:$VERSION" >/dev/null 2>&1 \
      || docker load -q -i "$SCRIPT_DIR/images/watcher-kit-tools.tar.gz" >/dev/null
    docker run --rm -v "$SCRIPT_DIR:/src:ro" -v "$KEYS_DIR:/keys:ro" -v "$tmp:/out" "watcher-kit-tools:$VERSION" \
      /src/tools/agent-kit/build-kit.sh --server-url "$SERVER_URL" --enroll-token "$token" --maintenance-key "$maintenance_key" --keys /keys \
      --agents /src/agents --out /out/kit || { rm -rf "$tmp"; return 1; }
  elif command -v docker >/dev/null; then
    docker build -q -t watcher-agent-toolchain "$SCRIPT_DIR/tools/agent-kit" >/dev/null || { rm -rf "$tmp"; return 1; }
    docker run --rm -v "$SCRIPT_DIR:/src" -v "$KEYS_DIR:/keys:ro" -v "$tmp:/out" \
      -v watcher-agent-nuget:/root/.nuget/packages watcher-agent-toolchain \
      /src/tools/agent-kit/build-kit.sh --server-url "$SERVER_URL" --enroll-token "$token" --maintenance-key "$maintenance_key" --keys /keys --out /out/kit \
      || { rm -rf "$tmp"; return 1; }
  else
    install_kit_deps || { rm -rf "$tmp"; return 1; }
    local agents=()
    [ "$BUNDLE" = false ] || agents=(--agents "$SCRIPT_DIR/agents")
    DOTNET="$DOTNET" "$SCRIPT_DIR/tools/agent-kit/build-kit.sh" --server-url "$SERVER_URL" \
      --enroll-token "$token" --maintenance-key "$maintenance_key" --keys "$KEYS_DIR" --out "$tmp/kit" "${agents[@]}" || { rm -rf "$tmp"; return 1; }
  fi
  # Тот же каталог, а не переименование: в Docker он смонтирован в контейнер
  # сервера (/kit, страница «Установка агентов»), bind mount держит inode.
  mkdir -p "$KIT_DIR"
  find "$KIT_DIR" -mindepth 1 -delete
  mv "$tmp/kit"/* "$KIT_DIR"/ || return 1
  rm -rf "$tmp"
  # Читает root и сервер (раздаёт администраторам в веб-UI); в комплекте токен
  # регистрации — остальным закрыто.
  local gid
  # Контейнер не отвечает (перезапускается) — uid/gid пользователя app образа
  # aspnet известен; без этого каталог остался бы root:root и сервер его не читал бы.
  if [ "$MODE" = docker ]; then gid="$(compose exec -T watcher-server id -g 2>/dev/null)" || gid=1654; [ -n "$gid" ] || gid=1654
  else gid="$(id -g "$SERVICE_USER")"; fi
  chown -R "root:$gid" "$KIT_DIR"
  find "$KIT_DIR" -type d -exec chmod 750 {} +
  find "$KIT_DIR" -type f -exec chmod 640 {} +

  if [ "${UPDATE_KEY_OK:-false}" = true ] && [ -f "$KIT_DIR/updates/packages.tsv" ]; then
    say "Пакеты автообновления → сервер (Настройки → Обновления)"
    local platform version file sig
    # Цикл читает из fd 3: docker compose exec без -i всё равно съедает stdin.
    while IFS=$'\t' read -r -u 3 platform version file sig; do
      if [ "$MODE" = docker ]; then
        compose exec -T watcher-server sh -c 'cat > /tmp/agent-package' < "$KIT_DIR/$file"
        compose exec -T watcher-server sh -c 'cat > /tmp/agent-package.sig' < "$KIT_DIR/$sig"
        compose exec -T watcher-server dotnet Watcher.Server.dll --import-agent-package --platform "$platform" \
          --version "$version" --signature-file /tmp/agent-package.sig /tmp/agent-package 2>/dev/null \
          || import_failed "$platform" "$version" $?
        compose exec -T watcher-server rm -f /tmp/agent-package /tmp/agent-package.sig
      else
        local t; t="$(mktemp -d)"
        install -m 644 -o "$SERVICE_USER" "$KIT_DIR/$file" "$t/pkg"
        install -m 644 -o "$SERVICE_USER" "$KIT_DIR/$sig" "$t/pkg.sig"
        chmod 755 "$t"
        server_cli --import-agent-package --platform "$platform" --version "$version" --signature-file "$t/pkg.sig" "$t/pkg" 2>/dev/null \
          || import_failed "$platform" "$version" $?
        rm -rf "$t"
      fi
    done 3< "$KIT_DIR/updates/packages.tsv"
  fi
}

summary() {
  say "Готово"
  echo "Веб-интерфейс:   ${SERVER_URL:-http://$(primary_ip):$SERVICE_PORT}/  (логин admin)"
  if [ "${FIRST_INSTALL:-false}" = true ] && [ -n "${ADMIN_PASSWORD:-}" ]; then
    echo "Пароль admin:    $ADMIN_PASSWORD   ← сохраните, повторно не показывается"
    echo "                 (сменить — docs/install.md, «Смена пароля администратора»)"
  elif [ "$MODE" = docker ] && [ -n "$(env_get Server__AdminPassword 2>/dev/null)" ]; then
    # Первая установка могла оборваться до этого места — пароль тогда не показан.
    echo "Пароль admin:    первоначальный — в $COMPOSE_DIR/.env (Server__AdminPassword), если не меняли;"
    echo "                 после смены пароля строку можно удалить"
  fi
  echo "Ключи и CA:      $KEYS_DIR  (только root; сделайте резервную копию)"
  if [ -d "$KIT_DIR" ] && [ "$NO_KIT" = false ]; then
    echo "Агенты:          $KIT_DIR — инструкция в $KIT_DIR/README.txt"
    echo "  Windows:       $KIT_DIR/windows/watcher-agent.msi — двойной щелчок, msiexec /qn или GPO"
    echo "  Linux:         $KIT_DIR/linux/watcher-agent-linux-*.tar.gz — распаковать, sudo sh watcher-agent-*/install.sh"
  fi
  echo "Браузер предупредит о сертификате, пока $KEYS_DIR/watcher-ca.crt не добавлен в доверенные."
}

# --- Действия --------------------------------------------------------------------
case "$ACTION" in
  export-ca)
    if [ "$MODE" = docker ]; then
      compose exec -T watcher-proxy cat /data/caddy/pki/authorities/local/root.crt > "$EXPORT_OUT"
    else
      cp "$CADDY_CA/root.crt" "$EXPORT_OUT"
    fi
    echo "корневой CA сервера сохранён в $EXPORT_OUT"
    exit 0
    ;;
esac

[ "$EUID" -eq 0 ] || { echo "нужен sudo" >&2; exit 1; }

case "$ACTION" in
  uninstall)
    if [ "$MODE" = docker ]; then
      echo "Docker Compose: docker compose down (данные — в томах watcher-data и caddy-data)." >&2
      exit 1
    fi
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true
    rm -f "$UNIT_FILE" /etc/logrotate.d/watcher-server
    systemctl daemon-reload
    echo "служба удалена. Данные сохранены в $DATA_DIR, ключи — в $KEYS_DIR"
    echo "чтобы стереть данные: rm -rf $INSTALL_DIR"
    exit 0
    ;;
  kit)
    ensure_keys
    if [ "$MODE" = docker ]; then
      SERVER_URL="https://$(env_get WATCHER_DOMAIN | cut -d, -f1 | tr -d ' ')"
      UPDATE_KEY_OK=true; check_update_key "$(env_get Server__UpdatePublicKey)" || UPDATE_KEY_OK=false
    else
      SERVER_URL="https://$(sed -n 's/^# domain: //p' /etc/caddy/Caddyfile 2>/dev/null | cut -d, -f1 | tr -d ' ')"
      UPDATE_KEY_OK=true; check_update_key "$(json_get UpdatePublicKey)" || UPDATE_KEY_OK=false
    fi
    build_kit
    summary
    ;;
  install)
    if [ "$MODE" = docker ]; then install_docker; else install_systemd; fi
    # Новая БД — прежний токен из $KEYS_DIR ей неизвестен.
    [ "${FIRST_INSTALL:-false}" = true ] && NEW_TOKEN=true
    if [ "$NO_KIT" = false ]; then
      build_kit || echo "Комплект агентов не собран — исправьте и запустите: sudo $0 --rebuild-kit" >&2
    fi
    summary
    ;;
esac
