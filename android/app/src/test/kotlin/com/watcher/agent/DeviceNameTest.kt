package com.watcher.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * JVM unit-тесты имени хоста по умолчанию и санитайза (Этап 2). Санитайз обязан
 * совпадать с серверной проверкой (AgentAuth.IsValidHostName), иначе сервер
 * отклонит регистрацию.
 */
class DeviceNameTest {

    @Test
    fun combinesManufacturerAndModel() {
        assertEquals("Google Pixel 7", DeviceName.default("Google", "Pixel 7"))
    }

    @Test
    fun dropsManufacturerWhenModelAlreadyContainsIt() {
        assertEquals("Samsung Galaxy S21", DeviceName.default("samsung", "Samsung Galaxy S21"))
    }

    @Test
    fun fallsBackWhenEmpty() {
        assertEquals(DeviceName.FALLBACK, DeviceName.default("", ""))
        assertEquals(DeviceName.FALLBACK, DeviceName.default(null, null))
    }

    @Test
    fun sanitizeStripsForbiddenAndControlChars() {
        // < > " ' ` запрещены на сервере (IsSafeMetaField) — заменяем на '-'.
        assertEquals("a-b", DeviceName.sanitize("a<b"))
        assertEquals("x y", DeviceName.sanitize("x\ty"))
        assertNull(DeviceName.sanitize("   "))
        assertNull(DeviceName.sanitize(null))
    }

    @Test
    fun sanitizeCapsLength() {
        val long = "n".repeat(500)
        val result = DeviceName.sanitize(long)!!
        assertTrue(result.length <= DeviceName.MAX_LENGTH)
    }
}
