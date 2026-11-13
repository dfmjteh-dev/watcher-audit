package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AndroidPolicyLogicTest {

    @Test
    fun parsesPolicyFromConfig() {
        val cfg = """{"outputFps":4,"androidPolicy":{"suspendedPackages":["com.a","com.b"],
            "restrictions":["no_debugging_features","bogus"],"meteredDataDisabledPackages":["com.c"]}}"""
        val p = AndroidPolicyLogic.fromConfig(cfg)
        assertEquals(setOf("com.a", "com.b"), p.suspend)
        assertEquals(setOf("com.c"), p.metered)
        // Неизвестный запрет отфильтрован.
        assertEquals(setOf("no_debugging_features"), p.restrictions)
    }

    @Test
    fun parsesScreenRecord() {
        val p = AndroidPolicyLogic.fromConfig("""{"androidPolicy":{"screenRecord":{"mode":"perApp","apps":["com.x","com.y"]}}}""")
        assertEquals("perApp", p.screenRecordMode)
        assertEquals(setOf("com.x", "com.y"), p.screenRecordApps)
        // Режим по умолчанию off; неизвестный режим → off.
        assertEquals("off", AndroidPolicyLogic.fromConfig("""{"androidPolicy":{}}""").screenRecordMode)
        assertEquals("off", AndroidPolicyLogic.fromConfig("""{"androidPolicy":{"screenRecord":{"mode":"always"}}}""").screenRecordMode)
        assertEquals("device", AndroidPolicyLogic.fromConfig("""{"androidPolicy":{"screenRecord":{"mode":"device"}}}""").screenRecordMode)
    }

    @Test
    fun noPolicyOrGarbageIsEmpty() {
        assertEquals(AndroidPolicyLogic.EMPTY, AndroidPolicyLogic.fromConfig("""{"outputFps":4}"""))
        assertEquals(AndroidPolicyLogic.EMPTY, AndroidPolicyLogic.fromConfig("<<<"))
        assertEquals(AndroidPolicyLogic.EMPTY, AndroidPolicyLogic.fromConfig(null))
    }

    @Test
    fun protectRemovesProtectedAndReportsRefused() {
        val policy = AndroidPolicyLogic.Policy(
            suspend = setOf("com.game", "com.android.settings", "com.watcher.agent"),
            hide = setOf("com.vendor.home"),
        )
        val (filtered, refused) = AndroidPolicyLogic.protect(policy, extraProtected = setOf("com.vendor.home"))
        assertEquals(setOf("com.game"), filtered.suspend)
        assertTrue(filtered.hide.isEmpty())
        assertEquals(setOf("com.android.settings", "com.watcher.agent", "com.vendor.home"), refused)
    }

    @Test
    fun protectNoopWhenNothingProtected() {
        val policy = AndroidPolicyLogic.Policy(suspend = setOf("com.game"))
        val (filtered, refused) = AndroidPolicyLogic.protect(policy, emptySet())
        assertEquals(policy, filtered)
        assertTrue(refused.isEmpty())
    }

    @Test
    fun diffComputesApplyAndRevertDeltas() {
        val prev = AndroidPolicyLogic.Policy(
            suspend = setOf("com.a", "com.b"), hide = setOf("com.h1"),
            restrictions = setOf("no_config_vpn"), metered = setOf("com.m"),
        )
        val next = AndroidPolicyLogic.Policy(
            suspend = setOf("com.b", "com.c"), hide = emptySet(),
            restrictions = setOf("no_config_vpn", "no_factory_reset"), metered = setOf("com.m"),
        )
        val d = AndroidPolicyLogic.diff(prev, next)
        assertEquals(setOf("com.c"), d.suspendOn)
        assertEquals(setOf("com.a"), d.suspendOff)     // снятое — откатываем
        assertEquals(setOf("com.h1"), d.hideOff)
        assertEquals(setOf("no_factory_reset"), d.restrictAdd)
        assertTrue(d.restrictRemove.isEmpty())
        assertFalse(d.meteredChanged)                  // список тот же
        assertFalse(d.isEmpty)
    }

    @Test
    fun diffEmptyWhenUnchanged() {
        val p = AndroidPolicyLogic.Policy(suspend = setOf("com.a"))
        assertTrue(AndroidPolicyLogic.diff(p, p).isEmpty)
    }

    @Test
    fun toJsonRoundTrips() {
        val p = AndroidPolicyLogic.Policy(suspend = setOf("com.a"), restrictions = setOf("no_factory_reset"))
        val back = AndroidPolicyLogic.parse(AndroidPolicyLogic.toJson(p))
        assertEquals(p.suspend, back.suspend)
        assertEquals(p.restrictions, back.restrictions)
    }
}
