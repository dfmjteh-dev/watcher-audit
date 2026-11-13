namespace Watcher.Server;

// Этап 11 Android (docs/android-plan.md): политики управления устройством-владельцем
// — блокировка/скрытие приложений, пользовательские запреты, запрет мобильного
// интернета приложениям. Политика живёт в конфиг-блобе (ключ androidPolicy,
// см. AgentConfigValidation) и доставляется агенту тем же каналом, что и
// настройки станций (merge группа+хост, versioning, «конфиг применён»).
//
// Защитный список и список разрешённых запретов продублированы в агенте
// (AndroidPolicyLogic.kt) — проверка с двух сторон. При изменении держать
// синхронно; расхождение не опасно (агент — последняя линия), но сбивает с толку.
public static class AndroidPolicy
{
    // Короткие имена запретов в конфиге → ключи UserManager применяет агент.
    // Только то, что имеет смысл для корпоративного телефона и не блокирует связь.
    public static readonly string[] Restrictions =
    [
        "no_install_unknown_sources", // DISALLOW_INSTALL_UNKNOWN_SOURCES
        "no_debugging_features",      // DISALLOW_DEBUGGING_FEATURES (ADB и пр.)
        "no_factory_reset",           // DISALLOW_FACTORY_RESET
        "no_config_vpn",              // DISALLOW_CONFIG_VPN
        "no_config_private_dns",      // DISALLOW_CONFIG_PRIVATE_DNS
        "no_usb_file_transfer",       // DISALLOW_USB_FILE_TRANSFER
    ];

    // Пакеты, которые нельзя блокировать/скрывать: связь, настройки, системный UI,
    // магазин и сам агент. Блокировка любого из них сделала бы телефон
    // «полурабочим» или отрезала бы управление. Сервер их отвергает при
    // сохранении политики; агент дополнительно резолвит домашний экран, звонилку
    // и SMS в рантайме (их пакеты у вендоров разные) и тоже отказывается.
    public static readonly HashSet<string> ProtectedPackages = new(StringComparer.Ordinal)
    {
        "com.watcher.agent",                 // сам агент (защита управления)
        "android",                           // каркас системы
        "com.android.settings",              // настройки
        "com.android.systemui",              // системный UI (статус-бар, навигация)
        "com.android.phone",                 // телефония
        "com.android.dialer",                // звонилка (AOSP)
        "com.google.android.dialer",         // звонилка (GMS)
        "com.android.mms",                   // SMS (AOSP)
        "com.google.android.apps.messaging", // SMS (GMS)
        "com.android.vending",               // Google Play (канал обновлений/магазин)
        "com.google.android.gms",            // сервисы Google (связность, Play Protect)
        "com.google.android.packageinstaller",
        "com.android.packageinstaller",      // установщик пакетов (обновление агента)
    };

    // Этап 12+: режимы записи экрана по согласию (androidPolicy.screenRecord.mode).
    // off — выключено; device — всё устройство (согласие при разблокировке, отказ =
    // блокировка); perApp — по приложению (согласие при запуске, отказ = не открывать).
    public static readonly string[] ScreenRecordModes = ["off", "device", "perApp"];

    public const int MaxPackages = 500;
    public const int MaxPackageLength = 255;

    // Имя Android-пакета (applicationId): сегменты [a-zA-Z0-9_], разделённые точками.
    public static bool IsValidPackage(string s) =>
        s.Length is > 0 and <= MaxPackageLength &&
        System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$");
}
