package com.watcher.agent

import java.time.Instant

/**
 * Время событий в ISO 8601 UTC (…Z) — тот же формат, что сервер парсит
 * (EventsEndpoints.TryParseTs, Db.ParseUtc). java.time доступен на minSdk 29
 * без desugaring. Чистая функция — покрыта JVM unit-тестами.
 */
object TimeUtil {
    fun iso(epochMs: Long): String = Instant.ofEpochMilli(epochMs).toString()
}
