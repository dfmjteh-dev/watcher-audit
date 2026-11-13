package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** JVM unit-тесты diff перечня приложений (Этап 3). */
class AppInventoryTest {

    @Test
    fun firstSnapshotIsNotReportedAsInstalls() {
        val d = AppInventory.diff(null, setOf("a", "b"))
        assertTrue(d.installed.isEmpty())
        assertTrue(d.removed.isEmpty())
    }

    @Test
    fun diffFindsInstalledAndRemovedSorted() {
        val d = AppInventory.diff(setOf("a", "b", "c"), setOf("c", "z", "a", "x"))
        assertEquals(listOf("x", "z"), d.installed)
        assertEquals(listOf("b"), d.removed)
    }

    @Test
    fun emptyPreviousSnapshotIsARealSnapshot() {
        // Пустой (но снятый) снимок отличается от «ещё не снимали».
        assertEquals(listOf("a"), AppInventory.diff(emptySet(), setOf("a")).installed)
    }
}
