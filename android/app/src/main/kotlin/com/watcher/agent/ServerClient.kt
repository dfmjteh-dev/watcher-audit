package com.watcher.agent

import android.util.Log
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * HTTP-клиент агента (Этап 2, docs/android-plan.md) на
 * java.net.HttpURLConnection + org.json — без сторонних сетевых библиотек
 * (OkHttp/Retrofit не берём, docs/development.md, «Политика зависимостей»).
 *
 * Регистрация (/api/enroll) и heartbeat (/api/heartbeat). Секрет уходит
 * заголовком Authorization: Bearer и нигде не логируется. Адрес сервера
 * задаётся при подключении (не зашит в код).
 */
class ServerClient(private val baseUrl: String) {

    /** Регистрация по токену. null — сервер отказал или сеть недоступна. */
    fun enroll(token: String, host: String): Protocol.EnrollResult? {
        val (code, resp) = post("/api/enroll", Protocol.enrollBody(token, host), bearer = null) ?: return null
        if (code != HttpURLConnection.HTTP_OK) {
            Log.w(AgentService.TAG, "enroll: сервер вернул $code")
            return null
        }
        return Protocol.parseEnroll(resp)
    }

    /** Heartbeat с секретом агента. null — сеть/ошибка; иначе действующий конфиг. */
    fun heartbeat(
        secret: String,
        host: String,
        version: String,
        configHash: String?,
        queueFiles: Int,
        ownerActive: Boolean,
        updateStatus: String? = null,
        locked: Boolean = false,
        adminUnlocked: Boolean = false,
    ): Protocol.HeartbeatResult? {
        val body = Protocol.heartbeatBody(host, version, configHash, queueFiles, ownerActive, updateStatus, locked, adminUnlocked)
        val (code, resp) = post("/api/heartbeat", body, bearer = secret) ?: return null
        if (code != HttpURLConnection.HTTP_OK) {
            Log.w(AgentService.TAG, "heartbeat: сервер вернул $code")
            return null
        }
        return Protocol.parseHeartbeat(resp)
    }

    /**
     * Отправка пачки событий (Этап 3) на /api/events с секретом агента. Возвращает
     * число подтверждённых событий при HTTP 200, включая повторы eventId;
     * null — нет корректного подтверждения, не 200 или сеть недоступна.
     * Пустую пачку не шлём.
     */
    fun postEvents(secret: String, host: String, events: List<PendingEvent>): Int? {
        if (events.isEmpty()) return 0
        val (code, resp) = post("/api/events", Protocol.eventsBody(host, events), bearer = secret) ?: return null
        if (code != HttpURLConnection.HTTP_OK) {
            Log.w(AgentService.TAG, "events: сервер вернул $code")
            return null
        }
        return Protocol.parseEventsAccepted(resp)
    }

    private fun post(path: String, body: String, bearer: String?): Pair<Int, String>? {
        var conn: HttpURLConnection? = null
        return try {
            val url = URL(baseUrl.trimEnd('/') + path)
            // HttpURLConnection сам отдаёт HttpsURLConnection для https-адреса.
            conn = (url.openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = CONNECT_TIMEOUT_MS
                readTimeout = READ_TIMEOUT_MS
                doOutput = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
                if (bearer != null) setRequestProperty("Authorization", "Bearer $bearer")
            }
            conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
            code to text
        } catch (e: IOException) {
            Log.w(AgentService.TAG, "POST $path: сеть недоступна: ${e.javaClass.simpleName}")
            null
        } finally {
            conn?.disconnect()
        }
    }

    companion object {
        private const val CONNECT_TIMEOUT_MS = 15_000
        private const val READ_TIMEOUT_MS = 15_000
    }
}
