namespace Watcher.Capture.Linux;

// Выделено ради тестируемости ConfigReload.Apply без живого X11-дисплея: на
// Windows-агенте тот же тест конструирует настоящий ScreenGrabber (там это
// не проблема, GetSystemMetrics работает и без интерактивного рабочего
// стола), а здесь конструктор ScreenGrabber делает реальный XOpenDisplay,
// которого на ubuntu-latest CI-раннере нет. См. ConfigReloadTests.cs.
public interface IScreenGrabber : IResizableGrabber
{
}
