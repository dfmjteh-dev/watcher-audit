package com.watcher.agent

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * Лаунчер киоска (Этап 13): домашний экран в режиме киоска. Входит в lock-task
 * (разрешённые приложения — из [KioskState.allowed], выставлены владельцем в
 * setLockTaskPackages), показывает их кнопками. Выйти из киоска нельзя — только
 * командой сервера (allowed пустеет → активность выходит из lock-task).
 */
class KioskActivity : Activity() {
    private val ui = Handler(Looper.getMainLooper())
    private val poll = object : Runnable {
        override fun run() {
            if (KioskState.allowed.isEmpty()) { exitKiosk(); return }
            ui.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        restoreAllowed()
        render()
    }

    // После ребута система открывает киоск как домашний экран раньше, чем служба
    // сделала первый тик, — список ещё пуст, и киоск сам себя снимал (с HOME-ролью),
    // открывая обычный лаунчер. Берём список из закэшированной политики.
    private fun restoreAllowed() {
        if (KioskState.allowed.isNotEmpty()) return
        val cfg = AgentConfig(this)
        if (cfg.adminOverride) return
        KioskState.allowed = runCatching { AndroidPolicyLogic.fromConfig(cfg.configJson).kiosk }.getOrDefault(emptySet())
    }

    override fun onResume() {
        super.onResume()
        restoreAllowed()
        KioskState.active = true
        if (KioskState.allowed.isEmpty()) { exitKiosk(); return }
        runCatching { startLockTask() }.onFailure { Log.w(AgentService.TAG, "kiosk startLockTask: ${it.javaClass.simpleName}") }
        render()
        ui.removeCallbacks(poll); ui.post(poll)
    }

    override fun onBackPressed() { /* в киоске выход заблокирован */ }

    override fun onDestroy() {
        ui.removeCallbacks(poll)
        KioskState.active = false
        super.onDestroy()
    }

    // Админ офлайн вышел из киоска: override + очищаем allowed (heartbeat
    // сообщит adminUnlocked=true), выходим из lock-task.
    private fun onAdminUnlock() {
        AgentConfig(this).adminOverride = true
        KioskState.allowed = emptySet()
        exitKiosk()
    }

    private fun exitKiosk() {
        ui.removeCallbacks(poll)
        runCatching { stopLockTask() }
        // Снимаем нашу HOME-привязку: иначе система снова откроет этот лаунчер при
        // возврате «домой» и после finish() (самоисцеление уже «залипшего» киоска).
        runCatching { KioskController.releaseHome(this) }
        KioskState.active = false
        finish()
    }

    private fun render() {
        val pm = packageManager
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            val pad = (24 * resources.displayMetrics.density).toInt()
            setPadding(pad, pad, pad, pad)
            addView(TextView(this@KioskActivity).apply {
                text = getString(R.string.kiosk_title); textSize = 20f; gravity = Gravity.CENTER
                setPadding(0, 0, 0, pad)
            })
        }
        for (pkg in KioskState.allowed.sorted()) {
            val launch = runCatching { pm.getLaunchIntentForPackage(pkg) }.getOrNull()
            val label = runCatching { pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString() }.getOrDefault(pkg)
            root.addView(Button(this).apply {
                text = label
                isEnabled = launch != null
                setOnClickListener { launch?.let { startActivity(it) } }
            })
        }
        // Офлайн-выход из киоска по учётке администратора (работает без сети).
        root.addView(Button(this).apply {
            text = getString(R.string.admin_unlock_open)
            val padT = (24 * resources.displayMetrics.density).toInt()
            setPadding(0, padT, 0, 0)
            setOnClickListener { AdminUnlockDialog.show(this@KioskActivity) { onAdminUnlock() } }
        })
        setContentView(ScrollView(this).apply { addView(root) })
    }
}
