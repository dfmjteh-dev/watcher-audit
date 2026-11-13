package com.watcher.agent

import android.util.Log
import java.io.ByteArrayInputStream
import java.security.KeyStore
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

/**
 * Доверие сертификату сервера (0.15.0). Сервер Watcher по умолчанию — HTTPS с
 * собственным CA (install-server.sh, Caddy), которого нет в системном хранилище
 * Android: без этого агент к такому адресу не подключится. CA передаётся при
 * подключении (extra `caCert` от install.cmd, base64 DER или PEM) и хранится в
 * настройках; сертификат сервера принимается, если его подтверждают системные
 * CA или этот. Проверка имени хоста — штатная (HttpsURLConnection).
 *
 * Ставится процессу целиком (setDefaultSSLSocketFactory) — все соединения
 * агента (ServerClient, VideoUploader, AgentUpdater) идут к одному серверу.
 */
object ServerTrust {

    /** CA из extra/настроек → сертификат; null — пусто или не X.509 (пишем в лог). */
    fun parseCa(text: String?): X509Certificate? {
        val t = text?.trim().orEmpty()
        if (t.isEmpty()) return null
        val b64 = t.replace(Regex("-----(BEGIN|END) CERTIFICATE-----"), "").replace(Regex("\\s"), "")
        return try {
            val der = java.util.Base64.getDecoder().decode(b64)
            CertificateFactory.getInstance("X.509").generateCertificate(ByteArrayInputStream(der)) as X509Certificate
        } catch (e: IllegalArgumentException) {
            null
        } catch (e: CertificateException) {
            null
        }
    }

    /** Нормализованный вид для хранения (base64 DER одной строкой). */
    fun encode(cert: X509Certificate): String = java.util.Base64.getEncoder().encodeToString(cert.encoded)

    /** Поставить доверие по CA из настроек (старт процесса, после регистрации). */
    fun apply(config: AgentConfig) {
        val ca = parseCa(config.serverCa) ?: return
        try {
            val ctx = SSLContext.getInstance("TLS")
            ctx.init(null, arrayOf(trustManager(ca)), null)
            HttpsURLConnection.setDefaultSSLSocketFactory(ctx.socketFactory)
        } catch (e: Exception) {
            Log.w(AgentService.TAG, "CA сервера не применён: ${e.javaClass.simpleName}")
        }
    }

    /** Системные CA, а не подтвердили — CA сервера. */
    internal fun trustManager(ca: X509Certificate): X509TrustManager {
        val system = tm(null)
        val ours = tm(KeyStore.getInstance(KeyStore.getDefaultType()).apply {
            load(null, null)
            setCertificateEntry("watcher-ca", ca)
        })
        return object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) =
                system.checkClientTrusted(chain, authType)

            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                try {
                    system.checkServerTrusted(chain, authType)
                } catch (e: CertificateException) {
                    ours.checkServerTrusted(chain, authType)
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = system.acceptedIssuers + ca
        }
    }

    private fun tm(ks: KeyStore?): X509TrustManager {
        val f = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
        f.init(ks)
        return f.trustManagers.filterIsInstance<X509TrustManager>().first()
    }
}
