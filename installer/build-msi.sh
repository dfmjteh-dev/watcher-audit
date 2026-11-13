#!/usr/bin/env bash
# MSI агента Windows — на Linux, без WiX Toolset: wixl + msibuild (msitools).
#
# Общий (без настроек) — из распакованного содержимого zip агента:
#   installer/build-msi.sh --payload dist/stage --version 0.11.0 --out dist/watcher-agent-0.11.0.msi
#
# Настроенный на сервер — из общего MSI (только меняются свойства, сборки нет):
#   installer/build-msi.sh --configure dist/watcher-agent-0.11.0.msi --out kit/watcher-agent.msi \
#       --server-url https://watcher.corp --enroll-token wenr_... --update-key MFkw... --ca-cert ca.crt
# Такой MSI ставится двойным щелчком или GPO без параметров; свойства из
# командной строки msiexec по-прежнему перекрывают зашитые.
#
# WATCHER_SIGN_PFX + WATCHER_SIGN_PASSWORD — MSI подписывается Authenticode
# (tools/codesign/sign-windows.sh). Подпись ставится последней: любая правка
# таблиц после неё её ломает, поэтому настроенный MSI подписывается заново.
#
# Чего не умеет wixl, дописывается SQL-запросами (installer/watcher-agent.wxs,
# комментарий в начале): custom actions, их место в последовательности,
# SecureCustomProperties, MsiHiddenProperties.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PAYLOAD="" VERSION="" OUT="" CONFIGURE=""
SERVER_URL="" ENROLL_TOKEN="" UPDATE_KEY="" CA_CERT="" TERMINAL_AUDIT="" MAINTENANCE_KEY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --payload) PAYLOAD="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --configure) CONFIGURE="$2"; shift 2 ;;
    --server-url) SERVER_URL="$2"; shift 2 ;;
    --enroll-token) ENROLL_TOKEN="$2"; shift 2 ;;
    --update-key) UPDATE_KEY="$2"; shift 2 ;;
    --maintenance-key) MAINTENANCE_KEY="$2"; shift 2 ;;
    --ca-cert) CA_CERT="$2"; shift 2 ;;
    --terminal-audit) TERMINAL_AUDIT="$2"; shift 2 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done
[ -n "$OUT" ] || { echo "Укажите --out <файл.msi>" >&2; exit 1; }
for tool in wixl msibuild; do
  command -v "$tool" >/dev/null || { echo "Нужен $tool (apt install wixl msitools)" >&2; exit 1; }
done

# Значение в SQL-строке msibuild. Экранирования кавычек в SQL Windows Installer
# нет — в токенах (base64url), ключах (base64), URL и командной строке их и не
# бывает; встретилась — ошибка, а не тихо испорченный MSI.
sql_str() {
  case "$1" in *"'"*) echo "Одинарная кавычка в значении для MSI не поддерживается: $1" >&2; exit 1 ;; esac
  printf "'%s'" "$1"
}

set_property() {  # msi name value — UPDATE или INSERT
  local msi="$1" name="$2" value="$3"
  if msiinfo export "$msi" Property | cut -f1 | grep -qx "$name"; then
    msibuild "$msi" -q "UPDATE \`Property\` SET \`Value\` = $(sql_str "$value") WHERE \`Property\` = '$name'"
  else
    msibuild "$msi" -q "INSERT INTO \`Property\` (\`Property\`, \`Value\`) VALUES ('$name', $(sql_str "$value"))"
  fi
}

# msibuild дописывает строки в конец таблицы и переиспользует освободившиеся
# номера строк — Windows такую таблицу не загружает (MSI 0.15.2: Property,
# ошибка 1605 до начала установки). После всех правок таблиц, до подписи.
order_tables() {
  python3 "$SCRIPT_DIR/msi-table-order.py" "$1"
  python3 "$SCRIPT_DIR/msi-table-order.py" --check "$1"
}

sign() {
  if [ -n "${WATCHER_SIGN_PFX:-}" ]; then
    "$SCRIPT_DIR/../tools/codesign/sign-windows.sh" "$1"
  fi
}

if [ -z "$CONFIGURE" ]; then
  [ -d "$PAYLOAD" ] || { echo "Укажите --payload <каталог с exe и ffmpeg.exe>" >&2; exit 1; }
  [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "--version: X.Y.Z" >&2; exit 1; }
  for f in Watcher.Capture.exe Watcher.SessionService.exe GuardRepair.exe ffmpeg.exe; do
    [ -f "$PAYLOAD/$f" ] || { echo "В $PAYLOAD нет $f" >&2; exit 1; }
  done
  wixl -a x64 -D ProductVersion="$VERSION" -D PayloadDir="$PAYLOAD" -o "$OUT" "$SCRIPT_DIR/watcher-agent.wxs"

  # 0.16: понижение версии целью группы — как обычная установка MSI (одна транзакция
  # Windows Installer с откатом). wixl пишет MajorUpgrade с запретом downgrade:
  # детектор WIX_DOWNGRADE_DETECTED и условие запуска. Снимаем запрет, а строка
  # WIX_UPGRADE_DETECTED находит продукт любой версии (без верхней границы).
  msibuild "$OUT" -q "DELETE FROM \`LaunchCondition\` WHERE \`Condition\` = 'NOT WIX_DOWNGRADE_DETECTED'"
  msibuild "$OUT" -q "DELETE FROM \`Upgrade\` WHERE \`ActionProperty\` = 'WIX_DOWNGRADE_DETECTED'"
  # VersionMax — часть ключа: строку пересоздаём. 257 = MigrateFeatures | VersionMinInclusive.
  msibuild "$OUT" -q "DELETE FROM \`Upgrade\` WHERE \`ActionProperty\` = 'WIX_UPGRADE_DETECTED'"
  msibuild "$OUT" -q "INSERT INTO \`Upgrade\` (\`UpgradeCode\`, \`VersionMin\`, \`Attributes\`, \`ActionProperty\`) VALUES ('{B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7}', '0.0.0', 257, 'WIX_UPGRADE_DETECTED')"

  product="$(msiinfo export "$OUT" Property | tr -d '\r' | awk -F'\t' '$1=="ProductCode"{print $2}')"
  msibuild "$OUT" -q "INSERT INTO \`CustomAction\` (\`Action\`, \`Type\`, \`Source\`, \`Target\`) VALUES ('PrepareLegacyRecovery', 2, 'LegacyRecovery', 'legacy-msi-prepare $product')"
  msibuild "$OUT" -q "INSERT INTO \`InstallExecuteSequence\` (\`Action\`, \`Condition\`, \`Sequence\`) VALUES ('PrepareLegacyRecovery', 'NOT Installed AND WIX_UPGRADE_DETECTED', 1450)"
  # Прежние MSI удаляли продукт до транзакции. В новом пакете удаление —
  # после InstallInitialize; внешние действия установки страхует SYSTEM-задача.
  msibuild "$OUT" -q "UPDATE \`InstallExecuteSequence\` SET \`Sequence\` = 1510 WHERE \`Action\` = 'RemoveExistingProducts'"

  # Версии exe в таблицу File — wixl её не заполняет. Без версии Windows
  # Installer при обновлении считает новый exe «младше» установленного
  # (Disallowing installation of component ... higher versioned keyfile exists)
  # и не ставит его, а старый уходит вместе с прежней версией продукта.
  for spec in CaptureExe:Watcher.Capture.exe SessionServiceExe:Watcher.SessionService.exe GuardExe:GuardRepair.exe FfmpegExe:ffmpeg.exe; do
    ver="$(python3 "$SCRIPT_DIR/pe-version.py" "$PAYLOAD/${spec#*:}")"
    [ -z "$ver" ] || msibuild "$OUT" -q "UPDATE \`File\` SET \`Version\` = '$ver' WHERE \`File\` = '${spec%%:*}'"
  done

  # 0.16: установка без PowerShell — GuardRepair agent-install/agent-uninstall
  # (бывший install.ps1). Тип 18 (exe из таблицы File, Source — ключ файла) +
  # 0x400 deferred + 0x800 без имперсонации = 3090; у установки ещё 0x2000
  # HideTarget (в командной строке токены) = 11282. Return="check" — без 0x40.
  # Аргументы --name=value: пустое свойство MSI доходит пустой строкой.
  #
  # "[INSTALLFOLDER]." — не опечатка: Directory-свойства MSI всегда с хвостовым
  # "", а """ перед закрывающей кавычкой парсится как экранированная кавычка
  # и рвёт остаток командной строки. Журнал установки GuardRepair пишет сам —
  # в ProgramData\Watcher\logs\install-*.log (EXE-действие MSI stdout не сохраняет).
  INSTALL_CMD='agent-install "--install-dir=[INSTALLFOLDER]." "--server-url=[SERVERURL]" "--api-token=[APITOKEN]" "--enroll-token=[ENROLLTOKEN]" "--terminal-audit=[TERMINALAUDIT]" "--update-key=[UPDATEKEY]" "--maintenance-key=[MAINTENANCEKEY]" "--ca-cert=[CACERT]" "--recovery-source=[OriginalDatabase]" "--upgrade-recovery-product=[ProductCode]" "--previous-service-start=[WATCHERSERVICESTART]"'
  UNINSTALL_CMD='agent-uninstall "--install-dir=[INSTALLFOLDER]."'
  msibuild "$OUT" -q "INSERT INTO \`CustomAction\` (\`Action\`, \`Type\`, \`Source\`, \`Target\`) VALUES ('RunInstallScript', 11282, 'GuardExe', $(sql_str "$INSTALL_CMD"))"
  msibuild "$OUT" -q "INSERT INTO \`CustomAction\` (\`Action\`, \`Type\`, \`Source\`, \`Target\`) VALUES ('RunUninstallScript', 3090, 'GuardExe', $(sql_str "$UNINSTALL_CMD"))"
  # Удаление — до RemoveFiles (3500): GuardRepair ещё на диске. Установка —
  # сразу после InstallFiles (4000). Обе внутри InstallInitialize (1500) ..
  # InstallFinalize (6600), как положено deferred-действиям.
  msibuild "$OUT" -q "INSERT INTO \`InstallExecuteSequence\` (\`Action\`, \`Condition\`, \`Sequence\`) VALUES ('RunUninstallScript', 'REMOVE=\"ALL\"', 3450)"
  msibuild "$OUT" -q "INSERT INTO \`InstallExecuteSequence\` (\`Action\`, \`Condition\`, \`Sequence\`) VALUES ('RunInstallScript', 'NOT Installed', 4001)"

  secure="$(msiinfo export "$OUT" Property | tr -d '\r' | awk -F'\t' '$1=="SecureCustomProperties"{print $2}')"
  set_property "$OUT" SecureCustomProperties "${secure:+$secure;}SERVERURL;APITOKEN;ENROLLTOKEN;UPDATEKEY;MAINTENANCEKEY;CACERT;TERMINALAUDIT;WATCHERSERVICESTART"
  set_property "$OUT" MsiHiddenProperties "APITOKEN;ENROLLTOKEN"
  order_tables "$OUT"
  sign "$OUT"
  echo "MSI: $OUT"
  exit 0
fi

[ -f "$CONFIGURE" ] || { echo "Нет $CONFIGURE" >&2; exit 1; }
[ -n "$SERVER_URL" ] || { echo "Для --configure нужен --server-url" >&2; exit 1; }
[ -z "$CA_CERT" ] || [ -f "$CA_CERT" ] || { echo "Нет $CA_CERT" >&2; exit 1; }
trap 'rm -f "$OUT.tmp" "$OUT.unsigned"' EXIT
cp "$CONFIGURE" "$OUT.tmp"
# Подпись общего MSI — в отдельном потоке; после правки таблиц она недействительна.
if command -v osslsigncode >/dev/null && osslsigncode remove-signature -in "$OUT.tmp" -out "$OUT.unsigned" >/dev/null 2>&1; then
  mv "$OUT.unsigned" "$OUT.tmp"
else
  rm -f "$OUT.unsigned"
fi
# 0.16: копии общего MSI внутри (WatcherRecoveryPackage) больше нет — базу
# восстановления Guard берёт из самого устанавливаемого MSI по подписи
# <msi>.sig рядом с ним (docs/guardrepair-bootstrap.md).
set_property "$OUT.tmp" SERVERURL "$SERVER_URL"
[ -z "$ENROLL_TOKEN" ] || set_property "$OUT.tmp" ENROLLTOKEN "$ENROLL_TOKEN"
[ -z "$UPDATE_KEY" ] || set_property "$OUT.tmp" UPDATEKEY "$UPDATE_KEY"
[ -z "$MAINTENANCE_KEY" ] || set_property "$OUT.tmp" MAINTENANCEKEY "$MAINTENANCE_KEY"
[ -z "$TERMINAL_AUDIT" ] || set_property "$OUT.tmp" TERMINALAUDIT "$TERMINAL_AUDIT"
if [ -n "$CA_CERT" ]; then
  CA_B64="$(openssl x509 -in "$CA_CERT" -outform der | base64 -w0)"
  set_property "$OUT.tmp" CACERT "$CA_B64"
fi
order_tables "$OUT.tmp"
sign "$OUT.tmp"
mv "$OUT.tmp" "$OUT"
echo "MSI (настроен на $SERVER_URL): $OUT"
