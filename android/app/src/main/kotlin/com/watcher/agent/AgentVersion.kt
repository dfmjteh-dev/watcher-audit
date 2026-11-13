package com.watcher.agent

/**
 * Вычисление versionCode из versionName (X.Y.Z).
 *
 * ВАЖНО: формула продублирована в [app/build.gradle.kts] (agentVersionCode).
 * Gradle считает versionCode на этапе конфигурации и не может использовать
 * классы приложения, поэтому формула живёт в двух местах — держать их
 * синхронно. Формат и правило версионирования — docs/development.md,
 * «Версионирование и артефакты сборки».
 */
object AgentVersion {

    /** MAJOR*1_000_000 + MINOR*1_000 + PATCH (напр. 0.14.4 → 14004). */
    fun code(name: String): Int {
        val parts = name.split('.')
        require(parts.size == 3) { "версия должна быть X.Y.Z, получено: $name" }
        val (major, minor, patch) = parts.map { part ->
            part.toIntOrNull() ?: error("версия должна быть X.Y.Z, получено: $name")
        }
        return major * 1_000_000 + minor * 1_000 + patch
    }
}
