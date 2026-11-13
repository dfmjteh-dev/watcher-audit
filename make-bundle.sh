#!/usr/bin/env bash
# Сборка файлов Windows-агента: dist/watcher-agent-vX.Y.Z.zip (версия — из
# Directory.Build.props) — архив файлов, из которого собирается MSI (комплект
# install-server.sh, release.yml). Ставится агент только MSI (0.16: install.ps1 и
# установка скриптом убраны).
#
# Содержимое:
#   - Watcher.Capture.exe (self-contained .NET 8 single-file)
#   - Watcher.SessionService.exe (служба, запускающая Capture в каждой
#     пользовательской сессии — единственный режим с Задачи D1, roadmap-next.md)
#   - ffmpeg.exe
#   - GuardRepair.exe (установка агента и независимый ремонт; MSI кладёт его в
#     C:\Program Files\WatcherGuard\setup)
#   - README.txt
#
# H3: WATCHER_SIGN_PFX + WATCHER_SIGN_PASSWORD заданы — Watcher.Capture.exe,
# Watcher.SessionService.exe и GuardRepair.exe подписываются Authenticode
# (tools/codesign/sign-windows.sh, docs/codesign.md). ffmpeg.exe — чужой
# бинарник, им не подписываем. Без PFX — неподписанная сборка с предупреждением.
#
# --msi — вдобавок dist/watcher-agent-X.Y.Z.msi из тех же файлов
# (installer/build-msi.sh, wixl на Linux; подписывается тем же PFX).
set -euo pipefail

MSI=false
for arg in "$@"; do
  case "$arg" in
    --msi) MSI=true ;;
    *) echo "Неизвестный аргумент: $arg" >&2; exit 1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-/root/.dotnet/dotnet}"
VERSION="$(grep -oP '<AgentVersion>\K[^<]+' "$SCRIPT_DIR/Directory.Build.props")"   # версия агентов
OUT_DIR="$SCRIPT_DIR/dist"
STAGE="$OUT_DIR/stage"
ZIP="$OUT_DIR/watcher-agent-v$VERSION.zip"

rm -rf "$STAGE"
mkdir -p "$STAGE"

echo "[1/4] publish capture..."
"$DOTNET" publish "$SCRIPT_DIR/src/Watcher.Capture/Watcher.Capture.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -v quiet --nologo
cp "$SCRIPT_DIR/src/Watcher.Capture/bin/Release/net8.0-windows/win-x64/publish/Watcher.Capture.exe" "$STAGE/"

# Watcher.Uploader.exe на Windows с Задачи D2 не нужен: выгрузку делает служба
# (UploaderLoop внутри Watcher.SessionService.exe).
echo "[2/4] publish session service (запись в сессиях + выгрузка)..."
"$DOTNET" publish "$SCRIPT_DIR/src/Watcher.SessionService/Watcher.SessionService.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -v quiet --nologo
cp "$SCRIPT_DIR/src/Watcher.SessionService/bin/Release/net8.0-windows/win-x64/publish/Watcher.SessionService.exe" "$STAGE/"

"$DOTNET" publish "$SCRIPT_DIR/src/Watcher.Guard/Watcher.Guard.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v quiet --nologo
cp "$SCRIPT_DIR/src/Watcher.Guard/bin/Release/net8.0/win-x64/publish/GuardRepair.exe" "$STAGE/"

echo "[3/4] bundle ffmpeg..."
# Свой tools/ffmpeg.exe не трогаем; нет — качаем закреплённую версию.
[ -f "$SCRIPT_DIR/tools/ffmpeg.exe" ] || "$SCRIPT_DIR/tools/fetch-ffmpeg.sh"
cp "$SCRIPT_DIR/tools/ffmpeg.exe" "$STAGE/"

cat > "$STAGE/README.txt" <<'EOF'
Watcher agent — файлы Windows-агента
====================================

Устанавливается агент MSI из комплекта, который install-server.sh собрал на
сервере: он уже знает адрес сервера, токен регистрации и CA, ставится двойным
щелчком, GPO или msiexec /i watcher-agent.msi /qn. Общий MSI без настроек —
msiexec /i watcher-agent-X.Y.Z.msi SERVERURL=https://<сервер> ENROLLTOKEN=wenr_...

Этот архив — исходные файлы для сборки MSI, не установщик.
EOF

if [ -n "${WATCHER_SIGN_PFX:-}" ]; then
  echo "[3b] Authenticode..."
  "$SCRIPT_DIR/tools/codesign/sign-windows.sh" "$STAGE/Watcher.Capture.exe" "$STAGE/Watcher.SessionService.exe" "$STAGE/GuardRepair.exe"
else
  echo "ВНИМАНИЕ: WATCHER_SIGN_PFX не задан — агент НЕ подписан (AV/EDR и AllSigned-политики" >&2
  echo "PowerShell будут против). См. docs/codesign.md." >&2
fi

if [ "$MSI" = true ]; then
  echo "[3c] MSI..."
  "$SCRIPT_DIR/installer/build-msi.sh" --payload "$STAGE" --version "$VERSION" --out "$OUT_DIR/watcher-agent-$VERSION.msi"
fi

echo "[4/4] zipping → $ZIP"
mkdir -p "$OUT_DIR"
rm -f "$ZIP"
( cd "$STAGE" && zip -q -r "$ZIP" . )
rm -rf "$STAGE"

ls -lh "$ZIP"
echo "Done."
