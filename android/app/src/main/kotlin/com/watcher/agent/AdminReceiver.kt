package com.watcher.agent

import android.app.admin.DeviceAdminReceiver
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * Приёмник администратора устройства (Этап 5, docs/android-plan.md). Агент —
 * владелец устройства (device owner): назначается на чистом устройстве через
 * `dpm set-device-owner com.watcher.agent/.AdminReceiver` (onboarding — Этап 7).
 *
 * ВАЖНО: имя пакета и этого компонента после назначения роли менять нельзя —
 * иначе на устройствах слетит роль владельца (docs/android-plan.md, «Сквозные
 * правила»). Сама политика (что закрепляем, какие разрешения выдаём) живёт в
 * [DeviceOwner]; receiver только реагирует на события и держит службу живой.
 */
class AdminReceiver : DeviceAdminReceiver() {

    override fun onEnabled(context: Context, intent: Intent) {
        Log.i(AgentService.TAG, "администратор устройства включён (owner=${DeviceOwner.isOwner(context)})")
        // Роль только что получена — сразу закрепляем политики и поднимаем службу,
        // не дожидаясь первого heartbeat.
        DeviceOwner.enforce(context)
        AgentService.start(context)
    }

    // Этап 6: журналы владельца приходят пачками в эти колбэки (система решает
    // когда). Извлечение и агрегация — в OwnerLogs; receiver только передаёт.
    override fun onNetworkLogsAvailable(context: Context, intent: Intent, batchToken: Long, networkLogsCount: Int) {
        val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager ?: return
        runCatching { OwnerLogs.onNetworkLogs(context, dpm, batchToken) }
            .onFailure { Log.w(AgentService.TAG, "onNetworkLogsAvailable: ${it.javaClass.simpleName}") }
    }

    override fun onSecurityLogsAvailable(context: Context, intent: Intent) {
        val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager ?: return
        val admin = component(context)
        val events = runCatching { dpm.retrieveSecurityLogs(admin) }.getOrNull() ?: return
        runCatching { OwnerLogs.onSecurityLogs(context, events) }
            .onFailure { Log.w(AgentService.TAG, "onSecurityLogsAvailable: ${it.javaClass.simpleName}") }
    }

    override fun onDisabled(context: Context, intent: Intent) {
        // Роль снимают либо нашей командой (DeviceOwner.clear), либо вручную на
        // стенде — служба продолжает работать как обычное приложение.
        Log.i(AgentService.TAG, "администратор устройства выключен")
    }

    companion object {
        fun component(context: Context): ComponentName =
            ComponentName(context.applicationContext, AdminReceiver::class.java)
    }
}
