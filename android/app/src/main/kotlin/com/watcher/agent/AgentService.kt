package com.watcher.agent

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit

/**
 * Foreground-служба агента (Этапы 1–3).
 *
 * Этап 1 — удержать службу живой с постоянным, недемонтируемым уведомлением
 * (устройство корпоративное, действия фиксируются). Этап 2 — цикл heartbeat:
 * если агент зарегистрирован, периодически шлёт на сервер версию, состояние
 * и очередь и применяет пришедший конфиг (docs/android-plan.md). Этап 3 — в том
 * же цикле сбор событий в локальную очередь ([EventCollection]) и её отправка
 * на /api/events ([EventFlusher]); установки/удаления ловит runtime-receiver
 * [PackageEventReceiver] на том же потоке. Ничего скрытого: уведомление видно всегда.
 */
class AgentService : Service() {

    private var scheduler: ScheduledExecutorService? = null
    private var packageReceiver: PackageEventReceiver? = null
    // Этап 12+: разблокировка устройства — триггер согласия на запись экрана (режим device).
    private var unlockReceiver: android.content.BroadcastReceiver? = null
    // Этап 12+: держим CPU «бодрым», чтобы цикл heartbeat/сбора не замирал в Doze
    // при выключенном экране (планировщик на uptime, который в глубоком сне стоит).
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onCreate() {
        super.onCreate()
        ServerTrust.apply(AgentConfig(this))
        createChannel()
    }

    // Служба без привязки (bound) — bind не поддерживаем.
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i(TAG, "onStartCommand: перевод в foreground")
        val notification = buildNotification()
        // На 14+ (UPSIDE_DOWN_CAKE) тип foreground-сервиса обязателен при
        // старте и должен совпадать с объявленным в манифесте (specialUse).
        // На 29..33 тип не навязывается — обычный 2-аргументный startForeground.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE,
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
        startHeartbeatLoop()
        // START_STICKY: система переподнимет службу, если убьёт её под нагрузкой.
        return START_STICKY
    }

    /**
     * Цикл heartbeat на одном фоновом потоке (Этап 2). Запускается один раз;
     * повторные onStartCommand (автозапуск, повторный старт из активности) его
     * не дублируют. Интервал берётся из [AgentConfig]. Сеть идёт на потоке
     * планировщика, не на главном.
     */
    private fun startHeartbeatLoop() {
        if (scheduler != null) return
        val config = AgentConfig(this)
        val exec = Executors.newSingleThreadScheduledExecutor()
        scheduler = exec
        acquireWakeLock()
        val receiver = PackageEventReceiver { task ->
            if (!exec.isShutdown) {
                runCatching { exec.execute(task) }
                    .onFailure { Log.w(TAG, "package receiver: ${it.javaClass.simpleName}") }
            }
        }
        val filter = IntentFilter().apply {
            addAction(Intent.ACTION_PACKAGE_ADDED)
            addAction(Intent.ACTION_PACKAGE_REMOVED)
            addDataScheme("package")
        }
        // Эти защищённые broadcasts получает только живой runtime-receiver;
        // пропуски при остановленной службе восстановит периодический diff.
        // Не зарегистрировался (вендорская прошивка и т.п.) — служба живёт дальше,
        // установки поймает периодический diff, только с задержкой.
        runCatching {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
            } else {
                @Suppress("DEPRECATION")
                registerReceiver(receiver, filter)
            }
        }.onSuccess { packageReceiver = receiver }
            .onFailure { Log.w(TAG, "package receiver не зарегистрирован: ${it.javaClass.simpleName}") }
        // Этап 12+: ACTION_USER_PRESENT (разблокировка) — не из манифеста (implicit
        // broadcast режется на O+), только у живой службы. Для режима «device»
        // показывает согласие на запись экрана не чаще раза в сутки.
        val unlock = object : android.content.BroadcastReceiver() {
            override fun onReceive(c: Context?, i: Intent?) {
                runCatching { ScreenRecordController.onUnlock(this@AgentService) }
                    .onFailure { Log.w(TAG, "unlock receiver: ${it.javaClass.simpleName}") }
            }
        }
        runCatching {
            registerReceiver(unlock, IntentFilter(Intent.ACTION_USER_PRESENT))
        }.onSuccess { unlockReceiver = unlock }
            .onFailure { Log.w(TAG, "unlock receiver не зарегистрирован: ${it.javaClass.simpleName}") }
        exec.scheduleWithFixedDelay(
            {
                // Отдельный tick не должен ронять планировщик: сеть/ключ могут бросить.
                runCatching { tick(config) }
                    .onFailure { Log.w(TAG, "heartbeat tick: ${it.javaClass.simpleName}: ${it.message}") }
            },
            0,
            config.heartbeatIntervalMs,
            TimeUnit.MILLISECONDS,
        )
    }

    // Этап 11: переприменяли ли политику форсированно в этом процессе (после старта/ребута).
    private var policyAppliedThisStart = false

    private fun tick(config: AgentConfig) {
        if (!config.isEnrolled) return
        val secret = config.secret() ?: return
        val url = config.serverUrl ?: return
        val host = config.host ?: return

        // Этап 3: сбор событий — локальный, идёт всегда (не зависит от сети) и
        // устойчив к сбоям отдельных коллекторов (служба не падает).
        val queue = EventQueue(this, config.eventQueueMaxRows)
        try {
            queue.prune()
            runCatching { EventCollection(this, config, queue).collectAndEnqueue(System.currentTimeMillis()) }
                .onFailure { Log.w(TAG, "сбор событий: ${it.javaClass.simpleName}: ${it.message}") }

            // Этап 5: если агент — владелец устройства, закрепляем политики
            // (запрет удаления, выдача разрешений) и сообщаем роль серверу.
            val ownerActive = runCatching { DeviceOwner.enforce(this) }.getOrDefault(false)

            val client = ServerClient(url)
            val result = client.heartbeat(
                secret, host, BuildConfig.VERSION_NAME, config.configHash,
                queueFiles = runCatching { queue.count() }.getOrDefault(0),
                ownerActive = ownerActive,
                updateStatus = config.updateStatus,
                locked = LockController.locked(),
                adminUnlocked = config.adminOverride,
            )
            // Первый тик процесса (после ребута/перезапуска): политику, киоск и
            // блокировку применяем и без сети — из закэшированного конфига и
            // запомненной команды блокировки. Иначе перезагрузка без сети снимала
            // блокировку/киоск/запреты, пока сервер недоступен.
            val firstTick = !policyAppliedThisStart
            val configChanged = result != null && config.applyConfig(result)
            if (configChanged) {
                Log.i(TAG, "конфиг обновлён: version=${result!!.configVersion}, hash=${result.configHash}")
                // Этап 12+: новая политика/команда с сервера снимает локальный офлайн-оверрайд.
                config.adminOverride = false
            }
            // Этап 12+: обновить креды админов для офлайн-разблокировки (меняются при смене пароля).
            if (result != null) config.setAdminCreds(result.adminCredentials)
            // Этап 11: применить политику управления (владелец устройства). На первом
            // тике процесса — форсированно (переживает ребут: suspension может не
            // сохраниться), дальше — дельтой при смене конфига.
            if (configChanged || firstTick) {
                runCatching { AndroidPolicyManager.apply(this, config, force = firstTick) }
                    .onFailure { Log.w(TAG, "политика: ${it.javaClass.simpleName}: ${it.message}") }
                // Этап 13: режим киоска — привести к списку разрешённых приложений из политики.
                val pol = AndroidPolicyLogic.fromConfig(config.configJson)
                runCatching { KioskController.converge(this, if (config.adminOverride) emptySet() else pol.kiosk) }
                    .onFailure { Log.w(TAG, "киоск: ${it.javaClass.simpleName}: ${it.message}") }
                // Этап 12+: запись экрана по согласию — привести режим к политике
                // (сам захват в режиме device запускается по разблокировке, с согласия).
                runCatching { ScreenRecordController.converge(this, pol.screenRecordMode, pol.screenRecordApps) }
                    .onFailure { Log.w(TAG, "запись экрана: ${it.javaClass.simpleName}: ${it.message}") }
                policyAppliedThisStart = true
            }
            // Этап 8: запинить ключ обновлений, если агента зарегистрировали до того,
            // как ключ задали на сервере (fill-once; уже запинённый не трогаем).
            if (result != null && config.fillUpdatePublicKeyIfEmpty(result.updatePublicKey)) {
                Log.i(TAG, "ключ обновлений получен из heartbeat")
            }
            // Этап 8: самообновление — если сервер предложил пакет целевой версии.
            if (result?.update != null) {
                runCatching { AgentUpdater.maybeUpdate(this, config, result.update, secret) }
                    .onFailure { Log.w(TAG, "обновление: ${it.javaClass.simpleName}: ${it.message}") }
            }
            // Этап 12: удалённая блокировка устройства — привести к желаемому состоянию
            // (офлайн-оверрайд админа снимает блокировку, пока сервер не переназначит).
            if (result != null) config.lockDesired = result.lockDevice
            if (result != null || firstTick) {
                runCatching { LockController.converge(this, config.lockDesired && !config.adminOverride) }
                    .onFailure { Log.w(TAG, "блокировка: ${it.javaClass.simpleName}: ${it.message}") }
            }
            // Этап 12+: офлайн-оверрайд одноразовый. Как только сервер узнал о локальной
            // разблокировке (мы отчитались adminUnlocked, он снял команду → lockDevice=false),
            // снимаем оверрайд — иначе будущая команда блокировки гасилась бы сама собой.
            if (config.adminOverride && result != null && !result.lockDevice) config.adminOverride = false
            // Этап 5: команда сервера снять роль владельца (откат стенда/пилота
            // без сброса). Снимаем только когда реально владелец.
            if (result?.clearOwner == true && ownerActive) {
                runCatching { DeviceOwner.clear(this) }
                    .onFailure { Log.w(TAG, "снятие роли владельца: ${it.javaClass.simpleName}") }
            }

            // Отправка очереди — с учётом локальной настройки «только Wi-Fi».
            if (NetworkUtil.canSend(this, config.wifiOnly)) {
                runCatching { EventFlusher(client, queue).flush(secret, host) }
                    .onFailure { Log.w(TAG, "отправка событий: ${it.javaClass.simpleName}: ${it.message}") }
                // Этап 12+: сегменты записи экрана, не ушедшие сразу.
                if (result != null) runCatching { ScreenSpool.retryPending(this, url, secret, host) }
                    .onFailure { Log.w(TAG, "досылка записи: ${it.javaClass.simpleName}: ${it.message}") }
            }
        } finally {
            queue.close()
        }
    }

    @android.annotation.SuppressLint("WakelockTimeout")
    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        runCatching {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "watcher:agent").apply {
                setReferenceCounted(false)
                acquire()   // без таймаута: агент аудита должен работать постоянно
            }
            Log.i(TAG, "wakelock взят — агент работает при погашенном экране")
        }.onFailure { Log.w(TAG, "wakelock: ${it.javaClass.simpleName}") }
    }

    override fun onDestroy() {
        packageReceiver?.let { runCatching { unregisterReceiver(it) } }
        packageReceiver = null
        unlockReceiver?.let { runCatching { unregisterReceiver(it) } }
        unlockReceiver = null
        scheduler?.shutdownNow()
        scheduler = null
        wakeLock?.let { runCatching { if (it.isHeld) it.release() } }; wakeLock = null
        runCatching { ForegroundWatcher.stop() }   // perApp-наблюдатель (Этап 12+ PR2)
        super.onDestroy()
    }

    /**
     * Канал уведомлений. Каналы существуют с API 26; minSdk = 29, поэтому
     * проверка версии не нужна. IMPORTANCE_LOW — видно, но без звука/вибрации.
     */
    private fun createChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.channel_name),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = getString(R.string.channel_description)
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(): Notification =
        Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_agent)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(getString(R.string.notification_text))
            // Постоянное, несмахиваемое уведомление.
            .setOngoing(true)
            .setCategory(Notification.CATEGORY_STATUS)
            .build()

    companion object {
        const val TAG = "WatcherAgent"
        private const val CHANNEL_ID = "watcher_agent"
        private const val NOTIFICATION_ID = 1

        /**
         * Запустить службу как foreground. startForegroundService доступен с
         * API 26 (minSdk 29) — отдельная зависимость androidx.core не нужна
         * (docs/development.md, «Политика зависимостей»).
         */
        fun start(context: Context) {
            try {
                context.startForegroundService(Intent(context, AgentService::class.java))
            } catch (e: RuntimeException) {
                // На 12+ запуск foreground-службы из фона запрещён вне списка
                // исключений (ForegroundServiceStartNotAllowedException ⊂
                // RuntimeException). Не роняем вызывающего (ресивер/активити) —
                // состояние «агент не стартовал» будет видно на сервере.
                Log.w(TAG, "startForegroundService отклонён: ${e.javaClass.simpleName}: ${e.message}")
            }
        }
    }
}
