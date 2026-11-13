package com.watcher.agent

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.util.Log

/**
 * Оркестратор сбора (Этап 3, docs/android-plan.md): прогоняет коллекторы, кладёт
 * собранное в локальную очередь [EventQueue] одной транзакцией и только после
 * успеха фиксирует курсоры/снимки (commit). Сбой любого коллектора логируется и
 * не прерывает остальных — служба не падает.
 */
class EventCollection(
    private val context: Context,
    private val config: AgentConfig,
    private val queue: EventQueue,
) {
    fun collectAndEnqueue(now: Long) {
        val collected = ArrayList<Collected>()
        add(collected) { UsageEventsCollector(context, config).collect(now) }
        add(collected) { AppInventoryCollector(context, config).collect(now) }
        // «Тяжёлые» срезы — не каждый тик, а раз в eventCollectionIntervalMs.
        // Отметка в будущем (часы перевели назад) — срок считаем наступившим,
        // иначе срезы встали бы до тех пор, пока часы не догонят отметку.
        val lastState = config.lastStateSampleAt
        if (lastState > now || now - lastState >= config.eventCollectionIntervalMs) {
            add(collected) { DeviceStateCollector(context).collect(now) }
            add(collected) { TrafficCollector(context, config).collect(now) }
            // Отметка фиксируется вместе с курсорами — только после записи очереди.
            // Упавший коллектор её не держит: следующий срез — через интервал, а
            // не каждый тик (курсор трафика той сети не сдвинут, окно не потеряно).
            collected.add(Collected(emptyList()) { config.lastStateSampleAt = now })
        }

        val all = collected.flatMap { it.events }
        if (all.isEmpty()) {
            collected.forEach { safeCommit(it) }
            return
        }
        try {
            queue.enqueueAll(all)
            collected.forEach { safeCommit(it) }
        } catch (e: Exception) {
            // Очередь не записалась — курсоры НЕ двигаем: события соберутся заново.
            Log.w(AgentService.TAG, "очередь событий: ${e.javaClass.simpleName}: ${e.message}")
        }
    }

    private inline fun add(list: MutableList<Collected>, block: () -> Collected) {
        try {
            list.add(block())
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "коллектор: ${e.javaClass.simpleName}: ${e.message}")
        }
    }

    private fun safeCommit(c: Collected) {
        try {
            c.commit()
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "commit коллектора: ${e.javaClass.simpleName}: ${e.message}")
        }
    }
}

/**
 * Отправка очереди пачками на POST /api/events (Этап 3). Успех (HTTP 200) —
 * удаляем отправленное; сеть/ошибка — оставляем (повторим в следующий тик).
 * Размер пачки меньше серверного предела (Db.MaxEventsPerRequest = 500).
 */
class EventFlusher(private val client: ServerClient, private val queue: EventQueue) {
    fun flush(secret: String, host: String) {
        var batches = 0
        while (batches++ < MAX_BATCHES_PER_FLUSH) {
            val batch = queue.take(BATCH_SIZE)
            if (batch.isEmpty()) break
            // null — не 200/сеть недоступна: оставляем в очереди и выходим.
            val accepted = client.postEvents(secret, host, batch) ?: break
            // Сервер подтверждает пачку целиком, включая ранее принятые eventId.
            // Частичное подтверждение оставляем в очереди: повтор безопасен благодаря
            // идемпотентному eventId и не теряет непринятый хвост.
            if (accepted != batch.size) break
            queue.delete(batch.map { it.id })
            if (batch.size < BATCH_SIZE) break
        }
    }

    private companion object {
        const val BATCH_SIZE = 200
        const val MAX_BATCHES_PER_FLUSH = 20
    }
}

object NetworkUtil {
    /** Можно ли отправлять сейчас с учётом «только Wi-Fi». */
    fun canSend(context: Context, wifiOnly: Boolean): Boolean {
        if (!wifiOnly) return true
        return activeNetworkType(context) == "wifi"
    }

    /** Тип активной сети: "wifi" | "cellular" | "ethernet" | null (нет сети). */
    fun activeNetworkType(context: Context): String? {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return null
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return null
        return when {
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
            else -> null
        }
    }
}
