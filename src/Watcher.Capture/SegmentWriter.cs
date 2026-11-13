using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Capture;

[SupportedOSPlatform("windows")]
public sealed class SegmentWriter : IDisposable
{
    private readonly CaptureConfig _cfg;
    private readonly string _host;
    private readonly string? _sessionUser;
    private readonly ILogger _logger;
    private Process? _ffmpeg;
    private Stream? _stdin;
    // Задача 18 (roadmap-fixes.md): ffmpeg пишет фрагментированный mp4 в _recPath
    // (индекс в начале, фрагменты по секунде) — убитый посреди записи (выход из
    // системы убивает и Capture, и ffmpeg меньше чем за секунду, на стенде) файл
    // остаётся читаемым до последнего фрагмента. Обычный mp4 пишет индекс moov
    // только в конце — без него не воспроизводится и не чинится. Close()
    // перепаковывает (-c copy, без перекодирования) в обычный faststart _currentPath —
    // формат, который получают сервер и плеер, не меняется. Оборванный .rec.mp4
    // перепакует Uploader (OrphanSegments).
    private string? _recPath;
    private string? _currentPath;
    private DateTime _startedAt;
    private int _frameCount;
    private int _motionEvents;
    private string _startReason = "";

    public bool IsOpen => _ffmpeg is not null && !_ffmpeg.HasExited;
    public DateTime StartedAt => _startedAt;

    // sessionUser — Фаза 6 (roadmap.md): пусто для обычных одно-сессионных станций
    // (Scheduled Task), задано Watcher.SessionService через --session-user для
    // процесса, запущенного в конкретной RDS-сессии. Санитайз — SafeName
    // (src/Shared), тот же, что у сервера для каталога блобов.
    // H9: activity — смены активного окна в sidecar; null — не писать (тесты).
    private readonly ActivityLog? _activity;

    public SegmentWriter(CaptureConfig cfg, string host, string? sessionUser, ILogger logger, ActivityLog? activity = null)
    {
        _activity = activity;
        _cfg = cfg;
        _host = host;
        // В метаданные — настоящее имя (a.butakov): очищенное для файлов
        // (abutakov) не совпадало со списками пользователей. Имя файла чистит
        // BuildBaseName.
        _sessionUser = string.IsNullOrWhiteSpace(sessionUser) ? null : sessionUser.Trim();
        _logger = logger;
    }

    // public + static ради тестируемости без реального ffmpeg — тот же приём, что
    // уже применён для UploadValidation на сервере (см. development.md, Фаза 0).
    public static string SanitizeForFilename(string s) => SafeName.ForPath(s);

    public static string BuildBaseName(string host, string? sessionUser) =>
        string.IsNullOrWhiteSpace(sessionUser) ? host : $"{host}_{SanitizeForFilename(sessionUser)}";

    // 0.13: layout — размер кадра и мониторы в его координатах (sidecar: плеер
    // умеет показать один монитор).
    private CaptureLayout? _layout;

    public void Open(string reason, CaptureLayout? layout = null)
    {
        if (IsOpen) return;
        _layout = layout;

        if (_ffmpeg is not null)
        {
            // Previous segment was torn down via Kill() (write timeout/failure), not
            // through Close() — dispose the dead process handle before starting a new one.
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

        string ts = _startedAt.ToString("yyyyMMdd_HHmmss");
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
            "-f", "image2pipe",
            "-vcodec", "bmp",
            "-framerate", _cfg.OutputFps.ToString(),
            "-i", "-",
            "-c:v", "libx264",
            "-preset", _cfg.Preset,
            "-tune", "stillimage",
            "-crf", _cfg.Crf.ToString(),
            "-pix_fmt", "yuv420p",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000",
            // Готовый фрагмент — сразу на диск (как в Watcher.Capture.Linux): без
            // этого фрагменты статичного экрана копятся в буфере вывода ffmpeg и
            // пропадают при обрыве записи.
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

    public void WriteFrame(Bitmap frame, bool isMotion)
    {
        if (!IsOpen || _stdin is null) return;
        // Время видео этого кадра — см. ActivityLog.
        _activity?.Sample(DateTime.Now, (double)_frameCount / Math.Max(1, _cfg.OutputFps), _cfg.Activity);

        byte[] buffer;
        using (var ms = new MemoryStream())
        {
            frame.Save(ms, ImageFormat.Bmp);
            buffer = ms.ToArray();
        }

        var stdin = _stdin;
        var writeTask = Task.Run(() =>
        {
            stdin.Write(buffer, 0, buffer.Length);
            stdin.Flush();
        });

        if (!writeTask.Wait(_cfg.FfmpegWriteTimeoutMs))
        {
            _logger.LogWarning(
                "write to ffmpeg timed out after {TimeoutMs} ms, killing process", _cfg.FfmpegWriteTimeoutMs);
            writeTask.ContinueWith(t =>
                _logger.LogWarning(t.Exception, "delayed write failure after timeout"),
                TaskContinuationOptions.OnlyOnFaulted);
            try { _ffmpeg?.Kill(); }
            catch (Exception ex) { _logger.LogWarning(ex, "kill after write timeout failed"); }
            return;
        }

        if (writeTask.IsFaulted)
        {
            _logger.LogWarning(writeTask.Exception, "write failed");
            try { _ffmpeg?.Kill(); }
            catch (Exception ex) { _logger.LogWarning(ex, "kill after write failure failed"); }
            return;
        }

        _frameCount++;
        if (isMotion) _motionEvents++;
    }

    public void Close()
    {
        if (_ffmpeg is null) return;
        // Закрыть ffmpeg, перепаковать .rec.mp4, sidecar — SegmentFiles (src/Shared).
        SegmentFiles.Finish(_ffmpeg, _stdin, _cfg.FfmpegPath, _recPath, _currentPath, new SegmentSidecar(_host, _sessionUser ?? "", _startedAt, _frameCount, _motionEvents, _startReason, AppVersion.Current, _activity, _layout?.Width, _layout?.Height, _layout), _logger);
        _ffmpeg.Dispose();
        _ffmpeg = null;
        _stdin = null;
        _recPath = null;
        _currentPath = null;
    }

    public const string RecSuffix = SegmentFiles.RecSuffix;

    public void Dispose() => Close();
}
