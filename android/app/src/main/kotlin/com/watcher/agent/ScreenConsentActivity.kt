package com.watcher.agent

import android.app.Activity
import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.util.Log

/**
 * Запрашивает системное согласие MediaProjection на запись экрана (Этап 12+).
 * Владелец устройства НЕ может писать экран молча — это согласие и есть то, что
 * план называет «по согласию». Прозрачная активность: виден только диалог системы.
 *
 * Режим «device» (EXTRA_DEVICE_MODE): «да» → запись весь сеанс (и каденс: сутки не
 * переспрашиваем); «нет» → просто гасим экран (lockNow) — обычная разблокировка
 * телефона, НЕ анти-тампер-блокировка; и каденс не ставим, чтобы переспросить при
 * следующей разблокировке.
 * Режим «perApp» (EXTRA_PACKAGE): открыто целевое приложение; «да» → запись, пока
 * им пользуются; «нет» → уходим на домашний экран (приложение не остаётся).
 */
class ScreenConsentActivity : Activity() {
    private var perAppPkg: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        perAppPkg = intent?.getStringExtra(EXTRA_PACKAGE)
        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        try { startActivityForResult(mpm.createScreenCaptureIntent(), REQ) }
        catch (e: Exception) { finish() }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ) {
            if (resultCode == RESULT_OK && data != null) onGranted(data) else onDeclined()
        }
        finish()
    }

    private fun onGranted(data: Intent) {
        // Каденс режима «device» ставим ТОЛЬКО при согласии: сутки не переспрашиваем.
        // Отказ каденс не трогает — переспросим при следующей разблокировке.
        if (perAppPkg == null) AgentConfig(this).lastScreenConsentAt = System.currentTimeMillis()
        val svc = Intent(this, ScreenCaptureService::class.java)
            .putExtra(ScreenCaptureService.EXTRA_CODE, RESULT_OK)
            .putExtra(ScreenCaptureService.EXTRA_DATA, data)
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(svc) else startService(svc)
    }

    private fun onDeclined() {
        if (perAppPkg != null) {
            // perApp: приложение не должно остаться открытым без согласия — уходим домой.
            runCatching {
                startActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            return
        }
        // device: отказ — просто гасим экран (как кнопка питания). Разблокировка —
        // обычная, телефоном можно пользоваться; запись переспросят при след. разблокировке.
        if (intent?.getBooleanExtra(EXTRA_DEVICE_MODE, false) == true) {
            runCatching { (getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager).lockNow() }
                .onFailure { Log.w(AgentService.TAG, "lockNow: ${it.javaClass.simpleName}") }
        }
    }

    companion object {
        private const val REQ = 7001
        const val EXTRA_DEVICE_MODE = "deviceMode"
        const val EXTRA_PACKAGE = "perAppPackage"
    }
}
