package com.watcher.agent

import android.Manifest
import android.os.Build

/**
 * Чистая логика политики владельца устройства (Этап 5, docs/android-plan.md) —
 * без android-вызовов, покрыта JVM unit-тестами. Решает, ЧТО делает владелец;
 * сами вызовы DevicePolicyManager — в [DeviceOwner].
 */
object DeviceOwnerPolicy {

    /**
     * Runtime-разрешения, которые владелец выдаёт агенту сам (без участия
     * пользователя), с указанием минимального SDK. Только опасные (runtime)
     * разрешения: доступ к статистике использования (PACKAGE_USAGE_STATS) —
     * специальный appop, а не runtime-разрешение, его setPermissionGrantState
     * не выдаёт (см. [DeviceOwner] и docs/android-plan.md, «Отклонение»).
     */
    fun runtimePermissionsToGrant(sdkInt: Int = Build.VERSION.SDK_INT): List<String> =
        buildList {
            // 13+: без него не видно постоянное уведомление агента.
            if (sdkInt >= Build.VERSION_CODES.TIRAMISU) add(Manifest.permission.POST_NOTIFICATIONS)
        }
}
