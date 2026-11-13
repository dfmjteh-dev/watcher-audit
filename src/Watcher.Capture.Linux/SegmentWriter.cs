using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Capture.Linux;

// Как Watcher.Capture (Windows)/SegmentWriter.cs — пайпит кадры в ffmpeg
// сабпроцесс, тот же таймаут-на-запись (зависший ffmpeg не блокирует цикл
// захвата навечно) и тот же sidecar .json. Отличие: кадры у нас уже чистый
// RGB24 byte[] (Frame), поэтому вместо "-f image2pipe -vcodec bmp" (кодировать
// каждый кадр в BMP) используем "-f rawvideo -pixel_format rgb24" — тот же
// эффект (сырые кадры по stdin), без накладных расходов на BMP-заголовки.
public sealed class SegmentWriter : IDisposable
{
    private readonly CaptureConfig _cfg;
    private readonly string _host;
    private readonly string _sessionUser;
    private readonly ILogger _logger;
    private Process? _ffmpeg;
    private Stream? _stdin;
    // Как на Windows (Задача 18): ffmpeg пишет фрагментированный _recPath — после
    // выхода из сессии, ребута или падения файл читается до последнего фрагмента
    // (раньше обычный mp4 без индекса moov не чинился и уходил в «потери»). Close()
    // перепаковывает его в обычный faststart _currentPath, брошенный .rec.mp4 —
    // Uploader (OrphanSegments).
    private string? _recPath;
    private string? _currentPath;
    private DateTime _startedAt;
    private int _frameCount;
    private int _motionEvents;
    private string _startReason = "";
    private int _width, _height;

    public bool IsOpen => _ffmpeg is not null && !_ffmpeg.HasExited;
    public DateTime StartedAt => _startedAt;

    // H9: activity — смены активного окна в sidecar; null — не писать (тесты).
    private readonly ActivityLog? _activity;

    public SegmentWriter(CaptureConfig cfg, string host, string sessionUser, ILogger logger, ActivityLog? activity = null)
    {
        _activity = activity;
        _cfg = cfg;
        _host = host;
        _sessionUser = sessionUser;
        _logger = logger;
    }

    // 0.13: layout — мониторы в координатах кадра (sidecar: плеер умеет показать
    // один монитор).
    private CaptureLayout? _layout;

    public void Open(string reason, int width, int height, CaptureLayout? layout = null)
    {
        if (IsOpen) return;
        _layout = layout;

        if (_ffmpeg is not null)
        {
            // Предыдущий сегмент оборвался через Kill() (таймаут/ошибка записи), не
            // через Close() — освобождаем мёртвый хендл перед запуском нового.
            _ffmpeg.Dispose();
            _ffmpeg = null;
            _stdin = null;
            _recPath = null;
            _currentPath = null;
        }

        Directory.CreateDirectory(_cfg.QueueDir);
        _startedAt = DateTime.Now;
        _startReason = reason;
        _frameCount = 0;
        _motionEvents = 0;
        _activity?.Start();
        _width = width;
        _height = height;

        string ts = _startedAt.ToString("yyyyMMdd_HHmmss");
        // Пользователь в имени — как на Windows (Watcher.Capture BuildBaseName): несколько
        // одновременных X11-сессий на одном хосте иначе давали одно имя сегмента, и
        // сервер принимал второй за повтор первого (docs/code-review-2026-10.md, R2).
        string baseName = $"{BuildBaseName(_host, _sessionUser)}_{ts}";
        _currentPath = Path.Combine(_cfg.QueueDir, baseName + ".mp4");
        _recPath = Path.Combine(_cfg.QueueDir, baseName + RecSuffix);

        // R4 (docs/code-review-2026-10.md): ArgumentList, не склейка строки — каждый
        // элемент уходит ffmpeg ровно одним аргументом, значение из серверного
        // конфига (Preset) не может добавить лишние аргументы или выходной файл.
        string[] args =
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-f", "rawvideo",
            "-pixel_format", "rgb24",
            "-video_size", $"{width}x{height}",
            "-framerate", _cfg.OutputFps.ToString(),
            "-i", "-",
            "-c:v", "libx264",
            "-preset", _cfg.Preset,
            "-tune", "stillimage",
            "-crf", _cfg.Crf.ToString(),
            "-pix_fmt", "yuv420p",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000",
            // Готовый фрагмент — сразу на диск: статичный экран кодируется в
            // крошечные фрагменты, и без этого они секундами копятся в буфере вывода
            // ffmpeg (32 КБ) — при обрыве терялись вместе с ним (проверено: без флага
            // 28 Б, с ним — 50 кадров из 60).
            "-flush_packets", "1",
            _recPath
        ];

        var psi = new ProcessStartInfo
        {
            FileName = _cfg.FfmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string a in args) psi.ArgumentList.Add(a);

        _ffmpeg = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg");
        _stdin = _ffmpeg.StandardInput.BaseStream;

        _ = Task.Run(async () =>
        {
            try
            {
                var err = await _ffmpeg.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(err))
                    _logger.LogWarning("ffmpeg stderr: {Stderr}", err);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg stderr read failed");
            }
        });
    }

    public void WriteFrame(Frame frame, bool isMotion)
    {
        if (!IsOpen || _stdin is null) return;

        if (frame.Width != _width || frame.Height != _height)
        {
            // Сегмент открыт под фиксированный -video_size — кадр другого размера
            // (например, разрешение сменилось конфигом между Open() и этим вызовом)
            // сломал бы ffmpeg-пайп молча. Program.cs обязан закрыть сегмент перед
            // сменой разрешения (см. ConfigReload) — если долетели сюда, это баг
            // вызывающего кода, не сценарий, который нужно тихо проглатывать.
            _logger.LogWarning(
                "frame size {FW}x{FH} does not match open segment {SW}x{SH}, dropping frame",
                frame.Width, frame.Height, _width, _height);
            return;
        }
        // Время видео этого кадра — см. ActivityLog.
        _activity?.Sample(DateTime.Now, (double)_frameCount / Math.Max(1, _cfg.OutputFps), _cfg.Activity);

        byte[] buffer = frame.Pixels;
        var stdin = _stdin;
        var writeTask = Task.Run(() =>
        {
            stdin.Write(buffer, 0, buffer.Length);
            stdin.Flush();
        });

        // Task.Wait(ms) не просто "вернуть false по таймауту" — если задача успевает
        // упасть ДО таймаута, Wait бросает AggregateException немедленно, а не ждёт
        // отведённое время. На Windows это почти не всплывает (зависший pipe-write
        // туда просто блокируется, не падает с ошибкой), но на Linux редиректнутый
        // stdin процесса — это сокет под капотом (.NET), и падение раньше таймаута
        // (ECONNRESET/EPIPE-подобное) — реальный наблюдаемый путь, не гипотетический.
        // Без этого try/catch необработанное исключение валило бы весь цикл захвата
        // в Program.cs (там это FATAL), хотя ровно для такого случая уже была ветка
        // ниже — просто недостижимая, раз Wait бросает раньше, чем до неё доходит.
        bool completed;
        try
        {
            completed = writeTask.Wait(_cfg.FfmpegWriteTimeoutMs);
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex.InnerException ?? ex, "write failed");
            KillAndWait("kill after write failure failed");
            return;
        }

        if (!completed)
        {
            _logger.LogWarning(
                "write to ffmpeg timed out after {TimeoutMs} ms, killing process", _cfg.FfmpegWriteTimeoutMs);
            writeTask.ContinueWith(t =>
                _logger.LogWarning(t.Exception, "delayed write failure after timeout"),
                TaskContinuationOptions.OnlyOnFaulted);
            KillAndWait("kill after write timeout failed");
            return;
        }

        _frameCount++;
        if (isMotion) _motionEvents++;
    }

    // Kill() сигналит SIGKILL, но не ждёт, пока процесс реально умрёт — без
    // короткого WaitForExit() сразу после IsOpen (HasExited) мог недолго всё ещё
    // показывать true вызывающему коду (Program.cs решает по нему, открывать ли
    // следующий сегмент). SIGKILL не перехватывается, так что это ограниченное
    // ожидание подтверждения, не риск реально зависнуть.
    private void KillAndWait(string warningMessage)
    {
        try
        {
            _ffmpeg?.Kill();
            _ffmpeg?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, warningMessage);
        }
    }

    public void Close()
    {
        if (_ffmpeg is null) return;
        // Закрыть ffmpeg, перепаковать .rec.mp4, sidecar — SegmentFiles (src/Shared).
        SegmentFiles.Finish(_ffmpeg, _stdin, _cfg.FfmpegPath, _recPath, _currentPath, new SegmentSidecar(_host, _sessionUser, _startedAt, _frameCount, _motionEvents, _startReason, AppVersion.Current, _activity, _width, _height, _layout), _logger);
        _ffmpeg.Dispose();
        _ffmpeg = null;
        _stdin = null;
        _recPath = null;
        _currentPath = null;
    }

    public const string RecSuffix = SegmentFiles.RecSuffix;

    public static string BuildBaseName(string host, string sessionUser) =>
        SafeName.Strip(sessionUser) is { Length: > 0 } user ? $"{host}_{user}" : host;

    public void Dispose() => Close();
}
