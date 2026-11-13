package com.watcher.agent

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * Автозапуск агента после перезагрузки устройства и после обновления самого
 * приложения.
 *
 * ВАЖНО: BOOT_COMPLETED доставляется только после того, как пользователь хотя
 * бы раз запустил приложение вручную (а на части вендорских прошивок — ещё и
 * если приложение не было принудительно остановлено). Это ожидаемо и
 * закрывается на onboarding (Этап 7, docs/android-plan.md): мастер проводит
 * первый запуск и вендорские настройки фоновой работы. MY_PACKAGE_REPLACED
 * доставляется и без первого запуска после обновления агента.
 */
class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        Log.i(AgentService.TAG, "BootReceiver.onReceive: ${intent.action}")
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED,
            Intent.ACTION_MY_PACKAGE_REPLACED -> AgentService.start(context)
        }
    }
}
