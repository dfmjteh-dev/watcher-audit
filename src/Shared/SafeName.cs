namespace Watcher;

// Имя хоста / пользователя сессии в имени файла и каталоге: только буквы,
// цифры, «-» и «_» (без разделителей пути и «..»). Тот же набор у агента
// (имя сегмента в очереди) и у сервера (каталог блобов) — один файл на всех
// (src/Shared, подключается ссылкой).
public static class SafeName
{
    // Только допустимые символы; может вернуть пустую строку.
    public static string Strip(string s) =>
        new(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());

    // То же, но пустое — "unknown".
    public static string ForPath(string s) => Strip(s) is { Length: > 0 } safe ? safe : "unknown";
}
