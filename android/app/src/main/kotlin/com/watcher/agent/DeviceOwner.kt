package com.watcher.agent

import android.app.admin.DevicePolicyManager
import android.content.Context
import android.os.Build
import android.util.Log

/**
 * Обёртка над [DevicePolicyManager] для роли владельца устройства (Этап 5,
 * docs/android-plan.md). Все вызовы безопасны, когда агент НЕ владелец: на не-
 * владельце это обычное приложение (этапы 1–4), поэтому каждый метод сначала
 * проверяет роль и при отсутствии просто ничего не делает.
 *
 * Поведение DPM проверяется на эмуляторах (инструментальная приёмка), а не JVM-
 * тестами; решения «что делать» вынесены в [DeviceOwnerPolicy].
 */
object DeviceOwner {

    private fun dpm(context: Context): DevicePolicyManager? =
        context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager

    /** Агент — владелец устройства? Любой сбой трактуем как «нет». */
    fun isOwner(context: Context): Boolean =
        try {
            dpm(context)?.isDeviceOwnerApp(context.packageName) == true
        } catch (e: RuntimeException) {
            false
        }

    /**
     * Закрепить политики владельца: запрет удаления агента и выдача себе
     * runtime-разрешений ([DeviceOwnerPolicy]). Идемпотентно — зовётся каждый
     * tick и при получении роли. На не-владельце — no-op. Возвращает true, если
     * агент сейчас владелец (для отчёта ownerActive серверу).
     */
    fun enforce(context: Context): Boolean {
        val dpm = dpm(context) ?: return false
        if (!isOwner(context)) return false
        val admin = AdminReceiver.component(context)
        runCatching { dpm.setUninstallBlocked(admin, context.packageName, true) }
            .onFailure { Log.w(AgentService.TAG, "setUninstallBlocked: ${it.javaClass.simpleName}") }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            for (perm in DeviceOwnerPolicy.runtimePermissionsToGrant()) {
                runCatching {
                    dpm.setPermissionGrantState(admin, context.packageName, perm,
                        DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED)
                }.onFailure { Log.w(AgentService.TAG, "grant $perm: ${it.javaClass.simpleName}") }
            }
        }
        // Этап 6: журналы владельца. Включаем идемпотентно — если уже включены,
        // система ничего не меняет. Записи приходят в колбэки AdminReceiver.
        runCatching { if (!dpm.isNetworkLoggingEnabled(admin)) dpm.setNetworkLoggingEnabled(admin, true) }
            .onFailure { Log.w(AgentService.TAG, "setNetworkLoggingEnabled: ${it.javaClass.simpleName}") }
        runCatching { if (!dpm.isSecurityLoggingEnabled(admin)) dpm.setSecurityLoggingEnabled(admin, true) }
            .onFailure { Log.w(AgentService.TAG, "setSecurityLoggingEnabled: ${it.javaClass.simpleName}") }
        return true
    }

    /**
     * Снять роль владельца по команде сервера (Этап 5): сначала разблокировать
     * удаление, затем clearDeviceOwnerApp. После этого агент — обычное
     * приложение, его можно удалить штатно (пилот/стенд откатываются без сброса).
     * На не-владельце — no-op, возвращает true (снимать нечего). false — роль
     * осталась (снять не удалось).
     */
    fun clear(context: Context): Boolean {
        val dpm = dpm(context) ?: return false
        if (!isOwner(context)) return true
        val admin = AdminReceiver.component(context)
        runCatching { dpm.setUninstallBlocked(admin, context.packageName, false) }
            .onFailure { Log.w(AgentService.TAG, "unblock uninstall: ${it.javaClass.simpleName}") }
        // Этап 6: выключаем журналы перед снятием роли (после clear владелец не
        // сможет их трогать).
        runCatching { if (dpm.isNetworkLoggingEnabled(admin)) dpm.setNetworkLoggingEnabled(admin, false) }.getOrNull()
        runCatching { if (dpm.isSecurityLoggingEnabled(admin)) dpm.setSecurityLoggingEnabled(admin, false) }.getOrNull()
        return runCatching {
            dpm.clearDeviceOwnerApp(context.packageName)
            Log.i(AgentService.TAG, "роль владельца снята по команде сервера")
            !isOwner(context)
        }.getOrElse {
            Log.w(AgentService.TAG, "clearDeviceOwnerApp: ${it.javaClass.simpleName}")
            false
        }
    }
}
