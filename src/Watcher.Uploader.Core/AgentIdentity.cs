using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watcher.Uploader;

// Задача G1 (roadmap-next.md): учётка агента. Инсталлятор кладёт в конфиг токен
// регистрации (EnrollToken), агент сам меняет его на собственный секрет через
// POST /api/enroll и дальше ходит только с секретом. Регистрация — здесь, а не в
// инсталляторе: при раскатке через GPO/MSI сети в момент установки может ещё не
// быть, агент просто повторит позже.
//
// Отозванная учётка (401) повторно сама не регистрируется тем же токеном —
// иначе «Отозвать» в веб-UI ничего бы не значило. Новый токен в конфиге
// (переустановка с другим -EnrollToken) — регистрируется заново.
public sealed record StoredCredential(string Secret, string Host, string Kind, string EnrollTokenHash, DateTime EnrolledAt);

// Что нужно для регистрации — общее у Watcher.Uploader (видео) и
// Watcher.TerminalAgent (консоль, Задача G1c), конфиги у них разные.
public sealed record AgentIdentityOptions(string ServerUrl, string EnrollToken, string CredentialPath)
{
    public static AgentIdentityOptions From(UploaderConfig cfg) => new(cfg.ServerUrl, cfg.EnrollToken, cfg.CredentialPath);
}

public sealed class AgentIdentity
{
    public const string KindVideo = "video";
    public const string KindTerminal = "terminal";

    private readonly AgentIdentityOptions _cfg;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _host;
    private readonly string _kind;
    private readonly Func<DateTime> _now;
    private StoredCredential? _cred;
    private bool _rejected;
    private DateTime _nextEnrollAttempt = DateTime.MinValue;
    private bool _revokedLogged;

    public AgentIdentity(UploaderConfig cfg, HttpClient http, ILogger logger, string host, string kind = KindVideo, Func<DateTime>? now = null)
        : this(AgentIdentityOptions.From(cfg), http, logger, host, kind, now) { }

    public AgentIdentity(AgentIdentityOptions cfg, HttpClient http, ILogger logger, string host, string kind, Func<DateTime>? now = null)
    {
        _cfg = cfg;
        _http = http;
        _logger = logger;
        _host = host;
        _kind = kind;
        _now = now ?? (() => DateTime.UtcNow);
        _cred = CredentialStore.Load(cfg.CredentialPath, logger);
        if (_cred is not null && !string.Equals(_cred.Host, host, StringComparison.OrdinalIgnoreCase))
            _logger.LogInformation("Machine renamed {CredHost} -> {Host}: server renames the host on next heartbeat (credential stays)", _cred.Host, host);
    }

    // Чем подписывать запрос: секрет учётки; агент ещё не регистрировался — ничего.
    public string? Bearer => _cred?.Secret;

    public string Kind => _kind;
    public string? MaintenanceBinding => _cred is null ? null : MaintenanceSigning.Binding(_cred.Secret);
    public bool HasCredential => _cred is not null && !_rejected;

    // Сервер ответил 401 на запрос с секретом — учётку отозвали (или БД сервера
    // потеряна). Не на общий токен: там 401 значит неверный ApiToken, как раньше.
    public void OnUnauthorized()
    {
        if (_cred is null || _rejected) return;
        _rejected = true;
        _revokedLogged = false;
        _logger.LogError("Agent credential rejected by server (401) — revoked? Reinstall with a new enroll token (-EnrollToken / --enroll-token)");
    }

    // Зовётся в начале каждого прохода цикла: дёшево, пока регистрироваться не нужно.
    public async Task EnsureEnrolledAsync(CancellationToken ct)
    {
        if (HasCredential) return;
        if (string.IsNullOrWhiteSpace(_cfg.EnrollToken)) return;

        string tokenHash = Hash(_cfg.EnrollToken);
        if (_rejected && _cred is not null && _cred.EnrollTokenHash == tokenHash)
        {
            if (!_revokedLogged)
            {
                _logger.LogError("Credential was rejected and EnrollToken in config is the same one it was issued for — not re-enrolling (revocation must stick). Set a new EnrollToken to register again");
                _revokedLogged = true;
            }
            return;
        }

        if (_now() < _nextEnrollAttempt) return;
        _nextEnrollAttempt = _now() + TimeSpan.FromMinutes(1);

        try
        {
            string url = $"{_cfg.ServerUrl.TrimEnd('/')}/api/enroll";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { token = _cfg.EnrollToken, host = _host, kind = _kind }),
            };
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Токен регистрации отозван, истёк или исчерпан — нового сервер не
                // выдаст, пока админ не создаст токен; не долбим каждую минуту.
                _nextEnrollAttempt = _now() + TimeSpan.FromMinutes(15);
                _logger.LogError("Enrollment refused: enroll token invalid, expired or exhausted — create a new one in web UI (Settings → Agents)");
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Enrollment failed: HTTP {Status}", (int)resp.StatusCode);
                return;
            }
            var body = await resp.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken: ct);
            if (body is null || string.IsNullOrEmpty(body.Secret))
            {
                _logger.LogWarning("Enrollment failed: empty response");
                return;
            }
            var cred = new StoredCredential(body.Secret, body.Host ?? _host, _kind, tokenHash, _now());
            CredentialStore.Save(_cfg.CredentialPath, cred);
            _cred = cred;
            _rejected = false;
            _logger.LogInformation("Enrolled on server as {Host} ({Kind}), credential saved to {Path}", cred.Host, _kind, _cfg.CredentialPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Enrollment error, will retry");
        }
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record EnrollResponse(string? Secret, string? Host, string? Kind);
}

// Файл с секретом читают только SYSTEM/Administrators (Windows: каталог с
// собственным ACL без наследования — ProgramData\Watcher открыт пользователям,
// там их очередь) или root (Linux: 700/600).
public static class CredentialStore
{
    public static StoredCredential? Load(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var c = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(path));
            return c is null || string.IsNullOrEmpty(c.Secret) ? null : c;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent credential {Path} unreadable, ignoring", path);
            return null;
        }
    }

    public static void Save(string path, StoredCredential cred)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        CreateProtectedDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cred));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }

    public static void CreateProtectedDirectory(string dir)
    {
        if (OperatingSystem.IsWindows()) CreateProtectedDirectoryWindows(dir);
        else
        {
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateProtectedDirectoryWindows(string dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), GuardPaths.Enabled && sid == WellKnownSidType.BuiltinAdministratorsSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        if (GuardPaths.Enabled) security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        var di = new DirectoryInfo(dir);
        if (!di.Exists) di.Create(security);
        // Существующий каталог (создан раньше, мог унаследовать права родителя) —
        // права выставляются заново каждый раз, дёшево.
        else di.SetAccessControl(security);
    }
}
