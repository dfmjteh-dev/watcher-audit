using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watcher.Uploader;

// Задача 18 (roadmap-fixes.md): сегмент, чей Capture убили (выход из системы,
// выключение, падение, обесточивание) — без .json, а цикл выгрузки берёт только
// пары mp4 + .json, такой файл лежал бы в очереди вечно.
//
// Windows-Capture пишет фрагментированный <имя>.rec.mp4 и в Close() перепаковывает
// его в обычный <имя>.mp4 (см. Watcher.Capture/SegmentWriter.cs). Оборванный
// .rec.mp4 читается до последнего фрагмента — здесь он перепаковывается так же
// (-c copy) и уходит с startReason "recovered" — с 0.13 так пишет и Linux-Capture.
// Обычный mp4 без .json (Linux-Capture до 0.13, установки до этой задачи)
// проверяется разбором контейнера: оборванный ffmpeg не пишет индекс moov, такой файл не воспроизводится и не чинится — удаляется и
// учитывается в droppedSegments, как и квота. Файл без единого кадра (Mp4Probe)
// просто удаляется: потери нет, в droppedSegments не идёт.
public static class OrphanSegments
{
    public const string RecoveredReason = "recovered";
    public const string RecSuffix = ".rec.mp4";

    // Имя сегмента — <host>[_<user>]_<yyyyMMdd_HHmmss>[_c<hex6>].mp4 (SegmentWriter
    // обеих ОС + Uploader.RenameForCollision).
    private static readonly Regex NamePattern = new(
        @"^(?<base>.+)_(?<ts>\d{8}_\d{6})(?:_c[0-9a-f]{6})?\.mp4$", RegexOptions.CultureInvariant);

    // Результат true/false/null у обоих делегатов: удалось / файл битый / проверить
    // нечем (ffmpeg не запустился, таймаут) — тогда файл не трогаем до следующего раза.
    // remux(rec, out) — перепаковать .rec.mp4 в обычный mp4; isPlayable(mp4) — целый ли.
    // Возвращает число удалённых (потерянных) сегментов.
    public static int Apply(string queueDir, TimeSpan minAge, string host, string version,
        Func<string, string, bool?> remux, Func<string, bool?> isPlayable, Action<string> log,
        Action<string, long>? onLoss = null)
    {
        if (!Directory.Exists(queueDir)) return 0;

        int dropped = 0;
        var files = new DirectoryInfo(queueDir).GetFiles("*.mp4", SearchOption.TopDirectoryOnly);

        foreach (var rec in files.Where(f => f.Name.EndsWith(RecSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            if (AbandonedLength(rec, minAge) is not long recSize) continue;
            string outPath = rec.FullName[..^RecSuffix.Length] + ".mp4";
            // Обычный mp4 рядом без .json — перепаковку в Close() оборвали на полпути.
            if (File.Exists(outPath) && !File.Exists(outPath + ".json")) TryDelete(outPath, log);
            if (File.Exists(outPath))
            {
                // Перепаковка дошла до конца, не успели удалить .rec.mp4.
                TryDelete(rec.FullName, log);
                continue;
            }

            if (IsEmpty(rec.FullName, recSize))
            {
                if (TryDelete(rec.FullName, log))
                    log($"empty segment discarded: {rec.Name} ({recSize:N0} B, no frames — capture stopped right after start)");
                continue;
            }

            bool? ok = remux(rec.FullName, outPath);
            if (ok is null)
            {
                TryDelete(outPath, log);
                continue;
            }
            if (ok == true && WriteMeta(new FileInfo(outPath), rec.LastWriteTime, host, version, log))
            {
                TryDelete(rec.FullName, log);
                log($"orphan segment recovered: {Path.GetFileName(outPath)} (capture was killed mid-segment, remuxed from {rec.Name})");
            }
            else
            {
                TryDelete(outPath, log);
                if (TryDelete(rec.FullName, log))
                {
                    dropped++;
                    onLoss?.Invoke(rec.Name, recSize);
                    log($"orphan segment dropped: {rec.Name} ({recSize:N0} B, not recoverable)");
                }
            }
        }

        foreach (var mp4 in files.Where(f => !f.Name.EndsWith(RecSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            // Exists по диску, не кэш FileInfo — первый проход мог удалить недописанный выход.
            if (!File.Exists(mp4.FullName) || File.Exists(mp4.FullName + ".json")) continue;
            // Чей-то .rec.mp4 ещё пишется или ждёт своей очереди — это его выход.
            if (File.Exists(mp4.FullName[..^".mp4".Length] + RecSuffix)) continue;
            if (AbandonedLength(mp4, minAge) is not long size) continue;

            if (IsEmpty(mp4.FullName, size))
            {
                if (TryDelete(mp4.FullName, log))
                    log($"empty segment discarded: {mp4.Name} ({size:N0} B, no frames — capture stopped right after start)");
                continue;
            }

            bool? ok = isPlayable(mp4.FullName);
            if (ok is null) continue;

            if (ok == true)
            {
                if (WriteMeta(mp4, mp4.LastWriteTime, host, version, log))
                    log($"orphan segment recovered: {mp4.Name} (no .json, file intact)");
            }
            else
            {
                if (TryDelete(mp4.FullName, log))
                {
                    dropped++;
                    onLoss?.Invoke(mp4.Name, size);
                    log($"orphan segment dropped: {mp4.Name} ({size:N0} B, unreadable — capture was killed mid-segment)");
                }
            }
        }
        return dropped;
    }

    // Ни одного кадра — нулевой файл или корректный заголовок mp4 без данных
    // (Mp4Probe). Такой файл удаляется, но в droppedSegments не идёт.
    private static bool IsEmpty(string path, long size) => size == 0 || Mp4Probe.HasMedia(path) == false;

    // Длина брошенного файла или null, если файл ещё чей-то. Возраст по mtime:
    // открытый сегмент ffmpeg дописывает каждую секунду, минуты простоя бывают
    // только у брошенного файла. Windows: файл, который держит ffmpeg, эксклюзивно не
    // откроется; на Linux блокировок нет — только возраст.
    // Длина — с открытого файла, не из FileInfo перечисления: NTFS обновляет размер в
    // записи каталога лениво, у открытого ffmpeg'ом .rec.mp4 там 0 (видно на стенде),
    // после убийства процесса запись может так и остаться — целый файл посчитался бы
    // пустым и удалился.
    private static long? AbandonedLength(FileInfo f, TimeSpan minAge)
    {
        if (DateTime.UtcNow - f.LastWriteTimeUtc < minAge) return null;
        try
        {
            using var fs = File.Open(f.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            return fs.Length;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool TryDelete(string path, Action<string> log)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            log($"orphan segment: delete failed {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    private static bool WriteMeta(FileInfo mp4, DateTime endedAt, string host, string version, Action<string> log)
    {
        try
        {
            File.WriteAllText(mp4.FullName + ".json", BuildMeta(mp4.Name, endedAt, host, version));
            return true;
        }
        catch (Exception ex)
        {
            log($"orphan segment: write meta failed {mp4.Name}: {ex.Message}");
            return false;
        }
    }

    // public ради тестов. endedAt — время последней записи в оборванный файл.
    public static string BuildMeta(string mp4Name, DateTime endedAt, string host, string version)
    {
        DateTime startedAt = endedAt;
        string sessionUser = "";

        var m = NamePattern.Match(mp4Name);
        if (m.Success)
        {
            if (DateTime.TryParseExact(m.Groups["ts"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var ts) && ts <= endedAt)
                startedAt = ts;
            string baseName = m.Groups["base"].Value;
            if (baseName.StartsWith(host + "_", StringComparison.OrdinalIgnoreCase))
                sessionUser = baseName[(host.Length + 1)..];
        }

        var meta = new
        {
            host,
            sessionUser,
            startedAt = startedAt.ToString("o"),
            endedAt = endedAt.ToString("o"),
            durationSec = (endedAt - startedAt).TotalSeconds,
            frames = 0,
            motionEvents = 0,
            startReason = RecoveredReason,
            file = mp4Name,
            version,
        };
        return JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
    }

    // Разбор контейнера без декодирования (-c copy в null).
    public static bool? ProbeWithFfmpeg(string ffmpegPath, string mp4, TimeSpan timeout) =>
        RunFfmpeg(ffmpegPath, timeout, "-v", "error", "-i", mp4, "-c", "copy", "-f", "null", "-");

    // Те же аргументы, что перепаковка в Watcher.Capture/SegmentWriter.cs.
    public static bool? RemuxWithFfmpeg(string ffmpegPath, string recPath, string outPath, TimeSpan timeout)
    {
        bool? ok = RunFfmpeg(ffmpegPath, timeout, FfmpegArgs.Remux(recPath, outPath));
        return ok == true ? File.Exists(outPath) && new FileInfo(outPath).Length > 0 : ok;
    }

    private static bool? RunFfmpeg(string ffmpegPath, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception) { return null; }
        if (p is null) return null;
        using (p)
        {
            // Читаем асинхронно, чтобы ffmpeg не встал на полном пайпе.
            _ = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return p.ExitCode == 0;
        }
    }
}
