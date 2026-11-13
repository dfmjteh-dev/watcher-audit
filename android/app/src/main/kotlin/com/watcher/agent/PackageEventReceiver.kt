package com.watcher.agent

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * Приёмник установок/удалений приложений (Этап 3, docs/android-plan.md):
 * ACTION_PACKAGE_ADDED/REMOVED со схемой данных package. Ставит событие в
 * локальную очередь сразу, не дожидаясь периодического diff'а, — установка APK
 * попадает на сервер быстро. Diff в [AppInventoryCollector] остаётся как
 * подстраховка на случай, когда агент был выключен и broadcast не получил.
 *
 * Обновление приложения приходит парой REMOVED(replacing)+ADDED(replacing):
 * «удаление» в этом случае пропускаем, оставляем только установку.
 */
class PackageEventReceiver(private val dispatch: (Runnable) -> Unit) : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val app = context.applicationContext
        val copy = Intent(intent)
        dispatch(Runnable { enqueue(app, copy) })
    }

    private fun enqueue(context: Context, intent: Intent) {
        if (!AgentConfig(context).isEnrolled) return
        val pkg = intent.data?.schemeSpecificPart ?: return
        val replacing = intent.getBooleanExtra(Intent.EXTRA_REPLACING, false)
        val ts = TimeUtil.iso(System.currentTimeMillis())
        val config = AgentConfig(context)
        val queue = EventQueue(context, config.eventQueueMaxRows)
        try {
            val event = when (intent.action) {
                Intent.ACTION_PACKAGE_ADDED -> Events.appInstalled(pkg, ts, InstallSource.of(context, pkg))
                Intent.ACTION_PACKAGE_REMOVED -> if (replacing) null else Events.appRemoved(pkg, ts)
                else -> null
            }
            if (event != null) {
                queue.enqueue(event)
                // Сбор и receiver выполняются одним планировщиком: diff не повторит
                // уже записанную установку/удаление. Первый снимок остаётся полным.
                config.appSnapshot?.let { previous ->
                    config.appSnapshot = if (intent.action == Intent.ACTION_PACKAGE_ADDED) previous + pkg else previous - pkg
                }
                config.lastInventoryAt = 0L
            }
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "package receiver: ${e.javaClass.simpleName}: ${e.message}")
        } finally {
            queue.close()
        }
    }
}
