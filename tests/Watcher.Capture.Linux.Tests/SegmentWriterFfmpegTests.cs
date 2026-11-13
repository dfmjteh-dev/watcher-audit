using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher;
using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

// С настоящим ffmpeg: сегмент пишется фрагментированным .rec.mp4 и при Close()
// перепаковывается в обычный mp4; убитый посреди записи — остаётся читаемым до
// последнего фрагмента (раньше на Linux такой файл уходил в «потери»). Нет
// ffmpeg/ffprobe на машине — тесты ничего не проверяют (на стенде и
// control-host они есть; ScreenGrabber и ffmpeg-кодирование в CI осознанно не
// тестируются, см. docs/development.md).
public sealed class SegmentWriterFfmpegTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"watcher-seg-ffmpeg-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static bool Has(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private CaptureConfig Config() => new() { QueueDir = _dir, FfmpegPath = "ffmpeg", OutputFps = 2, Crf = 30, Preset = "ultrafast" };

    private static Frame Colored(int i)
    {
        var px = new byte[320 * 240 * 3];
        for (int k = 0; k < px.Length; k += 3) { px[k] = (byte)(i * 40); px[k + 1] = (byte)(255 - i * 40); px[k + 2] = (byte)(k % 251); }
        return new Frame(320, 240, px);
    }

    private static Frame Uniform(int i) => new(320, 240, Enumerable.Repeat((byte)(i * 40), 320 * 240 * 3).ToArray());

    private static int VideoFrames(string path)
    {
        var psi = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(30_000);
        return int.TryParse(o, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    [Fact]
    public void Close_remuxes_rec_into_plain_mp4()
    {
        if (!Has("ffmpeg") || !Has("ffprobe")) return;
        using var w = new SegmentWriter(Config(), "h", "u", NullLogger.Instance);
        w.Open("motion 1.00%", 320, 240);
        for (int i = 0; i < 6; i++) w.WriteFrame(Colored(i), isMotion: true);
        w.Close();

        var mp4 = Directory.GetFiles(_dir, "*.mp4").Single();
        Assert.False(mp4.EndsWith(SegmentWriter.RecSuffix, StringComparison.Ordinal));
        Assert.True(File.Exists(mp4 + ".json"));
        Assert.Equal(6, VideoFrames(mp4));
    }

    // Выход из сессии/ребут: ffmpeg убит, Close() не было. В очереди — .rec.mp4
    // без .json, который перепаковывается (так делает OrphanSegments) с кадрами
    // до последнего записанного фрагмента.
    [Fact]
    public void Killed_segment_leaves_recoverable_rec()
    {
        if (!Has("ffmpeg") || !Has("ffprobe")) return;
        var w = new SegmentWriter(Config(), "h", "u", NullLogger.Instance);
        w.Open("motion 1.00%", 320, 240);
        // Однородные кадры — статичный экран: фрагменты крошечные, и без
        // -flush_packets они так и оставались в буфере ffmpeg (файл в 28 Б). x264
        // с потоками держит по кадру на поток — кадров с запасом, 30 с при 2 fps.
        for (int i = 0; i < 60; i++) w.WriteFrame(Uniform(i % 6), isMotion: true);
        Thread.Sleep(3000);   // ffmpeg дописывает готовые фрагменты
        var ffmpeg = (Process)typeof(SegmentWriter).GetField("_ffmpeg", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        ffmpeg.Kill();
        ffmpeg.WaitForExit(5000);

        var files = Directory.GetFiles(_dir);
        Assert.True(files.Length > 0 && files.Any(f => f.EndsWith(SegmentWriter.RecSuffix, StringComparison.Ordinal)),
            "в очереди: " + string.Join(", ", files.Select(f => Path.GetFileName(f) + " " + new FileInfo(f).Length)) + $"; exited={ffmpeg.HasExited}");
        string rec = files.Single(f => f.EndsWith(SegmentWriter.RecSuffix, StringComparison.Ordinal));
        Assert.False(File.Exists(rec[..^SegmentWriter.RecSuffix.Length] + ".mp4.json"));
        string outPath = Path.Combine(_dir, "recovered.mp4");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in FfmpegArgs.Remux(rec, outPath)) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { p.StandardError.ReadToEnd(); p.WaitForExit(30_000); Assert.Equal(0, p.ExitCode); }
        Assert.InRange(VideoFrames(outPath), 1, 60);
    }
}
