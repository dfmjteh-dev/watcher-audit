using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Watcher.Server;

// --reset-password/--create-user/--set-user-groups — выход до WebApplication.
// CreateBuilder: не поднимаем Kestrel ради однострочной операции с БД (Задача
// 10b, docs/roadmap-fixes.md; --create-user/--set-user-groups — тот же приём
// для Этапа C1, roadmap-next.md). Безопасно гонять параллельно с работающим
// сервисом — Db открывается в WAL с busy_timeout=5000 (Db.cs), конкурентная
// запись не роняет ни эту команду, ни сервер.
if (args.Length > 0 && args[0] == "--reset-password")
{
    Environment.Exit(CliCommands.ResetPassword(args));
}
if (args.Length > 0 && args[0] == "--create-user")
{
    Environment.Exit(CliCommands.CreateUser(args));
}
if (args.Length > 0 && args[0] == "--set-user-groups")
{
    Environment.Exit(CliCommands.SetUserGroups(args));
}
// H5: резервная копия и восстановление (ServerBackup).
if (args.Length > 0 && args[0] == "--backup")
{
    Environment.Exit(CliCommands.Backup(args));
}
if (args.Length > 0 && args[0] == "--restore")
{
    Environment.Exit(await CliCommands.Restore(args));
}
// Установка в один шаг (install-server.sh): токен регистрации и пакеты
// обновлений комплекта агентов — без веб-UI.
if (args.Length > 0 && args[0] == "--create-enroll-token")
{
    Environment.Exit(CliCommands.CreateEnrollToken(args));
}
if (args.Length > 0 && args[0] == "--import-agent-package")
{
    Environment.Exit(await CliCommands.ImportAgentPackage(args));
}
if (args is ["--maintenance-public-key"])
{
    Environment.Exit(CliCommands.MaintenancePublicKey());
}
// Версия сервера — помощник обновления из веб-UI (tools/server-update) сверяет,
// что после установки работает именно новая версия, а не прежняя.
if (args.Length > 0 && args[0] == "--version")
{
    Console.WriteLine(AppVersion.Core(AppVersion.Current));
    return;
}
// H4: Docker HEALTHCHECK — в образе нет curl/wget, ставить их ради этого незачем.
if (args.Length > 0 && args[0] == "--healthcheck")
{
    Environment.Exit(await CliCommands.HealthCheck());
}

var builder = WebApplication.CreateBuilder(args);

var cfg = new ServerConfig();
builder.Configuration.GetSection("Server").Bind(cfg);

// B2 → G1 (roadmap-next.md): до G1 пустой ApiToken означал открытый upload, и
// сервер без него не стартовал. С учётками агентов пустой ApiToken значит
// «общего токена нет, только зарегистрированные агенты» — закрыто, не открыто.
// Открыть без токена можно только явным Server__AllowAnonymousAgents=true (dev).

Directory.CreateDirectory(cfg.DataDir);
Directory.CreateDirectory(Path.Combine(cfg.DataDir, "blobs"));

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = cfg.MaxUploadSizeMb * 1024L * 1024L;
});
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = cfg.MaxUploadSizeMb * 1024L * 1024L;
});

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(new Db(cfg.DbPath));
builder.Services.AddSingleton<RecordingIngest>();
builder.Services.AddSingleton<MaintenanceSigner>();
builder.Services.AddSingleton<AppCatalogCache>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<HostRenameNotices>();
builder.Services.AddSingleton(services =>
{
    var rollout = new UpdateRollout(cfg.MaxConcurrentAgentUpdates);
    ProtectedGroupUpdates.RestoreRollout(services.GetRequiredService<Db>(), rollout, DateTime.UtcNow);
    return rollout;
});
// H1: вход через AD/LDAP; без Server:Ldap:Url — Enabled=false.
builder.Services.AddSingleton<DirectoryAuth>();
builder.Services.AddSingleton(new ServerHealth());
// H4: экспорт журнала доступа в SIEM; без Server:Siem:Host служба сразу выходит.
builder.Services.AddHostedService<SiemForwarder>();
builder.Services.AddHostedService<RetentionService>();
// 0.14.4: заготовки отчётов (ReportFactsService) — один экземпляр: Wake из «Настройки → Программы».
builder.Services.AddSingleton<ReportFactsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportFactsService>());
// Пережатие записей раз в час; нет ffmpeg с AV1 или Server:Transcode:Enabled=false — служба сразу выходит.
builder.Services.AddSingleton<IVideoTranscoder, FfmpegTranscoder>();
builder.Services.AddSingleton<TranscodeState>();
builder.Services.AddHostedService<TranscodeService>();
// 0.13: загрузка сервера для «Обзора» — замер раз в 15 с.
builder.Services.AddSingleton<ServerLoad>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerLoad>());
// H4: встроенные алерты; без каналов в Server:Alerts служба сразу выходит.
builder.Services.AddHttpClient("alerts", c => c.Timeout = TimeSpan.FromSeconds(15));
// Обновление сервера из веб-UI (ServerUpdate.cs): GitHub API и скачивание архива релиза.
builder.Services.AddHttpClient("server-update", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ServerUpdateService>();
builder.Services.AddHostedService<AlertService>();

// A3 (roadmap-next.md): /api/login без ограничений можно перебирать — тайминг-
// защита (DummyHash ниже) есть, лимит попыток нет. По IP — отдельно от
// per-username лимита рядом с самим хендлером (там же логируем неудачи, см.
// A3): этот, по IP, защищает от массового перебора разных логинов с одного
// адреса, который per-username лимит не ловит.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(5),
            PermitLimit = 20,
            QueueLimit = 0,
        }));
    // Задача G1: /api/enroll — перебор токена регистрации. Мягче, чем login:
    // раскатка GPO даёт по запросу с каждой станции, адреса у них разные.
    options.AddPolicy("enroll", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(5),
            PermitLimit = 30,
            QueueLimit = 0,
        }));
    options.OnRejected = async (ctx, ct) =>
    {
        // H4: отказ IP-лимита до хендлера /api/login не доходит — считаем здесь.
        if (ctx.HttpContext.Request.Path.StartsWithSegments("/api/login"))
            ctx.HttpContext.RequestServices.GetRequiredService<ServerHealth>().CountLogin("rate_limited");
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "too many login attempts" }, ct);
    };
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "watcher_auth";
        o.Cookie.HttpOnly = true;
        // A2 (roadmap-next.md): по умолчанию Kestrel не видит Caddy и считает
        // запрос http (CookieSecurePolicy.SameAsRequest без forwarded-заголовка
        // ниже выдавал бы cookie без Secure). CookieSecure=false — только для
        // dev/тестов без TLS впереди (WebApplicationFactory в тестах, systemd
        // fallback без reverse proxy); прод за Caddy его не трогает.
        o.Cookie.SecurePolicy = cfg.CookieSecure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        // Своя AuthGate ниже решает редирект-vs-401 по пути запроса — здесь
        // отключаем дефолтные редиректы cookie middleware на несуществующие
        // /Account/Login, чтобы не конфликтовать с ней.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });

// Этап C1 (roadmap-next.md): role пишется в claim с Фазы 1, но до этой
// задачи ни один эндпоинт её не проверял — любой залогиненный был полным
// админом. "Management" — парк/группы/конфиги, без просмотра записей.
// "Recordings" — видео/консоль, viewer опционально ограничен списком групп
// (ViewerScope.AllowedGroupIds, user_groups в Db.Users.cs) — сама политика роли не
// видит эту доп. фильтрацию, та применяется внутри хендлеров.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Management", p => p.RequireRole("admin", "operator"));
    options.AddPolicy("Recordings", p => p.RequireRole("admin", "viewer"));
    // Этап C2 (roadmap-next.md): управление самими пользователями (создать,
    // сменить роль, отключить, сбросить пароль) — только admin, operator сюда
    // не попадает, хотя у него есть Management на группы/парк.
    options.AddPolicy("AdminOnly", p => p.RequireRole("admin"));
    // H7: выгрузка записей — роль с доступом к записям И отдельное право
    // can_export. Флаг читается из БД на каждый запрос, не из cookie: снятое
    // право действует сразу, без перелогина.
    options.AddPolicy("Export", p => p.RequireRole("admin", "viewer").RequireAssertion(a =>
        a.Resource is HttpContext http &&
        long.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out long uid) &&
        http.RequestServices.GetRequiredService<Db>().UserById(uid) is { CanExport: true }));
    // Этап 11: управление телефонами (редактирование Android-политик) — admin
    // всегда, либо отдельное право can_manage_phones у любой роли. Флаг читается
    // из БД на каждый запрос (снятое право действует сразу).
    options.AddPolicy("PhoneManagement", p => p.RequireAssertion(a =>
        a.Resource is HttpContext http && http.User.Identity?.IsAuthenticated == true && (
            http.User.IsInRole("admin") ||
            (long.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out long uid) &&
             http.RequestServices.GetRequiredService<Db>().UserById(uid) is { CanManagePhones: true }))));
});

var app = builder.Build();

// A2 (roadmap-next.md): Caddy — обратный прокси внутри того же
// docker-compose, порт сервера не published на хост (`expose`, не `ports`
// в docker-compose.yml) — единственный, кто может достучаться до Kestrel
// напрямую, это другой контейнер того же compose-проекта. Доверяем диапазону
// дефолтных Docker-бриджей, а не конкретному IP — он не фиксирован между
// перезапусками compose. Без этого CookieSecurePolicy.Always увидел бы
// внутренний http от Caddy и не выдавал бы cookie вообще.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
forwardedHeadersOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12));
// 0.14.4: systemd-установка — Caddy на той же машине (127.0.0.1). Адрес клиента
// за внешним прокси Caddy уже определил сам (trusted_proxies) и передаёт
// одним значением; свой прокси прямо перед Kestrel — Server__TrustedProxies.
forwardedHeadersOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Loopback, 8));
forwardedHeadersOptions.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
foreach (string cidr in cfg.TrustedProxies.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
{
    if (Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(cidr, out var net)) forwardedHeadersOptions.KnownNetworks.Add(net);
    else app.Logger.LogWarning("Server:TrustedProxies: «{Cidr}» — не адрес/маска, пропущено", cidr);
}
app.UseForwardedHeaders(forwardedHeadersOptions);
app.UseRateLimiter();

app.Logger.LogInformation("Watcher.Server {Version} starting", AppVersion.Current);

// Миграция: старый общий AdminUser/AdminPassword (Basic Auth, Фаза 0) становится
// первым реальным пользователем при первом старте после апдейта — тем же путём
// заводится и самый первый аккаунт на свежей установке, отдельного UI создания
// пользователей в этой фазе нет. Если AdminUser/AdminPassword не заданы и в БД
// нет ни одного пользователя — веб остаётся недоступен (не "открыт", как было
// раньше при пустых Basic Auth кредах): безопаснее по умолчанию.
{
    var db = app.Services.GetRequiredService<Db>();
    if (db.CountUsers() == 0 && !string.IsNullOrEmpty(cfg.AdminUser) && !string.IsNullOrEmpty(cfg.AdminPassword))
    {
        db.InsertUser(cfg.AdminUser, UserAuth.HashPassword(cfg.AdminPassword), "admin");
        app.Logger.LogWarning(
            "Создан пользователь {User} из AdminUser/AdminPassword — теперь это значения конфига можно убрать, они больше не используются для аутентификации",
            cfg.AdminUser);
    }
}

SecurityHeaders.Use(app);
app.UseAuthentication();
AuthGate.Use(app);
// После AuthGate — она уже вернула 401 неаутентифицированным на /api/*, сюда
// долетают только залогиненные; эта проверяет роль и возвращает 403 (через
// OnRedirectToAccessDenied выше) там, где эндпоинт помечен RequireAuthorization.
app.UseAuthorization();

app.UseDefaultFiles();
// Задача E6 (roadmap-next.md), найдено e2e-тестом: без Cache-Control браузер
// кэширует статику эвристически и отдаёт её из кэша, не спрашивая сервер. Для
// UI из ES-модулей без бандлера это значит, что после обновления сервера в
// браузере могут смешаться старый app.js и новые views/*.js; заодно после выхода
// «/» открывался из кэша мимо AuthGate. no-cache — всегда ревалидировать
// (304 по ETag, дёшево), а не «не кэшировать».
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

// R14 (code-review-2026-10.md): эндпоинты — в Endpoints/*.cs, по группам маршрутов;
// политика доступа навешивается на группу, а не на каждый эндпоинт. Контракт
// «эндпоинт → политика» целиком — EndpointPolicyTests.
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapLdapSettingsEndpoints();
app.MapAgentEndpoints();
app.MapDiagnosticEndpoints();
app.MapDiagnosticArchiveEndpoints();
app.MapMaintenanceEndpoints();
app.MapUploadEndpoints();
app.MapEventsEndpoints();
app.MapFleetEndpoints();
app.MapAndroidPolicyEndpoints();
app.MapRecordingEndpoints();
app.MapExportEndpoints();
app.MapStorageEndpoints();
app.MapUpdateEndpoints();
app.MapServerUpdateEndpoints();
app.MapAgentKitEndpoints();
app.MapInventoryEndpoints();
app.MapReportEndpoints();
app.MapReportSourceEndpoints();
app.MapAppKindsEndpoints();
app.MapHealthEndpoints();

app.Run();

// Делает Program доступным для WebApplicationFactory<Program> в тестах —
// top-level statements генерируют internal класс без этого маркера.
public partial class Program { }

namespace Watcher.Server
{
    public sealed record LoginRequest(string Username, string Password);

    // H2: Platform — как агент умеет обновляться (windows-zip / linux-deb /
    // linux-rpm, null — никак), UpdateStatus — итог последней попытки.
    // 0.13: ConfigHash — хэш конфига, который у агента сейчас (получен прошлым
    // heartbeat'ом); Captures — процессы записи экрана и какой конфиг они
    // применили; Losses — журнал потерь (null — агент старше 0.13, счётчик).
    public sealed record HeartbeatRequest(string Host, string? Ip, string? Version, int? DroppedSegments,
        string? Platform = null, string? UpdateStatus = null,
        string? ConfigHash = null, List<CaptureReport>? Captures = null, List<LossReport>? Losses = null,
        ResourceReport? Resources = null,
        // Этап 5 (Android): роль владельца устройства на месте.
        bool? OwnerActive = null,
        // Этап 12 (Android): фактическое состояние удалённой блокировки устройства.
        bool? Locked = null,
        // Этап 12+ (Android): админ снял блокировку локально (офлайн-разблокировка).
        bool? AdminUnlocked = null);
    // 0.13 (PR9б): CPU/память процессов агента и очередь (AgentResources у агента).
    public sealed record ResourceReport(double? CpuPercent, long MemBytes, int Processes, long QueueBytes, int QueueFiles);
    public sealed record GroupAgentVersionRequest(string? Version);

    public sealed record GroupRequest(string Name);
    // H6: срок хранения группы и legal hold.
    public sealed record GroupRetentionRequest(int? RetentionDays);
    public sealed record LegalHoldRequest(string? Host, DateTime? From, DateTime? To, string? Reason);
    public sealed record ReleaseLegalHoldRequest(string? Reason);

    // Задача G1 (roadmap-next.md).
    public sealed record EnrollRequest(string? Token, string? Host, string? Kind);

    // Этап 3 Android-агента (docs/android-plan.md): пачка событий устройства.
    // Detail — непрозрачный для сервера JSON (type-специфичные поля), поэтому
    // JsonElement, не типизированная модель.
    public sealed record AndroidEventsRequest(string? Host, List<AndroidEventDto>? Events);
    public sealed record AndroidEventDto(string? Ts, string? Type, string? Package, JsonElement? Detail, string? EventId);

    public sealed record CreateEnrollTokenRequest(string? Name, long? GroupId, int TtlHours, int? MaxUses);

    public sealed record BulkAssignGroupRequest(List<long> HostIds, long? GroupId);

    public sealed record ConfigRequest(JsonElement Config);

    // 0.13: «Скрыть похожие» из «Консоли» — правило служебного подключения.
    // 0.13 (проверка RC): удалить выбранные записи хоста.
    public sealed record DeleteRecordsRequest(string? Host, List<long>? SegmentIds, List<long>? TerminalIds);

    public sealed record ServiceRuleRequest(string? Scope, string? User, string? From, string? Command, string? Action, string? Note);

    // Этап C2 (roadmap-next.md): #/settings/users.
    public sealed record CreateUserRequest(string Username, string Role, string? Password);

    public sealed record UpdateUserRequest(string? Role, bool? Disabled, bool? CanExport = null, bool? CanManagePhones = null);

    public sealed record ResetUserPasswordRequest(string? Password);

    public sealed record SetUserGroupsRequest(List<long> GroupIds);

    public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

    public readonly record struct UploadMetaFields(
        string Host, DateTime StartedAt, DateTime EndedAt, double DurationSec,
        int Frames, int MotionEvents, string StartReason, string? SessionUser,
        // H9: смены активного окна из sidecar (пусто у агентов до H9).
        List<ActivityEntry> Activity,
        // 0.13: раскладка мониторов (UploadValidation.ParseMonitors), null — нет.
        string? MonitorsJson = null);

    public readonly record struct TerminalMetaFields(
        string Kind, string Host, string SessionUser, string? RemoteAddr,
        DateTime StartedAt, DateTime EndedAt, double DurationSec, int? ExitCode,
        long SessionId, string? Input, string OutFile, string TimingFile, string? Source = null, string? Shell = null,
        // 0.12, выгрузка кусками: Live задан — это кусок (true — сессия ещё идёт,
        // false — последний); без него — запись целиком, как у агентов до 0.12.
        bool? Live = null, long InputOffset = 0, long OutOffset = 0, long TimingOffset = 0, long? AuditSession = null,
        int? Cols = null, int? Rows = null,
        // 0.16: метки транскрипта PowerShell (TerminalOrigins), только известные значения.
        string? Origin = null, string? Mode = null);

    public sealed class ServerConfig
    {
        public string? MaintenanceSigningKeyPath { get; set; }
        public string? MaintenancePreviousSigningKeyPath { get; set; }
        public string? MaintenanceKeyPasswordFile { get; set; }
        public string DataDir { get; set; } = "./server-data";
        public string DbPath { get; set; } = "./server-data/watcher.db";
        public int MaxUploadSizeMb { get; set; } = 512;
        // H4: экспорт журнала доступа в SIEM (syslog CEF/JSON) — см. SiemConfig.
        public SiemConfig Siem { get; set; } = new();
        // H1: вход через AD/LDAP — см. LdapConfig.
        public LdapConfig Ldap { get; set; } = new();
        public string ApiToken { get; set; } = "";
        public string AdminUser { get; set; } = "";
        public string AdminPassword { get; set; } = "";
        public int RetentionDays { get; set; } = 30;
        public long RetentionMaxBytes { get; set; } = 0;
        // R8 (docs/code-review-2026-10.md): ниже этого процента свободного места
        // на томе с DataDir retention удаляет самые старые записи независимо от
        // возраста. 0 — выключено.
        public double MinFreeDiskPercent { get; set; } = 5;
        public int RetentionScanIntervalMinutes { get; set; } = 60;
        // Этап C3 (roadmap-next.md): журнал доступа живёт дольше записей —
        // «кто смотрел» спрашивают и после того, как саму запись уже удалили.
        // 0 = хранить бессрочно. RetentionMaxBytes к журналу не применяется.
        public int AuditRetentionDays { get; set; } = 365;
        // A2 (roadmap-next.md): true в проде (за Caddy, с ForwardedHeaders —
        // см. Program.cs). false только для dev/тестов без TLS впереди.
        public bool CookieSecure { get; set; } = true;
        // B2 (roadmap-next.md): явный opt-in на пустой ApiToken (см. проверку
        // при старте в Program.cs) — без него пустой токен останавливает сервер.
        public bool AllowAnonymousAgents { get; set; } = false;
        // 0.14.4: обратные прокси прямо перед Kestrel (CIDR через пробел/запятую),
        // кроме Caddy из compose и 127.0.0.1 — им верим X-Forwarded-For.
        public string TrustedProxies { get; set; } = "";
        // Задача G1: общий ApiToken на весь парк — переходный путь до регистрации
        // всех агентов (в #/fleet такие хосты помечены). false — только учётки.
        public bool AllowLegacyApiToken { get; set; } = true;
        // H4: Bearer-токен для /metrics (Prometheus). Пусто — эндпоинт выключен (404).
        public string MetricsToken { get; set; } = "";
        // H4: встроенные алерты (webhook, SMTP) — см. AlertConfig.
        public AlertConfig Alerts { get; set; } = new();
        // H7: пределы одной выгрузки (zip собирается на лету, но держит поток
        // запроса и диск на всё время скачивания).
        public int ExportMaxItems { get; set; } = 5000;
        public long ExportMaxMb { get; set; } = 20480;
        // H2: открытый ключ обновлений агентов (base64 SubjectPublicKeyInfo из
        // tools/update-signing/new-update-key.sh). Пусто — пакеты не принимаются.
        public string UpdatePublicKey { get; set; } = "";
        // 0.15.0: сколько агентов одновременно получают пакет обновления (~100 МБ);
        // остальные ждут очереди (UpdateRollout). 0 — без ограничения.
        public int MaxConcurrentAgentUpdates { get; set; } = 10;
        // Каталог комплекта установки агентов (install-server.sh → /opt/watcher-kit,
        // в Docker смонтирован в /kit только на чтение). Пусто — страницы
        // «Установка агентов» нет (404 на /api/agent-kit*).
        public string AgentKitDir { get; set; } = "";
        // Пережатие записей в AV1 на сервере — см. Transcode.cs.
        public TranscodeConfig Transcode { get; set; } = new();
        // Обновление сервера из веб-UI — см. ServerUpdate.cs.
        public ServerUpdateConfig ServerUpdate { get; set; } = new();
    }
}
