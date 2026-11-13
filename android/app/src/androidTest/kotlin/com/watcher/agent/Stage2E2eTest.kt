package com.watcher.agent

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeFalse
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Сквозной (e2e) инструментальный тест Этапа 2: реальный сетевой стек агента
 * (ServerClient + Protocol + AgentConfig + SecretStore) против ЖИВОГО сервера.
 *
 * Прогоняется вручную на стенде, не в обычном CI: без аргумента `serverUrl`
 * тест ПРОПУСКАЕТСЯ (assumeFalse), поэтому в штатном
 * `connectedDebugAndroidTest` он безвреден. Запуск на стенде — через
 * `am instrument` с аргументами на каждый эмулятор свои, напр.:
 *
 *   adb -s emulator-5554 shell am instrument -w \
 *     -e class com.watcher.agent.Stage2E2eTest \
 *     -e serverUrl http://10.0.2.2:5099 -e enrollToken wenr_… -e host android-a10 \
 *     com.watcher.agent.test/androidx.test.runner.AndroidJUnitRunner
 *
 * (10.0.2.2 — хост эмулятора со стороны гостя.) Проверяет критерий готовности
 * Этапа 2: регистрация → хост виден на сервере, heartbeat идёт, назначенный
 * конфиг доходит до устройства и кэшируется.
 */
@RunWith(AndroidJUnit4::class)
class Stage2E2eTest {

    private val args = InstrumentationRegistry.getArguments()

    @Test
    fun enrollHeartbeatAndConfigReachDevice() {
        val serverUrl = args.getString("serverUrl")
        val token = args.getString("enrollToken")
        // Без адреса сервера/токена — это не стендовый e2e-прогон, пропускаем.
        assumeFalse("serverUrl/enrollToken не заданы — пропуск e2e", serverUrl.isNullOrEmpty() || token.isNullOrEmpty())

        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val host = args.getString("host")?.takeIf { it.isNotEmpty() }
            ?: DeviceName.default(android.os.Build.MANUFACTURER, android.os.Build.MODEL)

        val client = ServerClient(serverUrl!!)

        // 1. Регистрация по токену → секрет агента.
        val enroll = client.enroll(token!!, host)
        assertNotNull("регистрация не вернула секрет", enroll)
        assertTrue("секрет не похож на wagt_…", enroll!!.secret.startsWith("wagt_"))

        // Сохранить, как это делает MainActivity.
        val config = AgentConfig(context)
        assertTrue("регистрация не записалась на диск", config.saveEnrollment(serverUrl, enroll.host.ifEmpty { host }, enroll.secret))
        assertTrue("после регистрации агент должен считаться подключённым", config.isEnrolled)

        // 2. Heartbeat с секретом → действующий конфиг от сервера.
        val hb = client.heartbeat(
            secret = config.secret()!!,
            host = config.host!!,
            version = BuildConfig.VERSION_NAME,
            configHash = config.configHash,
            queueFiles = 0,
            ownerActive = false,
        )
        assertNotNull("heartbeat не прошёл", hb)
        config.applyConfig(hb!!)

        // 3. Если токену назначена группа с конфигом — конфиг доходит до
        //    устройства. Проверяем, только когда ждём конфиг (expectConfig=1),
        //    чтобы тест годился и для токена без группы.
        if (args.getString("expectConfig") == "1") {
            assertNotNull("конфиг с сервера не дошёл", hb.configJson)
            assertNotNull("метка конфига (configHash) не пришла", hb.configHash)
        }
    }
}
