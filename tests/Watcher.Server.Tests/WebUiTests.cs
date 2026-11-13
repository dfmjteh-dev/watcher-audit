using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Watcher.Server.Tests;

// Этап E1 (roadmap-next.md): UI разнесён на ES-модули без бандлера, CSP без
// 'unsafe-inline'. Бандлера нет — значит, некому поймать опечатку в пути
// импорта или случайно вернувшийся инлайн-скрипт; эти проверки делают это в CI.
public class WebUiTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;

    public WebUiTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-webui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", Path.Combine(_dataDir, "watcher.db"));
        Environment.SetEnvironmentVariable("Server__AdminUser", "admin");
        Environment.SetEnvironmentVariable("Server__AdminPassword", "test-password-123");
        Environment.SetEnvironmentVariable("Server__CookieSecure", "false");
        Environment.SetEnvironmentVariable("Server__AllowAnonymousAgents", "true");
        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__AdminUser", "Server__AdminPassword", "Server__CookieSecure", "Server__AllowAnonymousAgents" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private HttpClient Anon() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Watcher.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Watcher.Server", "wwwroot");
    }

    [Theory]
    [InlineData("/login.html")]
    [InlineData("/login.js")]
    [InlineData("/login.css")]
    [InlineData("/favicon.svg")]
    [InlineData("/fonts/fonts.css")]
    [InlineData("/fonts/jetbrains-mono-latin-700-normal.woff2")]
    public async Task Login_assets_are_public_and_carry_strict_csp(string path)
    {
        using var client = Anon();
        using var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string csp = resp.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("/app.js")]
    [InlineData("/views/users.js")]
    [InlineData("/vendor/preact.module.js")]
    [InlineData("/fonts/../app.js")]
    [InlineData("/fonts%2F..%2Fapp.js")]
    [InlineData("/styles.css")]
    public async Task App_modules_require_login(string path)
    {
        using var client = Anon();
        using var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/login.html", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Logged_in_user_gets_modules_as_javascript()
    {
        using var client = Anon();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123" })).Dispose();
        using var resp = await client.GetAsync("/app.js");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // <script type="module"> отказывается исполнять не-JS MIME (nosniff тоже).
        Assert.Equal("text/javascript", resp.Content.Headers.ContentType?.MediaType);
        // Ревалидация на каждый запрос — иначе после обновления сервера модули
        // разных версий смешиваются из кэша браузера (E6).
        Assert.True(resp.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public void Html_has_no_inline_scripts_or_styles()
    {
        foreach (string file in Directory.GetFiles(WwwRoot(), "*.html", SearchOption.AllDirectories))
        {
            string src = Regex.Replace(File.ReadAllText(file), @"<!--.*?-->", "", RegexOptions.Singleline);
            Assert.False(Regex.IsMatch(src, @"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase), $"inline <script> in {file}");
            Assert.False(Regex.IsMatch(src, @"<style\b", RegexOptions.IgnoreCase), $"inline <style> in {file}");
            Assert.False(Regex.IsMatch(src, @"\sstyle\s*=", RegexOptions.IgnoreCase), $"style= attribute in {file}");
            Assert.False(Regex.IsMatch(src, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase), $"inline event handler in {file}");
        }
    }

    // A1: данные от агента/URL уходят в DOM только через Preact (экранирует сам).
    [Fact]
    public void App_code_never_writes_raw_html()
    {
        foreach (string file in AppScripts())
        {
            string src = StripJsComments(File.ReadAllText(file));
            Assert.DoesNotContain("innerHTML", src);
            Assert.DoesNotContain("dangerouslySetInnerHTML", src);
            Assert.DoesNotContain("insertAdjacentHTML", src);
            Assert.DoesNotContain("document.write", src);
        }
    }

    [Fact]
    public void Every_relative_import_resolves_to_a_file()
    {
        string root = WwwRoot();
        var importRe = new Regex(@"(?:import|export)\s[^;]*?from\s*['""](\.{1,2}/[^'""]+)['""]");
        int checkedImports = 0;
        foreach (string file in Directory.GetFiles(root, "*.js", SearchOption.AllDirectories))
        {
            foreach (Match m in importRe.Matches(File.ReadAllText(file)))
            {
                string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, m.Groups[1].Value));
                Assert.True(File.Exists(target), $"{Path.GetRelativePath(root, file)} imports missing {m.Groups[1].Value}");
                checkedImports++;
            }
        }
        Assert.True(checkedImports > 10, "import regex found suspiciously few imports");
    }

    // E4: xterm.js из npm вставляет <style> и style="…" — при CSP без
    // 'unsafe-inline' браузер их режет, терминал рисуется без размеров/цветов.
    // В vendor/ лежит копия с механической правкой (vendor/README.md); тест
    // ловит обновление xterm, после которого правку забыли повторить.
    [Fact]
    public void Vendored_xterm_is_patched_for_strict_csp()
    {
        string src = File.ReadAllText(Path.Combine(WwwRoot(), "vendor", "xterm.module.js"));
        Assert.DoesNotContain("createElement(\"style\")", src);
        Assert.DoesNotContain("setAttribute(\"style\"", src);
        Assert.Contains("__watcherCspStyle(", src);
    }

    // Комментарии в коде объясняют, почему innerHTML больше нет, — их не считаем.
    private static string StripJsComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"(^|\s)//.*$", "", RegexOptions.Multiline);

    private static IEnumerable<string> AppScripts() =>
        Directory.GetFiles(WwwRoot(), "*.js", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "vendor" + Path.DirectorySeparatorChar));
}
