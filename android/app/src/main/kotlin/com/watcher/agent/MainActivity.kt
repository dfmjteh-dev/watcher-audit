package com.watcher.agent

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import java.util.concurrent.Executors

/**
 * Экран агента (Этапы 1–3). Запрашивает разрешение на уведомления (13+),
 * запускает foreground-службу и показывает либо форму регистрации (если агент
 * ещё не подключён), либо статус. Ничего скрытого — экран нужен и для «первого
 * запуска», без которого система не доставляет BOOT_COMPLETED в [BootReceiver].
 *
 * Регистрация — ручной ввод адреса сервера, токена и имени хоста (Этап 2).
 * QR-ввод токена отложен: он тянет камеру + библиотеку распознавания — заводим
 * позже, если понадобится (TODO Этап 2+, docs/android-plan.md).
 *
 * Этап 3: на экране статуса — выдан ли доступ к статистике использования
 * (без него UsageEvents и трафик не собираются) с кнопкой в системные
 * настройки, и локальный переключатель «только Wi-Fi» для отправки очереди.
 */
class MainActivity : Activity() {

    private val io = Executors.newSingleThreadExecutor()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ServerTrust.apply(AgentConfig(this))
        requestNotificationsPermissionIfNeeded()
        // Старт службы из активности: «первый запуск» для автозапуска по
        // BOOT_COMPLETED и легальный старт foreground-сервиса (приложение на экране).
        AgentService.start(this)
        render()
        maybeAutoEnroll()
    }

    /**
     * Этап 7: авто-регистрация при онбординге. provision.sh запускает активность
     * с extras serverUrl/token/host; если агент ещё не подключён и параметры
     * валидны — регистрируемся без ручного ввода. Extras снимаем (setIntent),
     * чтобы поворот экрана/возврат не повторяли попытку.
     */
    private fun maybeAutoEnroll() {
        if (AgentConfig(this).isEnrolled) return
        val params = Provisioning.parse(
            intent?.getStringExtra(EXTRA_SERVER_URL),
            intent?.getStringExtra(EXTRA_TOKEN),
            intent?.getStringExtra(EXTRA_HOST),
            DeviceName.default(Build.MANUFACTURER, Build.MODEL),
        ) ?: return
        // CA сервера (install.cmd) — до регистрации: она уже идёт по https.
        ServerTrust.parseCa(intent?.getStringExtra(EXTRA_CA_CERT))?.let { ca ->
            AgentConfig(this).serverCa = ServerTrust.encode(ca)
            ServerTrust.apply(AgentConfig(this))
        }
        setIntent(Intent())
        enroll(params.serverUrl, params.token, params.host, null, null)
    }

    /**
     * Возврат из системных настроек (доступ к статистике) — перерисовать статус.
     * Форму регистрации не трогаем: пересоздание стёрло бы введённое.
     */
    override fun onResume() {
        super.onResume()
        if (statusShown && AgentConfig(this).isEnrolled) render()
    }

    override fun onDestroy() {
        io.shutdownNow()
        super.onDestroy()
    }

    private var statusShown = false

    private fun render() {
        statusShown = AgentConfig(this).isEnrolled
        setContentView(if (statusShown) statusView() else enrollView())
    }

    /**
     * На 13+ (TIRAMISU) без POST_NOTIFICATIONS служба работает, но постоянное
     * уведомление не отображается. На более старых версиях разрешение выдаётся
     * при установке — запрос не нужен.
     */
    private fun requestNotificationsPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQ_NOTIFICATIONS)
        }
    }

    private fun statusView(): View {
        val config = AgentConfig(this)
        return column {
            addView(heading(getString(R.string.notification_title)))
            addView(body(getString(R.string.status_enrolled, config.host ?: "", config.serverUrl ?: "")))
            addView(body(getString(R.string.status_version, BuildConfig.VERSION_NAME)))
            val usageGranted = UsageAccess.granted(this@MainActivity)
            addView(body(getString(if (usageGranted) R.string.status_usage_granted else R.string.status_usage_missing)))
            if (!usageGranted) {
                addView(Button(this@MainActivity).apply {
                    text = getString(R.string.status_usage_open)
                    setOnClickListener { openUsageAccessSettings() }
                })
            }
            addView(Switch(this@MainActivity).apply {
                text = getString(R.string.status_wifi_only)
                isChecked = config.wifiOnly
                setPadding(0, dp(8), 0, dp(8))
                setOnCheckedChangeListener { _, checked -> config.wifiOnly = checked }
            })
        }
    }

    /**
     * Доступ к статистике — специальный app-op, а не runtime-разрешение: выдаётся
     * только в системных настройках. На части прошивок экрана нет — тогда
     * подсказываем путь словами (или adb appops, docs/android-build.md).
     */
    private fun openUsageAccessSettings() {
        try {
            startActivity(Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS))
        } catch (e: ActivityNotFoundException) {
            AlertDialog.Builder(this)
                .setMessage(R.string.status_usage_no_settings)
                .setPositiveButton(android.R.string.ok, null)
                .show()
        }
    }

    private fun enrollView(): View {
        val serverUrl = editor(getString(R.string.enroll_server_url_hint))
        val token = editor(getString(R.string.enroll_token_hint))
        val host = editor(getString(R.string.enroll_host_hint)).apply {
            setText(DeviceName.default(Build.MANUFACTURER, Build.MODEL))
        }
        val status = body("")
        val button = Button(this).apply { text = getString(R.string.enroll_button) }

        button.setOnClickListener {
            val url = serverUrl.text.toString().trim()
            val tok = token.text.toString().trim()
            val hostName = DeviceName.sanitize(host.text.toString())
            // Ленивая проверка адреса (http/https) через java.net.URL —
            // Patterns.WEB_URL отвергает host:port и внутренние хосты, которые
            // как раз и используются в корпоративной сети.
            if (!Provisioning.isHttpUrl(url) || tok.isEmpty() || hostName == null) {
                status.text = getString(R.string.enroll_need_fields)
                return@setOnClickListener
            }
            button.isEnabled = false
            status.text = getString(R.string.enroll_progress)
            enroll(url, tok, hostName, button, status)
        }

        return column {
            addView(heading(getString(R.string.notification_title)))
            addView(body(getString(R.string.enroll_intro)))
            addView(serverUrl)
            addView(token)
            addView(host)
            addView(button)
            addView(status)
        }
    }

    private fun enroll(url: String, token: String, host: String, button: Button?, status: TextView?) {
        io.execute {
            val result = ServerClient(url).enroll(token, host)
            runOnUiThread {
                if (result == null) {
                    button?.isEnabled = true
                    status?.text = getString(R.string.enroll_failed)
                    return@runOnUiThread
                }
                // Подтверждённое сервером имя хоста (а не введённое) — источник правды.
                AgentConfig(this).saveEnrollment(url, result.host.ifEmpty { host }, result.secret, result.updatePublicKey)
                // Перезапуск службы подхватывает регистрацию для немедленного heartbeat.
                AgentService.start(this)
                render()
            }
        }
    }

    // --- Мелкие помощники построения UI (без layout-XML, как на Этапе 1). ---

    private fun column(block: LinearLayout.() -> Unit): View {
        val pad = dp(24)
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
            block()
        }
        return ScrollView(this).apply { addView(layout) }
    }

    private fun heading(text: String) = TextView(this).apply {
        this.text = text
        textSize = 20f
        gravity = Gravity.CENTER
        setPadding(0, 0, 0, dp(16))
    }

    private fun body(text: String) = TextView(this).apply {
        this.text = text
        setPadding(0, dp(8), 0, dp(8))
    }

    private fun editor(hint: String) = EditText(this).apply {
        this.hint = hint
        setSingleLine()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private companion object {
        const val REQ_NOTIFICATIONS = 1
        // Этап 7: extras авто-регистрации (provision.sh → am start -e …).
        const val EXTRA_SERVER_URL = "serverUrl"
        const val EXTRA_TOKEN = "token"
        const val EXTRA_HOST = "host"
        const val EXTRA_CA_CERT = "caCert"
    }
}
