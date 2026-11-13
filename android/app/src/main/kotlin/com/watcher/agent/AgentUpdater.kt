package com.watcher.agent

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.util.Log
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Самообновление агента (Этап 8, docs/android-plan.md). Сервер в ответе heartbeat
 * предлагает пакет целевой версии группы ([Protocol.UpdateOffer]); здесь он
 * скачивается, сверяется по SHA-256 и ПОДПИСИ владельца ([UpdateCheck]) и ставится
 * тихо через [PackageInstaller] — на устройстве-владельце без подтверждения
 * пользователя. Это обновление на месте (тот же пакет и та же подпись APK),
 * поэтому роль владельца, выданные разрешения и данные сохраняются.
 *
 * Итог ставит [UpdateInstallReceiver] (его вызывает система по PendingIntent):
 * успех приходит уже новой версии (она шлёт "ok <ver>"), ошибку ловит текущая.
 */
object AgentUpdater {
    private val busy = AtomicBoolean(false)
    // Сколько ждать результата отправленной установки, прежде чем пробовать снова.
    private const val INSTALL_WAIT_MS = 15 * 60 * 1000L

    fun maybeUpdate(context: Context, config: AgentConfig, offer: Protocol.UpdateOffer, secret: String) {
        if (offer.platform != Protocol.PLATFORM_ANDROID || offer.url.isEmpty()) return
        if (!UpdateCheck.shouldAttempt(offer.version, BuildConfig.VERSION_NAME,
                config.lastFailedUpdate, config.lastFailedUpdateAt, System.currentTimeMillis())) return
        // PackageInstaller не ставит меньший versionCode (только debuggable/системным) —
        // откат группы на старую версию на Android не сработает; говорим это прямо,
        // а не «INSTALL_FAILED_VERSION_DOWNGRADE» после скачивания каждый час.
        if (UpdateCheck.isDowngrade(offer.version, BuildConfig.VERSION_NAME)) {
            fail(config, offer.version, "downgrade is not supported on Android — переустановите агент вручную")
            return
        }
        // Тихая установка возможна только у владельца устройства. Иначе система
        // показала бы диалог — в фоне это бессмысленно; обновит админ при онбординге.
        if (!DeviceOwner.isOwner(context)) {
            Log.i(AgentService.TAG, "обновление ${offer.version}: агент не владелец — тихо не поставить, пропуск")
            return
        }
        // Установку этой версии только что отправили — ждём результат/ребут, не
        // пересоздаём сессию каждую минуту (иначе спам сессий, как на HarmonyOS).
        if (offer.version == config.installingVersion &&
            System.currentTimeMillis() - config.installingAt in 0 until INSTALL_WAIT_MS) return
        if (!busy.compareAndSet(false, true)) return
        try {
            runUpdate(context, config, offer, secret)
        } catch (e: Exception) {
            fail(config, offer.version, "${e.javaClass.simpleName}: ${e.message}")
        } finally {
            busy.set(false)
        }
    }

    private fun runUpdate(context: Context, config: AgentConfig, offer: Protocol.UpdateOffer, secret: String) {
        val serverUrl = config.serverUrl ?: return
        val apk = File(context.cacheDir, "agent-update.apk")
        val sha = download(serverUrl, offer.url, secret, offer.size, apk)
            ?: return fail(config, offer.version, "download failed")
        if (!UpdateCheck.sha256Matches(offer.sha256, sha))
            return fail(config, offer.version, "sha256 mismatch")
        if (!UpdateCheck.verify(config.updatePublicKey, offer.platform, offer.version, offer.sha256, offer.signature))
            return fail(config, offer.version, "bad signature")
        Log.i(AgentService.TAG, "обновление ${offer.version}: пакет проверен, ставлю через PackageInstaller")
        install(context, config, offer.version, apk)
    }

    /** Качает пакет под Bearer-учёткой во временный файл, возвращает его SHA-256 (hex) или null. */
    private fun download(serverUrl: String, path: String, secret: String, expectedSize: Long, dest: File): String? {
        var conn: HttpURLConnection? = null
        return try {
            val url = URL(serverUrl.trimEnd('/') + path)
            conn = (url.openConnection() as HttpURLConnection).apply {
                connectTimeout = 15_000
                readTimeout = 60_000
                setRequestProperty("Authorization", "Bearer $secret")
            }
            if (conn.responseCode != HttpURLConnection.HTTP_OK) {
                Log.w(AgentService.TAG, "обновление: скачивание вернуло ${conn.responseCode}")
                return null
            }
            val digest = MessageDigest.getInstance("SHA-256")
            dest.outputStream().use { out ->
                conn.inputStream.use { inp ->
                    val buf = ByteArray(64 * 1024)
                    while (true) {
                        val n = inp.read(buf); if (n < 0) break
                        digest.update(buf, 0, n); out.write(buf, 0, n)
                    }
                }
            }
            if (expectedSize > 0 && dest.length() != expectedSize) {
                Log.w(AgentService.TAG, "обновление: размер ${dest.length()} != объявленного $expectedSize")
                return null
            }
            digest.digest().joinToString("") { "%02x".format(it) }
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "обновление: скачивание ${e.javaClass.simpleName}: ${e.message}")
            null
        } finally {
            conn?.disconnect()
        }
    }

    private fun install(context: Context, config: AgentConfig, version: String, apk: File) {
        val pi = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        // Android 12+: без этого система ждёт подтверждения пользователя, и в
        // фоне установка «висит» без результата (видно на HarmonyOS). Владелец
        // устройства имеет право ставить тихо — явно требуем «без действий юзера».
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val sessionId = pi.createSession(params)
        pi.openSession(sessionId).use { session ->
            session.openWrite("agent.apk", 0, apk.length()).use { out ->
                apk.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val intent = Intent(context, UpdateInstallReceiver::class.java)
                .putExtra(UpdateInstallReceiver.EXTRA_VERSION, version)
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
            val pending = PendingIntent.getBroadcast(context, sessionId, intent, flags)
            session.commit(pending.intentSender)
        }
        config.markInstalling(version)
        Log.i(AgentService.TAG, "обновление $version: сессия отправлена, жду результат PackageInstaller")
    }

    fun fail(config: AgentConfig, version: String, message: String) {
        val status = "failed $version: $message".take(300)
        Log.w(AgentService.TAG, "обновление $version не удалось: $message")
        config.updateStatus = status
        config.lastFailedUpdate = version
        config.lastFailedUpdateAt = System.currentTimeMillis()
    }
}
