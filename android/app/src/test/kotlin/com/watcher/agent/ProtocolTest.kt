package com.watcher.agent

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * JVM unit-тесты контракта с сервером (Этап 2): сборка тел enroll/heartbeat и
 * разбор ответов. Чистая логика на org.json — реальная реализация подложена в
 * testImplementation (см. build.gradle.kts), Robolectric не нужен.
 */
class ProtocolTest {

    @Test
    fun enrollBodyHasTokenHostAndAndroidKind() {
        val o = JSONObject(Protocol.enrollBody("wenr_abc", "Pixel-7"))
        assertEquals("wenr_abc", o.getString("token"))
        assertEquals("Pixel-7", o.getString("host"))
        assertEquals("android", o.getString("kind"))
    }

    @Test
    fun parseEnrollReadsSecret() {
        val r = Protocol.parseEnroll("""{"secret":"wagt_xyz","host":"Pixel-7","kind":"android"}""")
        assertEquals("wagt_xyz", r?.secret)
        assertEquals("Pixel-7", r?.host)
    }

    @Test
    fun parseEnrollWithoutSecretIsNull() {
        assertNull(Protocol.parseEnroll("""{"error":"invalid token"}"""))
        assertNull(Protocol.parseEnroll("not json"))
    }

    @Test
    fun heartbeatBodyCarriesVersionPlatformAndQueue() {
        val o = JSONObject(Protocol.heartbeatBody("Pixel-7", "0.14.4", configHash = null, queueFiles = 3, ownerActive = false))
        assertEquals("Pixel-7", o.getString("host"))
        assertEquals("0.14.4", o.getString("version"))
        assertEquals("android", o.getString("platform"))
        // configHash отсутствует, если его нет.
        assertTrue(!o.has("configHash"))
        assertEquals(3, o.getJSONObject("resources").getInt("queueFiles"))
        assertEquals(false, o.getBoolean("ownerActive"))
    }

    @Test
    fun heartbeatBodyIncludesConfigHashWhenKnown() {
        val o = JSONObject(Protocol.heartbeatBody("Pixel-7", "0.14.4", configHash = "deadbeef", queueFiles = 0, ownerActive = true))
        assertEquals("deadbeef", o.getString("configHash"))
        assertEquals(true, o.getBoolean("ownerActive"))
    }

    @Test
    fun parseHeartbeatExtractsEffectiveConfig() {
        val r = Protocol.parseHeartbeat(
            """{"configVersion":5,"configHash":"abc123","config":{"outputFps":4},"lossesAccepted":0}""",
        )
        assertEquals(5, r?.configVersion)
        assertEquals("abc123", r?.configHash)
        assertEquals(4, JSONObject(r?.configJson!!).getInt("outputFps"))
    }

    @Test
    fun parseHeartbeatWithoutConfigIsNulls() {
        val r = Protocol.parseHeartbeat("""{"configVersion":null,"configHash":null,"config":null}""")
        assertNull(r?.configVersion)
        assertNull(r?.configHash)
        assertNull(r?.configJson)
    }

    @Test
    fun parseHeartbeatClearOwnerDefaultsFalseAndReadsTrue() {
        // Этап 5: обычный ответ — без команды снять роль.
        assertEquals(false, Protocol.parseHeartbeat("""{"configHash":null}""")?.clearOwner)
        assertEquals(true, Protocol.parseHeartbeat("""{"configHash":null,"clearOwner":true}""")?.clearOwner)
    }

    @Test
    fun heartbeatBodyAndParseLock() {
        val o = org.json.JSONObject(Protocol.heartbeatBody("P", "1", null, 0, ownerActive = true, locked = true))
        assertEquals(true, o.getBoolean("locked"))
        assertEquals(true, Protocol.parseHeartbeat("""{"configHash":null,"lockDevice":true}""")?.lockDevice)
        assertEquals(false, Protocol.parseHeartbeat("""{"configHash":null}""")?.lockDevice)
    }

    @Test
    fun parseHeartbeatReadsUpdatePublicKey() {
        assertNull(Protocol.parseHeartbeat("""{"configHash":null}""")?.updatePublicKey)
        assertEquals("MFkw...", Protocol.parseHeartbeat("""{"configHash":null,"updatePublicKey":"MFkw..."}""")?.updatePublicKey)
    }

    @Test
    fun parseHeartbeatMalformedIsNull() {
        assertNull(Protocol.parseHeartbeat("<<<"))
    }

    @Test
    fun eventsBodyCarriesEventIdAndDetailObject() {
        val e = PendingEvent(5, "2026-10-22T10:00:00Z", Events.APP_INSTALLED, "com.app", """{"installer":"x"}""", "3f2504e0-4f89-41d3-9a0c-0305e82c3301")
        val o = JSONObject(Protocol.eventsBody("Pixel-7", listOf(e)))
        assertEquals("Pixel-7", o.getString("host"))
        val ev = o.getJSONArray("events").getJSONObject(0)
        assertEquals("3f2504e0-4f89-41d3-9a0c-0305e82c3301", ev.getString("eventId"))
        assertEquals("com.app", ev.getString("package"))
        assertEquals("x", ev.getJSONObject("detail").getString("installer"))
        // Локальный id очереди наружу не уходит.
        assertTrue(!ev.has("id"))
    }

    @Test
    fun eventsBodyDropsBrokenDetailAndNullPackage() {
        val e = PendingEvent(1, "2026-10-22T10:00:00Z", Events.DEVICE_STATE, null, "not json", "3f2504e0-4f89-41d3-9a0c-0305e82c3301")
        val ev = JSONObject(Protocol.eventsBody("h", listOf(e))).getJSONArray("events").getJSONObject(0)
        assertTrue(!ev.has("detail"))
        assertTrue(!ev.has("package"))
    }

    @Test
    fun parseEventsAcceptedRequiresNonNegativeInteger() {
        assertEquals(3, Protocol.parseEventsAccepted("""{"accepted":3,"inserted":1}"""))
        assertNull(Protocol.parseEventsAccepted("""{"accepted":-1}"""))
        assertNull(Protocol.parseEventsAccepted("""{"accepted":"3"}"""))
        assertNull(Protocol.parseEventsAccepted("""{"accepted":2.5}"""))
        assertNull(Protocol.parseEventsAccepted("""{"inserted":3}"""))
        assertNull(Protocol.parseEventsAccepted("oops"))
    }
}
