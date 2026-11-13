#!/bin/sh
# Онбординг Android-устройства в watcher (Этап 7, docs/android-plan.md).
# Назначает агента владельцем устройства (device owner) и авто-регистрирует его
# на сервере — без ручного ввода, кроме мастера первого запуска Android.
#
# Запускать на ЧИСТОМ устройстве (после сброса, аккаунты не добавлены), с
# включённой отладкой по USB. Токен регистрации — Настройки → Агенты в веб-UI.
#
# Использование:
#   tools/provision.sh --server URL --token wenr_… [--host ИМЯ] [--serial S]
#                      [--apk ПУТЬ] [--ca-cert ФАЙЛ] [--allow-provisioned] [--disable-adb]
#
#   --server        адрес сервера watcher (http/https, как видит устройство)
#   --token         токен регистрации (wenr_…)
#   --host          имя хоста (по умолчанию — модель устройства)
#   --serial        serial устройства adb (если подключено несколько)
#   --apk           путь к APK (по умолчанию — свежесобранный debug)
#   --ca-cert       корневой CA сервера (watcher-ca.crt/.cer из комплекта) — для
#                   https с собственным CA Watcher (его нет в системных CA Android)
#   --allow-provisioned  снять device_provisioned (для эмулятора/стенда; на
#                        реальном устройстве так не делают — запускают до мастера)
#   --disable-adb   в конце выключить отладку по USB (оборвёт эту adb-сессию)
set -eu

PKG=com.watcher.agent
ADMIN="$PKG/.AdminReceiver"
server="" token="" host="" serial="" apk="" ca="" allow_prov=0 disable_adb=0
root="$(cd "$(dirname "$0")/.." && pwd)"

while [ $# -gt 0 ]; do
    case "$1" in
        --server) server="$2"; shift 2;;
        --token) token="$2"; shift 2;;
        --host) host="$2"; shift 2;;
        --serial) serial="$2"; shift 2;;
        --apk) apk="$2"; shift 2;;
        --ca-cert) ca="$2"; shift 2;;
        --allow-provisioned) allow_prov=1; shift;;
        --disable-adb) disable_adb=1; shift;;
        *) echo "неизвестный аргумент: $1" >&2; exit 2;;
    esac
done

if [ -z "$server" ] || [ -z "$token" ]; then
  echo "нужны --server и --token" >&2
  exit 2
fi
[ -n "$apk" ] || apk="$root/android/app/build/outputs/apk/debug/app-debug.apk"
[ -f "$apk" ] || apk="$root/app/build/outputs/apk/debug/app-debug.apk"
[ -f "$apk" ] || { echo "APK не найден: $apk (соберите tools/build-apk.sh)" >&2; exit 1; }

ADB="adb"
[ -n "$serial" ] && ADB="adb -s $serial"
sh_() { $ADB shell "$@"; }

echo "== устройство"
$ADB wait-for-device
man=$(sh_ getprop ro.product.manufacturer | tr -d '\r')
model=$(sh_ getprop ro.product.model | tr -d '\r')
echo "производитель: $man, модель: $model"

echo "== проверка: аккаунтов нет (device owner ставится только на чистом устройстве)"
accounts=$(sh_ dumpsys account | grep -c "Account {" || true)
if [ "${accounts:-0}" -gt 0 ]; then
    echo "на устройстве есть аккаунты ($accounts) — удалите их (или сделайте сброс) и повторите" >&2
    exit 1
fi

echo "== проверка: роль владельца свободна"
owner_line=$(sh_ dumpsys device_policy | grep -i "Device Owner" -A1 || true)
if echo "$owner_line" | grep -q "$PKG"; then
    echo "агент уже владелец устройства — продолжаю (идемпотентно)"
elif echo "$owner_line" | grep -qi "admin="; then
    echo "владельцем устройства уже является другое приложение — прервано" >&2
    echo "$owner_line" >&2
    exit 1
fi

echo "== установка APK"
$ADB install -r "$apk" >/dev/null && echo "установлен"

echo "== доступ к статистике использования (appop; владелец его сам не выдаёт)"
sh_ appops set "$PKG" GET_USAGE_STATS allow || true

prov=$(sh_ settings get global device_provisioned | tr -d '\r')
if [ "$prov" = "1" ]; then
    if [ "$allow_prov" = "1" ]; then
        echo "== снимаю device_provisioned (--allow-provisioned)"
        sh_ settings put global device_provisioned 0
        sh_ settings put secure user_setup_complete 0
    else
        echo "device_provisioned=1: устройство уже прошло мастер первого запуска." >&2
        echo "Запустите скрипт на свежесброшенном устройстве ДО мастера, либо --allow-provisioned (эмулятор/стенд)." >&2
        exit 1
    fi
fi

echo "== назначение роли владельца"
sh_ dpm set-device-owner "$ADMIN" | tail -1

echo "== авто-регистрация агента на сервере"
sh_ pm grant "$PKG" android.permission.POST_NOTIFICATIONS 2>/dev/null || true
# CA — base64 DER одной строкой (PEM тоже подойдёт, но в нём пробелы).
ca_b64=""
if [ -n "$ca" ]; then
    inform=pem
    if openssl x509 -inform der -in "$ca" -noout 2>/dev/null; then inform=der; fi
    ca_b64=$(openssl x509 -inform "$inform" -in "$ca" -outform der | base64 | tr -d '\n')
    [ -n "$ca_b64" ] || { echo "--ca-cert: не сертификат X.509: $ca" >&2; exit 2; }
fi
set -- -e serverUrl "$server" -e token "$token"
[ -z "$host" ] || set -- "$@" -e host "$host"
[ -z "$ca_b64" ] || set -- "$@" -e caCert "$ca_b64"
sh_ am start -S -f 0x10008000 -n "$PKG/.MainActivity" "$@" >/dev/null

echo "== самопроверка (до 90 c)"
i=0
while [ $i -lt 18 ]; do
    owner_ok=$(sh_ dumpsys device_policy | grep -ci "$PKG" || true)
    usage_ok=$(sh_ appops get "$PKG" GET_USAGE_STATS | grep -c allow || true)
    running=$(sh_ pidof "$PKG" | tr -d '\r')
    if [ "${owner_ok:-0}" -gt 0 ] && [ "${usage_ok:-0}" -gt 0 ] && [ -n "$running" ]; then
        echo "OK: владелец назначен, доступ к статистике выдан, агент запущен"
        break
    fi
    i=$((i+1)); sleep 5
done
[ $i -lt 18 ] || { echo "самопроверка не прошла за отведённое время" >&2; exit 1; }

echo "== подсказка по вендорской прошивке (фоновые ограничения)"
case "$(echo "$man" | tr '[:upper:]' '[:lower:]')" in
    xiaomi|redmi|poco) echo "  MIUI/HyperOS: включите «Автозапуск» для Watcher и снимите экономию батареи (Настройки → Приложения → Watcher → Экономия заряда → Без ограничений).";;
    huawei|honor) echo "  EMUI/HarmonyOS: разрешите автозапуск и фоновую активность (Настройки → Батарея → Запуск приложений → Watcher → вручную).";;
    oppo|realme|oneplus) echo "  ColorOS/OxygenOS: отключите оптимизацию батареи и разрешите фоновую работу для Watcher.";;
    vivo|iqoo) echo "  FuntouchOS/OriginOS: включите «Высокое потребление в фоне» и автозапуск для Watcher.";;
    samsung) echo "  One UI: Настройки → Батарея → Watcher → Не ограничивать (убрать из «спящих» приложений).";;
    *) echo "  Проверьте, что система не ограничивает фоновую работу и автозапуск Watcher.";;
esac

if [ "$disable_adb" = "1" ]; then
    echo "== отключение отладки по USB (оборвёт эту adb-сессию)"
    sh_ settings put global adb_enabled 0 || true
fi

echo "ГОТОВО. Проверьте в веб-UI (Хосты → $model): агент на связи, роль владельца — «владелец»."
