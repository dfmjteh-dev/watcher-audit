package com.watcher.agent

import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.Intent
import android.util.Log
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit

/**
 * Реактивный гейт записи по приложению (Этап 12+ PR2 Part B, режим perApp).
 * Решение пользователя: suspend-гейт с нашим согласием по тапу device-owner-
 * приложению недоступен (кастомный SuspendDialogInfo требует системного
 * SUSPEND_APPS), поэтому ловим момент, когда целевое приложение выходит на
 * передний план (UsageEvents, которые агент и так читает), и показываем согласие.
 *
 * Целевое приложение на переднем плане и записи ещё нет → [ScreenConsentActivity]
 * с пакетом: «да» → захват, пока им пользуются; «нет» → уходим домой. Ушли со всех
 * целевых → останавливаем захват (запись только во время целевого приложения).
 *
 * Своя лёгкая опрос-петля (~[POLL_MS]); heartbeat-тик (60с) слишком редкий. Петля
 * живёт только пока режим perApp (включает/выключает [ScreenRecordController]).
 */
object ForegroundWatcher {
    private const val POLL_MS = 1500L
    private const val PROMPT_COOLDOWN_MS = 6000L   // не переспрашивать согласие чаще
    private const val CAPTURE_GRACE_MS = 4000L     // после старта захвата не глушим сразу

    @Volatile private var exec: ScheduledExecutorService? = null
    @Volatile private var lastPromptAt = 0L
    @Volatile private var captureSince = 0L
    // Текущее приложение на переднем плане и курсор по событиям. Держим между
    // опросами: стабильно открытое приложение не генерит новых событий, и окно
    // «за последние N секунд» его теряло — отсюда обрыв записи через ~12с.
    @Volatile private var lastFg: String? = null
    @Volatile private var cursor = 0L

    @Synchronized
    fun start(context: Context) {
        if (exec != null) return
        lastFg = null; cursor = 0L; lastPromptAt = 0L; captureSince = 0L
        val app = context.applicationContext
        val e = Executors.newSingleThreadScheduledExecutor()
        exec = e
        e.scheduleWithFixedDelay({
            runCatching { poll(app) }.onFailure { Log.w(AgentService.TAG, "fg watcher: ${it.javaClass.simpleName}") }
        }, 0, POLL_MS, TimeUnit.MILLISECONDS)
        Log.i(AgentService.TAG, "perApp: наблюдатель переднего плана запущен")
    }

    @Synchronized
    fun stop() {
        exec?.shutdownNow(); exec = null
    }

    private fun poll(context: Context) {
        if (ScreenRecordState.mode != "perApp") return
        val targets = ScreenRecordState.apps
        if (targets.isEmpty()) return
        val fg = currentForeground(context)
        // Наш собственный экран согласия/оверлей на переднем плане — это переходное
        // состояние самого диалога; не считаем «ушли с приложения», чтобы захват не
        // глушился и не переспрашивал согласие (иначе в приложении мелькали повторы).
        if (fg == context.packageName) return
        val onTarget = fg != null && fg in targets
        val now = System.currentTimeMillis()
        if (onTarget) {
            if (ScreenRecordState.capturing) {
                captureSince = now  // держим запись, пока целевое на экране
            } else if (now - lastPromptAt > PROMPT_COOLDOWN_MS) {
                lastPromptAt = now
                context.startActivity(Intent(context, ScreenConsentActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    .putExtra(ScreenConsentActivity.EXTRA_PACKAGE, fg))
            }
        } else {
            // Не на целевом приложении. Игнорируем переходное состояние сразу после
            // старта захвата (системный диалог согласия / наш оверлей ещё на экране).
            if (ScreenRecordState.capturing && now - captureSince > CAPTURE_GRACE_MS) {
                ScreenRecordController.stop(context)
            }
        }
    }

    /** Текущее приложение на переднем плане. Обновляем [lastFg] только при реальном
     * переходе (новое событие FOREGROUND); если новых событий нет — приложение всё ещё
     * открыто, возвращаем прежнее (а не null — иначе запись рвётся через ~12с). */
    private fun currentForeground(context: Context): String? {
        if (!UsageAccess.granted(context)) return lastFg
        val usm = context.getSystemService(Context.USAGE_STATS_SERVICE) as? UsageStatsManager ?: return lastFg
        val now = System.currentTimeMillis()
        val start = if (cursor == 0L) now - 10_000L else cursor
        val events = usm.queryEvents(start, now) ?: return lastFg
        val ev = UsageEvents.Event()
        while (events.hasNextEvent()) {
            events.getNextEvent(ev)
            if (ev.eventType == UsageEventsLogic.FOREGROUND) lastFg = ev.packageName
        }
        cursor = now
        return lastFg
    }
}
