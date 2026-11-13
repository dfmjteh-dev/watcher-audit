package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * JVM unit-тест формулы versionCode. Она продублирована в
 * app/build.gradle.kts (agentVersionCode) — тест фиксирует их общий контракт,
 * чтобы правки не разошлись.
 */
class AgentVersionTest {

    @Test
    fun mapsCurrentAgentVersion() {
        // 0.14.4 → 14004 (см. комментарий в build.gradle.kts).
        assertEquals(14004, AgentVersion.code("0.14.4"))
    }

    @Test
    fun mapsComponentsIndependently() {
        assertEquals(1_000_000, AgentVersion.code("1.0.0"))
        assertEquals(1, AgentVersion.code("0.0.1"))
        assertEquals(1_002_003, AgentVersion.code("1.2.3"))
    }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsMalformedVersion() {
        // Слишком мало компонентов — require() в AgentVersion.code.
        AgentVersion.code("0.14")
    }
}
