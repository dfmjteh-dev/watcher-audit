package com.watcher.agent

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * Спул сегментов записи экрана (Этап 12+): то, что не ушло сразу (нет сети,
 * «только Wi-Fi» на мобильной сети, сервер недоступен), досылается из тика
 * службы. Раньше неотправленный сегмент так и лежал до чистки по размеру —
 * запись за время без сети терялась.
 *
 * Время сегмента — из имени файла (screen-<host>-<yyyyMMdd-HHmmss>-<n>.mp4, UTC),
 * конец — время последней записи файла.
 */
object ScreenSpool {
    /** Сколько сегментов досылать за один тик — тик не должен висеть минутами. */
    private const val MAX_PER_TICK = 3
    /** Свежие не трогаем: их, возможно, ещё грузит сама служба записи. */
    private const val MIN_AGE_MS = 120_000L

    fun dir(context: Context): File = File(context.filesDir, "screen").apply { mkdirs() }

    /** Досыл из тика службы (сеть уже разрешена настройкой «только Wi-Fi»). */
    fun retryPending(context: Context, serverUrl: String, secret: String, host: String) {
        val now = System.currentTimeMillis()
        val pending = dir(context).listFiles()
            ?.filter { it.name.endsWith(".mp4") && it.name != ScreenRecordState.currentFile && now - it.lastModified() > MIN_AGE_MS }
            ?.sortedBy { it.lastModified() } ?: return
        for (f in pending.take(MAX_PER_TICK)) {
            val startMs = startedAtMs(f.name) ?: f.lastModified()
            val endMs = maxOf(f.lastModified(), startMs)
            val ok = VideoUploader(serverUrl, secret)
                .upload(f, host, iso(startMs), iso(endMs), ((endMs - startMs) / 1000).toInt().coerceAtLeast(1))
            if (!ok) break   // сервер/сеть не отвечают — остальные в следующий тик
            f.delete()
            Log.i(AgentService.TAG, "запись экрана: досылка ${f.name}")
        }
        prune(context)
    }

    /** Ограничить спул по объёму — самые старые прочь. */
    fun prune(context: Context) {
        val files = dir(context).listFiles()?.sortedBy { it.lastModified() } ?: return
        var total = files.sumOf { it.length() }
        for (f in files) {
            if (total <= ScreenCapture.MAX_SPOOL_BYTES) break
            if (f.name == ScreenRecordState.currentFile) continue
            total -= f.length(); f.delete()
        }
    }

    /** Начало сегмента из имени файла (UTC), null — имя не нашего формата. */
    fun startedAtMs(name: String): Long? {
        val m = Regex("-(\\d{8}-\\d{6})-\\d+\\.mp4$").find(name) ?: return null
        return try {
            SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC"); isLenient = false }
                .parse(m.groupValues[1])?.time
        } catch (e: java.text.ParseException) { null }
    }

    private fun iso(ms: Long) = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US)
        .apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date(ms))
}
