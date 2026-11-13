package com.watcher.agent

/**
 * Состояние удалённой блокировки устройства (Этап 12, docs/android-plan.md).
 * Связь между службой (решает по команде сервера) и [LockActivity] (держит
 * экран-заглушку в режиме lock-task). Поля volatile — читаются из разных потоков.
 */
object LockState {
    /** Желаемое состояние (из команды сервера): держать ли устройство заблокированным. */
    @Volatile var shouldLock = false

    /** Фактическое: экран-заглушка на переднем плане в lock-task (сообщается серверу). */
    @Volatile var active = false
}
