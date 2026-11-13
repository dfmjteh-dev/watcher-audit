package com.watcher.agent

import java.nio.ByteBuffer
import java.util.Base64
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec

/**
 * Офлайн-проверка пароля администратора watcher (Этап 12+, docs/android-plan.md):
 * чтобы снять блокировку/киоск на устройстве, когда сервер недоступен. Сервер
 * присылает агенту логины админов и их хэши (PBKDF2 из ASP.NET PasswordHasher,
 * формат v3) в heartbeat; агент проверяет введённый пароль локально тем же
 * PBKDF2 через штатный JCA — без сторонних библиотек.
 *
 * Чистая логика (строки на входе) — покрыта JVM unit-тестами.
 */
object AdminAuth {
    /** Пара логин/хэш одного админа. */
    data class Cred(val username: String, val hash: String)

    /** Разбор JSON-массива [{u,h}] (как шлёт сервер). */
    fun parse(json: String?): List<Cred> {
        if (json.isNullOrBlank()) return emptyList()
        return try {
            val arr = org.json.JSONArray(json)
            (0 until arr.length()).mapNotNull {
                val o = arr.optJSONObject(it) ?: return@mapNotNull null
                val u = o.optString("u"); val h = o.optString("h")
                if (u.isEmpty() || h.isEmpty()) null else Cred(u, h)
            }
        } catch (e: Exception) { emptyList() }
    }

    /** Есть ли среди [creds] админ с таким логином и паролем. Логин без учёта регистра. */
    fun verify(creds: List<Cred>, username: String, password: String): Boolean {
        val u = username.trim()
        return creds.any { it.username.equals(u, ignoreCase = true) && verifyHash(it.hash, password) }
    }

    /**
     * Проверка пароля против ASP.NET Identity PasswordHasher v3:
     * [0]=0x01, [1..4]=PRF (BE: 0=SHA1,1=SHA256,2=SHA512), [5..8]=итерации (BE),
     * [9..12]=длина соли (BE), далее соль и subkey. Сверяем PBKDF2 того же PRF.
     */
    fun verifyHash(hashB64: String, password: String): Boolean {
        val bytes = try { Base64.getDecoder().decode(hashB64.trim()) } catch (e: Exception) { return false }
        if (bytes.size < 13 || bytes[0].toInt() != 0x01) return false
        val bb = ByteBuffer.wrap(bytes)
        bb.get() // marker
        val prf = bb.int
        val iterations = bb.int
        val saltLen = bb.int
        if (iterations <= 0 || saltLen <= 0 || 13 + saltLen >= bytes.size) return false
        val salt = ByteArray(saltLen); bb.get(salt)
        val subkey = ByteArray(bytes.size - 13 - saltLen); bb.get(subkey)
        val algo = when (prf) { 0 -> "PBKDF2WithHmacSHA1"; 1 -> "PBKDF2WithHmacSHA256"; 2 -> "PBKDF2WithHmacSHA512"; else -> return false }
        return try {
            val spec = PBEKeySpec(password.toCharArray(), salt, iterations, subkey.size * 8)
            val derived = SecretKeyFactory.getInstance(algo).generateSecret(spec).encoded
            constantTimeEquals(derived, subkey)
        } catch (e: Exception) { false }
    }

    private fun constantTimeEquals(a: ByteArray, b: ByteArray): Boolean {
        if (a.size != b.size) return false
        var diff = 0
        for (i in a.indices) diff = diff or (a[i].toInt() xor b[i].toInt())
        return diff == 0
    }
}
