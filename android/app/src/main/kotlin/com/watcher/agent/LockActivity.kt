package com.watcher.agent

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView

/**
 * Экран-заглушка удалённой блокировки (Этап 12). В onResume входит в lock-task
 * (kiosk) — владелец устройства пинит экран без подтверждения пользователя, home
 * и recents отключаются, уйти нельзя. Опрашивает [LockState]: как только сервер
 * снял блокировку (shouldLock=false) — выходит из lock-task и закрывается.
 */
class LockActivity : Activity() {
    private val ui = Handler(Looper.getMainLooper())
    private val poll = object : Runnable {
        override fun run() {
            if (!LockState.shouldLock) { unlockAndFinish(); return }
            ui.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) { setShowWhenLocked(true); setTurnScreenOn(true) }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(lockView())
    }

    override fun onResume() {
        super.onResume()
        LockState.active = true
        if (LockState.shouldLock) {
            runCatching { startLockTask() }.onFailure { Log.w(AgentService.TAG, "startLockTask: ${it.javaClass.simpleName}") }
            ui.removeCallbacks(poll); ui.post(poll)
        } else {
            unlockAndFinish()
        }
    }

    // Блокировка — кнопки «назад» и «домой» в lock-task и так не работают, но на
    // всякий случай back гасим.
    override fun onBackPressed() { /* заблокировано */ }

    override fun onDestroy() {
        ui.removeCallbacks(poll)
        LockState.active = false
        super.onDestroy()
    }

    // Админ успешно разблокировал офлайн: ставим override (heartbeat сообщит
    // серверу adminUnlocked=true, тот снимет запрос блокировки) и выходим.
    private fun onAdminUnlock() {
        AgentConfig(this).adminOverride = true
        LockState.shouldLock = false
        unlockAndFinish()
    }

    private fun unlockAndFinish() {
        ui.removeCallbacks(poll)
        runCatching { stopLockTask() }
        LockState.active = false
        LockController.clearNotification(this)
        finish()
    }

    private fun lockView(): View = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        gravity = Gravity.CENTER
        setBackgroundColor(0xFF101418.toInt())
        val pad = (24 * resources.displayMetrics.density).toInt()
        setPadding(pad, pad, pad, pad)
        addView(TextView(this@LockActivity).apply {
            text = getString(R.string.lock_title)
            textSize = 24f; gravity = Gravity.CENTER; setTextColor(0xFFFFFFFF.toInt())
            setPadding(0, 0, 0, pad)
        })
        addView(TextView(this@LockActivity).apply {
            text = getString(R.string.lock_body)
            textSize = 16f; gravity = Gravity.CENTER; setTextColor(0xFFB0B8C0.toInt())
        })
        // Офлайн-разблокировка по учётке администратора (работает без сети).
        addView(Button(this@LockActivity).apply {
            text = getString(R.string.admin_unlock_open)
            setPadding(0, pad, 0, 0)
            setOnClickListener { AdminUnlockDialog.show(this@LockActivity) { onAdminUnlock() } }
        })
    }
}
