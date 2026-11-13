package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** JVM unit-тесты разбора UsageEvents (Этап 3): фильтр по курсору, типы, порядок. */
class UsageEventsLogicTest {

    private fun raw(ts: Long, type: Int, pkg: String? = "com.app") = UsageEventsLogic.Raw(ts, type, pkg)

    @Test
    fun foregroundAndBackgroundBecomeAppEvents() {
        val out = UsageEventsLogic.process(
            listOf(raw(2_000, UsageEventsLogic.BACKGROUND), raw(1_000, UsageEventsLogic.FOREGROUND)),
            cursorMs = 0,
        )
        // Отсортировано по времени, независимо от порядка на входе.
        assertEquals(listOf(Events.APP_FOREGROUND, Events.APP_BACKGROUND), out.events.map { it.type })
        assertEquals("1970-01-01T00:00:01Z", out.events[0].ts)
        assertEquals("com.app", out.events[0].pkg)
        assertEquals(2_000L, out.newCursor)
    }

    @Test
    fun eventsAtOrBeforeCursorAreSkipped() {
        val out = UsageEventsLogic.process(
            listOf(raw(1_000, UsageEventsLogic.FOREGROUND), raw(1_500, UsageEventsLogic.FOREGROUND), raw(1_501, UsageEventsLogic.BACKGROUND)),
            cursorMs = 1_500,
        )
        assertEquals(1, out.events.size)
        assertEquals(Events.APP_BACKGROUND, out.events[0].type)
        assertEquals(1_501L, out.newCursor)
    }

    @Test
    fun otherTypesAndMissingPackageAreIgnored() {
        val out = UsageEventsLogic.process(
            listOf(raw(1_000, 23), raw(1_100, UsageEventsLogic.FOREGROUND, pkg = null)),
            cursorMs = 0,
        )
        assertTrue(out.events.isEmpty())
        // Курсор не двигается без обработанных событий.
        assertEquals(0L, out.newCursor)
    }
}
