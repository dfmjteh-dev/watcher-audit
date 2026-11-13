using Serilog.Core;
using Serilog.Events;
using Watcher.Uploader;

namespace Watcher;

public sealed class DiagnosticSink(Action<string?, bool> capture) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning) return;
        capture(logEvent.MessageTemplate.Text, logEvent.Level >= LogEventLevel.Error);
    }
}
