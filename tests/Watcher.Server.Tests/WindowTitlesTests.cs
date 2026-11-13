using Xunit;

namespace Watcher.Server.Tests;

// Название страницы браузера и группировка вариантов одного заголовка.
public class WindowTitlesTests
{
    [Theory]
    [InlineData("Watcher и еще 3 страницы — Личный: Microsoft\u200b Edge", "Watcher")]
    [InlineData("Watcher и ещё 12 страниц — Личный: Microsoft Edge", "Watcher")]
    [InlineData("Почта — Входящие — Рабочий: Microsoft\u200b Edge", "Почта — Входящие")]
    [InlineData("Inbox and 2 more pages - Personal - Microsoft\u200b Edge", "Inbox")]
    [InlineData("Foo - Bar - Google Chrome", "Foo - Bar")]
    [InlineData("Документы — Mozilla Firefox", "Документы")]
    [InlineData("Сбербанк Онлайн - Яндекс Браузер", "Сбербанк Онлайн")]
    [InlineData("Номенклатурная группа Напитки - Бухгалтерия", "Номенклатурная группа Напитки - Бухгалтерия")]
    public void Browser_tail_and_tab_count_are_dropped(string title, string expected) =>
        Assert.Equal(expected, WindowTitles.BrowserPage(title));

    [Fact]
    public void Addresses_are_not_merged_by_digits()
    {
        var g = WindowTitles.Group([("10.0.0.2", 2), ("10.0.0.3", 1)], maskDigits: false);
        Assert.Equal(["10.0.0.2", "10.0.0.3"], g.Select(x => x.Display));
    }

    [Theory]
    [InlineData("ТОП ТА и ТО (Revenue) 12.10.2026-18.10.2026 г", "ТОП ТА и ТО (Revenue)")]
    [InlineData("(12) Лиды", "Лиды")]
    [InlineData("8 (938) 000-00-00 - Входящий звонок", "Входящий звонок")]
    public void Constant_fragment_keeps_paired_brackets(string s, string expected) =>
        Assert.Equal(expected, WindowTitles.Display(["a", "b"], s));

    [Fact]
    public void Variants_differing_only_in_digits_are_one_group()
    {
        var g = WindowTitles.Group([
            ("Отель 5* - 15.10.2026 - Управление отелем", 3),
            ("Отель 5* - 16.10.2026 - Управление отелем", 4),
            ("(3) Входящие | Почта", 2),
            ("(12) Входящие | Почта", 1),
            ("Входящие | Почта", 1),   // без счётчика — другой вид, отдельно
            ("План", 1),
        ]);
        // «(N) Входящие | Почта» и просто «Входящие | Почта» — одна подпись → одна группа.
        Assert.Equal(3, g.Count);
        var hotel = g[0];
        // Подпись — общая часть без цифр (есть в заголовке в любой день), пример — частый вариант.
        Assert.Equal(("Управление отелем", "Отель 5* - 16.10.2026 - Управление отелем", 2, 7.0), (hotel.Display, hotel.Example, hotel.Variants, hotel.Weight));
        var mail = g.Single(x => x.Display == "Входящие | Почта");
        Assert.Equal((3, 4.0), (mail.Variants, mail.Weight));
        Assert.Equal("План", g.Single(x => x.Variants == 1).Display);
    }
}
