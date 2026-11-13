using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Capture;

// Метаданные сегмента для sidecar <файл>.mp4.json — по нему Uploader выгружает
// сегмент, сервер — разбирает (RecordingIngest).
public sealed record SegmentSidecar(
    string Host, string SessionUser, DateTime StartedAt, int Frames, int MotionEvents, string StartReason,
    string Version, ActivityLog? Activity, int? Width, int? Height, CaptureLayout? Layout);

// Закрытие сегмента, общее для агентов захвата (0.14.4: было две копии в
// SegmentWriter): закрыть вход ffmpeg и дождаться его, перепаковать
// фрагментированный .rec.mp4 в обычный .mp4, записать sidecar. Как кадры
// попадают в ffmpeg (image2pipe/rawvideo), у каждой ОС своё.
public static class SegmentFiles
{
    public const string RecSuffix = ".rec.mp4";

    public static void Finish(Process ffmpeg, Stream? stdin, string ffmpegPath, string? recPath, string? currentPath,
        SegmentSidecar sidecar, ILogger logger)
    {
        try { stdin?.Close(); }
        catch (Exception ex) { logger.LogWarning(ex, "stdin close failed"); }
        try
        {
            if (!ffmpeg.WaitForExit(15_000))
            {
                try { ffmpeg.Kill(); }
                catch (Exception ex) { logger.LogWarning(ex, "kill after wait timeout failed"); }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "wait for exit failed");
        }

        if (recPath is not null && currentPath is not null && File.Exists(recPath))
        {
            if (Remux(ffmpegPath, recPath, currentPath, logger))
            {
                try { File.Delete(recPath); }
                catch (Exception ex) { logger.LogWarning(ex, "delete {Rec} after remux failed", Path.GetFileName(recPath)); }
            }
            else
            {
                // .json не пишем — без него Uploader не возьмёт битый файл, а
                // .rec.mp4 разберёт OrphanSegments.
                logger.LogWarning("remux {Rec} failed, left for uploader recovery", Path.GetFileName(recPath));
                try { File.Delete(currentPath); } catch { }
            }
        }

        if (currentPath is not null && File.Exists(currentPath))
            WriteSidecar(currentPath, sidecar);
    }

    // sessionUser — всегда (пустая строка, если неизвестен): на RDS и при
    // нескольких X11-сессиях на одном хосте host без него одинаковый.
    public static void WriteSidecar(string mp4Path, SegmentSidecar s)
    {
        var now = DateTime.Now;
        var meta = new
        {
            host = s.Host,
            sessionUser = s.SessionUser,
            startedAt = s.StartedAt.ToString("o"),
            endedAt = now.ToString("o"),
            durationSec = (now - s.StartedAt).TotalSeconds,
            frames = s.Frames,
            motionEvents = s.MotionEvents,
            startReason = s.StartReason,
            file = Path.GetFileName(mp4Path),
            version = s.Version,
            activity = s.Activity?.ToJson() ?? [],
            activityTruncated = s.Activity?.Truncated ?? false,
            width = s.Width,
            height = s.Height,
            monitors = s.Layout?.Monitors.Select(m => new { x = m.X, y = m.Y, w = m.W, h = m.H, primary = m.Primary }),
        };
        File.WriteAllText(mp4Path + ".json", JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Аргументы — FfmpegArgs.Remux (src/Shared), те же у OrphanSegments в Uploader.
    public static bool Remux(string ffmpegPath, string recPath, string outPath, ILogger logger)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in FfmpegArgs.Remux(recPath, outPath))
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(); } catch { }
                logger.LogWarning("remux {Rec} timed out", Path.GetFileName(recPath));
                return false;
            }
            if (p.ExitCode != 0)
            {
                logger.LogWarning("remux {Rec} exit {Code}: {Err}", Path.GetFileName(recPath), p.ExitCode, err.Result);
                return false;
            }
            return File.Exists(outPath) && new FileInfo(outPath).Length > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "remux {Rec} failed", Path.GetFileName(recPath));
            return false;
        }
    }
}
