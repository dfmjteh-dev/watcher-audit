package com.watcher.agent

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import java.util.UUID

/**
 * Одно событие для очереди/отправки (Этап 3, docs/android-plan.md).
 *
 * [ts] — время события на устройстве в ISO 8601 UTC (…Z); [type] — один из типов
 * сервера (Db.AndroidEventTypes); [pkg] — приложение, если применимо; [detail] —
 * непрозрачный для сервера JSON-блоб строкой (type-специфичные поля) либо null.
 * [id] — идентификатор строки в локальной очереди (0 у ещё не поставленного).
 * [eventId] — UUID события, назначается при создании и хранится в очереди: повтор
 * пачки после потерянного ответа сервер распознаёт и не дублирует (идемпотентно).
 */
data class PendingEvent(
    val id: Long,
    val ts: String,
    val type: String,
    val pkg: String?,
    val detail: String?,
    val eventId: String = UUID.randomUUID().toString(),
) {
    constructor(ts: String, type: String, pkg: String? = null, detail: String? = null) :
        this(0, ts, type, pkg, detail)
}

/**
 * Локальная очередь событий на встроенном SQLite (Этап 3). Сторонних библиотек
 * не добавляем — только [SQLiteOpenHelper] из Android SDK (docs/development.md,
 * «Политика зависимостей»).
 *
 * Поведение при отсутствии сети: события копятся; старше [MAX_AGE_MS] (по времени
 * постановки, не по часам события) и сверх [maxRows] удаляются самые старые — очередь не растёт безгранично и не роняет агента
 * (критерий готовности Этапа 3). Отправка — пачками (take → POST → delete);
 * при неуспехе события остаются (delete не вызывается).
 */
class EventQueue(context: Context, private val maxRows: Int = DEFAULT_MAX_ROWS) :
    SQLiteOpenHelper(context.applicationContext, DB_NAME, null, DB_VERSION) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE $TABLE (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts TEXT NOT NULL,
              type TEXT NOT NULL,
              package TEXT,
              detail TEXT,
              event_id TEXT NOT NULL UNIQUE,
              queued_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        if (oldVersion < 2) {
            db.execSQL("ALTER TABLE $TABLE ADD COLUMN event_id TEXT")
            db.execSQL("ALTER TABLE $TABLE ADD COLUMN queued_at INTEGER NOT NULL DEFAULT 0")
            // Сначала читаем id целиком, потом обновляем: не пишем в таблицу,
            // пока по ней открыт курсор.
            val ids = db.rawQuery("SELECT id FROM $TABLE WHERE event_id IS NULL", null).use { rows ->
                ArrayList<Long>().apply { while (rows.moveToNext()) add(rows.getLong(0)) }
            }
            val queuedAt = System.currentTimeMillis()
            for (id in ids) {
                val values = ContentValues().apply {
                    put("event_id", UUID.randomUUID().toString())
                    put("queued_at", queuedAt)
                }
                db.update(TABLE, values, "id = ?", arrayOf(id.toString()))
            }
            db.execSQL("CREATE UNIQUE INDEX IF NOT EXISTS pending_events_event_id ON $TABLE(event_id)")
        }
    }

    /** Поставить событие и урезать очередь до лимита (старейшие — прочь). */
    fun enqueue(event: PendingEvent) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.insertOrThrow(TABLE, null, event.toValues())
            trimLocked(db)
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    /** Поставить пачку событий одной транзакцией (из коллекторов). */
    fun enqueueAll(events: List<PendingEvent>) {
        if (events.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            for (e in events) db.insertOrThrow(TABLE, null, e.toValues())
            trimLocked(db)
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    /** Старейшие события для отправки (по возрастанию id — в хронологическом порядке). */
    fun take(limit: Int): List<PendingEvent> {
        val n = limit.coerceIn(1, MAX_ROWS)
        val list = ArrayList<PendingEvent>()
        readableDatabase.query(
            TABLE, arrayOf("id", "ts", "type", "package", "detail", "event_id"),
            null, null, null, null, "id ASC", n.toString(),
        ).use { c ->
            while (c.moveToNext()) {
                list.add(
                    PendingEvent(
                        id = c.getLong(0),
                        ts = c.getString(1),
                        type = c.getString(2),
                        pkg = if (c.isNull(3)) null else c.getString(3),
                        detail = if (c.isNull(4)) null else c.getString(4),
                        eventId = c.getString(5),
                    ),
                )
            }
        }
        return list
    }

    /** Удалить подтверждённые сервером события по id (после успешной отправки). */
    fun delete(ids: List<Long>) {
        if (ids.isEmpty()) return
        val placeholders = ids.joinToString(",") { "?" }
        writableDatabase.delete(TABLE, "id IN ($placeholders)", ids.map { it.toString() }.toTypedArray())
    }

    fun count(): Int =
        readableDatabase.rawQuery("SELECT COUNT(*) FROM $TABLE", null).use { c ->
            if (c.moveToFirst()) c.getInt(0) else 0
        }

    /** Автоочистка по времени помещения в очередь, независимо от часов события. */
    fun prune(now: Long = System.currentTimeMillis()) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.delete(TABLE, "queued_at < ?", arrayOf((now - MAX_AGE_MS).toString()))
            trimLocked(db)
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    private fun trimLocked(db: SQLiteDatabase) {
        val limit = maxRows.coerceIn(1, MAX_ROWS)
        db.execSQL(
            "DELETE FROM $TABLE WHERE id NOT IN (SELECT id FROM $TABLE ORDER BY id DESC LIMIT ?)",
            arrayOf(limit.toString()),
        )
    }

    private fun PendingEvent.toValues() = ContentValues().apply {
        put("ts", ts)
        put("type", type)
        put("package", pkg)
        put("detail", detail)
        put("event_id", eventId)
        put("queued_at", System.currentTimeMillis())
    }

    companion object {
        private const val DB_NAME = "watcher_events.db"
        private const val DB_VERSION = 2
        private const val TABLE = "pending_events"

        /** Разумный дефолт лимита очереди — ~дни офлайна, крошечные строки. */
        const val DEFAULT_MAX_ROWS = 5000
        const val MAX_ROWS = 50_000
        const val MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000L
    }
}
