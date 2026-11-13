using Watcher.Capture.Linux;
using Xunit;

namespace Watcher.Capture.Linux.Tests;

public class MotionDetectorTests
{
    private static Frame SolidFrame(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
        }
        return new Frame(w, h, pixels);
    }

    [Fact]
    public void FirstFrame_establishes_baseline_and_reports_zero_change()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        var frame = SolidFrame(16, 16, 100, 100, 100);

        double change = detector.ComputeChange(frame);

        Assert.Equal(0.0, change);
    }

    [Fact]
    public void Identical_consecutive_frames_report_zero_change()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        var frame1 = SolidFrame(16, 16, 100, 100, 100);
        var frame2 = SolidFrame(16, 16, 100, 100, 100);
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(0.0, change);
    }

    [Fact]
    public void Change_within_pixel_delta_threshold_is_not_motion()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        var frame1 = SolidFrame(16, 16, 100, 100, 100);
        var frame2 = SolidFrame(16, 16, 120, 120, 120); // diff 20 <= 25
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(0.0, change);
    }

    [Fact]
    public void Change_beyond_pixel_delta_threshold_is_reported_as_full_motion()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        var frame1 = SolidFrame(16, 16, 100, 100, 100);
        var frame2 = SolidFrame(16, 16, 150, 150, 150); // diff 50 > 25
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(100.0, change, precision: 3);
    }

    [Fact]
    public void Dimension_change_resets_baseline_and_reports_zero()
    {
        var detector = new MotionDetector(pixelDelta: 25);
        var frame1 = SolidFrame(16, 16, 100, 100, 100);
        var frame2 = SolidFrame(32, 32, 255, 255, 255);
        detector.ComputeChange(frame1);

        double change = detector.ComputeChange(frame2);

        Assert.Equal(0.0, change);
    }
}
