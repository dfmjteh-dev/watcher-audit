// Версии плагинов — точные, не диапазоны (docs/development.md, «Политика
// зависимостей»): gradle/libs.versions.toml.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.android) apply false
}
