package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ProvisioningTest {

    @Test
    fun parsesValidTriple() {
        val p = Provisioning.parse("http://10.0.0.5:5099", "wenr_abc", "Pixel 7", "fallback")!!
        assertEquals("http://10.0.0.5:5099", p.serverUrl)
        assertEquals("wenr_abc", p.token)
        assertEquals("Pixel 7", p.host)
    }

    @Test
    fun blankHostFallsBackToDefault() {
        assertEquals("def-device", Provisioning.parse("https://s", "t", "   ", "def-device")!!.host)
        assertEquals(DeviceName.FALLBACK, Provisioning.parse("https://s", "t", null, "   ")!!.host)
    }

    @Test
    fun rejectsMissingUrlOrToken() {
        assertNull(Provisioning.parse(null, "t", "h", "d"))
        assertNull(Provisioning.parse("not-a-url", "t", "h", "d"))
        assertNull(Provisioning.parse("ftp://x", "t", "h", "d"))
        assertNull(Provisioning.parse("https://s", "", "h", "d"))
        assertNull(Provisioning.parse("https://s", "   ", "h", "d"))
    }

    @Test
    fun acceptsHostPortAndInternalHosts() {
        // Внутренние адреса корпоративной сети — валидны (не по Patterns.WEB_URL).
        assertEquals(true, Provisioning.isHttpUrl("http://watcher.lan:8443"))
        assertEquals(true, Provisioning.isHttpUrl("https://10.1.2.3"))
        assertEquals(false, Provisioning.isHttpUrl("://nohost"))
    }
}
