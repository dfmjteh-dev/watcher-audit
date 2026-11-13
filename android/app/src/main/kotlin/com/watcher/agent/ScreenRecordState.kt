package com.watcher.agent

/**
 * Состояние записи экрана по согласию (Этап 12+, docs/android-plan.md). Связь
 * службы (решает по политике), [ScreenConsentActivity] (системное согласие
 * MediaProjection) и [ScreenCaptureService] (держит захват). Поля volatile —
 * читаются из разных потоков.
 */
object ScreenRecordState {
    /** Желаемый режим из политики: off | device | perApp. */
    @Volatile var mode: String = "off"

    /** Приложения для режима perApp (PR2). */
    @Volatile var apps: Set<String> = emptySet()

    /** Идёт захват (сервис активен). */
    @Volatile var capturing = false

    /** Имя сегмента, который пишется сейчас (его досыл не трогает). */
    @Volatile var currentFile: String? = null
}
