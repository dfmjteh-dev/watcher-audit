using System.Text.RegularExpressions;

namespace Watcher.Server;

// Заголовки окон: название страницы браузера и группировка вариантов одного
// и того же. Что брать из заголовка у какой программы — AppKinds.
//
// Браузер пишет в заголовок не только страницу, но и сколько ещё вкладок
// открыто и чей профиль: «Watcher и ещё 3 страницы — Личный: Microsoft Edge».
// Это убираем (BrowserPage). Остальной шум — счётчики «(3) Входящие», рабочая
// дата 1С «… - 17.10.2026 - …», время «12:04 - …» — не перечисляем правилами,
// а группируем: варианты, которые отличаются только цифрами, — одно (Group).
public static partial class WindowTitles
{
    [GeneratedRegex(@"\s+(?:и\s+ещ[её]|and)\s+\d+\s+(?:more\s+)?(?:страниц[аы]?|вкладк[аи]|вкладок|pages?|tabs?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MoreTabs();

    // Хвост «— браузер» после последнего разделителя. Профиль («Личный:»,
    // «Personal -») бывает только у Edge — только перед ним; иначе «Foo - Bar -
    // Google Chrome» потеряла бы «Bar». В названии Edge — невидимый U+200B,
    // у Яндекса — неразрывный пробел и «(Инкогнито)» после названия.
    [GeneratedRegex(@"(?:^|\s+)[—–-]\s+(?:(?:[^—–:\-]{1,40}[—–:\-]\s*)?Microsoft​?\s*Edge|Google\s+Chrome|Chromium|Mozilla\s+Firefox|Firefox|Яндекс\s*Браузер|Yandex(?:\s+Browser)?|Opera|Brave|Vivaldi)(?:\s+\((?:Инкогнито|Incognito|InPrivate)\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserTail();

    // Яндекс Браузер дописывает состояние вкладки: «… вкладка закреплена, группа
    // Почта, профиль Работа». Группа и профиль — только после запятой.
    [GeneratedRegex(@"(?:\s+вкладка закреплена|,\s*(?:группа|профиль)\s+[^,—–]+?)+$", RegexOptions.IgnoreCase)]
    private static partial Regex YandexTabState();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^[\s\-\u2013\u2014|·:;,._#№/+*]+|[\s\-\u2013\u2014|·:;,._#№/+*]+$")]
    private static partial Regex EdgePunctuation();

    public static string BrowserPage(string title)
    {
        string t = BrowserTail().Replace(title.Trim(), "");
        t = MoreTabs().Replace(t, "");
        t = YandexTabState().Replace(t, "");
        return t.Trim();
    }

    // Одно и то же с разными цифрами. Display — у одного варианта он сам, у
    // нескольких — самый длинный кусок без цифр: он же для правила «заголовок
    // содержит» (есть в заголовке в любой день) и не показывает чей-то телефон
    // или дату из одного варианта. Example — самый частый вариант (подсказка).
    // Группы с одинаковым куском («Report_<даты>» и «Report_<даты> (1)») — одна.
    public sealed record SubjectGroup(string Display, string Example, int Variants, double Weight);

    public static List<SubjectGroup> Group(IEnumerable<(string Subject, double Weight)> items, bool maskDigits = true) =>
        items
            .Where(i => !string.IsNullOrWhiteSpace(i.Subject))
            .GroupBy(i => maskDigits ? Key(i.Subject) : i.Subject.ToLowerInvariant())
            .Select(g =>
            {
                var variants = g.GroupBy(i => i.Subject).Select(v => (Text: v.Key, W: v.Sum(x => x.Weight))).ToList();
                return (Display: Display(variants.Select(v => v.Text).ToList(), variants.MaxBy(v => v.W).Text), Variants: variants);
            })
            .GroupBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var variants = g.SelectMany(x => x.Variants).ToList();
                string example = variants.OrderByDescending(v => v.W).ThenBy(v => v.Text, StringComparer.Ordinal).First().Text;
                return new SubjectGroup(g.First().Display, example, variants.Count, variants.Sum(v => v.W));
            })
            .OrderByDescending(s => s.Weight)
            .ThenBy(s => s.Display, StringComparer.Ordinal)
            .ToList();

    // Ключ группы: регистр и цифры не различаем.
    public static string Key(string subject) => Digits().Replace(subject.ToLowerInvariant(), "#");

    public static string Display(IReadOnlyList<string> variants, string mostFrequent) =>
        variants.Count == 1 ? mostFrequent : ConstantFragment(mostFrequent);

    internal static string ConstantFragment(string s)
    {
        string best = Digits().Split(s).Select(TrimPiece).MaxBy(p => p.Length) ?? "";
        return best.Length >= 3 ? best : s;
    }

    // Края куска — без разделителей и непарных скобок: «) Входящие» → «Входящие»,
    // но «ТОП (Revenue)» скобку сохраняет.
    private static string TrimPiece(string p)
    {
        string prev;
        do
        {
            prev = p;
            p = EdgePunctuation().Replace(p, "");
            if (p.StartsWith(')') || p.StartsWith(']')) p = p[1..];
            if (p.EndsWith('(') || p.EndsWith('[')) p = p[..^1];
        } while (p != prev);
        return p;
    }
}
