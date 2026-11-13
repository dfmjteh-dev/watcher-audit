package com.watcher.agent

import android.app.admin.ConnectEvent
import android.app.admin.DeviceAdminReceiver
import android.app.admin.DevicePolicyManager
import android.app.admin.DnsEvent
import android.app.admin.NetworkEvent
import android.app.admin.SecurityLog.SecurityEvent
import android.content.Context
import android.util.Log

/**
 * Приём журналов владельца устройства (Этап 6, docs/android-plan.md): извлекает
 * сетевой журнал и журнал безопасности из [DevicePolicyManager] в колбэках
 * [AdminReceiver], прогоняет через чистую [OwnerLogsLogic] и кладёт в очередь
 * [EventQueue]. Android-часть (типы DnsEvent/ConnectEvent/SecurityEvent) — здесь;
 * агрегация — в OwnerLogsLogic (unit-тесты).
 *
 * Логи приходят пачками редко (система сама решает когда), поэтому работаем
 * синхронно в колбэке: извлечь → агрегировать → записать несколько строк в
 * SQLite. Любой сбой логируется и не роняет receiver.
 */
object OwnerLogs {

    fun onNetworkLogs(context: Context, dpm: DevicePolicyManager, batchToken: Long) {
        val admin = AdminReceiver.component(context)
        val events: List<NetworkEvent> = runCatching { dpm.retrieveNetworkLogs(admin, batchToken) }
            .getOrNull() ?: return
        val dns = ArrayList<OwnerLogsLogic.DnsHit>()
        val conn = ArrayList<OwnerLogsLogic.ConnHit>()
        for (e in events) when (e) {
            is DnsEvent -> dns.add(OwnerLogsLogic.DnsHit(
                e.hostname ?: "", e.packageName, e.totalResolvedAddressCount, e.timestamp))
            is ConnectEvent -> conn.add(OwnerLogsLogic.ConnHit(
                e.inetAddress?.hostAddress ?: "", e.port, e.packageName, e.timestamp))
        }
        val pending = OwnerLogsLogic.aggregateDns(dns) + OwnerLogsLogic.aggregateConnects(conn)
        enqueue(context, pending, "сетевой журнал (${events.size} → ${pending.size})")
    }

    fun onSecurityLogs(context: Context, events: List<SecurityEvent>) {
        val now = System.currentTimeMillis()
        val elapsed = android.os.SystemClock.elapsedRealtimeNanos()
        val hits = events.map {
            OwnerLogsLogic.SecHit(it.tag, OwnerLogsLogic.securityEventMs(it.timeNanos, now, elapsed), dataOf(it))
        }
        enqueue(context, OwnerLogsLogic.securityEvents(hits), "журнал безопасности (${events.size})")
    }

    private fun dataOf(e: SecurityEvent): String? =
        securityDataToString(when (val d = e.data) {
            is Array<*> -> d.toList()
            is Collection<*> -> d.toList()
            null -> null
            else -> listOf(d)
        })


    private fun enqueue(context: Context, pending: List<PendingEvent>, what: String) {
        if (pending.isEmpty()) return
        val queue = EventQueue(context, AgentConfig(context).eventQueueMaxRows)
        try {
            queue.prune()
            queue.enqueueAll(pending)
            Log.i(AgentService.TAG, "$what в очередь")
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "$what: ${e.javaClass.simpleName}: ${e.message}")
        } finally {
            queue.close()
        }
    }
}
