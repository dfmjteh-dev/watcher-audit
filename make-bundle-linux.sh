#!/usr/bin/env bash
# Build a deployable tarball for a Watcher Linux station — единый Linux-агент
# (Фаза 7 экран + Фаза 8a/8b консоль/SSH, roadmap.md).
# Output: dist/watcher-agent-linux-vX.Y.Z.tar.gz (version from Directory.Build.props)
#
# Contents:
#   - Watcher.Capture.Linux (self-contained .NET 8 single-file, linux-x64)
#   - Watcher.TerminalAgent (self-contained, linux-x64 — аудит ввода/вывода
#     консольных/SSH-сессий, системный коллектор для Фазы 8a)
#   - Watcher.Uploader (self-contained, linux-x64 — тот же проект, что и для
#     Windows-станций, см. docs/development.md "Watcher.Uploader переиспользуется
#     для Linux как есть")
#   - systemd/watcher-capture.service, systemd/watcher-uploader.service,
#     systemd/watcher-terminal-agent.service
#   - watcher-term-wrap.sh (Фаза 8b — shell-обёртка вывода через script(1))
#   - install-capture-linux.sh, install-terminal-agent.sh, install-linux-agent.sh
#   - README.txt
#
# ffmpeg НЕ бандлится (в отличие от Windows-бандла с ffmpeg.exe) — на Linux это
# системный пакет (`apt install ffmpeg`), не непровенансный бинарник без апстрим-
# релиза/чексуммы, который иначе пришлось бы тащить самим (см. docs/development.md,
# "Политика зависимостей и supply-chain" — та же забота, что уже отмечена про
# tools/ffmpeg.exe на Windows, здесь просто не возникает). auditd (Фаза 8a)
# тоже НЕ бандлится — системный пакет, install-terminal-agent.sh ставит его
# сам через apt-get/dnf/yum при необходимости.
#
# --packages (H3): вдобавок пакеты dist/watcher-agent_X.Y.Z-1_amd64.deb и
# dist/watcher-agent-X.Y.Z-1.x86_64.rpm из тех же бинарников — nfpm
# (packaging/linux/nfpm.yaml; путь к nfpm — $NFPM, по умолчанию из PATH).
#
# Usage on station:
#   tar -xzf ...
#   ./install-linux-agent.sh --server-url http://your-server:5050
# (либо по отдельности install-capture-linux.sh / install-terminal-agent.sh,
# см. README.txt в архиве)
set -euo pipefail

PACKAGES=false
for arg in "$@"; do
  case "$arg" in
    --packages) PACKAGES=true ;;
    *) echo "Неизвестный аргумент: $arg" >&2; exit 1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-/root/.dotnet/dotnet}"
VERSION="$(grep -oP '<AgentVersion>\K[^<]+' "$SCRIPT_DIR/Directory.Build.props")"   # версия агентов
OUT_DIR="$SCRIPT_DIR/dist"
STAGE="$OUT_DIR/stage-linux"
TARBALL="$OUT_DIR/watcher-agent-linux-v$VERSION.tar.gz"

rm -rf "$STAGE"
mkdir -p "$STAGE"

echo "[1/4] publish capture (linux-x64)..."
"$DOTNET" publish "$SCRIPT_DIR/src/Watcher.Capture.Linux/Watcher.Capture.Linux.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -v quiet --nologo
cp "$SCRIPT_DIR/src/Watcher.Capture.Linux/bin/Release/net8.0/linux-x64/publish/Watcher.Capture.Linux" "$STAGE/"

echo "[2/4] publish terminal agent (linux-x64)..."
"$DOTNET" publish "$SCRIPT_DIR/src/Watcher.TerminalAgent/Watcher.TerminalAgent.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -v quiet --nologo
cp "$SCRIPT_DIR/src/Watcher.TerminalAgent/bin/Release/net8.0/linux-x64/publish/Watcher.TerminalAgent" "$STAGE/"
cp "$SCRIPT_DIR/src/Watcher.TerminalAgent/bin/Release/net8.0/linux-x64/publish/appsettings.json" "$STAGE/"
cp "$SCRIPT_DIR/src/Watcher.TerminalAgent/scripts/watcher-term-wrap.sh" "$STAGE/"

echo "[3/4] publish uploader (linux-x64)..."
"$DOTNET" publish "$SCRIPT_DIR/src/Watcher.Uploader/Watcher.Uploader.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -v quiet --nologo
cp "$SCRIPT_DIR/src/Watcher.Uploader/bin/Release/net8.0/linux-x64/publish/Watcher.Uploader" "$STAGE/"

"$DOTNET" publish "$SCRIPT_DIR/src/Watcher.Guard/Watcher.Guard.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v quiet --nologo
cp "$SCRIPT_DIR/src/Watcher.Guard/bin/Release/net8.0/linux-x64/publish/GuardRepair" "$STAGE/"
echo "[4/4] bundle systemd units + install scripts..."
mkdir -p "$STAGE/systemd"
cp "$SCRIPT_DIR/systemd/watcher-capture.service" "$STAGE/systemd/"
cp "$SCRIPT_DIR/systemd/watcher-uploader.service" "$STAGE/systemd/"
cp "$SCRIPT_DIR/systemd/watcher-terminal-agent.service" "$STAGE/systemd/"
cp "$SCRIPT_DIR/install-capture-linux.sh" "$SCRIPT_DIR/install-terminal-agent.sh" "$SCRIPT_DIR/install-linux-agent.sh" "$STAGE/"
chmod +x "$STAGE/Watcher.Capture.Linux" "$STAGE/Watcher.TerminalAgent" "$STAGE/Watcher.Uploader" \
  "$STAGE/watcher-term-wrap.sh" "$STAGE/install-capture-linux.sh" "$STAGE/install-terminal-agent.sh" "$STAGE/install-linux-agent.sh"

cat > "$STAGE/README.txt" <<'EOF'
Watcher agent — установка на Linux-станцию
===========================================

Требует: ffmpeg в PATH (apt install ffmpeg; на RHEL-семействе — из RPM Fusion
или аналога) для захвата экрана. .NET НЕ требуется — бинарники
self-contained. auditd ставится сам (apt-get/dnf/yum) при установке
консольного агента, если его ещё нет.

Рекомендуемый способ — единый установщик (экран + консоль/SSH за один проход):

    ./install-linux-agent.sh --server-url http://<server>:5050

Запускать от обычного пользователя (НЕ root) — экран ставится в его
systemd --user, консольная часть сама вызовет sudo внутри себя (нужны
sudo-права). Консоль/SSH пишется у всех пользователей; кого не писать —
allowUsers/excludeUsers в конфиге группы на сервере. --api-token — опционально.

Удаление:
    ./install-linux-agent.sh --uninstall

--- По отдельности, если нужен только один из механизмов ---

Экран (Фаза 7), от обычного пользователя:
    ./install-capture-linux.sh --server-url http://<server>:5050 [--api-token ...]
    ./install-capture-linux.sh --uninstall

Консоль/SSH — ввод (pam_tty_audit) + вывод (script-обёртка), Фаза 8,
от root/sudo:
    sudo ./install-terminal-agent.sh --server-url http://<server>:5050
    sudo ./install-terminal-agent.sh --uninstall

После установки:
  - Файлы экрана:        ~/.local/opt/watcher/, очередь/логи — ~/.local/share/watcher/
  - Файлы консоли:       /opt/watcher-terminal-agent/
  - Данные консоли:      /var/lib/watcher-terminal-agent/queue/ (ввод),
                          /var/lib/watcher-terminal-agent/raw-output/ (вывод)
  - Службы:               systemctl --user status watcher-capture watcher-uploader
                           systemctl status watcher-terminal-agent

Кого не записывать (экран и консоль) — allowUsers/excludeUsers в конфиге
группы или хоста в веб-UI; переустанавливать агента не нужно.
EOF

if [ "$PACKAGES" = true ]; then
  NFPM="${NFPM:-nfpm}"
  echo "packages (nfpm)..."
  # Файлы, которые есть только в пакете: глобальная user-служба экрана (путь
  # вместо %h/.local/opt/watcher — как подставляет install-capture-linux.sh),
  # конфиг Capture с общей очередью, watcher-agent-setup и скрипты пакета.
  PKG="$STAGE/pkg"
  mkdir -p "$PKG"
  sed "s|%h/.local/opt/watcher|/opt/watcher-capture|g" "$SCRIPT_DIR/systemd/watcher-capture.service" > "$PKG/watcher-capture.service"
  cp "$SCRIPT_DIR/packaging/linux/Watcher.Capture.Linux.json" "$SCRIPT_DIR/packaging/linux/watcher-agent-setup" \
    "$SCRIPT_DIR/packaging/linux/apply-update.sh" "$SCRIPT_DIR"/packaging/linux/scripts/*.sh "$PKG/"
  for packager in deb rpm; do
    # Пути в nfpm.yaml — относительно stage (переменные в contents nfpm не раскрывает).
    ( cd "$STAGE" && VERSION="$VERSION" "$NFPM" package --config "$SCRIPT_DIR/packaging/linux/nfpm.yaml" \
      --packager "$packager" --target "$OUT_DIR/" )
  done
  # В tar.gz пакетные файлы не нужны.
  rm -rf "$PKG"
fi

echo "zipping → $TARBALL"
mkdir -p "$OUT_DIR"
rm -f "$TARBALL"
( cd "$STAGE" && tar -czf "$TARBALL" . )
rm -rf "$STAGE"

ls -lh "$TARBALL"
echo "Done."
