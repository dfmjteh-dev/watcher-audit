package com.watcher.agent

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AdminAuthTest {
    // Реальный хэш ASP.NET PasswordHasher (v3, PRF SHA512) для пароля "offline-admin-pw".
    private val hash = "AQAAAAIAAYagAAAAEGuXskpC4OOC/cnLF66rwU1yShwENOOZ6i8HmsyAYNLGecatmnb07f5QthM1nYGzJg=="

    @Test
    fun verifiesCorrectPasswordAgainstAspNetHash() {
        assertTrue(AdminAuth.verifyHash(hash, "offline-admin-pw"))
        assertFalse(AdminAuth.verifyHash(hash, "wrong"))
        assertFalse(AdminAuth.verifyHash("not-base64!!", "x"))
        assertFalse(AdminAuth.verifyHash("", "x"))
    }

    @Test
    fun verifyAgainstCredList() {
        val creds = AdminAuth.parse("""[{"u":"admin","h":"$hash"},{"u":"op","h":"garbage"}]""")
        assertTrue(AdminAuth.verify(creds, "admin", "offline-admin-pw"))
        assertTrue(AdminAuth.verify(creds, "ADMIN", "offline-admin-pw"))   // логин без учёта регистра
        assertFalse(AdminAuth.verify(creds, "admin", "nope"))
        assertFalse(AdminAuth.verify(creds, "nobody", "offline-admin-pw"))
        assertFalse(AdminAuth.verify(creds, "op", "whatever"))
    }

    @Test
    fun parseHandlesGarbage() {
        assertTrue(AdminAuth.parse(null).isEmpty())
        assertTrue(AdminAuth.parse("<<<").isEmpty())
        assertTrue(AdminAuth.parse("[]").isEmpty())
    }
}
