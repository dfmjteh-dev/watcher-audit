package com.watcher.agent

import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Telephony
import android.telecom.TelecomManager
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject

/**
 * Применение политик управления (Этап 11, docs/android-plan.md) владельцем
 * устройства: блокировка/скрытие приложений, пользовательские запреты, запрет
 * мобильного интернета приложениям. Политика берётся из конфиг-блоба
 * (androidPolicy), решения — в чистой [AndroidPolicyLogic]; здесь вызовы
 * DevicePolicyManager и защита домашнего экрана/звонилки/SMS по рантайму.
 *
 * Вызывается при смене конфига у агента-владельца; дельта относительно ранее
 * применённой политики (хранится в [AgentConfig]) делает вызовы минимальными,
 * а снятие пакета из списка — откатом (un-suspend / un-hide / clear-restriction).
 * Отчёт о применении и отклонённых защищённых пакетах уходит событием policy.
 */
object AndroidPolicyManager {

    // force=true — переприменить политику целиком (prev = пусто): на старте
    // процесса/после ребута, т.к. suspension у device owner на части прошивок
    // не сохраняется между перезагрузками. force=false — дельтой при смене конфига
    // (минимум вызовов + откат снятых пакетов/запретов).
    fun apply(context: Context, config: AgentConfig, force: Boolean = false) {
        val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager ?: return
        if (!DeviceOwner.isOwner(context)) return
        val admin = AdminReceiver.component(context)

        val requested = AndroidPolicyLogic.fromConfig(config.configJson)
        val (next, refused) = AndroidPolicyLogic.protect(requested, runtimeProtected(context))
        val prev = if (force) AndroidPolicyLogic.EMPTY else AndroidPolicyLogic.parse(config.lastAppliedPolicyJson)
        val d = AndroidPolicyLogic.diff(prev, next)
        if (d.isEmpty && refused.isEmpty()) return

        val notApplied = LinkedHashSet<String>()
        if (d.suspendOn.isNotEmpty()) runCatching {
            dpm.setPackagesSuspended(admin, d.suspendOn.toTypedArray(), true)?.let { notApplied.addAll(it) }
        }.onFailure { Log.w(AgentService.TAG, "suspend: ${it.javaClass.simpleName}") }
        if (d.suspendOff.isNotEmpty()) runCatching {
            dpm.setPackagesSuspended(admin, d.suspendOff.toTypedArray(), false)
        }.onFailure { Log.w(AgentService.TAG, "unsuspend: ${it.javaClass.simpleName}") }
        for (p in d.hideOn) runCatching { dpm.setApplicationHidden(admin, p, true) }
            .onFailure { Log.w(AgentService.TAG, "hide $p: ${it.javaClass.simpleName}") }
        for (p in d.hideOff) runCatching { dpm.setApplicationHidden(admin, p, false) }
            .onFailure { Log.w(AgentService.TAG, "unhide $p: ${it.javaClass.simpleName}") }
        for (r in d.restrictAdd) runCatching { dpm.addUserRestriction(admin, r) }
            .onFailure { Log.w(AgentService.TAG, "restrict $r: ${it.javaClass.simpleName}") }
        for (r in d.restrictRemove) runCatching { dpm.clearUserRestriction(admin, r) }
            .onFailure { Log.w(AgentService.TAG, "unrestrict $r: ${it.javaClass.simpleName}") }
        if (d.meteredChanged && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) runCatching {
            dpm.setMeteredDataDisabledPackages(admin, next.metered.toList())
        }.onFailure { Log.w(AgentService.TAG, "metered: ${it.javaClass.simpleName}") }

        config.lastAppliedPolicyJson = AndroidPolicyLogic.toJson(next)
        Log.i(AgentService.TAG, "политика применена: suspend=${next.suspend.size} hide=${next.hide.size} " +
            "restrict=${next.restrictions.size} metered=${next.metered.size} refused=${refused.size}")
        report(context, next, refused, notApplied)
    }

    /** Пакеты, которые нельзя блокировать, известные только в рантайме: домашний
     *  экран, звонилка и SMS по умолчанию у этого вендора, и сам агент. */
    private fun runtimeProtected(context: Context): Set<String> {
        val out = LinkedHashSet<String>()
        out.add(context.packageName)
        runCatching {
            val home = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
            context.packageManager.resolveActivity(home, 0)?.activityInfo?.packageName?.let { out.add(it) }
        }
        runCatching {
            (context.getSystemService(Context.TELECOM_SERVICE) as? TelecomManager)?.defaultDialerPackage?.let { out.add(it) }
        }
        runCatching { Telephony.Sms.getDefaultSmsPackage(context)?.let { out.add(it) } }
        return out
    }

    private fun report(context: Context, applied: AndroidPolicyLogic.Policy, refused: Set<String>, notApplied: Set<String>) {
        val detail = JSONObject()
            .put("suspended", applied.suspend.size)
            .put("hidden", applied.hide.size)
            .put("restrictions", JSONArray(applied.restrictions))
            .put("metered", applied.metered.size)
        if (refused.isNotEmpty()) detail.put("refusedProtected", JSONArray(refused))
        if (notApplied.isNotEmpty()) detail.put("notApplied", JSONArray(notApplied))
        val ev = PendingEvent(TimeUtil.iso(System.currentTimeMillis()), Events.POLICY, null, detail.toString())
        val queue = EventQueue(context, AgentConfig(context).eventQueueMaxRows)
        try {
            queue.prune(); queue.enqueue(ev)
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "policy event: ${e.javaClass.simpleName}")
        } finally {
            queue.close()
        }
    }
}
