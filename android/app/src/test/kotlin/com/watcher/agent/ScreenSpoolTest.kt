package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ScreenSpoolTest {
    @Test
    fun startTimeFromSegmentName() {
        // 2026-10-28T10:05:30Z
        assertEquals(1793181930000L, ScreenSpool.startedAtMs("screen-honor-tablet-20261028-100530-42.mp4"))
        assertNull(ScreenSpool.startedAtMs("other.mp4"))
        assertNull(ScreenSpool.startedAtMs("screen-x-20261399-100530-1.mp4"))
    }
}
