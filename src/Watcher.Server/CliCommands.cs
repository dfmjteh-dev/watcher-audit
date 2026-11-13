using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Watcher.Server;

public static class CliCommands
{
    // Для сборщика комплекта: только публичный SPKI активного ключа, без БД и Kestrel.
    public static int MaintenancePublicKey()
    {
        using var key = new MaintenanceSigner(LoadConfig()).Key();
        if (key is null) { Console.Error.WriteLine("Maintenance signing key unavailable"); return 1; }
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())); return 0;
    }

    // internal, не private — Этап C2 (roadmap-next.md) переиспользует их в
    // /api/users* (Endpoints/UserEndpoints.cs): одна политика паролей/ролей для CLI и веб-UI,
    // не две рассинхронизирующиеся копии.
    internal const int MinPasswordLength = 12;
    internal static readonly string[] ValidRoles = { "admin", "operator", "viewer" };

    public static int ResetPassword(string[] args)
    {
        string? username = null;
        string? password = null;
        bool passwordFromStdin = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--password" && i + 1 < args.Length)
            {
                password = args[++i];
            }
            // A6 (roadmap-next.md): --password светит пароль в `ps`/shell-истории
            // — --password-stdin читает его из потока, которого там не видно.
            else if (args[i] == "--password-stdin")
            {
                password = Console.In.ReadLine();
                passwordFromStdin = true;
            }
            else if (username is null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                username = args[i];
            }
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Error.WriteLine("Использование: Watcher.Server --reset-password <username> [--password <пароль> | --password-stdin]");
            Console.Error.WriteLine("Без --password/--password-stdin генерируется случайный и печатается один раз.");
            Console.Error.WriteLine("--password виден в `ps`/истории команд — предпочитать --password-stdin.");
            return 1;
        }

        if (passwordFromStdin && string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Пустой пароль со stdin.");
            return 1;
        }

        var cfg = LoadConfig();
        Directory.CreateDirectory(cfg.DataDir);
        var db = new Db(cfg.DbPath);
        // H1: у пользователя каталога пароля в БД нет и вход по нему не проверяется.
        if (db.UserByUsername(username!) is { IsLdap: true })
        {
            Console.Error.WriteLine($"'{username}' — пользователь каталога (AD/LDAP): пароль меняется в каталоге, не здесь.");
            return 1;
        }

        bool generated = password is null;
        password ??= GenerateRandomPassword();

        // A3 (roadmap-next.md): требований к паролю не было вообще —
        // `--reset-password admin --password 1` проходил. Не трогает
        // сгенерированный случайный (32 hex-символа, всегда длиннее).
        if (password.Length < MinPasswordLength)
        {
            Console.Error.WriteLine($"Пароль короче {MinPasswordLength} символов.");
            return 1;
        }

        if (!db.SetUserPassword(username, UserAuth.HashPassword(password)))
        {
            Console.Error.WriteLine($"Пользователь '{username}' не найден в {cfg.DbPath}.");
            return 1;
        }

        // Этап C3 (roadmap-next.md): CLI — тоже изменение пользователей, в журнал
        // наравне с веб-UI. Пользователь — "cli" (кто именно запускал, видно только
        // по shell-истории/sudo-логу хоста), IP нет.
        new AuditLog(db).Write(AuditLog.CliUser, null, "user.reset-password", $"user:{username}", new { generated });
        Console.WriteLine($"Пароль пользователя '{username}' изменён.");
        if (generated)
        {
            Console.WriteLine($">>> НОВЫЙ ПАРОЛЬ: {password}  <<<");
        }
        return 0;
    }

    // Этап C1 (roadmap-next.md): роли есть в схеме с Фазы 1, но единственный
    // способ завести пользователя был неявный bootstrap AdminUser/AdminPassword
    // (всегда role=admin). Без этой команды роли operator/viewer нельзя было
    // бы вообще ни на ком проверить — стопгэп до полноценного UI (Задача C2),
    // тот же приём, что --reset-password был стопгэпом до него же для паролей.
    public static int CreateUser(string[] args)
    {
        string? username = null;
        string? role = null;
        string? password = null;
        bool passwordFromStdin = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--password" && i + 1 < args.Length)
            {
                password = args[++i];
            }
            else if (args[i] == "--password-stdin")
            {
                password = Console.In.ReadLine();
                passwordFromStdin = true;
            }
            else if (username is null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                username = args[i];
            }
            else if (role is null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                role = args[i];
            }
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(role))
        {
            Console.Error.WriteLine("Использование: Watcher.Server --create-user <username> <admin|operator|viewer> [--password <пароль> | --password-stdin]");
            Console.Error.WriteLine("Без --password/--password-stdin генерируется случайный и печатается один раз.");
            return 1;
        }

        if (!ValidRoles.Contains(role))
        {
            Console.Error.WriteLine($"Неизвестная роль '{role}'. Допустимые: {string.Join(", ", ValidRoles)}.");
            return 1;
        }

        if (passwordFromStdin && string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Пустой пароль со stdin.");
            return 1;
        }

        var cfg = LoadConfig();
        Directory.CreateDirectory(cfg.DataDir);
        var db = new Db(cfg.DbPath);

        if (db.UserByUsername(username) is not null)
        {
            Console.Error.WriteLine($"Пользователь '{username}' уже существует — используйте --reset-password для смены пароля.");
            return 1;
        }

        bool generated = password is null;
        password ??= GenerateRandomPassword();
        if (password.Length < MinPasswordLength)
        {
            Console.Error.WriteLine($"Пароль короче {MinPasswordLength} символов.");
            return 1;
        }

        db.InsertUser(username, UserAuth.HashPassword(password), role);
        new AuditLog(db).Write(AuditLog.CliUser, null, "user.create", $"user:{username}", new { role, generated });
        Console.WriteLine($"Пользователь '{username}' создан с ролью '{role}'.");
        if (generated)
        {
            Console.WriteLine($">>> НОВЫЙ ПАРОЛЬ: {password}  <<<");
        }
        return 0;
    }

    // Этап C1 (roadmap-next.md): опциональное ограничение viewer'а списком
    // групп — без аргументов групп снимает ограничение (видит всё), иначе
    // полная замена набора (не инкремент). Применимо к любой роли технически,
    // но осмыслено только для viewer (admin/operator его не проверяют).
    public static int SetUserGroups(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: Watcher.Server --set-user-groups <username> [groupId ...]");
            Console.Error.WriteLine("Без groupId снимает ограничение — пользователь видит записи всех групп.");
            return 1;
        }

        string username = args[1];
        var groupIds = new List<long>();
        for (int i = 2; i < args.Length; i++)
        {
            if (!long.TryParse(args[i], out long gid))
            {
                Console.Error.WriteLine($"'{args[i]}' не похож на numeric id группы.");
                return 1;
            }
            groupIds.Add(gid);
        }

        var cfg = LoadConfig();
        Directory.CreateDirectory(cfg.DataDir);
        var db = new Db(cfg.DbPath);

        var user = db.UserByUsername(username);
        if (user is null)
        {
            Console.Error.WriteLine($"Пользователь '{username}' не найден в {cfg.DbPath}.");
            return 1;
        }

        db.SetUserGroups(user.Id, groupIds);
        new AuditLog(db).Write(AuditLog.CliUser, null, "user.groups", $"user:{username}", new { groupIds });
        Console.WriteLine(groupIds.Count == 0
            ? $"Ограничение групп для '{username}' снято — видит записи всех хостов."
            : $"'{username}' теперь ограничен группами: {string.Join(", ", groupIds)}.");
        return 0;
    }

    // H5: Watcher.Server --backup <каталог> [--include-blobs]. Можно на работающем
    // сервере. См. ServerBackup и docs/operations.md, «Резервная копия».
    public static int Backup(string[] args)
    {
        string? outDir = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (outDir is null)
        {
            Console.Error.WriteLine("Использование: Watcher.Server --backup <каталог> [--include-blobs]");
            Console.Error.WriteLine("Создаёт <каталог>/watcher-backup-<время>/: снимок БД, список файлов записей (blobs.jsonl), manifest.json.");
            Console.Error.WriteLine("--include-blobs — скопировать и сами файлы записей (видео, вывод консоли).");
            return 1;
        }
        try
        {
            ServerBackup.Backup(LoadConfig(), outDir, args.Contains("--include-blobs"), Console.WriteLine);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SqliteException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    // H5: Watcher.Server --restore <каталог копии> [--force]. Только на
    // остановленном сервере: отвечает /healthz на его адресе — отказ.
    public static async Task<int> Restore(string[] args)
    {
        string? backupDir = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (backupDir is null)
        {
            Console.Error.WriteLine("Использование: Watcher.Server --restore <каталог watcher-backup-…> [--force]");
            Console.Error.WriteLine("Сервер должен быть остановлен. Текущая БД откладывается рядом (.pre-restore-<время>), не удаляется.");
            return 1;
        }
        if (!args.Contains("--force") && await ServerIsRunning())
        {
            Console.Error.WriteLine("Сервер запущен (отвечает /healthz) — остановите его перед восстановлением. --force — пропустить проверку.");
            return 1;
        }
        try
        {
            ServerBackup.Restore(LoadConfig(), backupDir, Console.WriteLine);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SqliteException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    // Установка в один шаг (install-server.sh): токен регистрации для комплекта
    // агентов без веб-UI. Печатает только сам токен в stdout — скрипт берёт его
    // в переменную; всё остальное — в stderr.
    //   --create-enroll-token <имя> [--days N] [--max-uses N] [--group <id>]
    public static int CreateEnrollToken(string[] args)
    {
        string? name = null;
        int days = 365;
        int? maxUses = null;
        long? groupId = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--days" && i + 1 < args.Length && int.TryParse(args[++i], out int d)) days = d;
            else if (args[i] == "--max-uses" && i + 1 < args.Length && int.TryParse(args[++i], out int m)) maxUses = m;
            else if (args[i] == "--group" && i + 1 < args.Length && long.TryParse(args[++i], out long g)) groupId = g;
            else if (name is null && !args[i].StartsWith("--", StringComparison.Ordinal)) name = args[i].Trim();
            else
            {
                Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                return 1;
            }
        }
        if (string.IsNullOrEmpty(name) || !UploadValidation.IsSafeMetaField(name))
        {
            Console.Error.WriteLine("Использование: --create-enroll-token <имя> [--days N] [--max-uses N] [--group <id>]");
            return 1;
        }
        // Те же пределы, что у веб-UI (AgentEndpoints, /api/agents/enroll-tokens).
        if (days is < 1 or > 365 || maxUses is < 1)
        {
            Console.Error.WriteLine("--days: 1..365, --max-uses: положительное число.");
            return 1;
        }
        var cfg = LoadConfig();
        var db = new Db(cfg.DbPath);
        if (groupId is { } gid && !db.ListGroups().Any(gr => gr.Id == gid))
        {
            Console.Error.WriteLine($"Группы {gid} нет.");
            return 1;
        }
        string token = AgentAuth.NewEnrollToken();
        var expiresAt = DateTime.UtcNow.AddDays(days);
        long id = db.InsertEnrollToken(name, AgentAuth.Hash(token), groupId, expiresAt, maxUses, AuditLog.CliUser);
        new AuditLog(db).Write(AuditLog.CliUser, null, "enroll-token.create", $"enroll-token:{name}",
            new { id, groupId, ttlHours = days * 24, maxUses });
        Console.Error.WriteLine($"Токен регистрации «{name}» действует до {expiresAt:yyyy-MM-dd} UTC.");
        Console.WriteLine(token);
        return 0;
    }

    // H2 + установка в один шаг: пакет обновления агента из файла — то же, что
    // «Загрузить пакет» в веб-UI. Пакет этой версии уже есть с тем же SHA-256 —
    // успех (повторный запуск install-server.sh), с другим — ошибка.
    //   --import-agent-package --platform P --version X.Y.Z --signature-file F <файл>
    public static async Task<int> ImportAgentPackage(string[] args)
    {
        string? platform = null, version = null, sigFile = null, file = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--platform" && i + 1 < args.Length) platform = args[++i];
            else if (args[i] == "--version" && i + 1 < args.Length) version = args[++i];
            else if (args[i] == "--signature-file" && i + 1 < args.Length) sigFile = args[++i];
            else if (file is null && !args[i].StartsWith("--", StringComparison.Ordinal)) file = args[i];
            else
            {
                Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                return 1;
            }
        }
        if (platform is null || version is null || sigFile is null || file is null || !File.Exists(file) || !File.Exists(sigFile))
        {
            Console.Error.WriteLine("Использование: --import-agent-package --platform P --version X.Y.Z --signature-file F <файл>");
            return 1;
        }
        var cfg = LoadConfig();
        var db = new Db(cfg.DbPath);
        string signature = (await File.ReadAllTextAsync(sigFile)).Trim();
        AgentPackageStore.Result r;
        await using (var src = File.OpenRead(file))
            r = await AgentPackageStore.ImportAsync(db, cfg, platform, version, signature, src, AuditLog.CliUser);
        switch (r.Outcome)
        {
            case AgentPackageStore.Outcome.Ok:
                new AuditLog(db).Write(AuditLog.CliUser, null, "agent-package.upload", $"agent-package:{platform}/{version}",
                    new { id = r.Id, sha256 = r.Sha256, size = r.Size });
                Console.WriteLine($"Пакет {platform} {version} загружен (id {r.Id}).");
                return 0;
            case AgentPackageStore.Outcome.Exists:
                string sha;
                await using (var fs = File.OpenRead(file))
                    sha = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (db.AgentPackageFor(platform, version)?.Sha256 == sha)
                {
                    Console.WriteLine($"Пакет {platform} {version} уже загружен.");
                    return 0;
                }
                Console.Error.WriteLine($"Пакет {platform} {version} уже загружен с другим содержимым — удалите его в веб-UI (Обновления) или поднимите версию.");
                return 2;
            case AgentPackageStore.Outcome.NoPublicKey:
                Console.Error.WriteLine("Server:UpdatePublicKey не задан — пакеты не принимаются.");
                return 1;
            case AgentPackageStore.Outcome.BadSignature:
                Console.Error.WriteLine("Подпись не сходится с файлом, платформой и версией.");
                return 1;
            default:
                Console.Error.WriteLine($"Пакет не принят: {r.Outcome}.");
                return 1;
        }
    }

    private static async Task<bool> ServerIsRunning()
    {
        var cfgRoot = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync(HealthCheckUrl(cfgRoot["Kestrel:Endpoints:Http:Url"] ?? cfgRoot["ASPNETCORE_URLS"]));
            return true;   // ответил хоть как-то — значит, слушает
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // H4: Docker HEALTHCHECK / systemd — GET /healthz на свой же Kestrel. Адрес —
    // из того же Kestrel:Endpoints:Http:Url (appsettings + env), что слушает
    // сервер; 0.0.0.0/+/* заменяется на 127.0.0.1. 0 — здоров, 1 — нет.
    public static async Task<int> HealthCheck()
    {
        var cfgRoot = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        string url = HealthCheckUrl(cfgRoot["Kestrel:Endpoints:Http:Url"] ?? cfgRoot["ASPNETCORE_URLS"]);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync(url);
            Console.WriteLine($"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"{url}: {ex.Message}");
            return 1;
        }
    }

    public static string HealthCheckUrl(string? listenUrl)
    {
        string first = (listenUrl ?? "http://127.0.0.1:5050").Split(';')[0].Trim();
        var uri = new Uri(first.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1").Replace("://0.0.0.0", "://127.0.0.1").Replace("://[::]", "://127.0.0.1"));
        return new UriBuilder(uri) { Path = "/healthz" }.Uri.ToString();
    }

    private static ServerConfig LoadConfig()
    {
        var cfgRoot = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cfg = new ServerConfig();
        cfgRoot.GetSection("Server").Bind(cfg);
        return cfg;
    }

    internal static string GenerateRandomPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
