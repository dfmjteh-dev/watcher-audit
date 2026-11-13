package com.watcher.agent

import android.app.AppOpsManager
import android.content.Context
import android.os.Process

/**
 * Доступ к статистике использования (Этап 3) — специальный app-op, не runtime-
 * разрешение. Проверяем явно: без него UsageStats/NetworkStats молча отдают
 * пусто. Вендорские прошивки бывают странными — любой сбой проверки = «нет доступа».
 */
object UsageAccess {
    fun granted(context: Context): Boolean =
        try {
            val ops = context.getSystemService(AppOpsManager::class.java)
            ops != null && ops.unsafeCheckOpNoThrow(
                AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), context.packageName,
            ) == AppOpsManager.MODE_ALLOWED
        } catch (e: RuntimeException) {
            false
        }
}
