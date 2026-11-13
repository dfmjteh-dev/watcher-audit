#!/bin/sh
# Установка собранного debug-APK агента watcher на все подключённые adb-
# устройства/эмуляторы и вывод установленной версии (Этап 1).
# Требуется Android SDK (adb в PATH) и собранный debug-APK (tools/build-apk.sh).
set -eu

package="com.watcher.agent"
root="$(cd "$(dirname "$0")/.." && pwd)"
apk="$root/app/build/outputs/apk/debug/app-debug.apk"

if [ ! -f "$apk" ]; then
    echo "APK не найден: $apk — соберите сначала (tools/build-apk.sh debug)" >&2
    exit 1
fi

devices="$(adb devices | awk 'NR > 1 && $2 == "device" { print $1 }')"
if [ -z "$devices" ]; then
    echo "Нет подключённых adb-устройств." >&2
    exit 1
fi

# Серийники adb не содержат пробелов/глобов — пословное разбиение безопасно.
for serial in $devices; do
    echo "== $serial =="
    adb -s "$serial" install -r "$apk"
    adb -s "$serial" shell dumpsys package "$package" | grep versionName
done
