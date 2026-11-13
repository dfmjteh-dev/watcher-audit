using System.Collections.Concurrent;

namespace Watcher.Server;

// 0.15.0: очередь раскатки обновлений агентов. Пакет — ~100 МБ (MSI/zip/deb),
// и раньше предложение уходило каждому устаревшему агенту в первом же
// heartbeat: назначили версию нескольким группам — сотни агентов разом
// качают десятки гигабайт, сеть и диск сервера заняты, веб «тупит».
//
// Теперь одновременно обновляются не больше MaxConcurrent хостов (слот), остальные
// получают предложение, когда слот освободится. Слот освобождается, когда хост
// пришёл с целевой версией или с ошибкой установки этой версии, либо по
// истечении SlotTimeout (агент пропал посреди обновления). Состояние — в памяти:
// после перезапуска сервера очередь просто набирается заново.
public sealed class UpdateRollout
{
    private readonly ConcurrentDictionary<string, (string Version, DateTime At, string? Operation)> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;

    public UpdateRollout(int maxConcurrent, TimeSpan? slotTimeout = null, Func<DateTime>? clock = null)
    {
        MaxConcurrent = maxConcurrent;
        SlotTimeout = slotTimeout ?? TimeSpan.FromMinutes(30);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    // 0 и меньше — без ограничения (как до 0.15.0).
    public int MaxConcurrent { get; }
    public TimeSpan SlotTimeout { get; }

    public int Active
    {
        get { Expire(); return _slots.Count; }
    }

    // Можно ли сейчас предложить хосту пакет version. Хост, уже держащий слот
    // той же версии, получает предложение повторно, нового слота не занимая.
    public bool TryAcquire(string host, string version)
    {
        if (MaxConcurrent <= 0) return true;
        lock (_gate)
        {
            Expire();
            if (_slots.TryGetValue(host, out var s) && s.Version == version) return true;
            if (_slots.Count >= MaxConcurrent && !_slots.ContainsKey(host)) return false;
            _slots[host] = (version, _clock(), null);
            return true;
        }
    }

    // Выдача разрешения и резервирование слота — одна операция. Не занимать
    // новый слот при отказе БД и не освобождать слот уже идущего обновления.
    public bool TryDispatch(string host, string version, Func<bool> dispatch, string? operation = null)
    {
        lock (_gate)
        {
            Expire();
            if (MaxConcurrent > 0 && _slots.Count >= MaxConcurrent && !_slots.ContainsKey(host)) return false;
            if (!dispatch()) return false;
            if (MaxConcurrent > 0) _slots[host] = (version, _clock(), operation);
            return true;
        }
    }

    public void RestoreProtected(string host, string version, string operation, DateTime issuedAt)
    {
        if (MaxConcurrent <= 0) return;
        lock (_gate) _slots[host] = (version, issuedAt, operation);
    }

    public void ReleaseProtected(string host, string operation)
    {
        lock (_gate)
            if (_slots.TryGetValue(host, out var slot) && slot.Operation == operation) _slots.TryRemove(host, out _);
    }

    public void Release(string host) => _slots.TryRemove(host, out _);

    private void Expire()
    {
        var now = _clock();
        foreach (var (host, s) in _slots)
            if (now - s.At > SlotTimeout) _slots.TryRemove(host, out _);
    }
}
