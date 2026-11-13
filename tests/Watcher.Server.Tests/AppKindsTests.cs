using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Watcher.Server.Tests;

// 0.14.3: тип программы → что из окна идёт в отчёт и подсказки (синтетические
// примеры форматов, найденных на заголовках прода).
public class AppKindsTests
{
    private static string? S(AppKind k, string? title, string? site = null, string process = "p", bool rule = false) =>
        AppKinds.Subject(k, process, title, site, rule);

    [Fact]
    public void Defaults_overrides_and_unknown_process()
    {
        var map = new AppKinds.Map(new Dictionary<string, AppKind> { ["1cv8c"] = AppKind.Title, ["chrome"] = AppKind.App });
        Assert.Equal(AppKind.Browser, map.KindOf("msedge"));
        Assert.Equal(AppKind.Browser, map.KindOf("MSEDGE"));
        Assert.Equal(AppKind.App, map.KindOf("chrome"));        // правка админа главнее стандарта
        Assert.Equal(AppKind.Title, map.KindOf("1cv8c"));
        Assert.Equal(AppKind.App, map.KindOf("some-erp"));      // не перечисленное — только процесс
        Assert.Equal(AppKind.Ignore, map.KindOf("rdpinit"));
        Assert.Equal(AppKind.App, map.KindOf(null));
    }

    // 0.14.3: агент прислал домен — браузер, даже незнакомый; правка админа главнее.
    [Fact]
    public void Site_makes_unknown_process_a_browser_unless_overridden()
    {
        var map = new AppKinds.Map(new Dictionary<string, AppKind> { ["kiosk"] = AppKind.App });
        Assert.Equal(AppKind.Browser, map.KindOf("arc", hasSite: true));
        Assert.Equal(AppKind.App, map.KindOf("arc", hasSite: false));
        Assert.Equal(AppKind.App, map.KindOf("kiosk", hasSite: true));
    }

    [Fact]
    public void App_uses_process_and_ignore_nothing()
    {
        Assert.Equal("1cv8c", S(AppKind.App, "Отель - 17.10.2026 - Управление", process: "1cv8c"));
        Assert.Null(S(AppKind.Ignore, "RemoteApp Marker Window"));
    }

    // Название страницы — только для правил «заголовок содержит» и подсказок к ним.
    [Theory]
    [InlineData("Watcher и еще 3 страницы — Личный: Microsoft​ Edge", "Watcher")]
    [InlineData("TeamJet вкладка закреплена — Яндекс Браузер", "TeamJet")]
    [InlineData("Отчёт.xlsx - Яндекс Документы вкладка закреплена, группа группа 1, профиль Работа — Яндекс Браузер", "Отчёт.xlsx - Яндекс Документы")]
    [InlineData("Почта — Яндекс Браузер (Инкогнито)", "Почта")]
    [InlineData("Foo - Bar - Google Chrome", "Foo - Bar")]
    public void Browser_page_for_title_rules(string title, string expected) =>
        Assert.Equal(expected, S(AppKind.Browser, title, null, rule: true));

    // 0.14.4: в отчёте у браузера — только домен; без домена — не предмет.
    [Theory]
    [InlineData("(3) Входящие - Google Chrome", "mail.example.com", "mail.example.com")]
    [InlineData("2 непрочитанных чата - Google Chrome", null, null)]
    public void Browser_subject_is_site_only(string title, string? site, string? expected) =>
        Assert.Equal(expected, S(AppKind.Browser, title, site));

    [Fact]
    public void Title_rule_uses_page_even_when_site_is_known() =>
        Assert.Equal("Входящие", S(AppKind.Browser, "Входящие - Google Chrome", "mail.example.com", rule: true));

    [Theory]
    [InlineData("srv01 — Подключение к удаленному рабочему столу", "srv01")]
    [InlineData("10.0.0.5 - Remote Desktop Connection", "10.0.0.5")]
    [InlineData("Бэк Офис (rds.corp.local)", "rds.corp.local")]
    [InlineData("Подключение к удаленному рабочему столу", null)]      // диалог mstsc — без адреса
    [InlineData("Отель - Размещ. Иванов Иван (RU), 350", null)]         // окно внутри RemoteApp — данные программы
    public void Rdp_target(string title, string? expected) => Assert.Equal(expected, S(AppKind.Rdp, title));

    [Theory]
    [InlineData("Отчет за месяц  [Только для чтения] - Excel (Сбой активации продукта)", "Отчет за месяц")]
    [InlineData("Microsoft Excel (Сбой активации продукта) - План 2026", "План 2026")]
    [InlineData("Договор - копия - Word", "Договор - копия")]
    [InlineData("Смета.ods - LibreOffice Calc", "Смета.ods")]
    [InlineData("План - Excel (Не отвечает)", "План")]
    [InlineData("Отзыв.docx — LibreOffice Writer", "Отзыв.docx")]
    [InlineData("Открытие", null)]                 // диалог программы — не документ
    [InlineData("Microsoft Excel", null)]
    [InlineData("Найти и заменить", null)]
    public void Document_name(string title, string? expected) => Assert.Equal(expected, S(AppKind.Document, title));

    [Fact]
    public void Overrides_json_skips_unknown_kinds_and_bad_names()
    {
        var o = AppKinds.ParseOverrides("""{"Chrome":"app","x":"nonsense","":"browser","erp":"title"}""")!;
        Assert.Equal(2, o.Count);
        Assert.Equal(AppKind.App, o["chrome"]);
        Assert.Equal(AppKind.Title, o["erp"]);
        Assert.Null(AppKinds.ParseOverrides("not json"));
    }
}

// «Настройки → Программы»: только admin, хранятся только правки, в журнал.
public class AppKindsEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Db _db;

    public AppKindsEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-appkinds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        _factory = new WebApplicationFactory<Program>();
        _db = _factory.Services.GetRequiredService<Db>();
        _admin = Login("admin", "test-password-123");
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private HttpClient Login(string username, string password)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        c.PostAsJsonAsync("/api/login", new { username, password }).GetAwaiter().GetResult().Dispose();
        return c;
    }

    [Fact]
    public async Task Admin_saves_only_real_changes_and_others_are_forbidden()
    {
        using (var put = await _admin.PutAsJsonAsync("/api/settings/app-kinds", new
        {
            overrides = new Dictionary<string, string> { ["chrome"] = "app", ["msedge"] = "browser", ["erp"] = "title", ["notepad"] = "app" },
        }))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var got = JsonDocument.Parse(await _admin.GetStringAsync("/api/settings/app-kinds")).RootElement;
        var overrides = got.GetProperty("overrides").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        // msedge = стандарт, не хранится; notepad стандартно «документ» → «программа» — правка.
        Assert.Equal(new Dictionary<string, string?> { ["chrome"] = "app", ["erp"] = "title", ["notepad"] = "app" }, overrides);
        Assert.Equal("browser", got.GetProperty("defaults").GetProperty("msedge").GetString());
        Assert.Contains(_db.QueryAudit(null, "app-kinds.set", null, null, 10, 0), e => e.Username == "admin");

        using (var bad = await _admin.PutAsJsonAsync("/api/settings/app-kinds", new { overrides = new Dictionary<string, string> { ["x"] = "nope" } }))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        _db.InsertUser("op1", UserAuth.HashPassword("password-123456"), "operator");
        using var op = Login("op1", "password-123456");
        using var denied = await op.GetAsync("/api/settings/app-kinds");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
