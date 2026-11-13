package com.watcher.agent

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.DisplayMetrics
import android.util.Log
import android.view.WindowManager
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.Executors

/**
 * Запись экрана по согласию (Этап 12+, docs/android-plan.md). Foreground-служба
 * с типом mediaProjection: из одного согласия MediaProjection держит захват весь
 * сеанс, пишет сегменты MP4 фиксированной длины (ротация [ScreenCapture.SEGMENT_MS])
 * и грузит их [VideoUploader] на тот же /api/upload, что станции. Без звука.
 *
 * Согласие даёт пользователь ([ScreenConsentActivity]) — владелец устройства не
 * может писать экран молча; это и есть «по согласию» из плана. Один
 * MediaProjection на весь сеанс (ротация меняет только MediaRecorder на том же
 * VirtualDisplay) — чтобы не переспрашивать согласие на каждый сегмент (Android 14+).
 */
class ScreenCaptureService : Service() {
    private val ui = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var recorder: MediaRecorder? = null
    private var curFile: File? = null
    private var segStartMs = 0L
    private var w = 0; private var h = 0; private var dpi = 0
    private var stopping = false
    private var paused = false
    private var screenReceiver: android.content.BroadcastReceiver? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { stopSelfClean(); return START_NOT_STICKY }
        val code = intent?.getIntExtra(EXTRA_CODE, 0) ?: 0
        val data: Intent? = if (Build.VERSION.SDK_INT >= 33)
            intent?.getParcelableExtra(EXTRA_DATA, Intent::class.java)
        else @Suppress("DEPRECATION") intent?.getParcelableExtra(EXTRA_DATA)
        if (code == 0 || data == null) { stopSelfClean(); return START_NOT_STICKY }
        // Запись уже идёт (второе согласие — гонка разблокировки/perApp): второй
        // MediaProjection перетёр бы текущий, старый MediaRecorder остался бы
        // неостановленным, а ротация шла бы двумя таймерами.
        if (projection != null) {
            Log.i(AgentService.TAG, "запись экрана уже идёт — повторный старт пропущен")
            return START_NOT_STICKY
        }

        startForegroundNotif()
        try {
            val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val p = mpm.getMediaProjection(code, data) ?: run { stopSelfClean(); return START_NOT_STICKY }
            projection = p
            p.registerCallback(object : MediaProjection.Callback() {
                override fun onStop() { stopSelfClean() }
            }, ui)
            measureScreen()
            startSegment()
            registerScreenReceiver()
            ScreenRecordState.capturing = true
            Log.i(AgentService.TAG, "запись экрана начата ${w}x${h}")
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "запись экрана: ${e.javaClass.simpleName}: ${e.message}")
            stopSelfClean()
        }
        return START_NOT_STICKY
    }

    private val rotate = object : Runnable {
        override fun run() {
            if (stopping) return
            rotateSegment()
            ui.postDelayed(this, ScreenCapture.SEGMENT_MS)
        }
    }

    private fun startSegment() {
        val safeHost = (AgentConfig(this).host ?: "android").replace(Regex("[^A-Za-z0-9_.-]"), "_")
        val file = File(segDir(), "screen-$safeHost-${stamp()}-${(0..9999).random()}.mp4")
        val rec = newRecorder(file)
        rec.prepare()
        if (display == null) {
            display = projection!!.createVirtualDisplay(
                "watcher-screen", w, h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, rec.surface, null, null)
        } else {
            display!!.surface = rec.surface
        }
        rec.start()
        recorder = rec; curFile = file; segStartMs = System.currentTimeMillis()
        ScreenRecordState.currentFile = file.name
        ui.postDelayed(rotate, ScreenCapture.SEGMENT_MS)
    }

    private fun rotateSegment() {
        val done = finishCurrent() ?: return
        startSegment()
        enqueueUpload(done.first, done.second, done.third)
    }

    // Останавливает текущий сегмент, возвращает (файл, началоISO, длит.сек) или null.
    private fun finishCurrent(): Triple<File, String, Int>? {
        val rec = recorder ?: return null
        val file = curFile ?: return null
        val startMs = segStartMs
        recorder = null; curFile = null
        ScreenRecordState.currentFile = null
        return try {
            display?.surface = null
            rec.stop(); rec.reset(); rec.release()
            val iso = iso(startMs); val dur = ((System.currentTimeMillis() - startMs) / 1000).toInt().coerceAtLeast(1)
            Triple(file, iso, dur)
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "сегмент: ${e.javaClass.simpleName}")
            runCatching { rec.release() }; runCatching { file.delete() }
            null
        }
    }

    private fun enqueueUpload(file: File, startedIso: String, durSec: Int) {
        val cfg = AgentConfig(this)
        val url = cfg.serverUrl; val sec = cfg.secret(); val host = cfg.host
        if (url == null || sec == null || host == null) { file.delete(); return }
        // «Только Wi-Fi» касается и видео (минута — ~15 МБ): по мобильной сети
        // сегмент ждёт в спуле, его дошлёт тик службы ([ScreenSpool]).
        if (!NetworkUtil.canSend(this, cfg.wifiOnly)) { ScreenSpool.prune(this); return }
        io.execute {
            val ok = VideoUploader(url, sec).upload(file, host, startedIso, iso(System.currentTimeMillis()), durSec)
            if (ok) file.delete()   // не удалось — остаётся в спуле, дошлёт ScreenSpool
            else ScreenSpool.prune(this)
        }
    }

    private fun newRecorder(file: File): MediaRecorder {
        val rec = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(this) else @Suppress("DEPRECATION") MediaRecorder()
        rec.setVideoSource(MediaRecorder.VideoSource.SURFACE)
        rec.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
        rec.setVideoEncoder(MediaRecorder.VideoEncoder.H264)
        rec.setVideoSize(w, h)
        rec.setVideoFrameRate(ScreenCapture.FPS)
        rec.setVideoEncodingBitRate(ScreenCapture.BITRATE)
        rec.setOutputFile(file.absolutePath)
        return rec
    }

    private fun measureScreen() {
        val dm = DisplayMetrics()
        @Suppress("DEPRECATION")
        (getSystemService(Context.WINDOW_SERVICE) as WindowManager).defaultDisplay.getRealMetrics(dm)
        dpi = dm.densityDpi
        // Масштабируем по высоте до MAX_H, чётные стороны (требование кодека).
        val scale = if (dm.heightPixels > ScreenCapture.MAX_H) ScreenCapture.MAX_H.toDouble() / dm.heightPixels else 1.0
        w = ((dm.widthPixels * scale).toInt()) and 1.inv()
        h = ((dm.heightPixels * scale).toInt()) and 1.inv()
        if (w < 2) w = 2; if (h < 2) h = 2
    }

    private fun segDir(): File = ScreenSpool.dir(this)
    private fun stamp() = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date())
    private fun iso(ms: Long) = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date(ms))

    private fun startForegroundNotif() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(CHANNEL) == null) {
            nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.screen_rec_channel), NotificationManager.IMPORTANCE_LOW))
        }
        val notif: Notification = Notification.Builder(this, CHANNEL)
            .setContentTitle(getString(R.string.screen_rec_active))
            .setSmallIcon(android.R.drawable.presence_video_online)
            .setOngoing(true).build()
        if (Build.VERSION.SDK_INT >= 29)
            startForeground(NOTIF_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        else startForeground(NOTIF_ID, notif)
    }

    // Пишем только при включённом экране: на блокировку закрываем+грузим текущий
    // сегмент и останавливаем запись (MediaProjection/VirtualDisplay держим — без
    // повторного согласия), на разблокировку начинаем новый. Иначе бы писали чёрный
    // экран, а таймер ротации замирал в Doze (uptime не идёт) — сегменты не уходили.
    private fun registerScreenReceiver() {
        val r = object : android.content.BroadcastReceiver() {
            override fun onReceive(c: Context?, i: Intent?) {
                when (i?.action) {
                    Intent.ACTION_SCREEN_OFF -> ui.post { pauseCapture() }
                    Intent.ACTION_SCREEN_ON  -> ui.post { resumeCapture() }
                }
            }
        }
        val f = android.content.IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_OFF); addAction(Intent.ACTION_SCREEN_ON)
        }
        runCatching { registerReceiver(r, f) }.onSuccess { screenReceiver = r }
            .onFailure { Log.w(AgentService.TAG, "screen receiver: ${it.javaClass.simpleName}") }
    }

    private fun pauseCapture() {
        if (stopping || paused || recorder == null) return
        paused = true
        ui.removeCallbacks(rotate)
        val done = finishCurrent()
        if (done != null) enqueueUpload(done.first, done.second, done.third)
        Log.i(AgentService.TAG, "запись экрана: пауза (экран погашен), сегмент отправлен")
    }

    private fun resumeCapture() {
        if (stopping || !paused) return
        paused = false
        runCatching { startSegment() }
            .onFailure { Log.w(AgentService.TAG, "запись экрана: возобновление не удалось: ${it.javaClass.simpleName}") }
        Log.i(AgentService.TAG, "запись экрана: возобновлена (экран включён)")
    }

    private fun stopSelfClean() {
        if (stopping) return
        stopping = true
        screenReceiver?.let { runCatching { unregisterReceiver(it) } }; screenReceiver = null
        ui.removeCallbacks(rotate)
        val done = finishCurrent()
        if (done != null) enqueueUpload(done.first, done.second, done.third)
        runCatching { display?.release() }; display = null
        runCatching { projection?.stop() }; projection = null
        ScreenRecordState.capturing = false
        io.shutdown()
        runCatching { if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE) else @Suppress("DEPRECATION") stopForeground(true) }
        stopSelf()
    }

    override fun onDestroy() { stopSelfClean(); super.onDestroy() }

    companion object {
        const val ACTION_STOP = "com.watcher.agent.SCREEN_STOP"
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        private const val CHANNEL = "watcher-screen"
        private const val NOTIF_ID = 42
    }
}

/** Параметры записи экрана (одно место). */
object ScreenCapture {
    const val SEGMENT_MS = 60_000L        // длина сегмента
    const val FPS = 15
    const val BITRATE = 2_000_000
    const val MAX_H = 1280                 // кап по высоте — ограничить размер
    const val MAX_SPOOL_BYTES = 200L * 1024 * 1024  // лимит несоправленных сегментов
}
