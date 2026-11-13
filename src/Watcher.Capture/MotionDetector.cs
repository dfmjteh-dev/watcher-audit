using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Watcher.Capture;

[SupportedOSPlatform("windows")]
// Математика — MotionDetectorBase (src/Shared); здесь — байты кадра из Bitmap.
public sealed class MotionDetector(int pixelDelta) : MotionDetectorBase(pixelDelta)
{
    public double ComputeChange(Bitmap frame)
    {
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var cur = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, cur, 0, cur.Length);
            return Compare(cur, data.Width, data.Height, data.Stride);
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }
}
