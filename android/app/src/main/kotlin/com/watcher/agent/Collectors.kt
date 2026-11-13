package com.watcher.agent

import android.app.usage.NetworkStats
import android.app.usage.NetworkStatsManager
import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ApplicationInfo
import android.net.ConnectivityManager
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager

/**
 * Коллекторы событий (Этап 3, docs/android-plan.md). Здесь — только работа с
 * Android API; чистая логика вынесена отдельно и покрыта JVM-тестами
 * ([UsageEventsLogic], [AppInventory], [Events]). Каждый collect() возвращает
 * [Collected]: собранные события и commit() — фиксацию курсора/снимка, которую
 * оркестратор ([EventCollection]) вызывает ТОЛЬКО после успешной постановки в
 * очередь, чтобы сбой очереди не «съел» события молча.
 *
 * Любой коллектор может бросить (нет разрешения на доступ к статистике и т.п.) —
 * оркестратор ловит это и продолжает, служба не падает (принцип плана: сбой
 * агента не ломает телефон).
 */
class Collected(val events: List<PendingEvent>, val commit: () -> Unit)

// Источник установки пакета: getInstallSourceInfo на API 30+, устаревший
// getInstallerPackageName на 29. null — неизвестен или пакет уже исчез.
object InstallSource {
    fun of(context: Context, pkg: String): String? =
        try {
            val pm = context.packageManager
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                pm.getInstallSourceInfo(pkg).installingPackageName
            } else {
                @Suppress("DEPRECATION")
                pm.getInstallerPackageName(pkg)
            }
        } catch (e: Exception) {
            null
        }
}

/** UsageEvents → app_foreground/app_background, с курсором, чтобы не повторяться. */
class UsageEventsCollector(private val context: Context, private val config: AgentConfig) {
    fun collect(now: Long): Collected {
        if (!UsageAccess.granted(context)) return Collected(emptyList()) {}
        val usm = context.getSystemService(Context.USAGE_STATS_SERVICE) as? UsageStatsManager
            ?: return Collected(emptyList()) {}
        val end = now - 2_000L
        val lastCursor = config.usageCursor.takeIf { it < end } ?: 0L
        val start = if (lastCursor == 0L) now - LOOKBACK_MS else maxOf(lastCursor + 1, now - MAX_LOOKBACK_MS)
        val raw = ArrayList<UsageEventsLogic.Raw>()
        val stream = usm.queryEvents(start, end) ?: return Collected(emptyList()) {}
        val ev = UsageEvents.Event()
        while (stream.hasNextEvent()) {
            stream.getNextEvent(ev)
            raw.add(UsageEventsLogic.Raw(ev.timeStamp, ev.eventType, ev.packageName))
        }
        val outcome = UsageEventsLogic.process(raw, lastCursor)
        // queryEvents использует [start,end): отметка end-1 не теряет событие
        // ровно на границе следующего запроса. Задержка 2с даёт системе записать его.
        return Collected(outcome.events) { config.usageCursor = end - 1 }
    }

    private companion object {
        const val LOOKBACK_MS = 10 * 60 * 1000L
        const val MAX_LOOKBACK_MS = 24 * 60 * 60 * 1000L
    }
}

/** Перечень приложений → diff установок/удалений против прошлого снимка. */
class AppInventoryCollector(private val context: Context, private val config: AgentConfig) {
    fun collect(now: Long): Collected {
        val pm = context.packageManager
        @Suppress("DEPRECATION")
        val packages = pm.getInstalledPackages(0)
        val current = packages.mapNotNull { it.packageName }.toSet()
        val previous = config.appSnapshot
        val diff = AppInventory.diff(previous, current)
        val ts = TimeUtil.iso(now)
        val out = ArrayList<PendingEvent>()
        val snapshotDue = previous == null || diff.installed.isNotEmpty() || diff.removed.isNotEmpty() ||
            config.lastInventoryAt == 0L || now < config.lastInventoryAt || now - config.lastInventoryAt >= 86_400_000L
        if (snapshotDue) {
            out.addAll(Events.appInventory(packages.map {
                val versionCode = it.longVersionCode
                val system = (it.applicationInfo?.flags ?: 0) and ApplicationInfo.FLAG_SYSTEM != 0
                val label = it.applicationInfo?.let { ai -> runCatching { pm.getApplicationLabel(ai).toString() }.getOrNull() }
                Events.AppInfo(it.packageName, it.versionName, versionCode, InstallSource.of(context, it.packageName), system, label)
            }, ts))
        }
        if (previous != null) {
            for (p in diff.installed) out.add(Events.appInstalled(p, ts, InstallSource.of(context, p)))
            for (p in diff.removed) out.add(Events.appRemoved(p, ts))
        }
        return Collected(out) {
            config.appSnapshot = current
            if (snapshotDue) config.lastInventoryAt = now
        }
    }
}

/** Состояние устройства → одно событие device_state. */
class DeviceStateCollector(private val context: Context) {
    fun collect(now: Long): Collected {
        val battery = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val batteryPercent = battery
            ?.let { val level = it.getIntExtra(BatteryManager.EXTRA_LEVEL, -1); val scale = it.getIntExtra(BatteryManager.EXTRA_SCALE, -1); if (level >= 0 && scale > 0) level * 100 / scale else null }
        val charging = battery?.let {
            val status = it.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
            status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL
        }
        val screenOn = (context.getSystemService(Context.POWER_SERVICE) as? PowerManager)?.isInteractive
        val event = Events.deviceState(
            tsUtc = TimeUtil.iso(now),
            osVersion = Build.VERSION.RELEASE,
            securityPatch = Build.VERSION.SECURITY_PATCH,
            networkType = NetworkUtil.activeNetworkType(context),
            // SSID не берём: на 10+ он требует разрешения на геолокацию, которое
            // ради одного поля не запрашиваем (docs/android-plan.md). Только тип сети.
            ssid = null,
            batteryPercent = batteryPercent,
            charging = charging,
            screenOn = screenOn,
            usageAccess = UsageAccess.granted(context),
        )
        return Collected(listOf(event)) {}
    }
}

/** Трафик по приложениям (NetworkStatsManager) → app_traffic за интервал. */
class TrafficCollector(private val context: Context, private val config: AgentConfig) {
    fun collect(now: Long): Collected {
        if (!UsageAccess.granted(context)) return Collected(emptyList()) {}
        val nsm = context.getSystemService(Context.NETWORK_STATS_SERVICE) as? NetworkStatsManager
            ?: return Collected(emptyList()) {}
        val out = ArrayList<PendingEvent>()
        val completed = ArrayList<String>()
        val ts = TimeUtil.iso(now)
        @Suppress("DEPRECATION")
        for ((type, networkType) in listOf(ConnectivityManager.TYPE_WIFI to "wifi", ConnectivityManager.TYPE_MOBILE to "cellular")) {
            val previous = config.trafficSampleAt(networkType)
            val last = if (previous <= 0 || previous >= now) now - config.eventCollectionIntervalMs else maxOf(previous, now - EventQueue.MAX_AGE_MS)
            try {
                val perUid = HashMap<Int, LongArray>()
                nsm.querySummary(type, null, last, now).use { stats ->
                    val bucket = NetworkStats.Bucket()
                    while (stats.hasNextBucket()) {
                        stats.getNextBucket(bucket)
                        val acc = perUid.getOrPut(bucket.uid) { LongArray(2) }
                        acc[0] += bucket.rxBytes
                        acc[1] += bucket.txBytes
                    }
                }
                for ((uid, bytes) in perUid) {
                    if (bytes[0] == 0L && bytes[1] == 0L) continue
                    val packages = context.packageManager.getPackagesForUid(uid)?.sorted() ?: continue
                    val pkg = packages.firstOrNull() ?: continue
                    out.add(Events.appTraffic(pkg, ts, bytes[0], bytes[1], now - last, networkType, uid, packages))
                }
                completed.add(networkType)
            } catch (e: Exception) {
                android.util.Log.w(AgentService.TAG, "трафик $networkType: ${e.javaClass.simpleName}")
            }
        }
        // Курсор недоступной сети не двигаем: следующий замер повторит её окно.
        return Collected(out) { completed.forEach { config.setTrafficSampleAt(it, now) } }
    }
}
