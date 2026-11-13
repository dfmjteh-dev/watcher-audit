package com.watcher.agent

import android.util.Log
import java.io.File
import java.io.FileInputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Загрузка сегмента записи экрана на сервер (Этап 12+, запись по согласию).
 * Тот же эндпоинт, что у станций — POST /api/upload (multipart: video, meta,
 * sha256); сервер привязывает запись к хосту из учётки Android-агента и
 * показывает её на вкладке «Видео». Без сторонних сетевых библиотек
 * (HttpURLConnection), как [ServerClient].
 */
class VideoUploader(private val baseUrl: String, private val secret: String) {

    /** Загрузить один MP4-сегмент. true — принят (или уже на сервере, 409). */
    fun upload(file: File, host: String, startedAtIso: String, endedAtIso: String, durationSec: Int): Boolean {
        val sha = sha256(file)
        val boundary = "----watcher${System.currentTimeMillis()}"
        val meta = org.json.JSONObject()
            .put("host", host).put("startedAt", startedAtIso).put("endedAt", endedAtIso)
            .put("durationSec", durationSec).put("startReason", "screen").toString()
        return try {
            val conn = URL(join(baseUrl, "/api/upload")).openConnection() as HttpURLConnection
            conn.requestMethod = "POST"
            conn.connectTimeout = 15000; conn.readTimeout = 120000
            conn.doOutput = true
            // Без этого HttpURLConnection копит всё тело (сегмент ~15 МБ) в памяти.
            conn.setChunkedStreamingMode(64 * 1024)
            conn.setRequestProperty("Authorization", "Bearer $secret")
            conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            conn.outputStream.use { out ->
                fun field(name: String, value: String) {
                    out.write(("--$boundary\r\nContent-Disposition: form-data; name=\"$name\"\r\n\r\n$value\r\n").toByteArray())
                }
                fun fileHeader(name: String, filename: String, ctype: String) {
                    out.write(("--$boundary\r\nContent-Disposition: form-data; name=\"$name\"; filename=\"$filename\"\r\nContent-Type: $ctype\r\n\r\n").toByteArray())
                }
                field("sha256", sha)
                fileHeader("meta", "meta.json", "application/json")
                out.write(meta.toByteArray()); out.write("\r\n".toByteArray())
                fileHeader("video", file.name, "video/mp4")
                FileInputStream(file).use { it.copyTo(out) }
                out.write("\r\n".toByteArray())
                out.write("--$boundary--\r\n".toByteArray())
            }
            val code = conn.responseCode
            conn.disconnect()
            // 200 — принят, 409 — уже на сервере (повтор), оба успешны для нас.
            val ok = code == HttpURLConnection.HTTP_OK || code == HttpURLConnection.HTTP_CONFLICT
            if (!ok) Log.w(AgentService.TAG, "upload: сервер вернул $code для ${file.name}")
            ok
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "upload: ${e.javaClass.simpleName}: ${e.message}")
            false
        }
    }

    private fun sha256(file: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        FileInputStream(file).use { ins ->
            val buf = ByteArray(1 shl 16)
            while (true) { val n = ins.read(buf); if (n < 0) break; md.update(buf, 0, n) }
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    private fun join(base: String, path: String) = base.trimEnd('/') + path
}
