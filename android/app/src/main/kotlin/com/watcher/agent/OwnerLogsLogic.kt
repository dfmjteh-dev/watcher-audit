package com.watcher.agent

import org.json.JSONArray
import org.json.JSONObject

/**
 * Чистая логика журналов владельца устройства (Этап 6, docs/android-plan.md) —
 * без android-вызовов, покрыта JVM unit-тестами. Извлечение самих журналов из
 * DevicePolicyManager (DnsEvent/ConnectEvent/SecurityEvent) — в [OwnerLogs];
 * здесь только агрегация и формирование событий очереди.
 *
 * Зачем агрегация: сетевой журнал приходит пачками до ~1200 записей; слать их
 * по одной — забить и сеть, и сервер. Схлопываем по (домен/адрес × приложение)
 * в пределах пачки, храним счётчик (docs/android-plan.md, «агрегация»).
 */
object OwnerLogsLogic {

    /** DNS-резолв: домен, приложение-инициатор, сколько адресов вернулось. */
    data class DnsHit(val domain: String, val packageName: String?, val count: Int, val tsMs: Long)

    /** Соединение: адрес, порт, приложение-инициатор. */
    data class ConnHit(val ip: String, val port: Int, val packageName: String?, val tsMs: Long)

    /** Запись журнала безопасности: числовой тег, время, краткие данные (строкой). */
    data class SecHit(val tag: Int, val tsMs: Long, val data: String?)

    private const val MAX_DOMAIN = 253
    private const val MAX_IP = 64
    private const val MAX_PKG = 255
    private const val MAX_SEC_DATA = 512

    /**
     * Схлопнуть DNS-пачку в события net_dns: ключ (домен, приложение), в detail —
     * domain, package, count (сумма по пачке), ts — самое раннее в группе.
     * Пустой/слишком длинный домен отбрасывается.
     */
    fun aggregateDns(hits: List<DnsHit>): List<PendingEvent> {
        val byKey = LinkedHashMap<Pair<String, String?>, IntArray>() // [count, не нужен]
        val firstTs = HashMap<Pair<String, String?>, Long>()
        for (h in hits) {
            val domain = h.domain.trim().lowercase()
            if (domain.isEmpty() || domain.length > MAX_DOMAIN) continue
            val pkg = h.packageName?.takeIf { it.isNotEmpty() && it.length <= MAX_PKG }
            val key = domain to pkg
            val acc = byKey.getOrPut(key) { intArrayOf(0) }
            acc[0] += h.count.coerceAtLeast(1)
            firstTs.merge(key, h.tsMs) { a, b -> minOf(a, b) }
        }
        return byKey.map { (key, acc) ->
            val d = JSONObject().put("domain", key.first).put("count", acc[0])
            if (key.second != null) d.put("package", key.second)
            PendingEvent(TimeUtil.iso(firstTs[key] ?: 0L), Events.NET_DNS, null, d.toString())
        }
    }

    /**
     * Схлопнуть пачку соединений в события net_connect: ключ (ip, port,
     * приложение), count — сколько раз, ts — самое раннее.
     */
    fun aggregateConnects(hits: List<ConnHit>): List<PendingEvent> {
        val count = LinkedHashMap<Triple<String, Int, String?>, Int>()
        val firstTs = HashMap<Triple<String, Int, String?>, Long>()
        for (h in hits) {
            val ip = h.ip.trim()
            if (ip.isEmpty() || ip.length > MAX_IP) continue
            val pkg = h.packageName?.takeIf { it.isNotEmpty() && it.length <= MAX_PKG }
            val key = Triple(ip, h.port, pkg)
            count.merge(key, 1, Int::plus)
            firstTs.merge(key, h.tsMs) { a, b -> minOf(a, b) }
        }
        return count.map { (key, c) ->
            val d = JSONObject().put("ip", key.first).put("port", key.second).put("count", c)
            if (key.third != null) d.put("package", key.third)
            PendingEvent(TimeUtil.iso(firstTs[key] ?: 0L), Events.NET_CONNECT, null, d.toString())
        }
    }

    // getTimeNanos() журнала безопасности: по докам «наносекунды с момента загрузки»,
    // но на части прошивок (HarmonyOS/EMUI и др.) это наносекунды от Unix-эпохи —
    // тогда формула «от загрузки» уносит время на десятки лет вперёд. Различаем по
    // величине: эпоха сейчас ~1.7e18 нс, аптайм устройства < ~1e15 нс (≈11.5 суток).
    // Результат вне разумного окна [сейчас−30д, сейчас+1ч] заменяем временем приёма
    // (кривые часы устройства), чтобы запись не выпадала из суточной сводки.
    const val EPOCH_NANOS_THRESHOLD = 1_000_000_000_000_000L

    fun securityEventMs(timeNanos: Long, nowMs: Long, elapsedRealtimeNanos: Long): Long {
        val ms = if (timeNanos >= EPOCH_NANOS_THRESHOLD) timeNanos / 1_000_000
                 else nowMs - (elapsedRealtimeNanos - timeNanos) / 1_000_000
        return if (ms in (nowMs - 30L * 86_400_000L)..(nowMs + 3_600_000L)) ms else nowMs
    }

    /** События безопасности: по одному событию security на запись (их немного). */
    fun securityEvents(hits: List<SecHit>): List<PendingEvent> =
        hits.map { h ->
            val d = JSONObject()
                .put("tag", h.tag)
                .put("name", securityTagName(h.tag))
            if (!h.data.isNullOrEmpty()) d.put("data", h.data.take(MAX_SEC_DATA))
            PendingEvent(TimeUtil.iso(h.tsMs), Events.SECURITY, null, d.toString())
        }

    /**
     * Человекочитаемое имя тега журнала безопасности. Значения — константы
     * android.app.admin.SecurityLog (стабильны), продублированы здесь, чтобы
     * логика осталась без android-зависимостей. Неизвестный тег — tag_<n>.
     */
    fun securityTagName(tag: Int): String = when (tag) {
        210001 -> "adb_shell_interactive"
        210002 -> "adb_shell_command"
        210003 -> "sync_recv_file"
        210004 -> "sync_send_file"
        210005 -> "app_process_start"
        210006 -> "keyguard_dismissed"
        210007 -> "keyguard_dismiss_auth_attempt"
        210008 -> "keyguard_secured"
        210009 -> "os_startup"
        210010 -> "os_shutdown"
        210011 -> "logging_started"
        210012 -> "logging_stopped"
        210013 -> "media_mount"
        210014 -> "media_unmount"
        210015 -> "os_shutdown_log"
        210016 -> "logging_recovered"
        210017 -> "key_generated"
        210018 -> "key_imported"
        210019 -> "key_destroyed"
        210020 -> "user_restriction_added"
        210021 -> "user_restriction_removed"
        210022 -> "cert_authority_installed"
        210023 -> "cert_authority_removed"
        210024 -> "user_password_changed"
        210025 -> "wipe_failed"
        210026 -> "key_integrity_violation"
        210027 -> "cert_validation_failure"
        210028 -> "camera_policy_set"
        210029 -> "password_complexity_set"
        210030 -> "app_restrictions_set"
        210031 -> "connectivity_changed"
        else -> "tag_$tag"
    }
}

/** Небольшой JSONArray-помощник: список строк данных события безопасности в одну строку. */
fun securityDataToString(data: List<Any?>?): String? {
    if (data.isNullOrEmpty()) return null
    val arr = JSONArray()
    for (v in data) if (v != null) arr.put(v.toString())
    return if (arr.length() == 0) null else arr.toString()
}
