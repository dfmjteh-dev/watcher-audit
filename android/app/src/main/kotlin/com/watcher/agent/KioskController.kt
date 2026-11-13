package com.watcher.agent

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.content.Intent
import android.content.IntentFilter
import android.util.Log

/**
 * Режим киоска (Этап 13, docs/android-plan.md): устройство-владелец пинится на
 * разрешённый набор приложений (lock-task), а домашним экраном становится
 * лаунчер киоска [KioskActivity]. В настройки и прочие приложения выйти нельзя;
 * выход из киоска — только командой (пустой список kioskPackages). Переживает
 * ребут (служба переприменяет на старте).
 *
 * Отдельно от экстренной блокировки (Этап 12): там заглушка на весь экран, тут —
 * рабочий лаунчер с разрешёнными приложениями.
 */
object KioskController {

    fun converge(context: Context, allowed: Set<String>) {
        KioskState.allowed = allowed
        val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager ?: return
        if (!DeviceOwner.isOwner(context)) return
        val admin = AdminReceiver.component(context)
        val homeAlias = ComponentName(context, context.packageName + ".KioskHomeAlias")
        if (allowed.isNotEmpty()) {
            // В lock-task пускаем разрешённые приложения и сам агент (лаунчер киоска).
            runCatching { dpm.setLockTaskPackages(admin, (allowed + context.packageName).toTypedArray()) }
                .onFailure { Log.w(AgentService.TAG, "kiosk setLockTaskPackages: ${it.javaClass.simpleName}") }
            // Включаем HOME-роль агента (alias) только на время киоска и делаем его
            // домашним по умолчанию: кнопка «домой» возвращает в киоск.
            runCatching {
                context.packageManager.setComponentEnabledSetting(homeAlias,
                    PackageManager.COMPONENT_ENABLED_STATE_ENABLED, PackageManager.DONT_KILL_APP)
                val home = IntentFilter(Intent.ACTION_MAIN).apply {
                    addCategory(Intent.CATEGORY_HOME); addCategory(Intent.CATEGORY_DEFAULT)
                }
                dpm.addPersistentPreferredActivity(admin, home, homeAlias)
            }.onFailure { Log.w(AgentService.TAG, "kiosk home: ${it.javaClass.simpleName}") }
            if (!KioskState.active) launchKiosk(context)
        } else {
            // Киоск не нужен: ВСЕГДА снимаем себя с роли домашнего экрана, даже если
            // KioskActivity сейчас не на экране. Иначе осевшая HOME-привязка (её ставит
            // addPersistentPreferredActivity, она переживает ребут/перезапуск процесса)
            // продолжала бы открывать лаунчер киоска при каждом возврате «домой» —
            // в т.ч. когда планшет переподключается к сети. KioskActivity, если она на
            // экране, сама выйдет (poll видит allowed.isEmpty).
            clearHome(context, dpm, admin)
        }
    }

    /** Снять нашу HOME-роль: убрать persistent-привязку И выключить HOME-alias, чтобы
     * агент перестал быть кандидатом на «домой». lock-task allowlist не трогаем — он
     * общий с [LockController]. */
    private fun clearHome(context: Context, dpm: DevicePolicyManager, admin: ComponentName) {
        runCatching { dpm.clearPackagePersistentPreferredActivities(admin, context.packageName) }
            .onFailure { Log.w(AgentService.TAG, "kiosk clear home: ${it.javaClass.simpleName}") }
        runCatching {
            context.packageManager.setComponentEnabledSetting(
                ComponentName(context, context.packageName + ".KioskHomeAlias"),
                PackageManager.COMPONENT_ENABLED_STATE_DISABLED, PackageManager.DONT_KILL_APP)
        }.onFailure { Log.w(AgentService.TAG, "kiosk disable home alias: ${it.javaClass.simpleName}") }
    }

    /** Самоисцеление: снять HOME-привязку из [KioskActivity], когда киоск уже не нужен. */
    fun releaseHome(context: Context) {
        val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager ?: return
        if (!DeviceOwner.isOwner(context)) return
        clearHome(context, dpm, AdminReceiver.component(context))
    }

    fun active(): Boolean = KioskState.active

    private fun launchKiosk(context: Context) {
        val intent = Intent(context, KioskActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        runCatching { context.startActivity(intent) }
            .onFailure { Log.w(AgentService.TAG, "kiosk launch: ${it.javaClass.simpleName}") }
    }
}
