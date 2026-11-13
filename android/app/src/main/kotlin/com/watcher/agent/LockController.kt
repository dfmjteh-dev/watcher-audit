package com.watcher.agent

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log

/**
 * Удалённая полная блокировка устройства (Этап 12, docs/android-plan.md): по
 * команде сервера агент-владелец запирает телефон на экран-заглушку в режиме
 * lock-task (kiosk) — им нельзя пользоваться, выход только по команде снять.
 * Переживает ребут (служба поднимается BootReceiver и переприменяет желаемое).
 *
 * Экран показывает [LockActivity]; запускаем его full-screen-intent уведомлением
 * — надёжный способ поднять активность из фона (как экран входящего звонка).
 */
object LockController {
    private const val CHANNEL = "watcher_lock"
    // Не 42: это id foreground-уведомления записи экрана (ScreenCaptureService) —
    // уведомление блокировки заменяло его, а clearNotification пыталось снять чужое.
    private const val NOTIF_ID = 43

    /** Привести фактическое состояние к желаемому. desired из команды сервера. */
    fun converge(context: Context, desired: Boolean) {
        LockState.shouldLock = desired
        if (desired && DeviceOwner.isOwner(context) && !LockState.active) {
            val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager
            val admin = AdminReceiver.component(context)
            // Владелец разрешает lock-task только для пакетов из списка — вносим себя.
            runCatching { dpm?.setLockTaskPackages(admin, arrayOf(context.packageName)) }
                .onFailure { Log.w(AgentService.TAG, "setLockTaskPackages: ${it.javaClass.simpleName}") }
            launchLockScreen(context)
        }
        // Снятие (desired=false) делает сама LockActivity: она опрашивает
        // LockState.shouldLock и выходит из lock-task.
    }

    /** Заблокировано ли устройство сейчас (для heartbeat). */
    fun locked(): Boolean = LockState.active

    private fun launchLockScreen(context: Context) {
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val ch = NotificationChannel(CHANNEL, context.getString(R.string.lock_channel), NotificationManager.IMPORTANCE_HIGH)
            ch.description = context.getString(R.string.lock_channel_desc)
            nm.createNotificationChannel(ch)
        }
        val intent = Intent(context, LockActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or
            (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) PendingIntent.FLAG_IMMUTABLE else 0)
        val pending = PendingIntent.getActivity(context, 0, intent, flags)
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
            android.app.Notification.Builder(context, CHANNEL) else @Suppress("DEPRECATION") android.app.Notification.Builder(context)
        val n = builder
            .setContentTitle(context.getString(R.string.lock_title))
            .setContentText(context.getString(R.string.lock_text))
            .setSmallIcon(R.drawable.ic_agent)
            .setOngoing(true)
            .setFullScreenIntent(pending, true)
            .setContentIntent(pending)
            .build()
        runCatching { nm.notify(NOTIF_ID, n) }
            .onFailure { Log.w(AgentService.TAG, "lock notify: ${it.javaClass.simpleName}") }
        // На части прошивок full-screen-intent не поднимает активность сам —
        // пробуем ещё и напрямую (владельцу/в lock-task это разрешено).
        runCatching { context.startActivity(intent) }
    }

    fun clearNotification(context: Context) {
        runCatching { (context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(NOTIF_ID) }
    }
}
