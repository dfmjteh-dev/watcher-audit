namespace Watcher.Capture.Linux;

// Чистые управляемые данные (byte[]), без unmanaged-хендлов — в отличие от
// System.Drawing.Bitmap на Windows-агенте, здесь нет GDI-контекста, который
// нужно явно освобождать, и System.Drawing.Common на Linux всё равно бросает
// PlatformNotSupportedException. Побочный эффект: MotionDetector/RingBuffer
// здесь не нужно гонять только на реальном Windows CLR — они переносимы и
// тестируются в CI на ubuntu-latest (см. docs/development.md).
//
// Пиксели упакованы плотно (без паддинга строки под выравнивание): Stride
// всегда Width*3, порядок байт R,G,B на пиксель.
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 3;

    public Frame(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 3)
            throw new ArgumentException($"pixels length {pixels.Length} does not match {width}x{height}x3");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public Frame Clone() => new(Width, Height, (byte[])Pixels.Clone());
}
