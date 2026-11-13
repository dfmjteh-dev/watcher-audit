#!/bin/sh
# Прогон тестов агента watcher (Этап 1).
# Всегда гоняет JVM unit-тесты; инструментальные — только если adb видит хотя
# бы одно устройство/эмулятор. Требуется JDK 17, Android SDK (adb в PATH) и
# сгенерированный Gradle wrapper (gradle wrapper --gradle-version 8.11.1).
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root" || exit 1

if [ ! -x ./gradlew ]; then
    echo "gradlew не найден — сгенерируйте wrapper (gradle wrapper --gradle-version 8.11.1)" >&2
    exit 1
fi

echo "== JVM unit-тесты =="
./gradlew testDebugUnitTest

# Серийники только у устройств в состоянии device (не offline/unauthorized).
devices="$(adb devices | awk 'NR > 1 && $2 == "device" { print $1 }')"
if [ -n "$devices" ]; then
    echo "== Инструментальные тесты (подключены устройства) =="
    ./gradlew connectedDebugAndroidTest
else
    echo "Устройств adb нет — инструментальные тесты пропущены."
fi
