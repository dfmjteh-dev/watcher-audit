#!/usr/bin/env bash
# Проверка сборки MSI без Windows (CI и локально): общий MSI из заглушек,
# настроенный MSI, подпись — и что в таблицах ровно то, что нужно Windows
# Installer: custom actions внутри InstallInitialize..InstallFinalize, свойства
# защищены и скрыты, настройки зашиты. Нужны wixl, msitools, osslsigncode.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

mkdir "$T/payload"
for f in Watcher.Capture.exe Watcher.SessionService.exe GuardRepair.exe ffmpeg.exe; do echo stub > "$T/payload/$f"; done
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout "$T/ca.key" -out "$T/ca.crt" \
  -days 1 -subj "/CN=test CA" 2>/dev/null
WATCHER_SIGN_PASSWORD=test-password-123 "$SCRIPT_DIR/../tools/codesign/new-codesign-cert.sh" --name "Watcher test" --out "$T/cs" >/dev/null
export WATCHER_SIGN_PFX="$T/cs/watcher-codesign.pfx" WATCHER_SIGN_PASSWORD=test-password-123 WATCHER_SIGN_TIMESTAMP=""

# Без GuardRepair.exe (установщик агента) MSI не собирается.
mv "$T/payload/GuardRepair.exe" "$T/GuardRepair.exe"
if "$SCRIPT_DIR/build-msi.sh" --payload "$T/payload" --version 1.2.3 --out "$T/rejected.msi" > "$T/rejected.log" 2>&1; then
  fail "MSI собран без GuardRepair.exe"
fi
mv "$T/GuardRepair.exe" "$T/payload/GuardRepair.exe"

"$SCRIPT_DIR/build-msi.sh" --payload "$T/payload" --version 1.2.3 --out "$T/generic.msi" >/dev/null
"$SCRIPT_DIR/build-msi.sh" --configure "$T/generic.msi" --out "$T/kit.msi" --server-url https://watcher.test \
  --enroll-token wenr_test-token_1 --update-key MFkwTESTKEY --ca-cert "$T/ca.crt" >/dev/null
# 0.16: копии общего MSI внутри настроенного нет — база Guard берётся по <msi>.sig.
! "$SCRIPT_DIR/build-msi.sh" --configure "$T/generic.msi" --out "$T/legacy.msi" --server-url https://watcher.test \
  --recovery-package "$T/generic.msi" >/dev/null 2>&1 || fail "--recovery-package всё ещё принимается"
! msiinfo streams "$T/kit.msi" | grep -q '^WatcherRecovery' || fail "в настроенном MSI осталась копия MSI"
generic_size=$(stat -c %s "$T/generic.msi") kit_size=$(stat -c %s "$T/kit.msi")
[ "$kit_size" -lt $((generic_size + 1048576)) ] || fail "настроенный MSI заметно больше общего ($kit_size > $generic_size)"

# Windows не загружает таблицу с нарушенным порядком строк, msiinfo её читает
# (MSI 0.15.2: msibuild переиспользовал номер строки для нового ключа Property).
# Когда msibuild переиспользует номер, зависит от истории правок пула, поэтому
# здесь порядок ломается напрямую: строки Property — в обратном порядке.
cp "$T/generic.msi" "$T/order.msi"
before="$(msiinfo export "$T/order.msi" Property | tr -d '\r' | sort)"
python3 - "$SCRIPT_DIR/msi-table-order.py" "$T/order.msi" <<'PY'
import importlib.util, sys
spec = importlib.util.spec_from_file_location("order", sys.argv[1])
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
with open(sys.argv[2], "r+b") as f:
    cfb = m.Cfb(f)
    ref = m.strings(cfb)[1]
    cols, _ = m.split(cfb.read("!Property"), [ref, ref])
    cfb.write("!Property", b"".join(b"".join(c[::-1]) for c in cols))
PY
! python3 "$SCRIPT_DIR/msi-table-order.py" --check "$T/order.msi" 2>/dev/null || fail "нарушение порядка не обнаружено"
python3 "$SCRIPT_DIR/msi-table-order.py" "$T/order.msi" >/dev/null
python3 "$SCRIPT_DIR/msi-table-order.py" --check "$T/order.msi" || fail "порядок строк не восстановлен"
[ "$(msiinfo export "$T/order.msi" Property | tr -d '\r' | sort)" = "$before" ] || fail "содержимое Property изменилось при сортировке"

# msiinfo export отдаёт строки с CRLF.
table() { msiinfo export "$1" "$2" | tr -d '\r'; }
seq_of() { table "$1" InstallExecuteSequence | awk -F'\t' -v a="$2" '$1==a{print $3}'; }
prop() { table "$1" Property | awk -F'\t' -v p="$2" '$1==p{print $2}'; }

[ "$(prop "$T/generic.msi" ProductCode)" = "$(prop "$T/kit.msi" ProductCode)" ] || fail "recovery base ProductCode differs"
! msiinfo streams "$T/generic.msi" | grep -q '^WatcherRecovery' || fail "recursive recovery capsule"
for msi in "$T/generic.msi" "$T/kit.msi"; do
  # 0.16: понижение версии разрешено — MSI находит продукт любой версии.
  ! table "$msi" LaunchCondition | grep -q 'WIX_DOWNGRADE_DETECTED' || fail "запрет понижения версии в LaunchCondition"
  ! table "$msi" Upgrade | grep -q 'WIX_DOWNGRADE_DETECTED' || fail "детектор понижения версии в Upgrade"
  [ "$(table "$msi" Upgrade | awk -F'\t' '$7=="WIX_UPGRADE_DETECTED"{print $2"|"$3"|"$5}')" = "0.0.0||257" ] || fail "WIX_UPGRADE_DETECTED должен находить продукт любой версии"
  # 0.16: в каталоге агента — только Capture, SessionService, ffmpeg; GuardRepair —
  # в WatcherGuard\setup; ни install.ps1, ни alias Watcher.Guard.exe.
  files="$(table "$msi" File)"
  grep -q 'GuardRepair.exe' <<<"$files" || fail "нет GuardRepair.exe"
  ! grep -Eq 'install\.ps1|Watcher\.Guard\.exe|\.ps1' <<<"$files" || fail "в MSI остались скрипты или alias"
  [ "$(table "$msi" Component | awk -F'\t' '$1=="GuardExe"{print $3}')" = GUARDSETUPFOLDER ] || fail "GuardRepair не в WatcherGuard\\setup"
  table "$msi" Directory | grep -qP '^GUARDSETUPFOLDER\tGUARDFOLDER\tsetup' || fail "нет каталога WatcherGuard\\setup"
  ca="$(table "$msi" CustomAction)"
  ! grep -qi 'powershell' <<<"$ca" || fail "custom action всё ещё запускает PowerShell"
  grep -qP '^RunInstallScript\t11282\tGuardExe\tagent-install ' <<<"$ca" || fail "RunInstallScript в $msi"
  grep -qP '^RunUninstallScript\t3090\tGuardExe\tagent-uninstall ' <<<"$ca" || fail "RunUninstallScript в $msi"
  grep -Fq -- '"--recovery-source=[OriginalDatabase]"' <<<"$ca" || fail "initial recovery source not passed"
  grep -Fq -- '"--install-dir=[INSTALLFOLDER]."' <<<"$ca" || fail "каталог установки не передаётся"
  grep -Fq -- '"--ca-cert=[CACERT]"' <<<"$ca" || fail "CACERT не передаётся"
  grep -Fq -- '"--previous-service-start=[WATCHERSERVICESTART]"' <<<"$ca" || fail "режим службы не передаётся"
  table "$msi" RegLocator | grep -q 'WatcherSessionService' || fail "нет поиска режима старой службы"
  [ "$(seq_of "$msi" AppSearch)" -lt "$(seq_of "$msi" RemoveExistingProducts)" ] || fail "режим читается после удаления службы"
  init="$(seq_of "$msi" InstallInitialize)" fin="$(seq_of "$msi" InstallFinalize)"
  [ "$(seq_of "$msi" PrepareLegacyRecovery)" -lt "$(seq_of "$msi" RemoveExistingProducts)" ] || fail "recovery preparation follows removal"
  [ "$(seq_of "$msi" RemoveExistingProducts)" -gt "$init" ] || fail "old product removed outside transaction"
  [ "$(seq_of "$msi" RemoveExistingProducts)" -lt "$(seq_of "$msi" ProcessComponents)" ] || fail "old product removed after component changes"
  for a in RunInstallScript RunUninstallScript; do
    s="$(seq_of "$msi" "$a")"
    if [ -z "$s" ] || [ "$s" -le "$init" ] || [ "$s" -ge "$fin" ]; then fail "$a ($s) вне $init..$fin"; fi
  done
  [ "$(seq_of "$msi" RunUninstallScript)" -lt "$(seq_of "$msi" RemoveFiles)" ] || fail "удаление после RemoveFiles"
  [ "$(seq_of "$msi" RunInstallScript)" -gt "$(seq_of "$msi" InstallFiles)" ] || fail "установка до InstallFiles"
  for p in SERVERURL ENROLLTOKEN UPDATEKEY MAINTENANCEKEY CACERT TERMINALAUDIT WATCHERSERVICESTART; do
    grep -q "$p" <<<"$(prop "$msi" SecureCustomProperties)" || fail "$p не в SecureCustomProperties"
  done
  [ "$(prop "$msi" SecureCustomProperties)" = "WIX_DOWNGRADE_DETECTED;WIX_UPGRADE_DETECTED;SERVERURL;ENROLLTOKEN;UPDATEKEY;MAINTENANCEKEY;CACERT;TERMINALAUDIT;WATCHERSERVICESTART" ] \
    || fail "SecureCustomProperties: $(prop "$msi" SecureCustomProperties | od -c | head -3)"
  [ "$(prop "$msi" MsiHiddenProperties)" = "ENROLLTOKEN" ] || fail "MsiHiddenProperties"
  # 0.16: одна строка Upgrade (без детектора понижения версии) — см. проверку выше.
  [ "$(table "$msi" Upgrade | grep -c B57CFE32-A5D4-4ACC-8E2E-796E4F6B05C7)" = 1 ] || fail "UpgradeCode"
  python3 "$SCRIPT_DIR/msi-table-order.py" --check "$msi" || fail "порядок строк в $msi"
  osslsigncode verify -CAfile "$T/cs/watcher-codesign.pem" -in "$msi" 2>&1 | grep -q 'Number of verified signatures: 1' \
    || fail "подпись $msi"
done
[ -z "$(prop "$T/generic.msi" SERVERURL)" ] || fail "в общем MSI зашит SERVERURL"
[ "$(prop "$T/kit.msi" SERVERURL)" = https://watcher.test ] || fail "SERVERURL"
[ "$(prop "$T/kit.msi" ENROLLTOKEN)" = wenr_test-token_1 ] || fail "ENROLLTOKEN"
[ "$(prop "$T/kit.msi" UPDATEKEY)" = MFkwTESTKEY ] || fail "UPDATEKEY"
[ "$(prop "$T/kit.msi" CACERT)" = "$(openssl x509 -in "$T/ca.crt" -outform der | base64 -w0)" ] || fail "CACERT"
echo "MSI: OK"
