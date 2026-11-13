#!/usr/bin/env bash
# Комплект установки агентов, настроенный на конкретный сервер Watcher:
#   windows/watcher-agent.msi — адрес сервера, токен регистрации, ключ
#     обновлений и CA зашиты; на Windows ставится только он (0.13);
#   linux/watcher-agent-linux-<версия>.tar.gz — папка с install.sh, .deb, .rpm
#     и CA: распаковать и запустить install.sh;
#   certs/ — сертификаты для GPO; updates/ — пакеты автообновления с подписями
#     (их загружает install-server.sh, страница «Установка агентов» их не отдаёт);
#   android/watcher-android-tools.zip — adb (Windows), install.cmd с адресом и
#     токеном, инструкция; APK сервер добавляет в архив при скачивании (последний
#     пакет android из «Обновлений» — им же агент потом обновляется);
#   README.txt с готовыми командами.
#
# Обычно его вызывает install-server.sh в контейнере тулчейна
# (tools/agent-kit/Dockerfile). Вручную — где есть dotnet, nfpm, wixl,
# msitools, osslsigncode, zip:
#   tools/agent-kit/build-kit.sh --server-url https://watcher.corp --enroll-token wenr_... \
#       --keys /root/watcher-keys --out /opt/watcher-kit
#
# В --keys (install-server.sh выпускает их сам):
#   watcher-ca.crt                       корневой CA сервера (PEM) — обязателен
#   update-key.pem + update-key.pass     ключ обновлений и его пароль — подписи пакетов
#   update-key.pub.txt                   открытый ключ — зашивается в MSI/скрипт
#   watcher-codesign.pfx + codesign.pass Authenticode; нет — Windows-файлы без подписи
#
# --adb-zip FILE — platform-tools для Windows (platform-tools-latest-windows.zip);
# без него берётся одноимённый файл из корня репозитория/архива, иначе скачивается
# с dl.google.com; не нашёлся — комплект без Android.
#
# --agents DIR — готовые агенты из установочного архива (DIR/windows: exe,
# ffmpeg.exe, GuardRepair.exe; DIR/linux: .deb, .rpm; DIR/android:
# watcher-agent.apk): без dotnet и nfpm, только подпись, MSI и упаковка. Версия —
# из файла VERSION архива.
#
# --apk FILE — release-APK Android-агента той же версии (по умолчанию из --agents);
# подписывается ключом обновлений как пакет android — install-server.sh загрузит
# его в «Обновления», а страница «Установка агентов» положит в архив Android.
#
# Комплект содержит токен регистрации — каталог только для root (700).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
SERVER_URL="" ENROLL_TOKEN="" KEYS="" OUT="" AGENTS="" ADB_ZIP="" APK="" MAINTENANCE_KEY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --server-url) SERVER_URL="$2"; shift 2 ;;
    --enroll-token) ENROLL_TOKEN="$2"; shift 2 ;;
    --maintenance-key) MAINTENANCE_KEY="$2"; shift 2 ;;
    --keys) KEYS="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --agents) AGENTS="$2"; shift 2 ;;
    --adb-zip) ADB_ZIP="$2"; shift 2 ;;
    --apk) APK="$2"; shift 2 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 1 ;;
  esac
done
if [ -z "$SERVER_URL" ] || [ -z "$KEYS" ] || [ -z "$OUT" ]; then
  echo "Нужны --server-url, --keys и --out" >&2
  exit 1
fi
[ -f "$KEYS/watcher-ca.crt" ] || { echo "Нет $KEYS/watcher-ca.crt" >&2; exit 1; }

if [ -n "$AGENTS" ]; then
  # AGENT_VERSION — версия агентов в архиве; в архивах до разделения версий её
  # нет, там агенты той же версии, что и сервер (VERSION).
  VERSION="$(cat "$REPO/AGENT_VERSION" 2>/dev/null || cat "$REPO/VERSION")"
  DIST="$(mktemp -d)"
  trap 'rm -rf "$DIST"' EXIT
else
  VERSION="$(grep -oP '<AgentVersion>\K[^<]+' "$REPO/Directory.Build.props")"
  DIST="$REPO/dist"
fi
UPDATE_KEY=""
[ -f "$KEYS/update-key.pub.txt" ] && UPDATE_KEY="$(tr -d '\n' < "$KEYS/update-key.pub.txt")"
[ -n "$MAINTENANCE_KEY" ] || MAINTENANCE_KEY="$(tr -d '\r\n' < "$KEYS/maintenance-key.pub.txt")"
for key in "$UPDATE_KEY" "$MAINTENANCE_KEY"; do
  [[ "$key" =~ ^[A-Za-z0-9+/=]+$ ]] || { echo 'Prepared kit requires publisher and maintenance public keys' >&2; exit 1; }
  printf '%s' "$key" | base64 -d | openssl pkey -pubin -inform DER -text -noout | grep -q 'ASN1 OID: prime256v1'
done
[[ -f "$KEYS/update-key.pem" && -f "$KEYS/update-key.pass" ]] || { echo 'Publisher signing key required' >&2; exit 1; }
WATCHER_UPDATE_KEY_PASSWORD="$(cat "$KEYS/update-key.pass")"
export WATCHER_UPDATE_KEY_PASSWORD
actual_update_key=$(openssl pkey -in "$KEYS/update-key.pem" -passin env:WATCHER_UPDATE_KEY_PASSWORD -pubout -outform DER | base64 -w0)
[[ "$actual_update_key" == "$UPDATE_KEY" ]] || { echo 'Publisher key pair mismatch; existing kit preserved' >&2; exit 1; }

if [ -f "$KEYS/watcher-codesign.pfx" ] && [ -f "$KEYS/codesign.pass" ]; then
  export WATCHER_SIGN_PFX="$KEYS/watcher-codesign.pfx"
  WATCHER_SIGN_PASSWORD="$(cat "$KEYS/codesign.pass")"
  export WATCHER_SIGN_PASSWORD
  # Метка времени — если TSA доступен; без неё подпись перестанет проверяться
  # в день истечения сертификата (docs/codesign.md).
  if ! curl -fsS -m 5 -o /dev/null http://timestamp.digicert.com 2>/dev/null; then
    echo "TSA недоступен — подпись без метки времени"
    export WATCHER_SIGN_TIMESTAMP=""
  fi
fi

if [ -n "$AGENTS" ]; then
  # Установочный архив: агенты уже собраны (без подписи) — подписать exe и
  # скрипты сертификатом организации, упаковать zip и MSI; .deb/.rpm как есть.
  echo "=== Агент Windows: подпись, zip, MSI (готовые файлы) ==="
  cp -r "$AGENTS/windows" "$DIST/stage"
  if [ -n "${WATCHER_SIGN_PFX:-}" ]; then
    "$REPO/tools/codesign/sign-windows.sh" "$DIST/stage/Watcher.Capture.exe" "$DIST/stage/Watcher.SessionService.exe" \
      "$DIST/stage/GuardRepair.exe"
  fi
  "$REPO/installer/build-msi.sh" --payload "$DIST/stage" --version "$VERSION" --out "$DIST/watcher-agent-$VERSION.msi"
  ( cd "$DIST/stage" && zip -q -r "$DIST/watcher-agent-v$VERSION.zip" . )
  cp "$AGENTS"/linux/*.deb "$AGENTS"/linux/*.rpm "$DIST/"
else
  echo "=== Агент Windows: exe, zip, MSI ==="
  "$REPO/make-bundle.sh" --msi
  echo "=== Агент Linux: tar.gz, .deb, .rpm ==="
  "$REPO/make-bundle-linux.sh" --packages
fi

ZIP="$DIST/watcher-agent-v$VERSION.zip"
MSI="$DIST/watcher-agent-$VERSION.msi"
DEB="$DIST/watcher-agent_${VERSION}-1_amd64.deb"
RPM="$DIST/watcher-agent-${VERSION}-1.x86_64.rpm"
for f in "$ZIP" "$MSI" "$DEB" "$RPM"; do
  [ -f "$f" ] || { echo "Сборка не дала $f" >&2; exit 1; }
done

# База восстановления Linux — манифест рядом с пакетом (install.sh). Windows — подпись
# самого настроенного MSI (windows/watcher-agent.msi.sig, ниже).
for spec in "linux-deb:$DEB" "linux-rpm:$RPM"; do
  platform="${spec%%:*}" file="${spec#*:}"
  bash "$REPO/tools/update-signing/recovery-manifest.sh" "$KEYS/update-key.pem" "$platform" "$VERSION" "$file" "$file.recovery.json"
done

STAGE="$OUT.new"
rm -rf "$STAGE"
install -d -m 700 "$STAGE"
umask 077
mkdir -p "$STAGE/windows" "$STAGE/linux" "$STAGE/certs" "$STAGE/updates"

echo "=== Сертификаты ==="
cp "$KEYS/watcher-ca.crt" "$STAGE/certs/watcher-ca.crt"
openssl x509 -in "$KEYS/watcher-ca.crt" -outform der -out "$STAGE/certs/watcher-ca.cer"
[ -f "$KEYS/watcher-codesign.cer" ] && cp "$KEYS/watcher-codesign.cer" "$STAGE/certs/"
printf '%s\n' "$MAINTENANCE_KEY" > "$STAGE/certs/maintenance-key.pub.txt"
# 0.16: ключ обслуживания, заверенный ключом обновлений. Сервер отдаёт его Linux-агентам
# в heartbeat: агент без защиты сверяет подпись своим ключом обновлений и включает
# защиту сам — как Windows-агент из MSI, куда ключ зашит (подписанный пакет).
printf 'watcher-maintenance-key/1\n%s\n' "$MAINTENANCE_KEY" \
  | openssl dgst -sha256 -sign "$KEYS/update-key.pem" -passin env:WATCHER_UPDATE_KEY_PASSWORD \
  | base64 -w0 > "$STAGE/certs/maintenance-key.sig"
[ -n "$UPDATE_KEY" ] && printf '%s\n' "$UPDATE_KEY" > "$STAGE/certs/update-key.pub.txt"

echo "=== Windows ==="
"$REPO/installer/build-msi.sh" --configure "$MSI" --out "$STAGE/windows/watcher-agent.msi" \
  --server-url "$SERVER_URL" --enroll-token "$ENROLL_TOKEN" --update-key "$UPDATE_KEY" \
  --ca-cert "$KEYS/watcher-ca.crt" --maintenance-key "$MAINTENANCE_KEY"
# Подпись ключом обновлений (тот же формат, что у пакетов «Обновлений»): с ней
# Guard при установке берёт этот MSI базой восстановления. Без .sig рядом агент
# ставится, но удалённые файлы агента Guard вернёт только после первого обновления.
"$REPO/tools/update-signing/sign-update.sh" --key "$KEYS/update-key.pem" \
  --platform windows-msi --version "$VERSION" "$STAGE/windows/watcher-agent.msi" >/dev/null

echo "=== Linux ==="
# Один архив: папка с install.sh, пакетами и CA — распаковал и запустил.
LDIR="watcher-agent-$VERSION"
LSTAGE="$(mktemp -d)"
mkdir "$LSTAGE/$LDIR"
cp "$DEB" "$RPM" "$DEB.recovery.json" "$RPM.recovery.json" "$STAGE/certs/watcher-ca.crt" "$LSTAGE/$LDIR/"
cat > "$LSTAGE/$LDIR/install.sh" <<EOF
#!/bin/sh
# Установка агента Watcher на Linux-станцию из этой папки (от root).
# Адрес сервера, токен регистрации, ключ обновлений и CA уже заданы.
# Дополнительные параметры передаются в watcher-agent-setup, например:
#   sudo sh ./install.sh --no-screen      (сервер без рабочего стола)
#   sudo sh ./install.sh --no-console     (без аудита консоли/SSH)
# Экран и консоль пишутся у всех пользователей; кого не писать — конфиг группы на сервере.
set -e
cd "\$(dirname "\$0")"
[ "\$(id -u)" = 0 ] || { echo "Запускайте от root: sudo sh \$0 \$*" >&2; exit 1; }
if command -v apt-get >/dev/null; then
  recovery_package=./watcher-agent_${VERSION}-1_amd64.deb
  apt-get install -y "\$recovery_package"
elif command -v dnf >/dev/null; then
  recovery_package=./watcher-agent-${VERSION}-1.x86_64.rpm
  dnf install -y "\$recovery_package"
elif command -v yum >/dev/null; then
  recovery_package=./watcher-agent-${VERSION}-1.x86_64.rpm
  yum install -y "\$recovery_package"
else
  echo "Нет ни apt-get, ни dnf/yum — поставьте пакет вручную" >&2
  exit 1
fi
watcher-agent-setup --server-url "$SERVER_URL" --enroll-token "$ENROLL_TOKEN" \\
  --ca-cert ./watcher-ca.crt --update-key "$UPDATE_KEY" --maintenance-key "$MAINTENANCE_KEY" \\
  --recovery-package "\$recovery_package" --recovery-manifest "\$recovery_package.recovery.json" "\$@"
EOF
chmod 700 "$LSTAGE/$LDIR/install.sh"
tar -C "$LSTAGE" --owner=0 --group=0 -czf "$STAGE/linux/watcher-agent-linux-$VERSION.tar.gz" "$LDIR"
rm -rf "$LSTAGE"

echo "=== Android ==="
# adb из platform-tools — только сам adb и его две DLL; APK сервер кладёт в архив
# при скачивании (последний android-пакет из «Обновлений», той же подписи, что OTA).
[ -n "$ADB_ZIP" ] || ADB_ZIP="$REPO/platform-tools-latest-windows.zip"
ATMP="$(mktemp -d)"
if [ ! -f "$ADB_ZIP" ]; then
  curl -fsSL -m 120 -o "$ATMP/pt.zip" https://dl.google.com/android/repository/platform-tools-latest-windows.zip 2>/dev/null \
    && ADB_ZIP="$ATMP/pt.zip" || ADB_ZIP=""
fi
mkdir -p "$ATMP/watcher-android/platform-tools"
if [ -n "$ADB_ZIP" ] && unzip -q -j "$ADB_ZIP" platform-tools/adb.exe platform-tools/AdbWinApi.dll platform-tools/AdbWinUsbApi.dll -d "$ATMP/watcher-android/platform-tools"; then
  mkdir -p "$STAGE/android"
  # CA сервера — агенту при регистрации (caCert): своего CA Watcher в системном
  # хранилище Android нет, без него к https-адресу сервера агент не подключится.
  CA_B64="$(base64 -w0 < "$STAGE/certs/watcher-ca.cer")"
  # cmd.exe: UTF-8 (chcp 65001) и CRLF — иначе кириллица и переводы строк ломаются.
  cat > "$ATMP/watcher-android/install.cmd" <<EOF
@echo off
chcp 65001 >nul
setlocal
rem Подключение Android-устройства к Watcher ($SERVER_URL). Запускать из этой папки.
rem Устройство: сброшено, без Google-аккаунта, включена «Отладка по USB». См. ИНСТРУКЦИЯ.txt.
cd /d "%~dp0"
set ADB=platform-tools\adb.exe
set SERVER=$SERVER_URL
set TOKEN=$ENROLL_TOKEN
set CACERT=$CA_B64
set DEVHOST=%~1
if "%DEVHOST%"=="" set /p DEVHOST=Имя устройства латиницей (например honor-tablet-01): 
if "%DEVHOST%"=="" goto nohost
if not exist watcher-agent.apk goto noapk
echo == Устройство (на экране устройства разрешите отладку с этого компьютера)
%ADB% start-server >nul
%ADB% wait-for-device
%ADB% devices
echo == Установка агента
%ADB% install -r watcher-agent.apk || goto fail
echo == Доступ к статистике использования
%ADB% shell appops set com.watcher.agent GET_USAGE_STATS allow
echo == Назначение владельцем устройства
%ADB% shell dpm set-device-owner com.watcher.agent/.AdminReceiver || goto owner
echo == Регистрация на сервере
%ADB% shell am start -S -n com.watcher.agent/.MainActivity -e serverUrl %SERVER% -e token %TOKEN% -e host %DEVHOST% -e caCert %CACERT% || goto fail
echo.
echo Готово. Через минуту устройство %DEVHOST% появится в веб-интерфейсе (Хосты).
echo Дальше: отключите Play Защиту и оптимизацию батареи для Watcher, выключите отладку по USB — см. ИНСТРУКЦИЯ.txt.
goto end
:owner
echo.
echo Не удалось назначить владельца: на устройстве есть аккаунты (Google и др.) или уже есть владелец.
echo Сделайте сброс до заводских настроек и пройдите мастер БЕЗ входа в аккаунт, затем повторите.
goto end
:noapk
echo Нет watcher-agent.apk рядом со скриптом — скачайте архив заново со страницы «Установка агентов».
goto end
:nohost
echo Имя устройства не задано.
goto end
:fail
echo.
echo Ошибка — см. вывод выше.
:end
pause
EOF
  cat > "$ATMP/watcher-android/ИНСТРУКЦИЯ.txt" <<EOF
Подключение Android-устройства к Watcher ($SERVER_URL)
$(printf '=%.0s' $(seq 1 70))
Агент становится владельцем устройства (device owner): защищён от удаления,
сам выдаёт себе разрешения. Android разрешает это только на ЧИСТОМ устройстве.

1. Сброс до заводских настроек. Мастер первого запуска — язык, Wi-Fi,
   БЕЗ входа в Google-аккаунт (и любые другие аккаунты) — иначе шаг 6 не пройдёт.
2. Режим разработчика: Настройки → О телефоне (Об устройстве) → 7 раз нажать
   «Номер сборки» (на Honor/Huawei — «Номер сборки», на Xiaomi — «Версия MIUI/ОС»).
3. Настройки → Система → Для разработчиков → включить «Отладка по USB».
4. Подключить устройство кабелем к компьютеру с Windows. Если устройство не
   видно — режим USB «Передача файлов», драйвер производителя (HiSuite и т.п.).
5. На устройстве появится запрос «Разрешить отладку по USB?» — отметить
   «Всегда разрешать с этого компьютера» и «Разрешить».
6. Распаковать архив и запустить install.cmd (можно с именем: install.cmd honor-tablet-01).
   Скрипт делает то же, что вручную:
     platform-tools\adb.exe devices
     platform-tools\adb.exe install -r watcher-agent.apk
     platform-tools\adb.exe shell appops set com.watcher.agent GET_USAGE_STATS allow
     platform-tools\adb.exe shell dpm set-device-owner com.watcher.agent/.AdminReceiver
     platform-tools\adb.exe shell am start -S -n com.watcher.agent/.MainActivity -e serverUrl $SERVER_URL -e token <токен> -e host <имя> -e caCert <CA из install.cmd>
7. После регистрации на устройстве:
   - Play Маркет → профиль → Play Защита → выключить «Проверять приложения»
     (иначе Play Защита со временем блокирует агента и его обновления);
   - Батарея / Запуск приложений → Watcher → без ограничений, автозапуск
     (Xiaomi: «Автозапуск» + «Без ограничений»; Samsung: «Не ограничивать»;
     Huawei/Honor: «Управлять вручную», все три переключателя);
   - теперь можно вернуть аккаунты пользователя;
   - выключить «Отладка по USB».
8. Проверить в веб-интерфейсе: Хосты → имя устройства — на связи, роль «владелец».

Снять агента без сброса: в веб-интерфейсе на хосте — «Снять роль владельца»,
затем удалить приложение. Токен регистрации в install.cmd — храните архив как секрет.
EOF
  sed -i 's/$/\r/' "$ATMP/watcher-android/install.cmd" "$ATMP/watcher-android/ИНСТРУКЦИЯ.txt"
  ( cd "$ATMP" && zip -q -r "$STAGE/android/watcher-android-tools.zip" watcher-android )
else
  echo "platform-tools (adb) не найден и не скачан — комплект без Android (--adb-zip)" >&2
fi
rm -rf "$ATMP"
# Пакеты автообновления — не для скачивания: install-server.sh загружает их в
# «Обновления» по updates/packages.tsv. Windows — только MSI (0.16: windows-zip и
# переход C:\Watcher через zip убраны, на проде все Windows-агенты из MSI).
cp "$DEB" "$RPM" "$STAGE/updates/"
# Базу восстановления пакет обновления не несёт: агент кладёт подпись предложения
# рядом со скачанным package.msi, Guard берёт его по ней (0.16).
"$REPO/installer/build-msi.sh" --configure "$MSI" --out "$STAGE/updates/$(basename "$MSI")" \
  --server-url "$SERVER_URL" --update-key "$UPDATE_KEY" --maintenance-key "$MAINTENANCE_KEY" \
  --ca-cert "$KEYS/watcher-ca.crt"
[ -n "$APK" ] || [ -z "$AGENTS" ] || APK="$AGENTS/android/watcher-agent.apk"
UPDATE_SPECS=("windows-msi:$STAGE/updates/$(basename "$MSI")"
              "linux-deb:$STAGE/updates/$(basename "$DEB")" "linux-rpm:$STAGE/updates/$(basename "$RPM")")
if [ -n "$APK" ] && [ -f "$APK" ]; then
  cp "$APK" "$STAGE/updates/watcher-agent-$VERSION.apk"
  UPDATE_SPECS+=("android:$STAGE/updates/watcher-agent-$VERSION.apk")
else
  echo "APK Android-агента нет — в «Обновления» не попадёт (--apk)" >&2
fi

if [ -f "$KEYS/update-key.pem" ] && [ -f "$KEYS/update-key.pass" ]; then
  echo "=== Подписи пакетов автообновления ==="
  WATCHER_UPDATE_KEY_PASSWORD="$(cat "$KEYS/update-key.pass")"
  export WATCHER_UPDATE_KEY_PASSWORD
  : > "$STAGE/updates/packages.tsv"
  for spec in "${UPDATE_SPECS[@]}"; do
    platform="${spec%%:*}" file="${spec#*:}"
    "$REPO/tools/update-signing/sign-update.sh" --key "$KEYS/update-key.pem" \
      --platform "$platform" --version "$VERSION" "$file" >/dev/null
    # платформа, версия, пакет и подпись (относительно комплекта) — для install-server.sh
    printf '%s\t%s\t%s\t%s\n' "$platform" "$VERSION" "updates/$(basename "$file")" "updates/$(basename "$file").sig" \
      >> "$STAGE/updates/packages.tsv"
  done
fi

HOST="${SERVER_URL#https://}"; HOST="${HOST#http://}"; HOST="${HOST%%/*}"
cat > "$STAGE/README.txt" <<EOF
Комплект установки агентов Watcher $VERSION для $SERVER_URL
$(printf '=%.0s' $(seq 1 70))

Всё здесь уже настроено на этот сервер: адрес, токен регистрации, ключ
обновлений и корневой CA зашиты в MSI и в скрипт для Linux. Каталог содержит
токен регистрации — храните как секрет; токен отзывается в веб-UI
(Настройки → Агенты), новый комплект — sudo ./install-server.sh --rebuild-kit.

WINDOWS — windows/watcher-agent.msi (+ необязательно watcher-agent.msi.sig)
---------------------------------------------------------------------------
Одна станция — двойной щелчок по watcher-agent.msi (параметры не нужны).
watcher-agent.msi.sig — необязательно: агент ставится и обновляется без неё.
Рядом с MSI (та же папка, шара GPO) она даёт защите агента (GuardRepair) сразу
возвращать удалённые файлы агента; без неё — после первого обновления с сервера.

Скриптом / удалённо:
  msiexec /i \\\\<шара>\\watcher\\watcher-agent.msi /qn /l*v C:\\Windows\\Temp\\watcher-agent-msi.log
  Свойства из командной строки перекрывают зашитые: TERMINALAUDIT=0 —
  без аудита SSH-сессий (по умолчанию включён); SERVERURL=, ENROLLTOKEN= —
  другой сервер или токен.

GPO (сотни станций):
  1. Положите windows/watcher-agent.msi и .msi.sig на шару, доступную на чтение
     «Компьютерам домена» (Domain Computers) — и только им: в MSI токен.
  2. Computer Configuration → Policies → Software Settings → Software
     Installation → New → Package → UNC-путь к MSI → Assigned.
  3. Доверие сертификатам — той же или отдельной GPO, Computer Configuration →
     Policies → Windows Settings → Security Settings → Public Key Policies:
       Trusted Root Certification Authorities ← certs/watcher-ca.cer
         (MSI импортирует CA и сам, GPO — чтобы он был до установки и не
         пропадал);
       Trusted Root + Trusted Publishers ← certs/watcher-codesign.cer
         (подпись агента: исключения AV/EDR и AppLocker по издателю,
         PowerShell AllSigned — docs/codesign.md, docs/av-edr.md).
  Обновления дальше приходят с сервера (Настройки → Обновления) и ставятся
  тем же MSI (msiexec, обновление продукта) — в «Программах и компонентах»
  видна актуальная версия; новый MSI через GPO раскатывать не нужно.
Защита GuardRepair включается автоматически: сначала отправьте подписанную команду
«Удалить» из веб-интерфейса сервера; обычный msiexec /x требует разрешения обслуживания.

LINUX (Debian/Ubuntu/Astra, RHEL/Alma/Rocky/РЕД ОС)
---------------------------------------------------
Один архив linux/watcher-agent-linux-$VERSION.tar.gz (install.sh, .deb, .rpm,
CA). На станции от root:
  tar xzf watcher-agent-linux-$VERSION.tar.gz
  sudo sh watcher-agent-$VERSION/install.sh [--no-screen] [--no-console]
Экран и консоль/SSH пишутся у всех пользователей; кого не писать —
excludeUsers/allowUsers в конфиге группы (веб-UI, «Конфиг»).
Массово по SSH, например:
  for h in host1 host2; do scp linux/watcher-agent-linux-$VERSION.tar.gz \$h:/tmp/ && ssh -t \$h 'cd /tmp && tar xzf watcher-agent-linux-$VERSION.tar.gz && sudo sh watcher-agent-$VERSION/install.sh'; done

ЧТО ЕЩЁ ЗДЕСЬ
-------------
certs/watcher-ca.crt / .cer   корневой CA сервера ($HOST), PEM и DER
certs/watcher-codesign.cer    издатель подписи агента Windows
certs/update-key.pub.txt      открытый ключ обновлений
certs/maintenance-key.pub.txt открытый ключ команд обслуживания
certs/maintenance-key.sig       тот же ключ, заверенный ключом обновлений (Linux-агенты включают защиту при обновлении)
updates/                      пакеты автообновления с подписями — install-server.sh
                              уже загрузил их в Настройки → Обновления

GuardRepair включается при установке Windows/Linux автоматически. Подписанный
пакет восстановления сохраняется в закрытом каталоге вне программ агента;
восстановление этой версии не требует сети. Приватные ключи в комплект не входят.
EOF

# Манифест для страницы «Установка агентов» (сервер отдаёт только эти файлы).
# Токен — только SHA-256: по нему сервер находит запись токена и показывает его
# срок и состояние, сам токен в манифест не попадает.
TOKEN_SHA=""
[ -z "$ENROLL_TOKEN" ] || TOKEN_SHA="$(printf '%s' "$ENROLL_TOKEN" | sha256sum | cut -d' ' -f1)"
python3 - "$STAGE" "$VERSION" "$SERVER_URL" "$UPDATE_KEY" "$TOKEN_SHA" <<'PY'
import datetime, hashlib, json, os, sys
stage, version, url, key, token = sys.argv[1:]
rels = ["README.txt"] + sorted(
    os.path.relpath(os.path.join(d, n), stage)
    for top in ("windows", "linux", "android", "certs")
    for d, _, names in os.walk(os.path.join(stage, top)) for n in names)
files = []
for rel in rels:
    h = hashlib.sha256()
    with open(os.path.join(stage, rel), "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    files.append({"path": rel, "size": os.path.getsize(os.path.join(stage, rel)), "sha256": h.hexdigest()})
json.dump({"version": version, "serverUrl": url,
           "builtAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
           "updateKey": key or None, "enrollTokenSha256": token or None, "files": files},
          open(os.path.join(stage, "kit.json"), "w"), indent=2)
PY

rm -rf "$OUT.old"
[ -d "$OUT" ] && mv "$OUT" "$OUT.old"
mv "$STAGE" "$OUT"
rm -rf "$OUT.old"
echo "Комплект: $OUT"
