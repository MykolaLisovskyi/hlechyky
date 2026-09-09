using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Смак кімнати і якорі проти дрейфу авто-DJ. Перевіряємо саме те, що ламалося в ефірі: Глек сідився
/// від власного вибору, і за ніч кімната з української музики виїжджала у вікінг-метал.
/// </summary>
public class RoomTasteTests
{
    static TrackInfo T(string id, string artist, string title) =>
        new(id, title, artist, 200, null, "https://music.youtube.com/watch?v=" + id, null);

    static RoomTaste Rig(TempDb db, AutoDjOptions? o = null) => new(db.Db, new FixedOptions<AutoDjOptions>(o ?? new AutoDjOptions()));

    /// <summary>Ставить трек в ефір від імені людини або самого Глека — рівно так, як це робить рушій.</summary>
    static void Play(TempDb db, TrackInfo t, string source)
    {
        db.Db.UpsertTrack(t);
        db.Db.EndPlay(db.Db.StartPlay(t.Id, source, source == "user" ? "микола" : null, null, null), skipped: false);
    }

    static void PlayMany(TempDb db, string source, params (string Artist, string Title)[] tracks)
    {
        var n = 0;
        foreach (var (a, ti) in tracks) Play(db, T($"{source[0]}{n++:D10}", a, ti), source);
    }

    [Theory]
    [InlineData("SadSvit", "Касета", "cyr")]
    [InlineData("Мотор'Ролла", "Не дзвонила", "cyr")]
    [InlineData("Bob Dominator", "Blood Runs Wild", "lat")]
    [InlineData("DOROFEEVA", "Хартбіт", "cyr")]      // латиниця в імені, але назва своя
    [InlineData("サカナクション", "新宝島", "")]  // ні кирилиці, ні латиниці — письма не визначаємо
    // YouTube Music українською склеює виконавців через «і»: одна ця літера ще не робить трек своїм
    [InlineData("true viking, fijorddd і moonlighttt", "WINDS OF THE NORTH", "lat")]
    [InlineData("Жадан і Собаки", "Мальви", "cyr")]
    public void Script_is_read_off_artist_and_title(string artist, string title, string expected) =>
        Assert.Equal(expected, RoomTaste.ScriptOf(artist, title));

    [Fact]
    public void Profile_counts_only_what_people_asked_for()
    {
        using var db = new TempDb();
        PlayMany(db, "user", ("SadSvit", "Касета"), ("Жадан і Собаки", "Мальви"), ("YAKAYA", "ПРИПИНДА"),
            ("Zwyntar", "Потяг на Південь"), ("Vivienne Mort", "Готика"), ("MC Петя", "Я Канівес"),
            ("хейтспіч", "мама казала"), ("Харцизи", "Від зими до літа"), ("PANINI", "Трамадол"),
            ("Мотор'Ролла", "Не дзвонила"), ("OTOY", "Судоми"), ("Queen", "Don't Stop Me Now"));
        // а це вже дрейф самого Глека: у профіль він потрапити не повинен, інакше вчиться на своїй же помилці
        PlayMany(db, "autodj", ("Bob Dominator", "Blood Runs Wild"), ("Draugr Balled", "North wind calls"),
            ("Jernblod", "Shieldmaiden"), ("RAVNIR", "Viking On The Radio"), ("Wolfskald", "Mother Told Me"));

        var p = Rig(db).Current;
        Assert.Equal("cyr", p.Script);
        Assert.True(p.Lean > 0.5, $"нахил {p.Lean:F2} замало для кімнати, де 11 із 12 замовлень свої");
        Assert.Contains(AutoDj.ArtistKey("SadSvit"), p.Artists);
        Assert.DoesNotContain(AutoDj.ArtistKey("Bob Dominator"), p.Artists);
    }

    [Fact]
    public void Room_without_enough_history_is_not_pushed_anywhere()
    {
        using var db = new TempDb();
        PlayMany(db, "user", ("SadSvit", "Касета"), ("Жадан і Собаки", "Мальви"));
        var taste = Rig(db);
        Assert.Equal("", taste.Current.Script);
        Assert.Equal(0, taste.Current.Lean);
        Assert.Equal(1, taste.Multiplier("Bob Dominator", "Blood Runs Wild", 1), 3);
    }

    [Fact]
    public void Foreign_newcomer_is_damped_and_a_known_artist_is_lifted()
    {
        using var db = new TempDb();
        PlayMany(db, "user", ("SadSvit", "Касета"), ("SadSvit", "Молодість"), ("Жадан і Собаки", "Мальви"),
            ("Жадан і Собаки", "Троєщина"), ("YAKAYA", "ПРИПИНДА"), ("Zwyntar", "Потяг на Південь"),
            ("Vivienne Mort", "Готика"), ("MC Петя", "Я Канівес"), ("хейтспіч", "мама казала"),
            ("Харцизи", "Від зими до літа"), ("PANINI", "Трамадол"), ("OTOY", "Судоми"));
        var taste = Rig(db);

        var stranger = taste.Multiplier("Bob Dominator", "Blood Runs Wild", 1);
        var home = taste.Multiplier("Артем Пивоваров", "Очі", 1);
        // латиничне ім'я, але кімната його вже ставила — «свій» перебиває письмо
        var latinLocal = taste.Multiplier("SadSvit", "Інша пісня", 1);

        Assert.True(stranger < 0.75, $"чужого мали притиснути, а вийшло ×{stranger:F2}");
        Assert.True(home > 1, $"своє мали підняти, а вийшло ×{home:F2}");
        Assert.True(latinLocal > stranger * 2, $"свій артист латиницею ×{latinLocal:F2} проти чужого ×{stranger:F2}");
    }

    [Fact]
    public void Human_seed_is_trusted_more_than_the_djs_own_drift()
    {
        using var db = new TempDb();
        PlayMany(db, "user", ("SadSvit", "Касета"), ("Жадан і Собаки", "Мальви"), ("YAKAYA", "ПРИПИНДА"),
            ("Zwyntar", "Потяг"), ("Vivienne Mort", "Готика"), ("MC Петя", "Я Канівес"),
            ("хейтспіч", "мама казала"), ("Харцизи", "Від зими"), ("PANINI", "Трамадол"),
            ("OTOY", "Судоми"), ("Nikow", "розмова з містом"), ("Queen", "Don't Stop Me Now"));
        var taste = Rig(db);
        var underDj = taste.Multiplier("Bob Dominator", "Blood Runs Wild", 1);
        var underPerson = taste.Multiplier("Bob Dominator", "Blood Runs Wild", 0.5);
        Assert.True(underPerson > underDj, "коли трек поставила людина, тиснути смаком треба слабше");
        Assert.Equal(1, taste.Multiplier("Bob Dominator", "Blood Runs Wild", 0), 3);
    }

    [Fact]
    public void PreferScript_overrides_what_the_history_says()
    {
        using var db = new TempDb();
        PlayMany(db, "user", ("Queen", "Don't Stop Me Now"), ("Nirvana", "Smells Like Teen Spirit"),
            ("The Beatles", "Let It Be"), ("a-ha", "Take on Me"), ("Elton John", "I'm Still Standing"),
            ("Eurythmics", "Sweet Dreams"), ("The Police", "Every Breath You Take"), ("Wham!", "Wake Me Up"),
            ("The Doors", "People Are Strange"), ("Deep Purple", "Smoke On The Water"),
            ("Arctic Monkeys", "505"), ("Ray Charles", "Hit the Road Jack"));
        var p = Rig(db, new AutoDjOptions { PreferScript = "cyr" }).Current;
        Assert.Equal("cyr", p.Script);
        Assert.Equal(1, p.Lean);
    }

    [Fact]
    public void Anchor_comes_from_human_requests_and_does_not_repeat_itself()
    {
        using var db = new TempDb();
        PlayMany(db, "user", ("SadSvit", "Касета"), ("Жадан і Собаки", "Мальви"), ("YAKAYA", "ПРИПИНДА"));
        PlayMany(db, "autodj", ("Bob Dominator", "Blood Runs Wild"), ("Draugr Balled", "North wind calls"));
        var taste = Rig(db);

        var seen = new List<string>();
        for (var i = 0; i < 3; i++) seen.Add(taste.Anchor(new HashSet<string>())!.Artist);

        Assert.All(seen, a => Assert.Contains(a, new[] { "SadSvit", "Жадан і Собаки", "YAKAYA" }));
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public void Anchor_skips_a_voice_message_and_anything_already_in_play()
    {
        using var db = new TempDb();
        Play(db, T("voice-abc123", "владік", "Голосове"), "user");
        Play(db, T("uOnlyOneLeft", "SadSvit", "Касета"), "user");
        var taste = Rig(db);
        Assert.Equal("SadSvit", taste.Anchor(new HashSet<string>())!.Artist);
        Assert.Null(taste.Anchor(new HashSet<string> { "uOnlyOneLeft" }));
    }

    [Fact]
    public void Chain_of_the_djs_own_picks_is_counted_from_the_last_human_request()
    {
        using var db = new TempDb();
        PlayMany(db, "user", ("SadSvit", "Касета"));
        Assert.Equal(0, db.Db.AutoPlaysSinceUser());
        PlayMany(db, "autodj", ("Bob Dominator", "Blood Runs Wild"), ("Draugr Balled", "North wind calls"),
            ("Jernblod", "Shieldmaiden"));
        Assert.Equal(3, db.Db.AutoPlaysSinceUser());
        Play(db, T("uBackToUsXX", "Жадан і Собаки", "Мальви"), "user");
        Assert.Equal(0, db.Db.AutoPlaysSinceUser());
    }

    [Fact]
    public void Archive_holds_what_the_room_played_long_ago_and_nothing_fresh()
    {
        using var db = new TempDb();
        var old = T("uOldFavour1", "SadSvit", "Касета");
        db.Db.UpsertTrack(old);
        db.Db.Exec("INSERT INTO plays(track_id, source, requested_by, started_at) VALUES($t, 'user', 'микола', $s)",
            ("$t", old.Id), ("$s", DateTimeOffset.UtcNow.AddDays(-3).ToString("o")));
        PlayMany(db, "user", ("Жадан і Собаки", "Мальви"));               // щойно грало — не архів
        PlayMany(db, "autodj", ("Bob Dominator", "Blood Runs Wild"));     // чужий дрейф — не архів

        var pool = db.Db.ArchiveTracks(DateTimeOffset.UtcNow.AddHours(-24), 40);
        Assert.Equal(["SadSvit"], pool.Select(t => t.Artist));
    }
}
