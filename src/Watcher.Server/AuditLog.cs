using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Watcher.Server;

// Этап C3 (roadmap-next.md): единая точка записи в audit_log — эндпоинты не
// собирают username/ip сами. Запись синхронная и без try/catch: если журнал не
// пишется, запрос падает 500, а не отдаёт запись без следа — для журнала
// доступа к записям экранов это правильная сторона ошибки (fail-closed).
public sealed class AuditLog
{
    // Дедуп просмотра видео: <video> с enableRangeProcessing делает десятки
    // Range-запросов на один просмотр (старт, перемотка, буферизация) — без
    // дедупа одно открытие сегмента давало бы десятки строк. "Сессия плеера" =
    // тот же пользователь + тот же сегмент в пределах окна.
    internal static readonly TimeSpan VideoDedupWindow = TimeSpan.FromMinutes(30);
    private const int MaxFieldLength = 500;
    // Username для записей от CLI-команд (--create-user и т.п.) — веб-сессии нет.
    public const string CliUser = "cli";

    // Кириллица (имена групп, поиск по консоли) — как есть, не \uXXXX: журнал
    // читают и напрямую через sqlite3, и будущий SIEM-экспорт (H4). HTML-символы
    // энкодер по-прежнему экранирует.
    private static readonly JsonSerializerOptions DetailsJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private readonly Db _db;
    private readonly ConcurrentDictionary<(string User, long SegmentId), DateTime> _videoSeen = new();

    public AuditLog(Db db) => _db = db;

    // Возвращает id записи журнала (H7: номер выгрузки = id её записи).
    public long Write(HttpContext ctx, string action, string? target = null, object? details = null, string? username = null) =>
        Write(username ?? ctx.User.Identity?.Name, ctx.Connection.RemoteIpAddress?.ToString(), action, target, details);

    public long Write(string? username, string? ip, string action, string? target = null, object? details = null)
    {
        string? detailsJson = details is null ? null : JsonSerializer.Serialize(details, DetailsJson);
        return _db.InsertAudit(new AuditEntry(0, DateTime.UtcNow, Clip(username), ip, action, Clip(target), detailsJson));
    }

    // true — запись сделана, false — это продолжение уже записанного просмотра.
    public bool WriteVideoView(HttpContext ctx, long segmentId, object? details)
    {
        string user = ctx.User.Identity?.Name ?? "";
        DateTime now = DateTime.UtcNow;
        bool fresh = true;
        _videoSeen.AddOrUpdate((user, segmentId), now, (_, last) =>
        {
            if (now - last < VideoDedupWindow) { fresh = false; return last; }
            return now;
        });
        if (!fresh) return false;

        // Чистка просроченных ключей — словарь не растёт бесконечно на долгоживущем
        // сервере. Дёшево: это человеческий трафик, а не агентский.
        if (_videoSeen.Count > 1000)
            foreach (var kv in _videoSeen)
                if (now - kv.Value >= VideoDedupWindow) _videoSeen.TryRemove(kv.Key, out _);

        Write(ctx, "segment.video", $"segment:{segmentId}", details);
        return true;
    }

    // Username неудачного логина и строка поиска — от пользователя, длина не
    // ограничена ничем выше; журнал не должен раздуваться от одного запроса.
    internal static string? Clip(string? s) =>
        s is null || s.Length <= MaxFieldLength ? s : s[..MaxFieldLength];
}
