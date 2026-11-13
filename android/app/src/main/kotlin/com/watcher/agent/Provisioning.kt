package com.watcher.agent

/**
 * Онбординг устройства (Этап 7, docs/android-plan.md). Скрипт `tools/provision.sh`
 * после назначения роли владельца запускает [MainActivity] с extras
 * serverUrl/token/host — агент авто-регистрируется без ручного ввода. Здесь —
 * чистая проверка этих параметров (покрыта JVM unit-тестами); сам запуск и
 * регистрация — в MainActivity через ServerClient.
 *
 * Токен регистрации (wenr_…) сам по себе — право на подключение, поэтому extras
 * не дают больше, чем ручной ввод той же тройки в форме. Авто-регистрация идёт
 * только если агент ещё не подключён (иначе extras игнорируются).
 */
object Provisioning {
    data class Params(val serverUrl: String, val token: String, val host: String)

    /** Проверить тройку из extras. host пустой/битый → имя по умолчанию. null — нет адреса/токена. */
    fun parse(url: String?, token: String?, host: String?, defaultHost: String): Params? {
        val u = url?.trim().orEmpty()
        val t = token?.trim().orEmpty()
        if (!isHttpUrl(u) || t.isEmpty()) return null
        val h = DeviceName.sanitize(host) ?: DeviceName.sanitize(defaultHost) ?: DeviceName.FALLBACK
        return Params(u, t, h)
    }

    /** http/https с непустым хостом (host:port и внутренние адреса допустимы). */
    fun isHttpUrl(url: String): Boolean =
        try {
            val parsed = java.net.URL(url)
            parsed.protocol in setOf("http", "https") && !parsed.host.isNullOrEmpty()
        } catch (e: java.net.MalformedURLException) {
            false
        }
}
