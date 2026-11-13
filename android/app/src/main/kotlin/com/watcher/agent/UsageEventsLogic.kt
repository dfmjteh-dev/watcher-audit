package com.watcher.agent

/**
 * Чистая логика разбора UsageEvents (Этап 3, docs/android-plan.md): превращает
 * «сырые» события системы (время, тип, пакет) в [PendingEvent]
 * app_foreground/app_background и двигает курсор «последнее обработанное время»,
 * чтобы не слать повторно. Без android-зависимостей — покрыта JVM unit-тестами;
 * получение событий из UsageStatsManager — в [UsageEventsCollector].
 */
object UsageEventsLogic {

    // Константы UsageEvents (дублируем числами, чтобы логика была чистой JVM):
    // MOVE_TO_FOREGROUND = 1, ACTIVITY_RESUMED = 1 (то же число);
    // MOVE_TO_BACKGROUND = 2, ACTIVITY_PAUSED = 2 (то же число).
    const val FOREGROUND = 1
    const val BACKGROUND = 2

    /** Одно системное событие использования: время (мс), тип, пакет. */
    data class Raw(val timestampMs: Long, val type: Int, val packageName: String?)

    /** Разобранные события для очереди и новый курсор (max обработанное время). */
    data class Outcome(val events: List<PendingEvent>, val newCursor: Long)

    /**
     * Берём только события строго новее [cursorMs] (queryEvents отдаёт полуинтервал
     * и крайнее событие могло уже уйти в прошлый раз), только fg/bg и только с
     * известным пакетом. Курсор двигаем на максимум обработанного времени.
     */
    fun process(raw: List<Raw>, cursorMs: Long): Outcome {
        var cursor = cursorMs
        val out = ArrayList<PendingEvent>()
        for (e in raw.sortedBy { it.timestampMs }) {
            if (e.timestampMs <= cursorMs) continue
            val pkg = e.packageName ?: continue
            val event = when (e.type) {
                FOREGROUND -> Events.appForeground(pkg, TimeUtil.iso(e.timestampMs))
                BACKGROUND -> Events.appBackground(pkg, TimeUtil.iso(e.timestampMs))
                else -> null
            } ?: continue
            out.add(event)
            if (e.timestampMs > cursor) cursor = e.timestampMs
        }
        return Outcome(out, cursor)
    }
}
