package com.watcher.agent

/**
 * Имя хоста для регистрации (Этап 2, docs/android-plan.md). По умолчанию — из
 * модели устройства; пользователь подтверждает/меняет его при подключении.
 *
 * Санитайз повторяет серверную проверку (AgentAuth.IsValidHostName +
 * UploadValidation.IsSafeMetaField): ≤128 символов, без управляющих символов и
 * без < > " ' ` — чтобы сервер принял имя. Чистая логика (строки на входе) —
 * покрыта JVM unit-тестами, android.os.Build сюда не тянется.
 */
object DeviceName {
    const val MAX_LENGTH = 128
    const val FALLBACK = "android-device"
    private val FORBIDDEN = charArrayOf('<', '>', '"', '\'', '`')

    /** Приводит произвольную строку к допустимому имени хоста (или null). */
    fun sanitize(raw: String?): String? {
        if (raw == null) return null
        val cleaned = buildString {
            for (ch in raw.trim()) {
                when {
                    ch.code < 0x20 -> append(' ')
                    ch in FORBIDDEN -> append('-')
                    else -> append(ch)
                }
            }
        }.trim().take(MAX_LENGTH).trim()
        return cleaned.ifEmpty { null }
    }

    /**
     * Имя по умолчанию из производителя и модели (Build.MANUFACTURER / MODEL):
     * «Google» + «Pixel 7» → «Google Pixel 7»; модель уже содержит производителя
     * → только модель. Пусто → [FALLBACK].
     */
    fun default(manufacturer: String?, model: String?): String {
        val mf = sanitize(manufacturer) ?: ""
        val md = sanitize(model) ?: ""
        val name = when {
            md.isEmpty() -> mf
            mf.isEmpty() || md.contains(mf, ignoreCase = true) -> md
            else -> "$mf $md"
        }
        return sanitize(name) ?: FALLBACK
    }
}
