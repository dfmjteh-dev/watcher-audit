using Microsoft.AspNetCore.Identity;

namespace Watcher.Server;

public static class UserAuth
{
    // TUser не используется PasswordHasher<T> для PBKDF2-параметров, только как generic-якорь —
    // string безопасен как заглушка, реального доступа к объекту пользователя хэшер не требует.
    private static readonly PasswordHasher<string> Hasher = new();

    // Используется в /api/login, когда username не найден в БД — так PBKDF2-верификация
    // гоняется всегда, независимо от того, существует ли логин, и время ответа не выдаёт
    // его существование по таймингу (тот же класс бага, что чинили в Фазе 0 для
    // сравнения Basic Auth/ApiToken через FixedTimeEquals, только на уровне хэширования).
    public static readonly string DummyHash = new PasswordHasher<string>().HashPassword("", Guid.NewGuid().ToString());

    public static string HashPassword(string password) => Hasher.HashPassword("", password);

    public static bool VerifyPassword(string passwordHash, string password)
    {
        var result = Hasher.VerifyHashedPassword("", passwordHash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
