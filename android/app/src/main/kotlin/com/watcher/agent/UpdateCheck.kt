package com.watcher.agent

import java.security.KeyFactory
import java.security.PublicKey
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

/**
 * Проверка пакета обновления (Этап 8, docs/android-plan.md) — чистая логика,
 * покрыта JVM unit-тестами. Формат подписи тот же, что у станций
 * (Watcher/UpdateSigning.cs): ECDSA P-256 / SHA-256 по каноническому сообщению
 * `watcher-agent-update/1\n<platform>\n<version>\n<sha256hex>\n`, подпись — DER
 * (Rfc3279DerSequence) в base64; открытый ключ — SubjectPublicKeyInfo base64
 * (X509EncodedKeySpec). Это граница безопасности: сервер без закрытого ключа
 * владельца ничего не поставит. На Android вдобавок PackageInstaller примет
 * апдейт только за той же подписью APK — независимая вторая проверка.
 *
 * Скачивание и установка (PackageInstaller) — в [AgentUpdater]; здесь только
 * проверка подписи и решение «ставить ли».
 */
object UpdateCheck {
    /** Неудачную версию не пробуем чаще раза в час (битый/не встающий пакет). */
    const val RETRY_FAILED_MS = 3_600_000L

    fun message(platform: String, version: String, sha256Hex: String): ByteArray =
        "watcher-agent-update/1\n$platform\n$version\n${sha256Hex.lowercase()}\n".toByteArray(Charsets.UTF_8)

    /** Открытый ключ из base64 SubjectPublicKeyInfo (или PEM). null — не разобрался. */
    fun loadPublicKey(keyText: String?): PublicKey? {
        if (keyText.isNullOrBlank()) return null
        val b64 = keyText.lines().map { it.trim() }.filter { it.isNotEmpty() && !it.startsWith("-----") }.joinToString("")
        return try {
            KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(b64)))
        } catch (e: Exception) {
            null
        }
    }

    /** Проверка подписи пакета открытым ключом владельца. */
    fun verify(publicKeyText: String?, platform: String, version: String, sha256Hex: String, signatureB64: String): Boolean {
        val key = loadPublicKey(publicKeyText) ?: return false
        return try {
            val sig = Base64.getDecoder().decode(signatureB64.trim())
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(key)
                update(message(platform, version, sha256Hex))
                verify(sig)
            }
        } catch (e: Exception) {
            false
        }
    }

    /**
     * Стоит ли пытаться ставить предложенную версию. Сервер предлагает пакет
     * только если версия отличается от текущей (откат тоже разрешён), но
     * недавно провалившуюся версию пропускаем [RETRY_FAILED_MS].
     */
    fun shouldAttempt(offerVersion: String, currentVersion: String, failedVersion: String?, failedAt: Long, now: Long): Boolean {
        if (offerVersion == currentVersion) return false
        if (offerVersion == failedVersion && now - failedAt in 0 until RETRY_FAILED_MS) return false
        return true
    }

    /** Предложена версия старше текущей (сравнение по X.Y.Z, суффиксы игнорируем). */
    fun isDowngrade(offerVersion: String, currentVersion: String): Boolean {
        fun parts(v: String) = v.substringBefore('+').substringBefore('-').split('.').map { it.toIntOrNull() ?: 0 }
        val a = parts(offerVersion); val b = parts(currentVersion)
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }; val y = b.getOrElse(i) { 0 }
            if (x != y) return x < y
        }
        return false
    }

    /** Сверка SHA-256 скачанного файла с объявленным (без учёта регистра). */
    fun sha256Matches(expectedHex: String, actualHex: String): Boolean =
        expectedHex.trim().equals(actualHex.trim(), ignoreCase = true)
}
