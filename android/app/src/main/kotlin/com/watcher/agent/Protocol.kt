package com.watcher.agent

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/**
 * Контракт с сервером (Этап 2, docs/android-plan.md): сборка тел запросов
 * /api/enroll и /api/heartbeat и разбор ответов. Чистая логика на org.json
 * (входит в Android SDK — без сторонних библиотек, docs/development.md,
 * «Политика зависимостей»), без android-зависимостей — покрыта JVM unit-тестами.
 *
 * Формат совпадает с остальными агентами (docs/architecture.md, «Аутентификация
 * агентов»): регистрация отдаёт секрет wagt_…, heartbeat возвращает действующий
 * конфиг (configHash/configVersion) и сам конфиг-блоб.
 */
object Protocol {
    const val KIND_ANDROID = "android"
    const val PLATFORM_ANDROID = "android"

    /** Тело POST /api/enroll: {token, host, kind:"android"}. */
    fun enrollBody(token: String, host: String): String =
        JSONObject()
            .put("token", token)
            .put("host", host)
            .put("kind", KIND_ANDROID)
            .toString()

    /** Результат регистрации: секрет агента (wagt_…), подтверждённое имя хоста и
     * открытый ключ обновлений (Этап 8) — им агент проверяет подпись пакетов. */
    data class EnrollResult(val secret: String, val host: String, val updatePublicKey: String)

    /** Разбор ответа /api/enroll. null — секрета в ответе нет (ошибка/отказ). */
    fun parseEnroll(body: String): EnrollResult? =
        try {
            val o = JSONObject(body)
            val secret = o.optString("secret", "")
            if (secret.isEmpty()) null else EnrollResult(secret, o.optString("host", ""), o.optString("updatePublicKey", ""))
        } catch (e: JSONException) {
            null
        }

    /**
     * Тело POST /api/heartbeat. Поля из контракта станций, которые есть у
     * android: host, version, platform. configHash — какой конфиг у агента
     * сейчас (для «конфиг применён» на сервере), пусто — не слать. resources —
     * тот же record, что у станций (ResourceReport); для телефона значимо
     * только состояние очереди (queueFiles — число неотправленных событий в
     * очереди Этапа 3), остальное минимально-правдивое, чтобы сервер
     * принял отчёт (Db.Sanitize: 0 ≤ значения ≤ пределов).
     */
    fun heartbeatBody(host: String, version: String, configHash: String?, queueFiles: Int, ownerActive: Boolean, updateStatus: String? = null, locked: Boolean = false, adminUnlocked: Boolean = false): String {
        val o = JSONObject()
            .put("host", host)
            .put("version", version)
            .put("platform", PLATFORM_ANDROID)
            // Этап 5: роль владельца устройства на месте (сервер показывает её и
            // снимает флаг команды clear-owner, когда роль ушла).
            .put("ownerActive", ownerActive)
            // Этап 12: фактическое состояние удалённой блокировки устройства.
            .put("locked", locked)
            // Этап 12+: офлайн-админ снял блокировку локально.
            .put("adminUnlocked", adminUnlocked)
        if (!configHash.isNullOrEmpty()) o.put("configHash", configHash)
        // Этап 8: итог последней попытки обновления ("ok <ver>" / "failed <ver>: …").
        if (!updateStatus.isNullOrEmpty()) o.put("updateStatus", updateStatus)
        o.put(
            "resources",
            JSONObject()
                .put("memBytes", 0)
                .put("processes", 1)
                .put("queueBytes", 0)
                .put("queueFiles", queueFiles),
        )
        return o.toString()
    }

    /**
     * Действующий конфиг из ответа heartbeat. configHash/configVersion — метки
     * конфига, configJson — сам блоб строкой (как пришёл). Всё nullable: сервер
     * отдаёт null, если конфиг хосту не назначен.
     */
    /** Предложение обновления из heartbeat (Этап 8): пакет целевой версии группы. */
    data class UpdateOffer(
        val id: Long, val version: String, val platform: String,
        val sha256: String, val size: Long, val signature: String, val url: String,
    )

    data class HeartbeatResult(
        val configVersion: Int?,
        val configHash: String?,
        val configJson: String?,
        // Этап 5: сервер просит снять роль владельца (clearDeviceOwnerApp). По
        // умолчанию false — обычный ответ ничего такого не содержит.
        val clearOwner: Boolean = false,
        // Этап 8: предложенный пакет обновления (null — обновлять нечего).
        val update: UpdateOffer? = null,
        // Этап 8: открытый ключ обновлений (для агентов, зарегистрированных до
        // того, как ключ задали на сервере). Пусто — ключ не настроен.
        val updatePublicKey: String? = null,
        // Этап 12: сервер просит заблокировать устройство (true) или снять блокировку.
        val lockDevice: Boolean = false,
        // Этап 12+: логины/хэши админов (JSON-массив) для офлайн-разблокировки.
        val adminCredentials: String? = null,
    )

    /** Разбор ответа /api/heartbeat. null — тело не разобралось. */
    fun parseHeartbeat(body: String): HeartbeatResult? =
        try {
            val o = JSONObject(body)
            HeartbeatResult(
                configVersion = if (o.isNull("configVersion")) null else o.optInt("configVersion"),
                configHash = if (o.isNull("configHash")) null else o.optString("configHash").ifEmpty { null },
                configJson = o.optJSONObject("config")?.toString(),
                clearOwner = o.optBoolean("clearOwner", false),
                update = o.optJSONObject("update")?.let { u ->
                    UpdateOffer(
                        u.optLong("id"), u.optString("version"), u.optString("platform"),
                        u.optString("sha256"), u.optLong("size"), u.optString("signature"), u.optString("url"),
                    )
                },
                updatePublicKey = o.optString("updatePublicKey").ifEmpty { null },
                lockDevice = o.optBoolean("lockDevice", false),
                adminCredentials = o.optJSONArray("adminCredentials")?.toString(),
            )
        } catch (e: JSONException) {
            null
        }

    /**
     * Тело POST /api/events (Этап 3): {host, events:[{eventId,ts,type,package?,detail?}]}.
     * eventId — UUID из очереди, по нему сервер не дублирует повторную пачку.
     * detail хранится в очереди строкой JSON — вкладываем его объектом, как ждёт
     * сервер (не трактует содержимое, только хранит). Если detail не парсится как
     * объект — шлём событие без него (поле опционально).
     */
    fun eventsBody(host: String, events: List<PendingEvent>): String {
        val arr = JSONArray()
        for (e in events) {
            val o = JSONObject()
                .put("eventId", e.eventId)
                .put("ts", e.ts)
                .put("type", e.type)
            if (e.pkg != null) o.put("package", e.pkg)
            if (e.detail != null) {
                try {
                    o.put("detail", JSONObject(e.detail))
                } catch (ex: JSONException) {
                    // detail не объект — пропускаем поле, событие уходит без него.
                }
            }
            arr.put(o)
        }
        return JSONObject().put("host", host).put("events", arr).toString()
    }

    /**
     * Разбор ответа /api/events: сколько событий подтверждено (вся пачка, включая
     * уже известные серверу eventId). null — тело не разобралось или accepted не
     * неотрицательное целое: такую пачку агент не удаляет и повторит.
     */
    fun parseEventsAccepted(body: String): Int? =
        try {
            val o = JSONObject(body)
            val accepted = o.opt("accepted")
            if (accepted is Number && accepted.toDouble() == accepted.toInt().toDouble() && accepted.toInt() >= 0) {
                accepted.toInt()
            } else null
        } catch (e: JSONException) {
            null
        }
}
