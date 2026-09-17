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

/// <summary>Вхід через Google: свій — одразу, новий — спершу нік, чужий нік із паролем через Google не забрати.</summary>
public class GoogleAccountsTests
{
    static Accounts Make(TempDb t) => new(t.Db, new FixedOptions<SiteOptions>(new SiteOptions()));
    static readonly GoogleIdentity Vlad = new("sub-vlad", "vlad@gmail.com", "Влад");

    [Fact]
    public void A_newcomer_is_asked_for_a_nick_with_the_google_name_suggested()
    {
        using var t = new TempDb();
        var r = Make(t).Google(Vlad, null);
        Assert.True(r.NeedNick);
        Assert.Equal("Влад", r.Suggest);
        Assert.Null(r.Account);
        Assert.Null(t.Db.FindAccount("Влад"));   // поки не назвався — акаунта нема
    }

    [Fact]
    public void With_a_nick_the_newcomer_gets_a_passwordless_account_and_comes_back_by_google_alone()
    {
        using var t = new TempDb();
        var a = Make(t);
        var made = a.Google(Vlad, "Владик");
        Assert.NotNull(made.Account);
        Assert.False(made.Account!.HasPassword);
        Assert.Equal("vlad@gmail.com", made.Account.Email);

        var again = a.Google(Vlad with { Name = "хто завгодно" }, null);
        Assert.Equal("Владик", again.Account!.Nick);
        Assert.False(again.NeedNick);

        // Пароля нема — паролем не зайти, і відповідь каже, куди йти.
        var byPassword = a.Login("Владик", "");
        Assert.Null(byPassword.Account);
        Assert.Contains("Google", byPassword.Error);
    }

    [Fact]
    public void Google_cannot_take_a_nick_that_someone_registered_with_a_password()
    {
        using var t = new TempDb();
        var a = Make(t);
        Assert.NotNull(a.Register("Влад", "секрет123").Account);

        var asked = a.Google(Vlad, null);
        Assert.True(asked.NeedNick);
        Assert.Equal("", asked.Suggest);   // «Влад» зайнятий — не підказуємо

        var taken = a.Google(Vlad, "влад");
        Assert.Null(taken.Account);
        Assert.Equal(409, taken.Status);
        Assert.Contains("прив'яжи", taken.Error);
    }

    [Fact]
    public void Linking_google_lets_the_password_account_in_by_google_and_only_once()
    {
        using var t = new TempDb();
        var a = Make(t);
        var vlad = a.Register("Влад", "секрет123").Account!;
        Assert.NotNull(a.LinkGoogle(vlad, Vlad).Account);
        Assert.Equal("Влад", a.Google(Vlad, null).Account!.Nick);
        Assert.True(t.Db.FindAccount("Влад")!.HasPassword);   // пароль нікуди не дівся

        var olia = a.Register("Оля", "секрет123").Account!;
        var clash = a.LinkGoogle(olia, Vlad);
        Assert.Null(clash.Account);
        Assert.Contains("«Влад»", clash.Error);
        Assert.Contains("уже прив'язано", a.LinkGoogle(vlad, Vlad).Error);
    }

    [Fact]
    public void Setting_a_password_needs_the_current_one_only_if_there_is_one()
    {
        using var t = new TempDb();
        var a = Make(t);
        var g = a.Google(Vlad, "Влад").Account!;
        Assert.NotNull(a.SetPassword(g, null, "новий123").Account);
        Assert.NotNull(a.Login("Влад", "новий123").Account);

        var withPass = t.Db.FindAccount("Влад")!;
        Assert.Equal(401, a.SetPassword(withPass, "не той", "інший123").Status);
        Assert.NotNull(a.SetPassword(withPass, "новий123", "інший123").Account);
        Assert.NotNull(a.Login("Влад", "інший123").Account);
        Assert.Null(a.SetPassword(withPass, "інший123", "12345").Account);   // закороткий
    }
}

/// <summary>Гість зареєструвався — усе, що нафармив у цьому браузері, переїжджає на акаунт.</summary>
public class GuestAdoptionTests
{
    static Accounts Make(TempDb t) => new(t.Db, new FixedOptions<SiteOptions>(new SiteOptions()));
    static void Wallet(Db db, string nick, int balance, int earned) => db.Exec(
        "INSERT INTO wallets(nick_key, nick, balance, earned, spent, updated_at) VALUES($k, $n, $b, $e, 0, 'now')",
        ("$k", Auth.NickKey(nick)), ("$n", nick), ("$b", balance), ("$e", earned));
    static (int Balance, int Earned)? WalletOf(Db db, string nick) => db.With(c =>
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT balance, earned FROM wallets WHERE nick_key=$k";
        cmd.Parameters.AddWithValue("$k", Auth.NickKey(nick));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ((int)r.GetInt64(0), (int)r.GetInt64(1)) : ((int, int)?)null;
    });
    static string? State(Db db, string key) => db.With(c =>
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM game_state WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    });

    [Fact]
    public void Guest_shards_and_pottery_follow_the_new_account()
    {
        using var t = new TempDb();
        Wallet(t.Db, "гість Вася", 10, 12);
        t.Db.Exec("INSERT INTO game_state(key, json, updated_at) VALUES('clicker:гість вася', '{\"pots\":3,\"total\":40}', 'now')");
        t.Db.Exec("INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES('гість вася', 'first', 'гість Вася', 'now')");
        t.Db.Exec("INSERT INTO likes(track_id, nick, created_at) VALUES('t1', 'гість Вася', 'now')");

        var a = Make(t);
        var made = a.Register("Вася", "секрет123").Account!;
        a.Adopt(made, "гість Вася");

        Assert.Equal((10, 12), WalletOf(t.Db, "Вася"));
        Assert.Null(WalletOf(t.Db, "гість Вася"));
        Assert.Equal("{\"pots\":3,\"total\":40}", State(t.Db, "clicker:вася"));
        Assert.Null(State(t.Db, "clicker:гість вася"));
        Assert.Equal(1L, t.Db.With(c => { var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM achievements WHERE nick_key='вася' AND nick='Вася'"; return (long)q.ExecuteScalar()!; }));
        Assert.Equal(1L, t.Db.With(c => { var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM likes WHERE nick='Вася'"; return (long)q.ExecuteScalar()!; }));
    }

    [Fact]
    public void An_old_nick_with_its_own_loot_gets_the_guest_loot_added_and_keeps_the_bigger_pottery()
    {
        using var t = new TempDb();
        Wallet(t.Db, "Влад", 100, 300);        // грав ще до акаунтів
        Wallet(t.Db, "гість Влад", 10, 12);    // після оновлення трохи пофармив гостем
        t.Db.Exec("INSERT INTO game_state(key, json, updated_at) VALUES('clicker:влад', '{\"pots\":50,\"total\":900}', 'now')");
        t.Db.Exec("INSERT INTO game_state(key, json, updated_at) VALUES('clicker:гість влад', '{\"pots\":2,\"total\":7}', 'now')");
        t.Db.Exec("INSERT INTO ratings(nick_key, game, nick, elo, games, wins, losses, draws, updated_at) VALUES('влад', 'chess', 'Влад', 1100, 10, 6, 4, 0, 'now')");
        t.Db.Exec("INSERT INTO ratings(nick_key, game, nick, elo, games, wins, losses, draws, updated_at) VALUES('гість влад', 'chess', 'гість Влад', 1020, 2, 1, 1, 0, 'now')");
        t.Db.Exec("INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES('влад', 'first', 'Влад', 'now')");
        t.Db.Exec("INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES('гість влад', 'first', 'гість Влад', 'now')");
        t.Db.Exec("INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES('гість влад', 'hundred', 'гість Влад', 'now')");

        t.Db.MergeNick("гість Влад", "Влад");

        Assert.Equal((110, 312), WalletOf(t.Db, "Влад"));
        Assert.Equal("{\"pots\":50,\"total\":900}", State(t.Db, "clicker:влад"));   // більша гончарня перемагає
        Assert.Null(State(t.Db, "clicker:гість влад"));
        // Менша не зникає — лягає в резерв, щоб можна було повернути руками.
        var lost = t.Db.With(c => { var q = c.CreateCommand(); q.CommandText = "SELECT json FROM game_state WHERE key LIKE 'lost:clicker:влад:%'"; return q.ExecuteScalar() as string; });
        Assert.Equal("{\"pots\":2,\"total\":7}", lost);
        var rating = t.Db.With(c => { var q = c.CreateCommand(); q.CommandText = "SELECT elo, games, wins FROM ratings WHERE nick_key='влад' AND game='chess'"; using var r = q.ExecuteReader(); r.Read(); return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)); });
        Assert.Equal((1100L, 12L, 7L), rating);
        var ach = t.Db.With(c => { var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM achievements WHERE nick_key='влад'"; return (long)q.ExecuteScalar()!; });
        Assert.Equal(2L, ach);
        Assert.Equal(0L, t.Db.With(c => { var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM achievements WHERE nick_key='гість влад'"; return (long)q.ExecuteScalar()!; }));
    }

    [Fact]
    public void The_nameless_guest_pool_and_non_guests_are_never_merged()
    {
        using var t = new TempDb();
        Wallet(t.Db, "гість", 500, 500);
        Wallet(t.Db, "Оля", 7, 7);
        var a = Make(t);
        var vlad = a.Register("Влад", "секрет123").Account!;
        a.Adopt(vlad, "гість");
        a.Adopt(vlad, "Оля");
        a.Adopt(vlad, null);
        Assert.Null(WalletOf(t.Db, "Влад"));
        Assert.Equal((500, 500), WalletOf(t.Db, "гість"));
        Assert.Equal((7, 7), WalletOf(t.Db, "Оля"));
    }

    [Fact]
    public void Progress_reads_the_total_from_a_pottery_snapshot_and_shrugs_at_anything_else()
    {
        Assert.Equal(40, Db.Progress("{\"pots\":3,\"total\":40}"));
        Assert.Equal(0, Db.Progress("{\"pots\":3}"));
        Assert.Equal(0, Db.Progress("не json"));
    }
}
