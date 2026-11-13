package com.watcher.agent

import android.app.Activity
import android.app.AlertDialog
import android.text.InputType
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView

/**
 * Офлайн-разблокировка по учётке администратора watcher (Этап 12+). Открывается
 * кнопкой «Вход администратора» с экрана блокировки и из киоска. Проверка пароля
 * — локально ([AdminAuth], PBKDF2 на фоновом потоке), по кредам, которые сервер
 * прислал в heartbeat. Работает без сети.
 */
object AdminUnlockDialog {
    fun show(activity: Activity, onSuccess: () -> Unit) {
        val dp = { v: Int -> (v * activity.resources.displayMetrics.density).toInt() }
        val user = EditText(activity).apply { hint = activity.getString(R.string.admin_unlock_user); setSingleLine() }
        val pass = EditText(activity).apply {
            hint = activity.getString(R.string.admin_unlock_pw); setSingleLine()
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        val status = TextView(activity).apply { setPadding(0, dp(8), 0, 0) }
        val layout = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(12), dp(20), 0)
            addView(TextView(activity).apply { text = activity.getString(R.string.admin_unlock_hint) })
            addView(user); addView(pass); addView(status)
        }
        val dialog = AlertDialog.Builder(activity)
            .setTitle(R.string.admin_unlock_title)
            .setView(layout)
            .setPositiveButton(R.string.admin_unlock_btn, null)
            .setNegativeButton(android.R.string.cancel, null)
            .create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val u = user.text.toString(); val p = pass.text.toString()
                status.text = activity.getString(R.string.admin_unlock_checking)
                Thread {
                    val ok = runCatching { AdminAuth.verify(AgentConfig(activity).adminCreds(), u, p) }.getOrDefault(false)
                    activity.runOnUiThread {
                        if (ok) { dialog.dismiss(); onSuccess() }
                        else status.text = activity.getString(R.string.admin_unlock_bad)
                    }
                }.start()
            }
        }
        dialog.show()
    }
}
