#!/usr/bin/env bash
# Установочный архив релиза: dist/watcher-server-X.Y.Z.tar.gz — сервер и
# агенты ставятся из него без исходников, .NET SDK и интернета:
#
#   tar -xzf watcher-server-X.Y.Z.tar.gz && cd watcher-server-X.Y.Z && sudo ./install-server.sh
#
# Внутри: образы Docker (сервер, Caddy, инструменты комплекта), готовые агенты
# без подписи (подписывает install-server.sh сертификатом организации), бинарь
# сервера для --systemd, install-server.sh и всё, что он использует.
#
# Нужен только Docker: агенты и сервер компилируются в контейнере тулчейна
# (tools/agent-kit/Dockerfile). Запуск — из корня репозитория:
#   ./make-server-bundle.sh [--apk watcher-agent.apk]
#
# --apk — release-APK Android-агента той же версии (<AgentVersion>), собранный и
# подписанный отдельно (docs/android-build.md: тут нет Android SDK, ключ APK — не
# в репозитории). По умолчанию — dist/watcher-agent-<AgentVersion>.apk, если есть.
# install-server.sh загрузит его в «Обновления» вместе с остальными пакетами —
# с него же раздел Android на странице «Установка агентов». Без APK архив
# собирается, но Android-устройства подключать будет нечем (загрузить APK вручную).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
VERSION="$(grep -oP '<ServerVersion>\K[^<]+' "$ROOT/Directory.Build.props")"
AGENT_VERSION="$(grep -oP '<AgentVersion>\K[^<]+' "$ROOT/Directory.Build.props")"   # агенты — своя версия
NAME="watcher-server-$VERSION"
OUT="$ROOT/dist/$NAME"
CADDY_IMAGE="caddy:2-alpine"   # тот же, что в docker-compose.yml
APK="$ROOT/dist/watcher-agent-$AGENT_VERSION.apk"
while [ $# -gt 0 ]; do
  case "$1" in
    --apk) APK="$2"; [ -f "$APK" ] || { echo "Нет $APK" >&2; exit 1; }; shift 2 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done

rm -rf "$OUT"
mkdir -p "$OUT/images" "$OUT/agents/windows" "$OUT/agents/linux"

echo "=== Агенты и сервер (компиляция в контейнере тулчейна) ==="
docker build -q -t watcher-agent-toolchain "$ROOT/tools/agent-kit" >/dev/null
# Всё, кроме docker save и tar, — в контейнере: на машине сборки нужен только Docker.
docker run --rm -v "$ROOT:/src" -v watcher-agent-nuget:/root/.nuget/packages -e VERSION="$AGENT_VERSION" -e BUILD_OWNER="$(id -u):$(id -g)" watcher-agent-toolchain bash -c '
  set -e
  ./make-bundle.sh --msi
  ./make-bundle-linux.sh --packages
  rm -rf dist/bundle-windows && mkdir -p dist/bundle-windows
  unzip -q "dist/watcher-agent-v$VERSION.zip" -d dist/bundle-windows   # VERSION здесь — AGENT_VERSION
  rm -rf dist/server-publish
  dotnet publish src/Watcher.Server/Watcher.Server.csproj -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v quiet --nologo -o dist/server-publish
  # Возвращаем временные каталоги вызывающему пользователю для очистки на хосте.
  chown -R "$BUILD_OWNER" dist/bundle-windows dist/server-publish'
cp -r "$ROOT/dist/bundle-windows/." "$OUT/agents/windows/"
cp "$ROOT/dist/watcher-agent_${AGENT_VERSION}-1_amd64.deb" "$ROOT/dist/watcher-agent-${AGENT_VERSION}-1.x86_64.rpm" "$OUT/agents/linux/"
mkdir -p "$OUT/server"
cp -r "$ROOT/dist/server-publish/." "$OUT/server/"
# ffmpeg для пережатия (--systemd; в образе он свой) — та же закреплённая сборка.
"$ROOT/tools/fetch-ffmpeg-linux.sh" "$OUT/server"
rm -f "$OUT/server/"*.pdb
if [ -f "$APK" ]; then
  mkdir -p "$OUT/agents/android"
  cp "$APK" "$OUT/agents/android/watcher-agent.apk"
else
  echo "Нет APK Android-агента ($APK) — архив без него (--apk)" >&2
fi

echo "=== Образы ==="
docker build -q -f "$ROOT/src/Watcher.Server/Dockerfile" -t "watcher-server:$VERSION" "$ROOT" >/dev/null
docker build -q -f "$ROOT/tools/agent-kit/Dockerfile.tools" -t "watcher-kit-tools:$VERSION" "$ROOT/tools/agent-kit" >/dev/null
docker pull -q "$CADDY_IMAGE" >/dev/null
docker save "watcher-server:$VERSION" | gzip > "$OUT/images/watcher-server.tar.gz"
docker save "watcher-kit-tools:$VERSION" | gzip > "$OUT/images/watcher-kit-tools.tar.gz"
docker save "$CADDY_IMAGE" | gzip > "$OUT/images/caddy.tar.gz"

echo "=== Скрипты и конфиги ==="
for f in install-server.sh docker-compose.yml Caddyfile Caddyfile.systemd-example .env.example CHANGELOG.md \
         installer/watcher-agent.wxs installer/build-msi.sh installer/pe-version.py installer/msi-table-order.py \
         tools/agent-kit/build-kit.sh tools/agent-kit/Dockerfile.tools \
         tools/codesign/new-codesign-cert.sh tools/codesign/sign-windows.sh \
         tools/update-signing/new-update-key.sh tools/update-signing/sign-update.sh \
         tools/update-signing/ensure-maintenance-key.sh tools/update-signing/recovery-manifest.sh \
         tools/server-update/server-update.sh tools/server-update/watcher-server-update.path \
         tools/server-update/watcher-server-update.service \
         docs/report-drilldown.md docs/report-metrics-audit.md \
         docs/install.md docs/operations.md docs/codesign.md docs/av-edr.md \
         docs/agent-protection.md docs/guardrepair-bootstrap.md docs/guardrepair-update-rollback.md docs/guardrepair-update-progress.md docs/diagnostic-archives.md; do
  mkdir -p "$OUT/$(dirname "$f")"
  cp -p "$ROOT/$f" "$OUT/$f"
done
# 0.15.0: adb для Android-части комплекта (build-kit.sh) — в архиве, чтобы
# установке сервера интернет не был нужен. Нет локально — качаем; не вышло —
# build-kit.sh попробует сам при сборке комплекта.
ADB_ZIP="$ROOT/platform-tools-latest-windows.zip"
[ -f "$ADB_ZIP" ] || curl -fsSL -m 120 -o "$ADB_ZIP" https://dl.google.com/android/repository/platform-tools-latest-windows.zip \
  || { rm -f "$ADB_ZIP"; echo "platform-tools не скачан — Android в комплекте будет только при доступе к dl.google.com" >&2; }
[ -f "$ADB_ZIP" ] && cp -p "$ADB_ZIP" "$OUT/"
printf '%s\n' "$VERSION" > "$OUT/VERSION"
printf '%s\n' "$AGENT_VERSION" > "$OUT/AGENT_VERSION"
cat > "$OUT/README.txt" <<TXT
Watcher $VERSION — установочный архив сервера
==========================================

На Linux-сервере (x64) с Docker и плагином compose, от root:

    tar -xzf $NAME.tar.gz && cd $NAME
    sudo ./install-server.sh [--domain watcher.corp]

Без Docker: sudo ./install-server.sh --systemd (Caddy ставится из apt).

Скрипт ставит сервер с HTTPS, выпускает ключи и самоподписанные сертификаты
(/root/watcher-keys), собирает комплект агентов под этот сервер
(/opt/watcher-kit: MSI, deb/rpm, README с командами) и печатает адрес и пароль
admin. Комплект доступен и в веб-интерфейсе: Настройки → Установка агентов.

Обновление — архив новой версии распаковать где угодно и запустить тот же
sudo ./install-server.sh: конфигурация (/opt/watcher/.env), ключи и данные
сохраняются. Дальше можно и из веб-интерфейса: Настройки → Сервер (последний
релиз с GitHub или загрузка этого архива). Подробно — docs/install.md.
TXT

echo "=== Архив ==="
tar -czf "$ROOT/dist/$NAME.tar.gz" -C "$ROOT/dist" "$NAME"
rm -rf "$OUT" "$ROOT/dist/bundle-windows" "$ROOT/dist/server-publish"
ls -lh "$ROOT/dist/$NAME.tar.gz"
