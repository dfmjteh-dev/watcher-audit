package com.watcher.agent

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** JVM unit-тесты сборки событий (Этап 3): типы, detail, разбиение снимка приложений. */
class EventsTest {

    private fun app(pkg: String) = Events.AppInfo(pkg, "1.0", 1, "com.android.vending", false)

    @Test
    fun inventoryIsSplitIntoLinkedSortedParts() {
        val apps = (1..23).map { app("pkg.%02d".format(it)) }.shuffled()
        val events = Events.appInventory(apps, "2026-10-22T10:00:00Z")
        assertEquals(3, events.size)
        val details = events.map { JSONObject(it.detail!!) }
        assertEquals(1, details.map { it.getString("snapshotId") }.toSet().size)
        assertEquals(listOf(0, 1, 2), details.map { it.getInt("part") })
        assertTrue(details.all { it.getInt("parts") == 3 })
        assertEquals(listOf(10, 10, 3), details.map { it.getJSONArray("packages").length() })
        assertEquals("pkg.01", details[0].getJSONArray("packages").getJSONObject(0).getString("package"))
        assertTrue(events.all { it.type == Events.APP_INVENTORY && it.pkg == null })
        // Каждая часть — отдельное событие со своим eventId.
        assertEquals(3, events.map { it.eventId }.toSet().size)
    }

    @Test
    fun emptyInventoryStillSendsOnePart() {
        val events = Events.appInventory(emptyList(), "2026-10-22T10:00:00Z")
        assertEquals(1, events.size)
        assertEquals(0, JSONObject(events[0].detail!!).getJSONArray("packages").length())
    }

    @Test
    fun inventoryOmitsEmptyOptionalFields() {
        val e = Events.appInventory(listOf(Events.AppInfo("a", null, 7, null, true)), "2026-10-22T10:00:00Z")
        val item = JSONObject(e[0].detail!!).getJSONArray("packages").getJSONObject(0)
        assertEquals(7L, item.getLong("versionCode"))
        assertTrue(item.getBoolean("system"))
        assertFalse(item.has("versionName"))
        assertFalse(item.has("installer"))
    }

    @Test
    fun deviceStateKeepsOnlyKnownFields() {
        val e = Events.deviceState("2026-10-22T10:00:00Z", "14", "2026-09-01", "wifi", null, 55, true, null)
        assertEquals(Events.DEVICE_STATE, e.type)
        assertNull(e.pkg)
        val d = JSONObject(e.detail!!)
        assertEquals("wifi", d.getString("networkType"))
        assertEquals(55, d.getInt("batteryPercent"))
        assertFalse(d.has("ssid"))
        assertFalse(d.has("screenOn"))
        assertFalse(d.has("usageAccess"))
        val blind = JSONObject(Events.deviceState("2026-10-22T10:00:00Z", null, null, null, null, null, null, null, usageAccess = false).detail!!)
        assertFalse(blind.getBoolean("usageAccess"))
    }

    @Test
    fun trafficListsPackagesOnlyForSharedUid() {
        val single = JSONObject(Events.appTraffic("a", "2026-10-22T10:00:00Z", 10, 20, 300_000, "wifi", 10123, listOf("a")).detail!!)
        assertFalse(single.has("packages"))
        assertEquals(10123, single.getInt("uid"))
        val shared = JSONObject(Events.appTraffic("a", "2026-10-22T10:00:00Z", 10, 20, 300_000, "cellular", 1000, listOf("a", "b")).detail!!)
        assertEquals(2, shared.getJSONArray("packages").length())
        assertEquals("cellular", shared.getString("networkType"))
    }

    @Test
    fun installedCarriesInstallerWhenKnown() {
        assertEquals("com.android.vending", JSONObject(Events.appInstalled("a", "t", "com.android.vending").detail!!).getString("installer"))
        assertNull(Events.appInstalled("a", "t", null).detail)
    }

    @Test
    fun eventIdsAreUnique() {
        assertNotEquals(Events.appRemoved("a", "t").eventId, Events.appRemoved("a", "t").eventId)
    }
}
