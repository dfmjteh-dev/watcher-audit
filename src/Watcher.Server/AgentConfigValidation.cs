using System.Text.Json;

namespace Watcher.Server;

// R4 (docs/code-review-2026-10.md): конфиг из веб-UI уходит на все станции
// группы, а агент склеивал preset прямо в командную строку ffmpeg. Сервер
// раньше проверял только «это JSON-объект» — теперь схема: известные ключи,
// типы и диапазоны. Агенты (ConfigReload в обоих агентах захвата) проверяют то
// же самое у себя — защита с двух сторон, пределы общие: AgentConfigLimits
// (src/Shared).
//
// null у ключа допустим и значит то же, что отсутствие ключа: агент оставляет
// своё текущее значение (поля payload'а nullable).
public static class AgentConfigValidation
{
    public static readonly string[] Presets = AgentConfigLimits.Presets;

    public const int MaxUserListLength = 1000;
    public const int MaxUserNameLength = 256;

    private static readonly Dictionary<string, (double Min, double Max, bool Integer)> Numbers = new()
    {
        ["schemaVersion"] = (1, 1, true),
        ["captureIntervalMs"] = (AgentConfigLimits.CaptureIntervalMs.Min, AgentConfigLimits.CaptureIntervalMs.Max, true),
        ["downscaleWidth"] = (AgentConfigLimits.DownscaleWidth.Min, AgentConfigLimits.DownscaleWidth.Max, true),
        ["downscaleHeight"] = (AgentConfigLimits.DownscaleHeight.Min, AgentConfigLimits.DownscaleHeight.Max, true),
        ["motionThresholdPercent"] = (AgentConfigLimits.MotionThresholdPercent.Min, AgentConfigLimits.MotionThresholdPercent.Max, false),
        ["motionThresholdPixelDelta"] = (AgentConfigLimits.MotionThresholdPixelDelta.Min, AgentConfigLimits.MotionThresholdPixelDelta.Max, true),
        ["outputFps"] = (AgentConfigLimits.OutputFps.Min, AgentConfigLimits.OutputFps.Max, true),
        ["crf"] = (AgentConfigLimits.Crf.Min, AgentConfigLimits.Crf.Max, true),
        ["consoleLiveIntervalSec"] = (AgentConfigLimits.ConsoleLiveIntervalSec.Min, AgentConfigLimits.ConsoleLiveIntervalSec.Max, true),
        ["queueMaxAgeDays"] = (AgentConfigLimits.QueueMaxAgeDays.Min, AgentConfigLimits.QueueMaxAgeDays.Max, true),
    };

    private static readonly string[] UserLists = ["allowUsers", "excludeUsers"];

    // H9: что писать об активном окне рядом с видео. full — процесс и заголовок
    // (по умолчанию у агента), process — только процесс, off — ничего.
    public static readonly string[] ActivityModes = ["full", "process", "off"];

    // 0.13: «Снять с записи» — RecordingPause (src/Shared).
    private static readonly string[] PauseKeys = ["pauseVideo", "pauseConsole", "pauseUntil", "pauseReason"];

    // H8: исключённые окна (программы, подстроки заголовков) и расписание записи.
    private static readonly string[] ExclusionLists = ["excludeProcesses", "excludeTitles", "includeProcesses", "includeTitles", "excludeSites", "includeSites"];
    public const int MaxExclusions = 200;
    public const int MaxExclusionLength = 128;
    public const int MaxScheduleWindows = 50;

    // null — конфиг корректен, иначе текст ошибки для 400.
    public static string? Validate(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return "config must be a JSON object";

        foreach (var prop in config.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) continue;

            if (Numbers.TryGetValue(prop.Name, out var range))
            {
                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double v))
                    return $"{prop.Name}: must be a number";
                if (range.Integer && v != Math.Floor(v))
                    return $"{prop.Name}: must be an integer";
                if (v < range.Min || v > range.Max)
                    return $"{prop.Name}: must be in {range.Min}..{range.Max}";
            }
            else if (prop.Name == "preset")
            {
                if (prop.Value.ValueKind != JsonValueKind.String || !Presets.Contains(prop.Value.GetString()))
                    return $"preset: must be one of {string.Join(", ", Presets)}";
            }
            else if (prop.Name == "activity")
            {
                if (prop.Value.ValueKind != JsonValueKind.String || !ActivityModes.Contains(prop.Value.GetString()))
                    return $"activity: must be one of {string.Join(", ", ActivityModes)}";
            }
            else if (ExclusionLists.Contains(prop.Name))
            {
                if (ValidateStringList(prop.Name, prop.Value, MaxExclusions, MaxExclusionLength) is { } err) return err;
            }
            else if (prop.Name is "pauseVideo" or "pauseConsole" or "showCursor")
            {
                if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return $"{prop.Name}: must be true or false";
            }
            else if (prop.Name == "pauseUntil")
            {
                if (prop.Value.ValueKind != JsonValueKind.String || !RecordingPause.TryParseUntil(prop.Value.GetString(), out _))
                    return "pauseUntil: must be a date yyyy-MM-dd";
            }
            else if (prop.Name == "pauseReason")
            {
                if (prop.Value.ValueKind != JsonValueKind.String || prop.Value.GetString()!.Length > RecordingPause.MaxReasonLength || prop.Value.GetString()!.Any(char.IsControl))
                    return $"pauseReason: up to {RecordingPause.MaxReasonLength} characters without control characters";
            }
            else if (prop.Name == "schedule")
            {
                if (ValidateSchedule(prop.Value) is { } err) return err;
            }
            else if (prop.Name == ServiceConnections.Key)
            {
                if (ServiceConnections.Validate(prop.Value) is { } err) return err;
            }
            else if (UserLists.Contains(prop.Name))
            {
                if (ValidateUserList(prop.Name, prop.Value) is { } err) return err;
            }
            else if (prop.Name == "androidPolicy")
            {
                if (ValidateAndroidPolicy(prop.Value) is { } err) return err;
            }
            else
            {
                return $"unknown key '{prop.Name}', expected one of: {string.Join(", ", Numbers.Keys.Concat(UserLists).Concat(ExclusionLists).Append("preset").Append("activity").Append("showCursor").Append("schedule").Concat(PauseKeys).Append(ServiceConnections.Key).Append("androidPolicy"))}";
            }
        }
        return null;
    }

    // Этап 11: политика управления Android (androidPolicy). Списки пакетов —
    // валидный applicationId, не из защитного списка (AndroidPolicy); запреты —
    // из разрешённого набора. Пустой объект/списки — снятие политики.
    private static readonly string[] AndroidPackageLists = ["suspendedPackages", "hiddenPackages", "meteredDataDisabledPackages"];

    private static string? ValidateAndroidPolicy(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return "androidPolicy: must be an object";
        foreach (var prop in value.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) continue;
            if (AndroidPackageLists.Contains(prop.Name) || prop.Name == "kioskPackages")
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    return $"androidPolicy.{prop.Name}: must be an array of package names";
                if (prop.Value.GetArrayLength() > AndroidPolicy.MaxPackages)
                    return $"androidPolicy.{prop.Name}: at most {AndroidPolicy.MaxPackages} packages";
                // kioskPackages — список РАЗРЕШЁННЫХ в киоске приложений (не блокировка),
                // защитный список к нему не применяется.
                bool block = prop.Name is "suspendedPackages" or "hiddenPackages";
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !AndroidPolicy.IsValidPackage(item.GetString()!))
                        return $"androidPolicy.{prop.Name}: invalid package name";
                    if (block && AndroidPolicy.ProtectedPackages.Contains(item.GetString()!))
                        return $"androidPolicy.{prop.Name}: {item.GetString()} is protected and cannot be blocked or hidden";
                }
            }
            else if (prop.Name == "restrictions")
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    return "androidPolicy.restrictions: must be an array";
                foreach (var item in prop.Value.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String || !AndroidPolicy.Restrictions.Contains(item.GetString()))
                        return $"androidPolicy.restrictions: must be from {string.Join(", ", AndroidPolicy.Restrictions)}";
            }
            else if (prop.Name == "screenRecord")
            {
                if (ValidateScreenRecord(prop.Value) is { } err) return err;
            }
            else
            {
                return $"androidPolicy: unknown key '{prop.Name}', expected suspendedPackages, hiddenPackages, meteredDataDisabledPackages, kioskPackages, restrictions, screenRecord";
            }
        }
        return null;
    }

    // Этап 12+: запись экрана по согласию. {mode: off|device|perApp, apps:[...]}.
    // device — всё устройство (баннер согласия при разблокировке; отказ = блокировка),
    // perApp — при запуске приложения из apps (отказ = приложение не открывается).
    private static string? ValidateScreenRecord(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return "androidPolicy.screenRecord: must be an object";
        foreach (var prop in value.EnumerateObject())
        {
            if (prop.Name == "mode")
            {
                if (prop.Value.ValueKind != JsonValueKind.String || !AndroidPolicy.ScreenRecordModes.Contains(prop.Value.GetString()))
                    return $"androidPolicy.screenRecord.mode: must be from {string.Join(", ", AndroidPolicy.ScreenRecordModes)}";
            }
            else if (prop.Name == "apps")
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    return "androidPolicy.screenRecord.apps: must be an array of package names";
                if (prop.Value.GetArrayLength() > AndroidPolicy.MaxPackages)
                    return $"androidPolicy.screenRecord.apps: at most {AndroidPolicy.MaxPackages} packages";
                foreach (var item in prop.Value.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String || !AndroidPolicy.IsValidPackage(item.GetString()!))
                        return "androidPolicy.screenRecord.apps: invalid package name";
            }
            else
            {
                return $"androidPolicy.screenRecord: unknown key '{prop.Name}', expected mode, apps";
            }
        }
        return null;
    }

    private static string? ValidateStringList(string name, JsonElement value, int maxItems, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return $"{name}: must be an array of strings";
        if (value.GetArrayLength() > maxItems)
            return $"{name}: at most {maxItems} entries";
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return $"{name}: must be an array of strings";
            string s = item.GetString()!;
            if (s.Trim().Length == 0 || s.Length > maxLength || s.Any(char.IsControl))
                return $"{name}: entries must be 1..{maxLength} characters without control characters";
        }
        return null;
    }

    // [{"days":[1,2,3,4,5],"from":"09:00","to":"18:00"}] — дни ISO (1 = пн),
    // время станции. from > to — через полночь, from = to = "00:00" — весь день.
    private static string? ValidateSchedule(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return "schedule: must be an array of {days, from, to}";
        if (value.GetArrayLength() > MaxScheduleWindows)
            return $"schedule: at most {MaxScheduleWindows} entries";
        foreach (var w in value.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object)
                return "schedule: must be an array of {days, from, to}";
            foreach (var p in w.EnumerateObject())
                if (p.Name is not ("days" or "from" or "to"))
                    return $"schedule: unknown key '{p.Name}'";
            if (!w.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array || days.GetArrayLength() == 0)
                return "schedule: days must be a non-empty array of 1..7 (1 = Monday)";
            foreach (var d in days.EnumerateArray())
                if (d.ValueKind != JsonValueKind.Number || !d.TryGetInt32(out int day) || day < 1 || day > 7)
                    return "schedule: days must be a non-empty array of 1..7 (1 = Monday)";
            foreach (var key in new[] { "from", "to" })
                if (!w.TryGetProperty(key, out var t) || t.ValueKind != JsonValueKind.String
                    || !TimeSpan.TryParseExact(t.GetString(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var ts)
                    || ts >= TimeSpan.FromDays(1))
                    return $"schedule: {key} must be HH:mm (00:00..23:59)";
        }
        return null;
    }

    private static string? ValidateUserList(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return $"{name}: must be an array of strings";
        if (value.GetArrayLength() > MaxUserListLength)
            return $"{name}: at most {MaxUserListLength} entries";
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return $"{name}: must be an array of strings";
            string s = item.GetString()!;
            if (s.Length == 0 || s.Length > MaxUserNameLength || s.Any(char.IsControl))
                return $"{name}: invalid user name";
        }
        return null;
    }
}
