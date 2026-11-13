namespace Watcher.Capture.Linux;

// Математика — MotionDetectorBase (src/Shared); кадр — плотный byte[]
// (Frame.Stride == Width*3). Копия: буфер кадра переиспользует захват.
public sealed class MotionDetector(int pixelDelta) : MotionDetectorBase(pixelDelta)
{
    public double ComputeChange(Frame frame) =>
        Compare((byte[])frame.Pixels.Clone(), frame.Width, frame.Height, frame.Stride);
}
