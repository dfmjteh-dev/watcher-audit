namespace Watcher.Capture;

// Буфер pre-roll — последние кадры до начала записи (0.14.4: общий для агентов
// захвата; было две копии под Bitmap и Frame). Кадр Windows (Bitmap) держит
// GDI-ресурсы — его освобождает release; Linux-кадр — просто byte[], release нет.
public class FrameRing<T> : IDisposable where T : class
{
    private readonly Queue<T> _frames;
    private readonly Func<T, T> _clone;
    private readonly Action<T>? _release;
    private int _capacity;

    public int Capacity => _capacity;

    public FrameRing(int capacity, Func<T, T> clone, Action<T>? release = null)
    {
        _capacity = Math.Max(0, capacity);
        _frames = new Queue<T>(_capacity);
        _clone = clone;
        _release = release;
    }

    // CaptureIntervalMs меняется на лету (ConfigReload) — от него зависит, сколько
    // кадров соответствует PreRollSeconds; без пересчёта ёмкости длительность
    // pre-roll в секундах плывёт. Лишние кадры при уменьшении освобождаются сразу.
    public void Resize(int newCapacity)
    {
        _capacity = Math.Max(0, newCapacity);
        while (_frames.Count > _capacity) Release(_frames.Dequeue());
    }

    public void Push(T frame)
    {
        if (_capacity <= 0) { Release(frame); return; }
        if (_frames.Count >= _capacity) Release(_frames.Dequeue());
        _frames.Enqueue(frame);
    }

    public T[] DrainCopies()
    {
        var copies = new T[_frames.Count];
        int i = 0;
        foreach (var f in _frames) copies[i++] = _clone(f);
        return copies;
    }

    // Смена разрешения (ConfigReload): старые кадры другого размера, чем пойдут в
    // новый сегмент, — в один ffmpeg-поток их смешивать нельзя, буфер очищается.
    public void Clear()
    {
        while (_frames.Count > 0) Release(_frames.Dequeue());
    }

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    private void Release(T frame) => _release?.Invoke(frame);
}
