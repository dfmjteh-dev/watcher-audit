package com.watcher.agent

import org.json.JSONObject

/**
 * Чистая логика политик управления (Этап 11, docs/android-plan.md) — разбор
 * androidPolicy из конфига, защитный фильтр и вычисление дельты относительно
 * ранее применённой политики. Без android-вызовов, покрыта JVM unit-тестами;
 * сами вызовы DevicePolicyManager — в [AndroidPolicyManager].
 *
 * Имена запретов в конфиге намеренно совпадают со строковыми значениями
 * констант UserManager.DISALLOW_* — агент передаёт их в addUserRestriction как
 * есть. Защитный список и набор запретов продублированы с сервера
 * (AndroidPolicy.cs); держать синхронно.
 */
object AndroidPolicyLogic {
    val RESTRICTIONS = setOf(
        "no_install_unknown_sources", "no_debugging_features", "no_factory_reset",
        "no_config_vpn", "no_config_private_dns", "no_usb_file_transfer",
    )

    // Статический защитный список (как на сервере). Агент добавляет к нему
    // домашний экран/звонилку/SMS, определённые в рантайме ([AndroidPolicyManager]).
    val PROTECTED = setOf(
        "com.watcher.agent", "android", "com.android.settings", "com.android.systemui",
        "com.android.phone", "com.android.dialer", "com.google.android.dialer",
        "com.android.mms", "com.google.android.apps.messaging", "com.android.vending",
        "com.google.android.gms", "com.google.android.packageinstaller", "com.android.packageinstaller",
    )

    // Этап 12+: режимы записи экрана по согласию (как на сервере, AndroidPolicy.cs).
    val SCREEN_RECORD_MODES = setOf("off", "device", "perApp")

    data class Policy(
        val suspend: Set<String> = emptySet(),
        val hide: Set<String> = emptySet(),
        val restrictions: Set<String> = emptySet(),
        val metered: Set<String> = emptySet(),
        // Этап 13: разрешённые приложения в режиме киоска (пусто — киоск выключен).
        val kiosk: Set<String> = emptySet(),
        // Этап 12+: запись экрана по согласию. off — выкл; device — всё устройство
        // (согласие при разблокировке); perApp — при запуске приложений из screenRecordApps.
        val screenRecordMode: String = "off",
        val screenRecordApps: Set<String> = emptySet(),
    )

    val EMPTY = Policy()

    /** Политика из androidPolicy-объекта (строка JSON). Пусто/битьё → EMPTY. */
    fun parse(policyObjectJson: String?): Policy {
        if (policyObjectJson.isNullOrBlank()) return EMPTY
        return try { fromObject(JSONObject(policyObjectJson)) } catch (e: Exception) { EMPTY }
    }

    /** Политика из полного конфиг-блоба (берём ключ androidPolicy). */
    fun fromConfig(configJson: String?): Policy {
        if (configJson.isNullOrBlank()) return EMPTY
        return try {
            val o = JSONObject(configJson)
            if (!o.has("androidPolicy") || o.isNull("androidPolicy")) EMPTY
            else fromObject(o.getJSONObject("androidPolicy"))
        } catch (e: Exception) { EMPTY }
    }

    private fun fromObject(o: JSONObject): Policy {
        val sr = o.optJSONObject("screenRecord")
        return Policy(
            suspend = strings(o, "suspendedPackages"),
            hide = strings(o, "hiddenPackages"),
            restrictions = strings(o, "restrictions").filter { it in RESTRICTIONS }.toSet(),
            metered = strings(o, "meteredDataDisabledPackages"),
            kiosk = strings(o, "kioskPackages"),
            screenRecordMode = (sr?.optString("mode", "off") ?: "off").takeIf { it in SCREEN_RECORD_MODES } ?: "off",
            screenRecordApps = sr?.let { strings(it, "apps") } ?: emptySet(),
        )
    }

    private fun strings(o: JSONObject, key: String): Set<String> {
        val arr = o.optJSONArray(key) ?: return emptySet()
        val out = LinkedHashSet<String>()
        for (i in 0 until arr.length()) arr.optString(i).takeIf { it.isNotEmpty() }?.let { out.add(it) }
        return out
    }

    /** Убрать защищённые пакеты из блокировки/скрытия. Возвращает (политика, отклонённые). */
    fun protect(policy: Policy, extraProtected: Set<String>): Pair<Policy, Set<String>> {
        val prot = PROTECTED + extraProtected
        val refused = (policy.suspend + policy.hide).filter { it in prot }.toSet()
        if (refused.isEmpty()) return policy to emptySet()
        return policy.copy(suspend = policy.suspend - prot, hide = policy.hide - prot) to refused
    }

    data class Diff(
        val suspendOn: Set<String>, val suspendOff: Set<String>,
        val hideOn: Set<String>, val hideOff: Set<String>,
        val restrictAdd: Set<String>, val restrictRemove: Set<String>,
        val metered: Set<String>, val meteredChanged: Boolean,
    ) {
        val isEmpty get() = suspendOn.isEmpty() && suspendOff.isEmpty() && hideOn.isEmpty() &&
            hideOff.isEmpty() && restrictAdd.isEmpty() && restrictRemove.isEmpty() && !meteredChanged
    }

    /** Что доприменить/откатить относительно прежней политики. */
    fun diff(prev: Policy, next: Policy): Diff = Diff(
        suspendOn = next.suspend - prev.suspend,
        suspendOff = prev.suspend - next.suspend,
        hideOn = next.hide - prev.hide,
        hideOff = prev.hide - next.hide,
        restrictAdd = next.restrictions - prev.restrictions,
        restrictRemove = prev.restrictions - next.restrictions,
        metered = next.metered,
        meteredChanged = next.metered != prev.metered,
    )

    /** Сериализовать применённую политику для хранения (lastAppliedPolicy). */
    fun toJson(p: Policy): String = JSONObject()
        .put("suspendedPackages", org.json.JSONArray(p.suspend))
        .put("hiddenPackages", org.json.JSONArray(p.hide))
        .put("restrictions", org.json.JSONArray(p.restrictions))
        .put("meteredDataDisabledPackages", org.json.JSONArray(p.metered))
        .put("kioskPackages", org.json.JSONArray(p.kiosk))
        .toString()
}
