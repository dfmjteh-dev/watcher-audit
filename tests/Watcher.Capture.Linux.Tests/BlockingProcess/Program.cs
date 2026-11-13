// Имитирует зависший ffmpeg в тестах SegmentWriter: игнорирует аргументы, никогда
// не читает stdin, сам не завершается. Запускается только как дочерний процесс
// из SegmentWriterTimeoutTests — не часть продукта.
Thread.Sleep(Timeout.Infinite);
