package com.watcher.agent

/**
 * Diff перечня установленных приложений (Этап 3, docs/android-plan.md): сравнение
 * прошлого снимка с текущим даёт установленные и удалённые пакеты. Чистая логика
 * без android-зависимостей — покрыта JVM unit-тестами; сбор самого перечня (через
 * PackageManager) и источник установки (getInstallSourceInfo / устар.
 * getInstallerPackageName) — в [AppInventoryCollector], изолированно от этого.
 *
 * Diff — подстраховка к receiver'у ACTION_PACKAGE_ADDED/REMOVED: ловит
 * установки/удаления, пропущенные пока агент был выключен (не получал broadcast).
 */
object AppInventory {

    /** Что изменилось между снимками: появившиеся и исчезнувшие пакеты. */
    data class Diff(val installed: List<String>, val removed: List<String>)

    /**
     * previous — прошлый известный набор пакетов, current — текущий.
     * Первый запуск (previous == null) — это НЕ «всё только что установили»:
     * возвращаем пустой diff, снимок просто принимается за базовый (иначе при
     * подключении агента на сервер улетели бы «установки» всех системных
     * приложений). Отличать первый запуск от «снимок пуст» должен вызывающий,
     * поэтому previous именно nullable.
     */
    fun diff(previous: Set<String>?, current: Set<String>): Diff {
        if (previous == null) return Diff(emptyList(), emptyList())
        val installed = current.filter { it !in previous }.sorted()
        val removed = previous.filter { it !in current }.sorted()
        return Diff(installed, removed)
    }
}
