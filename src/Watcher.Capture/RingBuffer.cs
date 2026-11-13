using System.Drawing;
using System.Runtime.Versioning;

namespace Watcher.Capture;

// Буфер pre-roll — FrameRing (src/Shared); Bitmap держит GDI-ресурсы: копии —
// Clone, вытесненные — Dispose.
[SupportedOSPlatform("windows")]
public sealed class RingBuffer(int capacity) : FrameRing<Bitmap>(capacity, b => (Bitmap)b.Clone(), b => b.Dispose());
