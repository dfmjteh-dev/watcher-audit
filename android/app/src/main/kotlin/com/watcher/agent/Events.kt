package com.watcher.agent

import org.json.JSONArray
import org.json.JSONObject

/**
 * Сборка событий ([PendingEvent]) с их type-специфичным detail-блобом (Этап 3,
 * docs/android-plan.md). Чистая логика на org.json (входит в Android SDK — без
 * сторонних библиотек, docs/development.md), без android-зависимостей — покрыта
 * JVM unit-тестами. Сервер detail не трактует (Db.AndroidEvents): формат полей
 * знают только агент и веб-UI Этапа 4.
 *
 * Типы совпадают с Db.AndroidEventTypes на сервере.
 */
object Events {
    const val APP_FOREGROUND = "app_foreground"
    const val APP_BACKGROUND = "app_background"
    const val APP_INSTALLED = "app_installed"
    const val APP_REMOVED = "app_removed"
    const val DEVICE_STATE = "device_state"
    const val APP_TRAFFIC = "app_traffic"
    const val APP_INVENTORY = "app_inventory"
    // Этап 6 (владелец устройства): сетевой журнал и журнал безопасности.
    const val NET_DNS = "net_dns"
    const val NET_CONNECT = "net_connect"
    const val SECURITY = "security"
    // Этап 11: отчёт применения политики и отказы.
    const val POLICY = "policy"

    data class AppInfo(
        val packageName: String,
        val versionName: String?,
        val versionCode: Long,
        val installer: String?,
        val system: Boolean,
        // Человекочитаемое имя приложения (PackageManager.getApplicationLabel) —
        // чтобы в веб-UI показывать «TikTok», а не com.zhiliaoapp.musically.
        val label: String? = null,
    )

    /** Полный снимок делится на небольшие части; snapshotId связывает их вместе. */
    fun appInventory(apps: List<AppInfo>, tsUtc: String): List<PendingEvent> {
        val parts = apps.sortedBy { it.packageName }.chunked(10).ifEmpty { listOf(emptyList()) }
        val snapshotId = java.util.UUID.randomUUID().toString()
        return parts.mapIndexed { index, part ->
            val packages = JSONArray()
            for (app in part) {
                val item = JSONObject()
                    .put("package", app.packageName)
                    .put("versionCode", app.versionCode)
                    .put("system", app.system)
                if (!app.versionName.isNullOrEmpty()) item.put("versionName", app.versionName.take(256))
                if (!app.installer.isNullOrEmpty()) item.put("installer", app.installer.take(255))
                if (!app.label.isNullOrEmpty()) item.put("label", app.label.take(128))
                packages.put(item)
            }
            val detail = JSONObject()
                .put("snapshotId", snapshotId)
                .put("part", index)
                .put("parts", parts.size)
                .put("packages", packages)
            PendingEvent(tsUtc, APP_INVENTORY, null, detail.toString())
        }
    }

    /** Приложение вышло на передний план / ушло с него (UsageEvents). */
    fun appForeground(pkg: String, tsUtc: String) = PendingEvent(tsUtc, APP_FOREGROUND, pkg)

    fun appBackground(pkg: String, tsUtc: String) = PendingEvent(tsUtc, APP_BACKGROUND, pkg)

    /** Установка/удаление приложения; installer — источник установки, если известен. */
    fun appInstalled(pkg: String, tsUtc: String, installer: String?) =
        PendingEvent(tsUtc, APP_INSTALLED, pkg, installer?.let { JSONObject().put("installer", it).toString() })

    fun appRemoved(pkg: String, tsUtc: String) = PendingEvent(tsUtc, APP_REMOVED, pkg)

    /**
     * Снимок состояния устройства. Передаём только непустые поля — detail остаётся
     * компактным, а отсутствие Wi-Fi-SSID (нет разрешения на геолокацию) не
     * засоряет блоб. [ssid] не запрашиваем ценой геолокации (docs/android-plan.md):
     * если его нет, шлём только [networkType].
     */
    fun deviceState(
        tsUtc: String,
        osVersion: String?,
        securityPatch: String?,
        networkType: String?,
        ssid: String?,
        batteryPercent: Int?,
        charging: Boolean?,
        screenOn: Boolean?,
        usageAccess: Boolean? = null,
    ): PendingEvent {
        val d = JSONObject()
        if (!osVersion.isNullOrEmpty()) d.put("osVersion", osVersion)
        if (!securityPatch.isNullOrEmpty()) d.put("securityPatch", securityPatch)
        if (!networkType.isNullOrEmpty()) d.put("networkType", networkType)
        if (!ssid.isNullOrEmpty()) d.put("ssid", ssid)
        if (batteryPercent != null) d.put("batteryPercent", batteryPercent)
        if (charging != null) d.put("charging", charging)
        if (screenOn != null) d.put("screenOn", screenOn)
        // Этап 4: без доступа к статистике агент «слеп» — веб-UI и «Хосты» это показывают.
        if (usageAccess != null) d.put("usageAccess", usageAccess)
        return PendingEvent(tsUtc, DEVICE_STATE, null, d.toString())
    }

    /** Трафик приложения за интервал: rx/tx байт с прошлого замера. */
    fun appTraffic(
        pkg: String,
        tsUtc: String,
        rxBytes: Long,
        txBytes: Long,
        intervalMs: Long,
        networkType: String? = null,
        uid: Int? = null,
        packages: List<String> = emptyList(),
    ): PendingEvent {
        val d = JSONObject()
            .put("rxBytes", rxBytes)
            .put("txBytes", txBytes)
            .put("intervalMs", intervalMs)
        if (networkType != null) d.put("networkType", networkType)
        if (uid != null) d.put("uid", uid)
        if (packages.size > 1) d.put("packages", JSONArray(packages))
        return PendingEvent(tsUtc, APP_TRAFFIC, pkg, d.toString())
    }
}
