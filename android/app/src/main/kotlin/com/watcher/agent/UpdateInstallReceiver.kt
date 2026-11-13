package com.watcher.agent

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.util.Log

/**
 * Итог тихой установки обновления (Этап 8). PackageInstaller шлёт сюда статус
 * сессии. Успех приходит уже НОВОЙ версии (старый процесс заменён при установке)
 * — она записывает "ok <своя версия>"; ошибку получает текущая версия и
 * записывает "failed …" + отметку, чтобы не долбить битый пакет (UpdateCheck).
 */
class UpdateInstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val version = intent.getStringExtra(EXTRA_VERSION) ?: "?"
        val config = AgentConfig(context)
        Log.i(AgentService.TAG, "результат установки $version: статус $status ${intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: ""}")
        when (status) {
            PackageInstaller.STATUS_SUCCESS -> {
                Log.i(AgentService.TAG, "обновление установлено, версия ${BuildConfig.VERSION_NAME}")
                config.updateStatus = "ok ${BuildConfig.VERSION_NAME}"
                config.lastFailedUpdate = null
                AgentService.start(context)
            }
            PackageInstaller.STATUS_PENDING_USER_ACTION ->
                // У владельца не должно случаться; в фоне диалог не показываем.
                AgentUpdater.fail(config, version, "требуется подтверждение пользователя")
            else -> {
                val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "status $status"
                AgentUpdater.fail(config, version, msg)
            }
        }
    }

    companion object {
        const val EXTRA_VERSION = "watcher_update_version"
    }
}
