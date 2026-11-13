package com.watcher.agent

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OwnerLogsLogicTest {

    private fun detail(e: PendingEvent) = JSONObject(e.detail!!)

    @Test
    fun dnsCollapsedByDomainAndPackageWithSummedCount() {
        val hits = listOf(
            OwnerLogsLogic.DnsHit("Example.com", "com.browser", 2, 2000),
            OwnerLogsLogic.DnsHit("example.com", "com.browser", 3, 1000),
            OwnerLogsLogic.DnsHit("example.com", "com.other", 1, 1500),
            OwnerLogsLogic.DnsHit("", "com.browser", 9, 1000),   // пустой домен отброшен
        )
        val events = OwnerLogsLogic.aggregateDns(hits)
        assertEquals(2, events.size)
        assertTrue(events.all { it.type == Events.NET_DNS && it.pkg == null })
        val browser = events.map { detail(it) }.single { it.optString("package") == "com.browser" }
        assertEquals("example.com", browser.getString("domain"))
        assertEquals(5, browser.getInt("count"))
        // ts — самое раннее в группе (1000).
        assertEquals("1970-01-01T00:00:01Z", events.first { detail(it).optString("package") == "com.browser" }.ts)
    }

    @Test
    fun connectsCountedByIpPortPackage() {
        val hits = listOf(
            OwnerLogsLogic.ConnHit("1.2.3.4", 443, "com.app", 5000),
            OwnerLogsLogic.ConnHit("1.2.3.4", 443, "com.app", 6000),
            OwnerLogsLogic.ConnHit("1.2.3.4", 80, "com.app", 7000),
            OwnerLogsLogic.ConnHit("", 443, "com.app", 7000),      // пустой ip отброшен
        )
        val events = OwnerLogsLogic.aggregateConnects(hits)
        assertEquals(2, events.size)
        val https = events.map { detail(it) }.single { it.getInt("port") == 443 }
        assertEquals("1.2.3.4", https.getString("ip"))
        assertEquals(2, https.getInt("count"))
    }

    @Test
    fun dnsWithoutPackageKeepsNullPackageField() {
        val events = OwnerLogsLogic.aggregateDns(listOf(OwnerLogsLogic.DnsHit("sys.example", "", 1, 0)))
        assertFalse("пустой пакет не кладём в detail", detail(events.single()).has("package"))
    }

    @Test
    fun securityEventsNamedAndCarryData() {
        val hits = listOf(
            OwnerLogsLogic.SecHit(210002, 1000, "[\"ls\"]"),
            OwnerLogsLogic.SecHit(999999, 2000, null),
        )
        val events = OwnerLogsLogic.securityEvents(hits)
        assertEquals(2, events.size)
        assertTrue(events.all { it.type == Events.SECURITY })
        assertEquals("adb_shell_command", detail(events[0]).getString("name"))
        assertEquals("[\"ls\"]", detail(events[0]).getString("data"))
        assertEquals("tag_999999", detail(events[1]).getString("name"))
        assertFalse(detail(events[1]).has("data"))
    }

    @Test
    fun securityEventMsHandlesEpochAndBootClocksAndClamps() {
        val now = 1_761_000_000_000L          // «сейчас», мс
        val elapsed = 500_000_000_000L        // аптайм 500 c в нс
        // Конвенция «эпоха-нс» (Honor/HarmonyOS): timeNanos ≈ now в нс.
        val epochNanos = (now - 2_000) * 1_000_000L
        assertEquals(now - 2_000, OwnerLogsLogic.securityEventMs(epochNanos, now, elapsed))
        // Конвенция «от загрузки»: событие 10 c назад → now-10c.
        val bootNanos = elapsed - 10_000_000_000L
        assertEquals(now - 10_000, OwnerLogsLogic.securityEventMs(bootNanos, now, elapsed))
        // Мусор (2083-й из-за кривой конвенции) → время приёма now.
        val bogus = (now + 50L * 365 * 86_400_000L) * 1_000_000L
        assertEquals(now, OwnerLogsLogic.securityEventMs(bogus, now, elapsed))
    }

    @Test
    fun securityDataJoinsValues() {
        assertEquals("""["a","2"]""", securityDataToString(listOf("a", 2)))
        assertEquals(null, securityDataToString(emptyList()))
        assertEquals(null, securityDataToString(null))
    }
}
