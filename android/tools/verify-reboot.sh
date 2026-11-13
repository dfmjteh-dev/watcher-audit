#!/bin/sh
# Проверка автозапуска агента watcher после перезагрузки (Этап 1, критерий
# готовности): ставим debug-APK, один раз запускаем MainActivity (без первого
# запуска система не доставляет BOOT_COMPLETED), перезагружаем устройство и
# убеждаемся, что AgentService поднялся сам как foreground-служба. По всем
# подключённым устройствам.
# Требуется Android SDK (adb в PATH) и собранный debug-APK (tools/build-apk.sh).
set -eu

package="com.watcher.agent"
activity="$package/.MainActivity"
# Строка, по которой распознаём живую foreground-службу в dumpsys.
service_marker="$package/.AgentService"
root="$(cd "$(dirname "$0")/.." && pwd)"
apk="$root/app/build/outputs/apk/debug/app-debug.apk"

# Пауза перед ребутом: PackageManager пишет флаг «приложение запускалось» в
# packages.xml с задержкой. Ребут раньше, чем флаг окажется на диске, вернёт
# приложение в stopped-состояние, и BOOT_COMPLETED (FLAG_EXCLUDE_STOPPED_PACKAGES)
# до него не дойдёт. На реальном устройстве между установкой и ребутом времени
# всегда достаточно; на стенде ждём явно.
settle_seconds=25
# После загрузки ОС система ещё догоняет boot-time dexopt и рассылку
# BOOT_COMPLETED — служба появляется не мгновенно. Опрашиваем с ретраями.
poll_attempts=30
poll_interval=3

if [ ! -f "$apk" ]; then
    echo "APK не найден: $apk — соберите сначала (tools/build-apk.sh debug)" >&2
    exit 1
fi

devices="$(adb devices | awk 'NR > 1 && $2 == "device" { print $1 }')"
if [ -z "$devices" ]; then
    echo "Нет подключённых adb-устройств." >&2
    exit 1
fi

# Жив ли AgentService как foreground прямо сейчас (0 — да).
service_foreground() {
    adb -s "$1" shell dumpsys activity services "$package" 2>/dev/null \
        | tr -d '\r' | grep -q "isForeground=true"
}

status=0
# Серийники adb не содержат пробелов/глобов — пословное разбиение безопасно.
for serial in $devices; do
    echo "== $serial =="
    adb -s "$serial" install -r "$apk" >/dev/null
    # Первый запуск приложения — иначе BOOT_COMPLETED не придёт.
    adb -s "$serial" shell am start -n "$activity" >/dev/null
    sleep "$settle_seconds"
    adb -s "$serial" shell sync

    if service_foreground "$serial"; then
        echo "до ребута: $service_marker работает (foreground)"
    else
        echo "FAIL: $service_marker не поднялся ещё до ребута ($serial)" >&2
        status=1
        continue
    fi

    adb -s "$serial" reboot
    adb -s "$serial" wait-for-device
    # Дождаться завершения загрузки ОС (не только подключения adb).
    while [ "$(adb -s "$serial" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" != "1" ]; do
        sleep 2
    done

    echo "-- ждём автозапуск службы после перезагрузки --"
    attempt=0
    while [ "$attempt" -lt "$poll_attempts" ]; do
        if service_foreground "$serial"; then
            echo "OK: $service_marker поднялся сам после перезагрузки ($serial)"
            break
        fi
        attempt=$((attempt + 1))
        sleep "$poll_interval"
    done
    if [ "$attempt" -ge "$poll_attempts" ]; then
        echo "FAIL: $service_marker не появился за $((poll_attempts * poll_interval)) c после перезагрузки ($serial)" >&2
        status=1
    fi
done

exit "$status"
