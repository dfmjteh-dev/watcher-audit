using System.Globalization;
using System.Xml.Linq;

namespace Watcher.SessionService;

// Поля по именам, а не индексам: 4688 v0/v1/v2 имеют разное число полей.
public static class ProcessStartEvent
{
    public static ProcessStart? Parse(string xml)
    {
        var root = XElement.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var stamp = root.Element(ns + "System")?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) return null;
        var data = root.Element(ns + "EventData")?.Elements(ns + "Data")
            .Where(e => e.Attribute("Name") is not null).GroupBy(e => e.Attribute("Name")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Value);
        if (data is null) return null;
        int pid = Pid(data.GetValueOrDefault("NewProcessId"));
        string name = data.GetValueOrDefault("NewProcessName") ?? "";
        if (pid <= 0 || name.Length is 0 or > 1024) return null;
        return new ProcessStart(time, pid, name, Pid(data.GetValueOrDefault("ProcessId")), data.GetValueOrDefault("ParentProcessName") ?? "");
    }

    public static int Pid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return uint.TryParse(hex ? value[2..] : value, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out uint pid) && pid <= int.MaxValue ? (int)pid : 0;
    }
}
