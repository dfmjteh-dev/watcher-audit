using System.Text.Json;
using Watcher.Uploader;

namespace Watcher.SessionService;

// 0.15.0: чья сессия (SSH или нет) — решаем один раз, при первом взгляде на
// транскрипт, и помним до его удаления, в том числе через перезапуск службы.
//
// Раньше закрытый транскрипт классифицировался заново по 4688 в момент
// закрытия. Журнал Security по умолчанию 20 МБ — на занятой машине (сервер 1С,
// терминальный) он перезаписывается за пару часов, и событие о старте
// PowerShell долгой консоли к её закрытию уже вытеснено: Unknown → через сутки
// транскрипт удалялся невыгруженным с потерей transcript-no-4688 (на проде —
// ~100 в день на ~20 хостах). То же после перезапуска службы (обновление):
// живое состояние было только в памяти.
//
// Ключ — имя файла (PowerShell даёт уникальное: хост, случайная часть, время).
// Unknown не храним — его переспрашиваем при следующем опросе.
public sealed class TranscriptOrigins
{
    // Label (0.16) — метка происхождения для сервера; у записей до 0.16 её нет.
    public sealed record Entry(SessionOrigin Origin, string? Remote, string? Label = null);

    private readonly string _path;
    private readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private readonly object _lock = new();

    public TranscriptOrigins(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) is { } saved)
                foreach (var (k, v) in saved)
                    if (v.Origin != SessionOrigin.Unknown) _map[k] = v;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Битый/недоступный файл — начинаем с пустого: худший случай как до 0.15.0.
        }
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public Entry? Get(string fileName) { lock (_lock) return _map.TryGetValue(fileName, out var e) ? e : null; }

    public void Set(string fileName, SessionOrigin origin, string? remote, string? label = null)
    {
        lock (_lock)
        {
            if (origin == SessionOrigin.Unknown) return;
            if (_map.TryGetValue(fileName, out var old) && old.Origin == origin && old.Remote == remote && old.Label == label) return;
            _map[fileName] = new Entry(origin, remote, label);
            _dirty = true;

        }
    }

    public void Remove(string fileName) { lock (_lock) _dirty |= _map.Remove(fileName); }

    // Записи файлов, которых больше нет (выгружены, удалены, не наши).
    public void Retain(IEnumerable<string> existingFileNames)
    {
        lock (_lock)
        {
            var keep = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
            foreach (var k in _map.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _map.Remove(k);
                _dirty = true;
            }

        }
    }

    // Пишем только при изменениях; через временный файл — обрыв не оставит полфайла.
    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                CredentialStore.CreateProtectedDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
                File.Move(tmp, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Не записали — повторим при следующем изменении/опросе; в памяти всё есть.
            }

        }
    }
}
