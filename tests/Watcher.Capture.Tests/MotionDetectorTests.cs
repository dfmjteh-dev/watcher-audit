using System.Drawing;
using Watcher.Capture;
using Xunit;

namespace Watcher.Capture.Tests;

public class MotionDetectorTests
{
    private static Bitmap SolidBitmap(int w, int h, Color color)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    [Fact]
    public void FirstFrame_establishes_baseline_and_reports_zero_change()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        using var frame = SolidBitmap(16, 16, Color.FromArgb(100, 100, 100));

        double change = detector.ComputeChange(frame);

        Assert.Equal(0.0, change);
    }

    [Fact]
    public void Identical_consecutive_frames_report_zero_change()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        using var frame1 = SolidBitmap(16, 16, Color.FromArgb(100, 100, 100));
        using var frame2 = SolidBitmap(16, 16, Color.FromArgb(100, 100, 100));
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(0.0, change);
    }

    [Fact]
    public void Change_within_pixel_delta_threshold_is_not_motion()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        using var frame1 = SolidBitmap(16, 16, Color.FromArgb(100, 100, 100));
        using var frame2 = SolidBitmap(16, 16, Color.FromArgb(120, 120, 120)); // diff 20 <= 25
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(0.0, change);
    }

    [Fact]
    public void Change_beyond_pixel_delta_threshold_is_reported_as_full_motion()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        using var frame1 = SolidBitmap(16, 16, Color.FromArgb(100, 100, 100));
        using var frame2 = SolidBitmap(16, 16, Color.FromArgb(150, 150, 150)); // diff 50 > 25
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(100.0, change, precision: 3);
    }

    [Fact]
    public void Dimension_change_resets_baseline_and_reports_zero()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        using var frame1 = SolidBitmap(16, 16, Color.FromArgb(100, 100, 100));
        using var frame2 = SolidBitmap(32, 32, Color.FromArgb(255, 255, 255));
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(0.0, change);
    }
}
