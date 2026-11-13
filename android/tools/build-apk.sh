#!/bin/sh
# Сборка APK агента watcher (Этап 1).
# Аргумент: debug | release (по умолчанию debug).
# Требуется JDK 17 и Android SDK (ANDROID_HOME / ANDROID_SDK_ROOT), а также
# сгенерированный Gradle wrapper (docs/android-build.md). Ключ подписи release —
# через переменные окружения WATCHER_ANDROID_KEYSTORE* (вне репозитория).
set -eu

variant="${1:-debug}"

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root" || exit 1

if [ ! -x ./gradlew ]; then
    echo "gradlew не найден — сгенерируйте wrapper (docs/android-build.md)" >&2
    exit 1
fi

case "$variant" in
    debug)
        ./gradlew assembleDebug
        apk="app/build/outputs/apk/debug/app-debug.apk"
        ;;
    release)
        ./gradlew assembleRelease
        # С ключом — app-release.apk, без ключа — app-release-unsigned.apk.
        apk="$(find app/build/outputs/apk/release -maxdepth 1 -name 'app-release*.apk' | head -n1)"
        ;;
    *)
        echo "использование: $0 [debug|release]" >&2
        exit 2
        ;;
esac

echo "APK: $root/$apk"
