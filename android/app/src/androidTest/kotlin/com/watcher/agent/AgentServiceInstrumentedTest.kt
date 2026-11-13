package com.watcher.agent

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Минимальный инструментальный тест (Этап 1): проверяет пакет приложения и
 * что службу можно запустить без исключения. Должен компилироваться и
 * проходить на эмуляторах Android 10/14/15.
 */
@RunWith(AndroidJUnit4::class)
class AgentServiceInstrumentedTest {

    @Test
    fun usesExpectedPackage() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        assertEquals("com.watcher.agent", context.packageName)
    }

    @Test
    fun serviceStartsWithoutThrowing() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        AgentService.start(context)
    }

    // Этап 2: секрет шифруется ключом из Android Keystore и расшифровывается
    // обратно (нужен настоящий keystore — только на устройстве/эмуляторе).
    @Test
    fun secretSurvivesKeystoreRoundTrip() {
        val secret = "wagt_test-secret-value"
        val restored = SecretStore.decrypt(SecretStore.encrypt(secret))
        assertEquals(secret, restored)
    }

    // Этап 2: saveEnrollment сохраняет адрес/хост/шифрованный секрет, после чего
    // агент считается подключённым, а секрет читается обратно.
    @Test
    fun enrollmentPersistsAndSecretReadsBack() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val config = AgentConfig(context)
        config.saveEnrollment("https://example.invalid", "Pixel-7", "wagt_abc")
        assertEquals(true, config.isEnrolled)
        assertEquals("wagt_abc", config.secret())
        assertEquals("Pixel-7", config.host)
    }
}
