using Watcher.Server;
using Xunit;

namespace Watcher.Server.Tests;

public class UserAuthTests
{
    [Fact]
    public void VerifyPassword_correct_password_succeeds()
    {
        string hash = UserAuth.HashPassword("correct-horse-battery-staple");

        Assert.True(UserAuth.VerifyPassword(hash, "correct-horse-battery-staple"));
    }

    [Fact]
    public void VerifyPassword_wrong_password_fails()
    {
        string hash = UserAuth.HashPassword("correct-horse-battery-staple");

        Assert.False(UserAuth.VerifyPassword(hash, "wrong-password"));
    }

    [Fact]
    public void HashPassword_does_not_store_plaintext()
    {
        string password = "correct-horse-battery-staple";

        Assert.DoesNotContain(password, UserAuth.HashPassword(password));
    }

    [Fact]
    public void DummyHash_verification_fails_without_throwing()
    {
        // /api/login гоняет VerifyPassword против DummyHash для неизвестного username
        // (защита от timing-сайдчанела, см. /api/login в Endpoints/AuthEndpoints.cs) — он обязан вести себя как обычный
        // неверный пароль, не кидать исключение из-за какого-то особого формата.
        Assert.False(UserAuth.VerifyPassword(UserAuth.DummyHash, "any-password"));
    }
}
