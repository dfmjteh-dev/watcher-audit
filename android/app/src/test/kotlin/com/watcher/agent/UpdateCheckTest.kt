package com.watcher.agent

import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdateCheckTest {

    // Ключ P-256 и подпись сообщения того же формата, что у сервера.
    private val keyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pubB64 = Base64.getEncoder().encodeToString(keyPair.public.encoded)

    private fun sign(platform: String, version: String, sha: String): String {
        val s = Signature.getInstance("SHA256withECDSA")
        s.initSign(keyPair.private)
        s.update(UpdateCheck.message(platform, version, sha))
        return Base64.getEncoder().encodeToString(s.sign())
    }

    @Test
    fun verifiesGoodSignatureAndRejectsTampering() {
        val sha = "abc123"
        val sig = sign("android", "0.15.0", sha)
        assertTrue(UpdateCheck.verify(pubB64, "android", "0.15.0", sha, sig))
        // Другая версия/хэш/платформа — подпись уже не подходит.
        assertFalse(UpdateCheck.verify(pubB64, "android", "0.15.1", sha, sig))
        assertFalse(UpdateCheck.verify(pubB64, "android", "0.15.0", "deadbeef", sig))
        assertFalse(UpdateCheck.verify(pubB64, "linux-deb", "0.15.0", sha, sig))
    }

    @Test
    fun rejectsBadKeyOrSignature() {
        assertFalse(UpdateCheck.verify(null, "android", "1", "a", "x"))
        assertFalse(UpdateCheck.verify(pubB64, "android", "1", "a", "not-base64!!"))
        assertFalse(UpdateCheck.verify("garbage", "android", "1", "a", sign("android", "1", "a")))
    }

    @Test
    fun shaCaseInsensitive() {
        assertTrue(UpdateCheck.sha256Matches("ABC123", "abc123"))
        assertFalse(UpdateCheck.sha256Matches("abc", "abd"))
    }

    @Test
    fun shouldAttemptRespectsCurrentAndFailedThrottle() {
        assertFalse(UpdateCheck.shouldAttempt("1.0.0", "1.0.0", null, 0, 1000))
        assertTrue(UpdateCheck.shouldAttempt("1.0.1", "1.0.0", null, 0, 1000))
        // Недавно провалившуюся версию пропускаем час.
        assertFalse(UpdateCheck.shouldAttempt("1.0.1", "1.0.0", "1.0.1", 1000, 1000 + 60_000))
        assertTrue(UpdateCheck.shouldAttempt("1.0.1", "1.0.0", "1.0.1", 1000, 1000 + UpdateCheck.RETRY_FAILED_MS + 1))
    }

    @Test
    fun downgradeDetectedByNumericParts() {
        assertTrue(UpdateCheck.isDowngrade("0.14.9", "0.14.28"))
        assertTrue(UpdateCheck.isDowngrade("0.13.0", "0.14.0+abc"))
        assertFalse(UpdateCheck.isDowngrade("0.14.29", "0.14.28"))
        assertFalse(UpdateCheck.isDowngrade("0.14.28", "0.14.28"))
        assertFalse(UpdateCheck.isDowngrade("1.0.0", "0.99.99"))
    }
}
