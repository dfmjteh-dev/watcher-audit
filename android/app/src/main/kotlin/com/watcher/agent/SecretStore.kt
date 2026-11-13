package com.watcher.agent

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Шифрование секрета агента (wagt_…) ключом из Android Keystore (Этап 2,
 * docs/android-plan.md).
 *
 * Без сторонних зависимостей (docs/development.md, «Политика зависимостей»):
 * androidx.security:security-crypto не берём — хватает штатного AndroidKeyStore.
 * Ключ AES-256 живёт в keystore (не экспортируется из устройства), секрет
 * шифруется AES/GCM/NoPadding, в SharedPreferences кладётся только шифртекст
 * (Base64 от IV ‖ ciphertext). Сам секрет в открытом виде на диск не попадает
 * и не логируется.
 */
object SecretStore {
    private const val KEYSTORE = "AndroidKeyStore"
    private const val KEY_ALIAS = "watcher_agent_secret"
    private const val TRANSFORMATION = "AES/GCM/NoPadding"
    private const val IV_LENGTH = 12
    private const val TAG_BITS = 128

    /** Шифрует строку; результат — Base64(IV ‖ ciphertext) для SharedPreferences. */
    fun encrypt(plaintext: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val iv = cipher.iv
        val ct = cipher.doFinal(plaintext.toByteArray(Charsets.UTF_8))
        val combined = ByteArray(iv.size + ct.size)
        System.arraycopy(iv, 0, combined, 0, iv.size)
        System.arraycopy(ct, 0, combined, iv.size, ct.size)
        return Base64.encodeToString(combined, Base64.NO_WRAP)
    }

    /** Расшифровывает то, что вернул [encrypt]. Бросает при повреждении/подмене. */
    fun decrypt(stored: String): String {
        val combined = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, combined, 0, IV_LENGTH))
        val pt = cipher.doFinal(combined, IV_LENGTH, combined.size - IV_LENGTH)
        return String(pt, Charsets.UTF_8)
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (ks.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE)
        gen.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return gen.generateKey()
    }
}
