using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Акаунти: нік один на людину, гість — із приставкою, пароль — не в чистому вигляді.</summary>
public class AccountsTests
{
    static readonly SiteOptions Site = new();

    [Fact]
    public void A_nick_is_taken_once_regardless_of_case()
    {
        using var t = new TempDb();
        var hash = Auth.HashPassword("секрет123", out var salt);

        Assert.True(t.Db.AddAccount("Влад", hash, salt));
        Assert.False(t.Db.AddAccount("влад", hash, salt));
        Assert.False(t.Db.AddAccount("  ВЛАД ", hash, salt));

        // Знаходиться як завгодно написаний, а віддається так, як зареєстрували.
        Assert.Equal("Влад", t.Db.FindAccount("вЛаД")!.Nick);
        Assert.Null(t.Db.FindAccount("Оля"));
    }

    [Fact]
    public void Password_is_stored_hashed_and_verified_in_constant_time()
    {
        var hash = Auth.HashPassword("секрет123", out var salt);
        Assert.DoesNotContain("секрет", hash);
        Assert.True(Auth.VerifyPassword("секрет123", hash, salt));
        Assert.False(Auth.VerifyPassword("секрет124", hash, salt));
        // Та сама фраза, інша сіль — інший хеш: витік бази не видає однакових паролів.
        Assert.NotEqual(hash, Auth.HashPassword("секрет123", out _));
    }

    [Fact]
    public void Changing_the_password_changes_the_salt_so_old_sessions_die()
    {
        using var t = new TempDb();
        t.Db.AddAccount("Влад", Auth.HashPassword("старий123", out var salt), salt);
        t.Db.SetAccountPassword("влад", Auth.HashPassword("новий123", out var fresh), fresh);
        var a = t.Db.FindAccount("Влад")!;
        Assert.NotEqual(salt, a.PassSalt);   // сіль — частина сесійної куки, тож стара кука вже не пройде
        Assert.True(Auth.VerifyPassword("новий123", a.PassHash, a.PassSalt));
        Assert.False(Auth.VerifyPassword("старий123", a.PassHash, a.PassSalt));
    }

    [Fact]
    public void Admin_role_sticks_to_the_account()
    {
        using var t = new TempDb();
        t.Db.AddAccount("Влад", Auth.HashPassword("секрет123", out var salt), salt);
        Assert.Equal("member", t.Db.FindAccount("Влад")!.Role);
        t.Db.SetAccountRole("влад", "admin");
        Assert.Equal("admin", t.Db.FindAccount("Влад")!.Role);
    }

    [Theory]
    [InlineData("Вася", "гість Вася")]
    [InlineData("  Вася   Пупкін ", "гість Вася Пупкін")]
    [InlineData("гість Вася", "гість Вася")]          // приставку з минулого разу не подвоюємо
    [InlineData("Гість Вася", "гість Вася")]
    [InlineData("", "гість")]
    [InlineData("гість", "гість")]
    [InlineData("   ", "гість")]
    public void A_guest_always_wears_the_prefix(string typed, string expected) =>
        Assert.Equal(expected, Auth.GuestNick(typed));

    [Fact]
    public void A_guest_nick_never_outgrows_the_limit()
    {
        var n = Auth.GuestNick(new string('а', 40));
        Assert.StartsWith("гість ", n);
        Assert.True(n.Length <= Auth.NickMax);
    }

    [Theory]
    [InlineData("В")]
    [InlineData("гість")]
    [InlineData("гість Вася")]
    [InlineData("Гість")]
    [InlineData("Глечики")]
    [InlineData("дядько глек")]
    public void Some_nicks_cannot_be_registered(string nick) =>
        Assert.NotNull(Auth.NickProblem(Auth.CleanNick(nick), Site));

    [Theory]
    [InlineData("Влад")]
    [InlineData("Оля К")]
    [InlineData("гістьовий")]   // «гість» без пробілу далі — просто слово
    public void Ordinary_nicks_are_fine(string nick) =>
        Assert.Null(Auth.NickProblem(Auth.CleanNick(nick), Site));

    [Fact]
    public void Account_and_wallet_share_the_same_nick_key()
    {
        // Зареєстрований «Влад» має знайти глеки, зароблені ще як «влад»: обидва ключі мусять збігатися.
        Assert.Equal(Hlechyky.Games.Economy.EconomyStore.Key(" Влад "), Auth.NickKey(" Влад "));
        Assert.Equal("влад", Auth.NickKey("ВЛАД"));
    }
}
