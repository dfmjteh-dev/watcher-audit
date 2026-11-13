package com.watcher.agent

import android.content.Context
import android.util.Log

/**
 * Настройки и состояние агента в SharedPreferences (Этап 2, docs/android-plan.md):
 * адрес сервера, имя хоста, секрет агента (шифрованный — см. [SecretStore]) и
 * кэш действующего конфига (configHash/configVersion + сам блоб).
 *
 * Секрет наружу отдаётся только через [secret]; в лог не пишется
 * (docs/development.md, «Секреты»). Использование самого конфига (чёрные списки
 * приложений/доменов) — поздние этапы; Этап 2 только доказывает, что конфиг с
 * сервера доходит до устройства и кэшируется.
 */
class AgentConfig(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    val serverUrl: String? get() = prefs.getString(KEY_SERVER_URL, null)
    val host: String? get() = prefs.getString(KEY_HOST, null)

    val isEnrolled: Boolean
        get() = !serverUrl.isNullOrEmpty() && !host.isNullOrEmpty() && prefs.contains(KEY_SECRET)

    /** Расшифрованный секрет агента или null (не зарегистрирован / ошибка ключа). */
    fun secret(): String? {
        val enc = prefs.getString(KEY_SECRET, null) ?: return null
        return try {
            SecretStore.decrypt(enc)
        } catch (e: Exception) {
            // Секрет в лог не пишем — только класс ошибки.
            Log.w(AgentService.TAG, "секрет не расшифровался: ${e.javaClass.simpleName}")
            null
        }
    }

    /**
     * Сохранить результат регистрации: адрес, имя хоста и шифрованный секрет.
     * Синхронно (commit, не apply): учётка на сервере уже израсходована, и если
     * процесс убьют до фоновой записи, секрет потеряется — подключать заново.
     */
    fun saveEnrollment(serverUrl: String, host: String, secret: String, updatePublicKey: String = ""): Boolean =
        prefs.edit()
            .putString(KEY_SERVER_URL, serverUrl)
            .putString(KEY_HOST, host)
            .putString(KEY_SECRET, SecretStore.encrypt(secret))
            .putString(KEY_UPDATE_PUBKEY, updatePublicKey)
            .commit()

    /** CA сервера (base64 DER) — [ServerTrust]; задаётся при подключении, null — только системные CA. */
    var serverCa: String?
        get() = prefs.getString(KEY_SERVER_CA, null)
        set(v) { prefs.edit().apply { if (v == null) remove(KEY_SERVER_CA) else putString(KEY_SERVER_CA, v) }.commit() }

    /** Открытый ключ обновлений, запинённый при регистрации (Этап 8). Пусто — автообновление выключено. */
    val updatePublicKey: String? get() = prefs.getString(KEY_UPDATE_PUBKEY, null)

    /**
     * Этап 8: запинить ключ обновлений из heartbeat, НО только если он ещё не
     * задан (fill-once) — чтобы агент, зарегистрированный до настройки ключа,
     * подхватил его, но уже запинённый ключ сервер не мог молча подменить.
     * Возвращает true, если ключ только что запинён.
     */
    fun fillUpdatePublicKeyIfEmpty(key: String?): Boolean {
        if (key.isNullOrEmpty()) return false
        if (!updatePublicKey.isNullOrEmpty()) return false
        prefs.edit().putString(KEY_UPDATE_PUBKEY, key).commit()
        return true
    }

    /** Итог последней попытки обновления для heartbeat ("ok <ver>" / "failed <ver>: …"). */
    var updateStatus: String?
        get() = prefs.getString(KEY_UPDATE_STATUS, null)
        set(v) { prefs.edit().apply { if (v == null) remove(KEY_UPDATE_STATUS) else putString(KEY_UPDATE_STATUS, v) }.apply() }

    /** Версия, установка которой провалилась, и когда (чтобы не долбить битый пакет каждую минуту). */
    var lastFailedUpdate: String?
        get() = prefs.getString(KEY_UPDATE_FAILED_VER, null)
        set(v) { prefs.edit().apply { if (v == null) remove(KEY_UPDATE_FAILED_VER) else putString(KEY_UPDATE_FAILED_VER, v) }.apply() }
    var lastFailedUpdateAt: Long
        get() = prefs.getLong(KEY_UPDATE_FAILED_AT, 0L)
        set(v) { prefs.edit().putLong(KEY_UPDATE_FAILED_AT, v).apply() }

    /** Версия, установку которой отправили в PackageInstaller, и когда — чтобы не
     * пересоздавать сессию каждый heartbeat, пока установка не завершилась/не упала. */
    val installingVersion: String? get() = prefs.getString(KEY_UPDATE_INSTALLING_VER, null)
    val installingAt: Long get() = prefs.getLong(KEY_UPDATE_INSTALLING_AT, 0L)
    fun markInstalling(version: String) {
        prefs.edit().putString(KEY_UPDATE_INSTALLING_VER, version).putLong(KEY_UPDATE_INSTALLING_AT, System.currentTimeMillis()).apply()
    }

    /** Этап 11: последняя применённая политика управления (JSON androidPolicy) —
     * чтобы вычислять дельту и откатывать снятые пакеты/запреты. */
    var lastAppliedPolicyJson: String?
        get() = prefs.getString(KEY_POLICY_APPLIED, null)
        set(v) { prefs.edit().apply { if (v == null) remove(KEY_POLICY_APPLIED) else putString(KEY_POLICY_APPLIED, v) }.apply() }

    /** Этап 12+: логины/хэши админов для офлайн-разблокировки (шифровано). */
    fun adminCreds(): List<AdminAuth.Cred> {
        val enc = prefs.getString(KEY_ADMIN_CREDS, null) ?: return emptyList()
        return try { AdminAuth.parse(SecretStore.decrypt(enc)) } catch (e: Exception) { emptyList() }
    }
    /** Обновить креды из heartbeat (шлются каждый раз; меняются при смене пароля админа). */
    fun setAdminCreds(json: String?) {
        prefs.edit().apply {
            if (json.isNullOrEmpty() || json == "[]") remove(KEY_ADMIN_CREDS)
            else putString(KEY_ADMIN_CREDS, SecretStore.encrypt(json))
        }.apply()
    }

    /** Этап 12+: офлайн-админ снял блокировку/киоск локально. Держится до смены
     * политики/команды с сервера (сбрасывается при смене конфига). */
    var adminOverride: Boolean
        get() = prefs.getBoolean(KEY_ADMIN_OVERRIDE, false)
        set(v) { prefs.edit().putBoolean(KEY_ADMIN_OVERRIDE, v).apply() }

    /** Этап 12: последняя команда блокировки с сервера — чтобы держать блокировку
     * после ребута без сети (раньше жила только в памяти процесса). */
    var lockDesired: Boolean
        get() = prefs.getBoolean(KEY_LOCK_DESIRED, false)
        set(v) { if (v != lockDesired) prefs.edit().putBoolean(KEY_LOCK_DESIRED, v).apply() }

    /** Этап 12+: когда последний раз показывали согласие на запись экрана (мс эпохи).
     * Каденс режима «устройство» — не чаще раза в сутки. */
    var lastScreenConsentAt: Long
        get() = prefs.getLong(KEY_SCREEN_CONSENT_AT, 0L)
        set(v) { prefs.edit().putLong(KEY_SCREEN_CONSENT_AT, v).apply() }


    val configVersion: Int get() = prefs.getInt(KEY_CONFIG_VERSION, 0)
    val configHash: String? get() = prefs.getString(KEY_CONFIG_HASH, null)
    val configJson: String? get() = prefs.getString(KEY_CONFIG_JSON, null)

    /**
     * Применить конфиг из ответа heartbeat (Этап 2 — только кэш). Возвращает
     * true, если конфиг изменился (по configHash), — это момент, когда поздние
     * этапы перечитают чёрные списки.
     */
    fun applyConfig(r: Protocol.HeartbeatResult): Boolean {
        if (r.configHash == configHash) return false
        prefs.edit()
            .putString(KEY_CONFIG_HASH, r.configHash)
            .putInt(KEY_CONFIG_VERSION, r.configVersion ?: 0)
            .putString(KEY_CONFIG_JSON, r.configJson)
            .apply()
        return true
    }

    /** Интервал heartbeat; настраивается (ключ в prefs), по умолчанию 60 с. */
    val heartbeatIntervalMs: Long get() = prefs.getLong(KEY_INTERVAL_MS, DEFAULT_INTERVAL_MS).coerceIn(10_000L, 3_600_000L)

    // --- Сбор событий (Этап 3, docs/android-plan.md). Ключи локальные (в prefs),
    // как heartbeat_interval_ms: серверная схема конфига (AgentConfigValidation)
    // знает только поля станций и чужие ключи отвергает, так что android-настройки
    // держим на устройстве. ---

    /**
     * Как часто снимать «тяжёлые» срезы (состояние устройства, трафик). Запуск/
     * закрытие приложений и установки собираются каждый тик heartbeat независимо
     * от этого (чтобы попадали на сервер ≤ 1 интервала). По умолчанию 5 минут.
     */
    val eventCollectionIntervalMs: Long get() = prefs.getLong(KEY_EVENT_INTERVAL_MS, DEFAULT_EVENT_INTERVAL_MS).coerceIn(60_000L, 86_400_000L)

    /** Предел строк в локальной очереди событий; сверх него выбрасываются старейшие. */
    val eventQueueMaxRows: Int get() = prefs.getInt(KEY_EVENT_QUEUE_MAX, EventQueue.DEFAULT_MAX_ROWS).coerceIn(100, EventQueue.MAX_ROWS)

    /** «Только Wi-Fi»: не отправлять очередь по мобильной сети (по умолчанию выкл.). */
    var wifiOnly: Boolean
        get() = prefs.getBoolean(KEY_WIFI_ONLY, false)
        set(value) { prefs.edit().putBoolean(KEY_WIFI_ONLY, value).apply() }

    /** Курсор UsageEvents — время последнего обработанного события (мс), чтобы не слать повторно. */
    var usageCursor: Long
        get() = prefs.getLong(KEY_USAGE_CURSOR, 0L)
        set(v) { prefs.edit().putLong(KEY_USAGE_CURSOR, v).apply() }

    /** Прошлый снимок установленных пакетов (для diff установок/удалений). null — ещё не снимали. */
    var appSnapshot: Set<String>?
        get() = prefs.getStringSet(KEY_APP_SNAPSHOT, null)
        set(v) {
            prefs.edit().apply { if (v == null) remove(KEY_APP_SNAPSHOT) else putStringSet(KEY_APP_SNAPSHOT, v) }.apply()
        }

    /** Время последнего замера трафика по каждому типу сети. */
    fun trafficSampleAt(networkType: String): Long = prefs.getLong("${KEY_TRAFFIC_SAMPLE_AT}_$networkType", 0L)
    fun setTrafficSampleAt(networkType: String, value: Long) {
        prefs.edit().putLong("${KEY_TRAFFIC_SAMPLE_AT}_$networkType", value).apply()
    }

    /** Время последнего полного снимка приложений (app_inventory); 0 — снять при ближайшем сборе. */
    var lastInventoryAt: Long
        get() = prefs.getLong(KEY_LAST_INVENTORY_AT, 0L)
        set(value) { prefs.edit().putLong(KEY_LAST_INVENTORY_AT, value).apply() }

    /** Время последнего «тяжёлого» среза (device_state/traffic). */
    var lastStateSampleAt: Long
        get() = prefs.getLong(KEY_LAST_STATE_AT, 0L)
        set(v) { prefs.edit().putLong(KEY_LAST_STATE_AT, v).apply() }

    companion object {
        private const val PREFS = "watcher_agent"
        private const val KEY_SERVER_URL = "server_url"
        private const val KEY_HOST = "host"
        private const val KEY_SERVER_CA = "server_ca"
        private const val KEY_SECRET = "secret_enc"
        private const val KEY_CONFIG_VERSION = "config_version"
        private const val KEY_CONFIG_HASH = "config_hash"
        private const val KEY_CONFIG_JSON = "config_json"
        private const val KEY_INTERVAL_MS = "heartbeat_interval_ms"
        const val DEFAULT_INTERVAL_MS = 60_000L

        private const val KEY_EVENT_INTERVAL_MS = "event_collection_interval_ms"
        private const val KEY_EVENT_QUEUE_MAX = "event_queue_max_rows"
        private const val KEY_WIFI_ONLY = "wifi_only"
        private const val KEY_USAGE_CURSOR = "event_usage_cursor"
        private const val KEY_APP_SNAPSHOT = "event_app_snapshot"
        private const val KEY_TRAFFIC_SAMPLE_AT = "event_traffic_sample_at"
        private const val KEY_LAST_INVENTORY_AT = "event_last_inventory_at"
        private const val KEY_LAST_STATE_AT = "event_last_state_at"
        const val DEFAULT_EVENT_INTERVAL_MS = 300_000L

        private const val KEY_UPDATE_PUBKEY = "update_public_key"
        private const val KEY_UPDATE_STATUS = "update_status"
        private const val KEY_UPDATE_FAILED_VER = "update_failed_version"
        private const val KEY_UPDATE_FAILED_AT = "update_failed_at"
        private const val KEY_UPDATE_INSTALLING_VER = "update_installing_version"
        private const val KEY_UPDATE_INSTALLING_AT = "update_installing_at"
        private const val KEY_POLICY_APPLIED = "policy_applied"
        private const val KEY_ADMIN_CREDS = "admin_creds_enc"
        private const val KEY_ADMIN_OVERRIDE = "admin_override"
        private const val KEY_SCREEN_CONSENT_AT = "screen_consent_at"
        private const val KEY_LOCK_DESIRED = "lock_desired"
    }
}
