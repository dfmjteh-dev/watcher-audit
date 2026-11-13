package com.watcher.agent

/**
 * Состояние режима киоска (Этап 13, docs/android-plan.md). Связь службы (решает
 * по политике) и [KioskActivity] (держит lock-task на разрешённых приложениях).
 */
object KioskState {
    /** Разрешённые в киоске приложения (пусто — киоск выключен). */
    @Volatile var allowed: Set<String> = emptySet()

    /** Экран-лаунчер киоска на переднем плане в lock-task. */
    @Volatile var active = false
}
