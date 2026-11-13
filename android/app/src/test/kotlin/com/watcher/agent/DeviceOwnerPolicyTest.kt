package com.watcher.agent

import android.Manifest
import android.os.Build
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceOwnerPolicyTest {

    @Test
    fun grantsPostNotificationsOnTiramisuAndUp() {
        val perms = DeviceOwnerPolicy.runtimePermissionsToGrant(Build.VERSION_CODES.TIRAMISU)
        assertTrue(perms.contains(Manifest.permission.POST_NOTIFICATIONS))
    }

    @Test
    fun noNotificationsPermissionBeforeTiramisu() {
        // До 13 POST_NOTIFICATIONS не существует — выдавать нечего.
        assertEquals(emptyList<String>(), DeviceOwnerPolicy.runtimePermissionsToGrant(Build.VERSION_CODES.S))
    }
}
