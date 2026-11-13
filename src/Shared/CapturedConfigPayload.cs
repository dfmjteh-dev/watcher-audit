namespace Watcher.Capture;

// Схема config_json от сервера для обоих агентов захвата (src/Shared, подключается
// ссылкой в Watcher.Capture и Watcher.Capture.Linux).
// Поля — то, что сервер (Фаза 4 roadmap) реально может прислать в config_json.
// Nullable, т.к. будущий schemaVersion может не содержать какое-то поле — отсутствующее
// не трогает текущее значение (см. Apply), не сбрасывает его в 0/default.
public sealed record CapturedConfigPayload(
    int? SchemaVersion,
    int? CaptureIntervalMs,
    int? DownscaleWidth,
    int? DownscaleHeight,
    double? MotionThresholdPercent,
    int? MotionThresholdPixelDelta,
    int? OutputFps,
    int? Crf,
    string? Preset,
    // Фаза 5 (roadmap): пер-юзерный allow/exclude список захвата — хранится здесь, в
    // конфиг-блобе сервера, не в отдельном локальном файле (иначе два источника правды
    // по тому, кого писать). AllowUsers заданы и непусты — захват только для них
    // (whitelist), иначе ExcludeUsers заданы и непусты — захват для всех, кроме них
    // (blacklist); если AllowUsers присутствует, он главнее ExcludeUsers. Ни один не
    // задан — захват для всех, как раньше (поведение по умолчанию не меняется).
    List<string>? AllowUsers,
    List<string>? ExcludeUsers,
    // H9: full | process | off — что писать об активном окне (ActivityLog).
    string? Activity = null,
    // H8: когда и при каких окнах не записывать — RecordingPolicy.
    List<ScheduleWindow>? Schedule = null,
    List<string>? ExcludeProcesses = null,
    List<string>? ExcludeTitles = null,
    // 0.13: хэш этого конфига (кладёт Uploader рядом с конфигом сервера) —
    // процесс записи сообщает его в отметке CaptureStatusFile.
    string? ConfigHash = null,
    // 0.13: «Снять с записи» — RecordingPause.
    bool? PauseVideo = null,
    string? PauseUntil = null,
    // 0.13: писать только при этих программах / заголовках — RecordingPolicy.
    List<string>? IncludeProcesses = null,
    List<string>? IncludeTitles = null,
    // 0.14.3: курсор мыши на видео (рисуется после детектора движения).
    bool? ShowCursor = null,
    // 0.14.3: по домену вкладки браузера — RecordingPolicy.
    List<string>? ExcludeSites = null,
    List<string>? IncludeSites = null)
{
    // Ключа нет в конфиге с сервера — берём значение агента по умолчанию (его
    // appsettings на момент запуска), а не оставляем последнее применённое:
    // иначе удалённое на сервере поле продолжало бы действовать до перезапуска.
    // Списки и расписание по умолчанию пусты — у них null и так значит «нет».
    public CapturedConfigPayload WithDefaults(CapturedConfigPayload defaults) => this with
    {
        CaptureIntervalMs = CaptureIntervalMs ?? defaults.CaptureIntervalMs,
        DownscaleWidth = DownscaleWidth ?? defaults.DownscaleWidth,
        DownscaleHeight = DownscaleHeight ?? defaults.DownscaleHeight,
        MotionThresholdPercent = MotionThresholdPercent ?? defaults.MotionThresholdPercent,
        MotionThresholdPixelDelta = MotionThresholdPixelDelta ?? defaults.MotionThresholdPixelDelta,
        OutputFps = OutputFps ?? defaults.OutputFps,
        Crf = Crf ?? defaults.Crf,
        Preset = Preset ?? defaults.Preset,
        Activity = Activity ?? defaults.Activity,
        ShowCursor = ShowCursor ?? defaults.ShowCursor,
    };
}
