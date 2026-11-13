namespace Watcher.Capture.Linux;

// Буфер pre-roll — FrameRing (src/Shared); Frame — чистый byte[], освобождать
// нечего, вытеснение — «перестать ссылаться».
public sealed class RingBuffer(int capacity) : FrameRing<Frame>(capacity, f => f.Clone());
