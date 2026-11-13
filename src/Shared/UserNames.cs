namespace Watcher;

// Кого записывать — allowUsers/excludeUsers из конфига группы/хоста. Одна
// сверка на всех: служба Windows (запускать ли запись в сеансе), процессы
// записи экрана (Windows и Linux) и сервер (консоль). Без учёта регистра и
// домена: CORP\a.butakov, a.butakov@corp и a.butakov — один пользователь.
//
// Совместимость: до 0.13 запись экрана на Windows отправляла имя очищенным для
// файлов (SafeName: a.butakov → abutakov), и такие имена уже есть в списках и
// подсказках. Поэтому «abutakov» в списке совпадает и с «a.butakov» (проверка
// RC 0.13: доменные учётки с точкой не записывались вовсе).
public static class UserNames
{
    public static string Short(string user)
    {
        string u = user.Trim();
        int slash = u.LastIndexOf('\\');
        if (slash >= 0) u = u[(slash + 1)..];
        int at = u.IndexOf('@');
        return at > 0 ? u[..at] : u;
    }

    public static bool Matches(string listed, string user)
    {
        if (string.IsNullOrWhiteSpace(listed) || string.IsNullOrWhiteSpace(user)) return false;
        string a = Short(listed), b = Short(user);
        if (string.Equals(listed.Trim(), user.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        string sa = SafeName.Strip(a), sb = SafeName.Strip(b);
        return sa.Length > 0 && string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    // Непустой allow — только они (главнее exclude); иначе непустой exclude — все,
    // кроме них; иначе все.
    public static bool IsAllowed(IReadOnlyCollection<string>? allow, IReadOnlyCollection<string>? exclude, string user)
    {
        if (allow is { Count: > 0 }) return allow.Any(u => Matches(u, user));
        if (exclude is { Count: > 0 }) return !exclude.Any(u => Matches(u, user));
        return true;
    }
}
