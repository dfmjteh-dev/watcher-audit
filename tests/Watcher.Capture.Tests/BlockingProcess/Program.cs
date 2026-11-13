// Stands in for a hung ffmpeg in SegmentWriter tests: ignores all args, never
// reads stdin, never exits on its own. Used only as a child process launched
// by SegmentWriterTimeoutTests — not part of the shipped product.
Thread.Sleep(Timeout.Infinite);
