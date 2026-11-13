package com.watcher.agent

import android.content.Context
import android.content.Intent

/**
 * Управление записью экрана по политике (Этап 12+). Чистая часть выбора режима +
 * запуск/остановка [ScreenCaptureService] и запрос согласия ([ScreenConsentActivity]).
 *
 * PR1: режим «device» — согласие при разблокировке не чаще раза в сутки, дальше
 * захват весь сеанс. «off» — останавливаем захват. «perApp» — PR2 (пока не
 * запускаем автоматически; список приложений сохраняем в состоянии).
 */
object ScreenRecordController {
    const val CONSENT_INTERVAL_MS = 24L * 60 * 60 * 1000

    /** Привести состояние к политике (вызывается из heartbeat-тика). */
    fun converge(context: Context, mode: String, apps: Set<String>) {
        val prev = ScreenRecordState.mode
        ScreenRecordState.mode = mode
        ScreenRecordState.apps = apps
        when (mode) {
            // perApp: захватом рулит наблюдатель переднего плана (запись только во
            // время целевого приложения).
            "perApp" -> {
                ForegroundWatcher.start(context)
            }
            // device: наблюдатель не нужен; захват поднимается по разблокировке (onUnlock).
            // Свежее включение режима (или старт процесса — prev=="off" по умолчанию)
            // сбрасывает каденс: спросим согласие при ближайшей разблокировке, а не
            // ждём сутки с прошлого раза.
            "device" -> {
                ForegroundWatcher.stop()
                if (prev != "device") AgentConfig(context).lastScreenConsentAt = 0L
            }
            // off: запись выключена — глушим наблюдатель, захват и снимаем блокировку-отказ.
            else -> {
                ForegroundWatcher.stop()
                if (ScreenRecordState.capturing) stop(context)
            }
        }
    }

    /** Разблокировка устройства: для режима «device» показать согласие, если пора. */
    fun onUnlock(context: Context) {
        if (ScreenRecordState.mode != "device") return
        if (ScreenRecordState.capturing) return
        val cfg = AgentConfig(context)
        if (System.currentTimeMillis() - cfg.lastScreenConsentAt < CONSENT_INTERVAL_MS) return
        context.startActivity(Intent(context, ScreenConsentActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            .putExtra(ScreenConsentActivity.EXTRA_DEVICE_MODE, true))
    }

    fun stop(context: Context) {
        context.startService(Intent(context, ScreenCaptureService::class.java)
            .setAction(ScreenCaptureService.ACTION_STOP))
    }
}
