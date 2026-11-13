using System.Security.Cryptography;
using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class CliCommandsTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _dbPath;

    public CliCommandsTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"watcher-cli-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dataDir, "watcher.db");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("Server__DataDir", _dataDir);
        Environment.SetEnvironmentVariable("Server__DbPath", _dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var key in new[] { "Server__DataDir", "Server__DbPath", "Server__UpdatePublicKey" })
            Environment.SetEnvironmentVariable(key, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public void ResetPassword_with_explicit_password_updates_hash()
    {
        var db = new Db(_dbPath);
        db.InsertUser("admin", UserAuth.HashPassword("old-pass"), "admin");

        int exitCode = CliCommands.ResetPassword(new[] { "--reset-password", "admin", "--password", "new-password-123" });

        Assert.Equal(0, exitCode);
        var user = db.UserByUsername("admin")!;
        Assert.False(UserAuth.VerifyPassword(user.PasswordHash, "old-pass"));
        Assert.True(UserAuth.VerifyPassword(user.PasswordHash, "new-password-123"));
    }

    // A3 (roadmap-next.md): `--reset-password admin --password 1` раньше проходил без возражений.
    [Fact]
    public void ResetPassword_with_short_password_is_rejected()
    {
        var db = new Db(_dbPath);
        db.InsertUser("admin", UserAuth.HashPassword("old-pass"), "admin");

        int exitCode = CliCommands.ResetPassword(new[] { "--reset-password", "admin", "--password", "short1" });

        Assert.Equal(1, exitCode);
        var user = db.UserByUsername("admin")!;
        Assert.True(UserAuth.VerifyPassword(user.PasswordHash, "old-pass"));
    }

    [Fact]
    public void ResetPassword_without_password_generates_one()
    {
        var db = new Db(_dbPath);
        db.InsertUser("admin", UserAuth.HashPassword("old-pass"), "admin");

        int exitCode = CliCommands.ResetPassword(new[] { "--reset-password", "admin" });

        Assert.Equal(0, exitCode);
        var user = db.UserByUsername("admin")!;
        Assert.False(UserAuth.VerifyPassword(user.PasswordHash, "old-pass"));
    }

    [Fact]
    public void ResetPassword_unknown_user_fails_without_creating_one()
    {
        var db = new Db(_dbPath);

        int exitCode = CliCommands.ResetPassword(new[] { "--reset-password", "ghost", "--password", "x" });

        Assert.Equal(1, exitCode);
        Assert.Equal(0, db.CountUsers());
    }

    [Fact]
    public void ResetPassword_without_username_fails()
    {
        int exitCode = CliCommands.ResetPassword(new[] { "--reset-password" });

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void ResetPassword_leaves_other_users_untouched()
    {
        var db = new Db(_dbPath);
        db.InsertUser("admin", UserAuth.HashPassword("admin-pass"), "admin");
        db.InsertUser("viewer", UserAuth.HashPassword("viewer-pass"), "viewer");

        CliCommands.ResetPassword(new[] { "--reset-password", "admin", "--password", "new-admin-pass" });

        var viewer = db.UserByUsername("viewer")!;
        Assert.True(UserAuth.VerifyPassword(viewer.PasswordHash, "viewer-pass"));
    }

    // A6 (roadmap-next.md): --password светит пароль в `ps`/shell-истории —
    // --password-stdin читает его из потока вместо аргумента командной строки.
    [Fact]
    public void ResetPassword_with_password_stdin_reads_from_stdin()
    {
        var db = new Db(_dbPath);
        db.InsertUser("admin", UserAuth.HashPassword("old-pass"), "admin");

        var originalIn = Console.In;
        Console.SetIn(new StringReader("password-from-stdin-123"));
        try
        {
            int exitCode = CliCommands.ResetPassword(new[] { "--reset-password", "admin", "--password-stdin" });
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        var user = db.UserByUsername("admin")!;
        Assert.True(UserAuth.VerifyPassword(user.PasswordHash, "password-from-stdin-123"));
    }

    // Этап C1 (roadmap-next.md): --create-user — стопгэп до UI пользователей (C2),
    // без него роли operator/viewer было бы физически некому проверить вживую.
    [Fact]
    public void CreateUser_with_valid_role_creates_account()
    {
        var db = new Db(_dbPath);

        int exitCode = CliCommands.CreateUser(new[] { "--create-user", "op1", "operator", "--password", "operator-pass-123" });

        Assert.Equal(0, exitCode);
        var user = db.UserByUsername("op1")!;
        Assert.Equal("operator", user.Role);
        Assert.True(UserAuth.VerifyPassword(user.PasswordHash, "operator-pass-123"));
    }

    // Этап C3 (roadmap-next.md): CLI-изменения пользователей — тоже в журнал.
    [Fact]
    public void CreateUser_writes_audit_entry_as_cli()
    {
        var db = new Db(_dbPath);

        CliCommands.CreateUser(new[] { "--create-user", "op-cli", "viewer", "--password", "viewer-pass-12345" });

        var entry = Assert.Single(db.QueryAudit(null, "user.create", null, null, 10, 0));
        Assert.Equal(AuditLog.CliUser, entry.Username);
        Assert.Equal("user:op-cli", entry.Target);
        Assert.DoesNotContain("viewer-pass-12345", entry.Details);
    }

    [Fact]
    public void CreateUser_with_unknown_role_is_rejected()
    {
        var db = new Db(_dbPath);

        int exitCode = CliCommands.CreateUser(new[] { "--create-user", "op1", "superadmin", "--password", "operator-pass-123" });

        Assert.Equal(1, exitCode);
        Assert.Null(db.UserByUsername("op1"));
    }

    [Fact]
    public void CreateUser_duplicate_username_is_rejected()
    {
        var db = new Db(_dbPath);
        db.InsertUser("op1", UserAuth.HashPassword("existing-pass"), "operator");

        int exitCode = CliCommands.CreateUser(new[] { "--create-user", "op1", "viewer", "--password", "new-pass-123456" });

        Assert.Equal(1, exitCode);
        Assert.Equal("operator", db.UserByUsername("op1")!.Role);
    }

    [Fact]
    public void SetUserGroups_assigns_then_clears_restriction()
    {
        var db = new Db(_dbPath);
        db.InsertUser("v1", UserAuth.HashPassword("viewer-pass-123"), "viewer");
        long groupId = db.CreateGroup("group-a");
        long userId = db.UserByUsername("v1")!.Id;

        int exitCode = CliCommands.SetUserGroups(new[] { "--set-user-groups", "v1", groupId.ToString() });
        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { groupId }, db.UserGroupIds(userId));

        exitCode = CliCommands.SetUserGroups(new[] { "--set-user-groups", "v1" });
        Assert.Equal(0, exitCode);
        Assert.Empty(db.UserGroupIds(userId));
    }

    [Fact]
    public void SetUserGroups_unknown_user_fails()
    {
        int exitCode = CliCommands.SetUserGroups(new[] { "--set-user-groups", "ghost", "1" });

        Assert.Equal(1, exitCode);
    }

    // Установка в один шаг: токен регистрации из CLI — stdout только сам токен.
    [Fact]
    public void CreateEnrollToken_prints_token_that_enrolls()
    {
        var db = new Db(_dbPath);
        var stdout = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(stdout);
        int exitCode;
        try { exitCode = CliCommands.CreateEnrollToken(new[] { "--create-enroll-token", "install-kit", "--days", "30" }); }
        finally { Console.SetOut(prev); }

        Assert.Equal(0, exitCode);
        string token = stdout.ToString().Trim();
        Assert.StartsWith("wenr_", token);
        var t = Assert.Single(db.ListEnrollTokens());
        Assert.Equal("install-kit", t.Name);
        var (outcome, cred, _) = db.Enroll(AgentAuth.Hash(token), "pc-1", AgentAuth.KindVideo, AgentAuth.Hash("s"), null, DateTime.UtcNow);
        Assert.Equal(EnrollOutcome.Ok, outcome);
        Assert.NotNull(cred);
    }

    [Theory]
    [InlineData("--days", "0")]
    [InlineData("--days", "400")]
    [InlineData("--max-uses", "0")]
    public void CreateEnrollToken_rejects_out_of_range(string flag, string value)
    {
        int exitCode = CliCommands.CreateEnrollToken(new[] { "--create-enroll-token", "kit", flag, value });

        Assert.Equal(1, exitCode);
        Assert.Empty(new Db(_dbPath).ListEnrollTokens());
    }

    [Fact]
    public async Task ImportAgentPackage_checks_signature_and_is_idempotent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("Server__UpdatePublicKey", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        string file = Path.Combine(_dataDir, "agent.zip");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        string sigFile = Path.Combine(_dataDir, "agent.zip.sig");
        string[] Args(string version) => new[] { "--import-agent-package", "--platform", "windows-msi", "--version", version, "--signature-file", sigFile, file };

        File.WriteAllText(sigFile, Convert.ToBase64String(key.SignData(UpdateSigning.Message("windows-msi", "0.11.0", sha), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
        Assert.Equal(1, await CliCommands.ImportAgentPackage(Args("0.11.1")));   // подпись другой версии
        Assert.Equal(0, await CliCommands.ImportAgentPackage(Args("0.11.0")));
        Assert.Equal(0, await CliCommands.ImportAgentPackage(Args("0.11.0")));   // повтор — тот же файл

        var p = new Db(_dbPath).AgentPackageFor("windows-msi", "0.11.0");
        Assert.NotNull(p);
        Assert.Equal(sha, p!.Sha256);
        Assert.True(File.Exists(Path.Combine(_dataDir, p.FilePath)));

        File.WriteAllBytes(file, new byte[] { 9 });
        Assert.Equal(2, await CliCommands.ImportAgentPackage(Args("0.11.0")));   // та же версия, другой файл
    }
}
