namespace Watcher.Capture;

// Детектор движения, общий для агентов захвата (0.14.4: была одна и та же
// математика в двух копиях): среднее по трём каналам пикселя, разница по
// модулю с прошлым кадром против порога PixelDelta, результат — процент
// изменившихся пикселей. Как достать байты кадра (Bitmap.LockBits на Windows,
// плотный byte[] на Linux) — дело наследника.
public abstract class MotionDetectorBase : IMotionThreshold
{
    // Не readonly — конфиг с сервера (ConfigReload) меняет порог на лету, без
    // пересоздания детектора и потери прошлого кадра.
    public int PixelDelta { get; set; }
    private byte[]? _prev;
    private int _w, _h, _stride;

    protected MotionDetectorBase(int pixelDelta)
    {
        PixelDelta = pixelDelta;
    }

    // cur — 24 бит на пиксель, строки по stride байт; массив переходит детектору
    // (станет «прошлым кадром»), вызывающий его больше не меняет.
    protected double Compare(byte[] cur, int w, int h, int stride)
    {
        if (_prev is null || _w != w || _h != h || _stride != stride)
        {
            _prev = cur;
            _w = w; _h = h; _stride = stride;
            return 0.0;
        }

        int changed = 0;
        int total = 0;
        int delta = PixelDelta;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 3;
                int gNew = (cur[i] + cur[i + 1] + cur[i + 2]) / 3;
                int gOld = (_prev[i] + _prev[i + 1] + _prev[i + 2]) / 3;
                if (Math.Abs(gNew - gOld) > delta) changed++;
                total++;
            }
        }

        _prev = cur;
        return total == 0 ? 0.0 : 100.0 * changed / total;
    }
}
