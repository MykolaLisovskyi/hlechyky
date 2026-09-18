using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Підроблене джерело: треки зі списку, уривок — кілька кілобайт нулів (або null для «зламаних»). Трек без файла
/// (<see cref="MelodyTrack.Pending"/>) «качається» миттєво — або не качається, якщо його назва в <paramref name="missing"/>.
/// </summary>
sealed class FakeMelodySource(IReadOnlyList<MelodyTrack> tracks, ISet<string>? broken = null, ISet<string>? missing = null) : IMelodySource
{
    public int Clips, Resolved;
    public IReadOnlyList<string>? Categories;

    public Task<IReadOnlyList<MelodyTrack>> PickAsync(int count, IReadOnlyList<string> categories, Random rng, CancellationToken ct)
    {
        Categories = categories;
        return Task.FromResult<IReadOnlyList<MelodyTrack>>([.. tracks.Take(count)]);
    }

    public Task<MelodyTrack?> ResolveAsync(MelodyTrack track, CancellationToken ct)
    {
        if (!track.Pending) return Task.FromResult<MelodyTrack?>(track);
        Interlocked.Increment(ref Resolved);
        if (missing?.Contains(track.Title) == true) return Task.FromResult<MelodyTrack?>(null);
        return Task.FromResult<MelodyTrack?>(track with { Id = "yt-" + track.Title, DurationSec = 240, FilePath = "/dev/null" });
    }

    public Task<byte[]?> ClipAsync(MelodyTrack track, double startSec, int seconds, CancellationToken ct)
    {
        Interlocked.Increment(ref Clips);
        return Task.FromResult(broken?.Contains(track.Id) == true ? null : new byte[5000]);
    }
}

/// <summary>«Вгадай мелодію» (specs/melody.md).</summary>
public class MelodyTests
{
    static MelodyTrack T(string id, string artist, string title, int dur = 200) => new(id, title, artist, dur, null, "/dev/null");

    static readonly MelodyTrack[] Songs =
    [
        T("a", "Океан Ельзи", "Обійми"),
        T("b", "Скрябін", "Старі фотографії"),
        T("c", "DakhaBrakha", "Vesna"),
        T("d", "KALUSH", "Stefania (feat. Skofka)"),
        T("e", "The Hardkiss", "Journey"),
    ];

    static RoomHarness Table(IMelodySource source, object? options = null, params string[] nicks)
    {
        var h = new RoomHarness("melody", options: options, seed: 3, services: RoomHarness.WithService(source));
        foreach (var n in nicks.Length == 0 ? ["Оля", "Петро"] : nicks) h.Join(n);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    /// <summary>Тикати, поки фон не наріже уривок і фаза не стане потрібною (фон — справжня задача).</summary>
    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < 400 && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++)
        {
            h.Tick();
            if (Phase(h) != phase) Thread.Sleep(2);
        }
        Assert.Equal(phase, Phase(h));
    }

    static ActResult Guess(RoomHarness h, int seat, string text)
    {
        h.Clock.AdvanceMs(Melody.GuessEveryMs);
        return h.Act(seat, "guess", new { text });
    }

    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    static RoomHarness Playing(object? options = null)
    {
        var h = Table(new FakeMelodySource([Songs[0], Songs[1], Songs[2]]), options ?? new { rounds = "5" });
        Until(h, "play");
        return h;
    }

    // ---------------------------------------------------------------- раунд

    [Fact]
    public void A_round_starts_with_a_clip_link_and_no_answer()
    {
        var h = Playing();
        var v = h.View(1);
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Matches(@"^/api/games/melody/[0-9a-f]{24}\.mp3$", v.GetProperty("clip").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("answer").ValueKind);
        Assert.DoesNotContain("Обійми", v.GetRawText());
        Assert.DoesNotContain("Океан", v.GetRawText());
    }

    [Fact]
    public void The_clip_is_served_by_its_token()
    {
        var h = Playing();
        var url = h.View(0).GetProperty("clip").GetString()!;
        var token = url[(url.LastIndexOf('/') + 1)..^4];
        Assert.Equal(5000, MelodyClips.Get(token)!.Length);
        Assert.Null(MelodyClips.Get("0123456789abcdef01234567"));
    }

    [Fact]
    public void Artist_and_title_score_separately_and_first_gets_a_bonus()
    {
        var h = Playing();

        var r = Guess(h, 0, "океан ельзи");
        Assert.True(r.Ok);
        Assert.Contains("виконавець", r.Message);
        Assert.Equal(Melody.ArtistPoints + Melody.ArtistFirst, Score(h, 0));

        Assert.True(Guess(h, 1, "Okean Elzy").Ok);
        Assert.Equal(Melody.ArtistPoints, Score(h, 1));

        Assert.True(Guess(h, 1, "обійми").Ok);
        Assert.Equal(Melody.ArtistPoints + Melody.TitlePoints + Melody.TitleFirst, Score(h, 1));
    }

    [Fact]
    public void Both_at_once_count_twice()
    {
        var h = Playing();
        var r = Guess(h, 0, "Океан Ельзи — Обійми");
        Assert.Contains("виконавець", r.Message);
        Assert.Contains("назва", r.Message);
        Assert.True(h.View(0).GetProperty("me").GetProperty("title").GetBoolean());
    }

    [Fact]
    public void A_miss_is_a_miss_and_repeats_score_nothing()
    {
        var h = Playing();
        Assert.Equal("Мимо", Guess(h, 0, "Бумбокс").Message);
        Guess(h, 0, "океан ельзи");
        var before = Score(h, 0);
        Assert.False(Guess(h, 0, "океан ельзи").Ok);
        Assert.Equal(before, Score(h, 0));
    }

    [Fact]
    public void Guesses_are_rate_limited()
    {
        var h = Playing();
        Guess(h, 0, "щось");
        Assert.Equal("Не так швидко", h.Act(0, "guess", new { text = "обійми" }).Message);
    }

    [Fact]
    public void When_everyone_has_both_the_round_ends_and_the_answer_opens()
    {
        var h = Playing();
        Guess(h, 0, "океан ельзи обійми");
        Guess(h, 1, "океан ельзи обійми");
        h.Tick();

        Assert.Equal("reveal", Phase(h));
        var answer = h.View(null).GetProperty("answer");
        Assert.Equal("Обійми", answer.GetProperty("title").GetString());
        Assert.False(Guess(h, 0, "скрябін").Ok);
    }

    [Fact]
    public void When_everyone_still_guessing_is_ready_to_skip_the_track_is_skipped()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5" }, "Оля", "Петро", "Ганна");
        Until(h, "play");
        Guess(h, 0, "океан ельзи обійми");            // Оля вгадала все — її голос не потрібен
        Assert.True(h.Act(1, "skip").Ok);
        h.Tick();
        Assert.Equal("play", Phase(h));
        Assert.Equal([1], h.View(null).GetProperty("skip").EnumerateArray().Select(e => e.GetInt32()));

        Assert.True(h.Act(2, "skip").Ok);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
    }

    [Fact]
    public void Skip_toggles_and_is_not_for_those_who_guessed_everything()
    {
        var h = Playing();
        Assert.True(h.Act(1, "skip").Ok);
        Assert.True(h.Act(1, "skip").Ok);   // передумав
        Assert.Empty(h.View(null).GetProperty("skip").EnumerateArray());

        Guess(h, 0, "океан ельзи обійми");
        Assert.False(h.Act(0, "skip").Ok);
    }

    [Fact]
    public void Skip_votes_reset_with_the_next_track()
    {
        var h = Playing(new { rounds = "5", clip = "10" });
        h.Act(0, "skip");
        h.Act(1, "skip");
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        h.Tick(Melody.RevealMs / Melody.TickMs + 1);
        Until(h, "play");
        Assert.Empty(h.View(null).GetProperty("skip").EnumerateArray());
    }

    [Fact]
    public void Time_runs_out_and_the_next_track_follows()
    {
        var h = Playing(new { rounds = "5", clip = "10" });
        h.Tick((10_000 + Melody.ExtraMs) / Melody.TickMs + 1);
        Assert.Equal("reveal", Phase(h));
        h.Tick(Melody.RevealMs / Melody.TickMs + 1);
        Until(h, "play");
        Assert.Equal(2, h.View(null).GetProperty("round").GetInt32());
    }

    [Fact]
    public void Fewer_tracks_than_rounds_means_a_shorter_game()
    {
        var h = Table(new FakeMelodySource([Songs[0], Songs[1]]), new { rounds = "5", clip = "10" });
        for (var i = 0; i < 10 && h.Room.Status == RoomStatus.Playing; i++)
        {
            Until(h, "play");
            Guess(h, 0, h.View(0).GetProperty("round").GetInt32() == 1 ? "обійми" : "старі фотографії");
            h.Tick((10_000 + Melody.ExtraMs) / Melody.TickMs + 1);
            h.Tick(Melody.RevealMs / Melody.TickMs + 2);
            for (var k = 0; k < 50 && h.Room.Status == RoomStatus.Playing && Phase(h) == "loading"; k++) { h.Tick(); Thread.Sleep(2); }
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("найкраще вухо в Оля", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_clip_that_fails_is_skipped()
    {
        var src = new FakeMelodySource([Songs[0], Songs[1]], new HashSet<string> { "a" });
        var h = Table(src, new { rounds = "5" });
        Until(h, "play");
        Assert.Contains("виконавець", Guess(h, 0, "скрябін").Message);
    }

    [Fact]
    public void No_tracks_at_all_closes_the_table_with_a_reason()
    {
        var h = Table(new FakeMelodySource([]), new { cat = "world,rock" });
        for (var i = 0; i < 200 && h.Room.Status == RoomStatus.Playing; i++) { h.Tick(); Thread.Sleep(2); }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Contains("для цих категорій ще нема", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Solo_works_and_leaving_everyone_ends_it()
    {
        var h = Table(new FakeMelodySource(Songs), null, "Оля");
        Until(h, "play");
        h.Leave("Оля");
        Assert.False(h.Rooms.Find(h.RoomId) is { Status: RoomStatus.Playing });
    }

    [Fact]
    public void Rematch_starts_over_with_zero_scores()
    {
        var h = Table(new FakeMelodySource([Songs[0]]), new { rounds = "5", clip = "10" });
        Until(h, "play");
        Guess(h, 0, "обійми");
        h.Tick((10_000 + Melody.ExtraMs) / Melody.TickMs + 1);
        h.Tick(Melody.RevealMs / Melody.TickMs + 2);
        for (var k = 0; k < 200 && h.Room.Status == RoomStatus.Playing; k++) { h.Tick(); Thread.Sleep(2); }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch();
        Until(h, "play");
        Assert.Equal(1, h.View(null).GetProperty("round").GetInt32());
        Assert.All(h.View(null).GetProperty("scores").EnumerateArray(), e => Assert.Equal(0, e.GetInt32()));
    }

    // ---------------------------------------------------------------- відповіді

    [Theory]
    [InlineData("Океан Ельзи", "Як ніколи", "океан ельзи", true)]
    [InlineData("Океан Ельзи", "Як ніколи", "Okean Elzy", true)]
    [InlineData("Скрябін", "Сам собі країна", "Skryabin", true)]
    [InlineData("Скрябін", "Сам собі країна", "скрябин", true)]
    [InlineData("KALUSH", "Stefania", "калуш", true)]
    [InlineData("DNK", "А Може Ти Мене Полюбиш (feat. TEMRA)", "temra", false)]
    [InlineData("Jerry Heil & alyona alyona", "Teresa & Maria", "alyona alyona", true)]
    [InlineData("Somber Sounds", "Скрябін - Спи собі сама", "скрябін", true)]
    [InlineData("Океан Ельзи - Topic", "Обійми", "океан ельзи", true)]
    [InlineData("TESLENKO", "Кожен раз", "teslenko", true)]
    [InlineData("TESLENKO", "Кожен раз", "tesla", false)]
    [InlineData("The Hardkiss", "Journey", "hardkis", false)]
    [InlineData("The Hardkiss", "Journey", "the hardkis", true)]
    public void Artist_matching(string artist, string title, string guess, bool hit) =>
        Assert.Equal(hit, MelodyAnswer.Hits(guess, MelodyAnswer.Artists(T("x", artist, title))));

    [Theory]
    [InlineData("DNK", "А Може Ти Мене Полюбиш (feat. TEMRA)", "а може ти мене полюбиш", true)]
    [InlineData("Somber Sounds", "Скрябін - Спи собі сама", "спи собі сама", true)]
    [InlineData("Пиріг і Батіг", "Гаї шумлять (1913)", "гаї шумлять", true)]
    [InlineData("Пиріг і Батіг", "Гаї шумлять (1913)", "гаи шумлять", true)]
    [InlineData("Океан Ельзи", "Обійми", "обійми мене", true)]
    [InlineData("Океан Ельзи", "Обійми", "обі", false)]
    [InlineData("KALUSH", "Stefania", "Стефанія", true)]
    public void Title_matching(string artist, string title, string guess, bool hit) =>
        Assert.Equal(hit, MelodyAnswer.Hits(guess, MelodyAnswer.Titles(T("x", artist, title))));

    // ---------------------------------------------------------------- категорії

    [Fact]
    public void Everything_is_the_default_and_several_categories_can_be_chosen()
    {
        var src = new FakeMelodySource(Songs);
        var h = Table(src);
        Until(h, "play");
        Assert.Contains("ua", src.Categories!);
        Assert.Contains("world", src.Categories!);
        Assert.Contains("hits", src.Categories!);          // з data/melody/classics.txt
        Assert.DoesNotContain("all", src.Categories!);

        var two = new FakeMelodySource(Songs);
        var h2 = Table(two, new { cat = "hits,ua" });
        Until(h2, "play");
        Assert.Equal(["ua", "hits"], two.Categories);      // у порядку паспорта

        var unknown = new FakeMelodySource(Songs);
        var h3 = Table(unknown, new { cat = "jazz" });
        Until(h3, "play");
        Assert.Contains("world", unknown.Categories!);     // невідоме → усе
    }

    [Fact]
    public void No_ukrainian_tracks_says_so()
    {
        var h = Table(new FakeMelodySource([]), new { cat = "ua" });
        for (var i = 0; i < 200 && h.Room.Status == RoomStatus.Playing; i++) { h.Tick(); Thread.Sleep(2); }
        Assert.Contains("Українських треків", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_pending_classic_is_fetched_and_one_that_cannot_be_is_skipped()
    {
        var pending = new MelodyTrack("", "Bohemian Rhapsody", "Queen", 0, null, "");
        var lost = new MelodyTrack("", "Nowhere Song", "Nobody", 0, null, "");
        var src = new FakeMelodySource([Songs[0], lost, pending], missing: new HashSet<string> { "Nowhere Song" });
        var h = Table(src, new { rounds = "5" });
        Until(h, "play");
        Assert.Equal(2, src.Resolved);
        Assert.Equal(2, h.View(null).GetProperty("rounds").GetInt32());
        Assert.Contains("виконавець", Guess(h, 0, "океан ельзи").Message);
        h.Clock.AdvanceMs(Melody.ExtraMs + 20_000);
        Until(h, "reveal");
        h.Clock.AdvanceMs(Melody.RevealMs + 100);
        Until(h, "play");
        Assert.Contains("назва", Guess(h, 1, "bohemian rhapsody").Message);
        h.Clock.AdvanceMs(Melody.ExtraMs + 20_000);
        Until(h, "reveal");
        Assert.Equal("yt-Bohemian Rhapsody", h.View(null).GetProperty("answer").GetProperty("id").GetString());   // id — уже справжній, з бази
    }

    [Fact]
    public void Classics_file_parses_categories_and_songs()
    {
        var c = MelodyClassics.Parse(
        [
            "# коментар", "", "[rock] Рок-класика", "Queen — Bohemian Rhapsody", "Queen - We Will Rock You",
            "без тире", "Queen — Bohemian Rhapsody (Official Video)", "[ua] не можна — зайнято", "Океан Ельзи — Обійми",
            "[hits] Світові хіти", "ABBA – Dancing Queen",
        ]);
        Assert.Equal([("rock", "Рок-класика"), ("hits", "Світові хіти")], c.Categories);
        Assert.Equal(["Bohemian Rhapsody", "We Will Rock You"], c.In("rock").Select(e => e.Title));   // дубль — не двічі
        Assert.Equal(["Dancing Queen"], c.In("hits").Select(e => e.Title));
        Assert.Empty(c.In("ua"));                                                                      // «ua» — з радіо, у файлі не буває
        Assert.Equal(SongKey.Of("Queen", "Bohemian Rhapsody"), c.Entries[0].Key);
    }

    [Fact]
    public void Real_classics_file_has_hits_and_ukrainian_categories()
    {
        var c = MelodyClassics.Default;
        Assert.Contains(c.Categories, x => x.Value == "hits");
        Assert.Contains(c.Categories, x => x.Value == "uahits");
        Assert.True(c.In("hits").Count() > 100);
        Assert.Contains(c.In("hits"), e => e.Artist == "Nirvana" && e.Title == "Smells Like Teen Spirit");
        Assert.All(c.Entries, e => Assert.True(e.Key.Length > 0));
    }

    [Fact]
    public void Categories_are_interleaved_and_a_ready_track_goes_first()
    {
        var rng = new Random(1);
        var rows = new List<MelodyLibrary.Row>
        {
            new(T("a", "Океан Ельзи", "Обійми"), 3, SongKey.Of("Океан Ельзи", "Обійми")),
            new(T("b", "Скрябін", "Мовчати"), 1, SongKey.Of("Скрябін", "Мовчати")),
            new(T("q", "Queen", "Bohemian Rhapsody (Remastered 2011)"), 2, SongKey.Of("Queen", "Bohemian Rhapsody (Remastered 2011)")),
            new(T("d", "Nirvana", "Lithium"), 0, SongKey.Of("Nirvana", "Lithium")),
        };
        var classics = MelodyClassics.Parse(["[rock] Рок", "Queen — Bohemian Rhapsody", "Nirvana — Smells Like Teen Spirit", "Nirvana — Lithium", "AC/DC — Thunderstruck"]);
        var disliked = new HashSet<string> { SongKey.Of("AC/DC", "Thunderstruck") };

        var pools = MelodyLibrary.Pools(rows, disliked, ["ua", "rock"], classics, rng);
        Assert.Equal(2, pools.Count);
        Assert.Equal(["a", "b"], pools[0].Select(t => t.Id).Order());                       // українське з радіо
        var rock = pools[1];
        Assert.Equal(3, rock.Count);                                                          // AC/DC з 👎 — ні
        var queen = Assert.Single(rock, t => t.Id == "q");
        Assert.Equal("Bohemian Rhapsody", queen.Title);                                       // назва з добірки, файл із кешу
        Assert.False(queen.Pending);
        Assert.Single(rock, t => t.Id == "d" && !t.Pending);                                 // Lithium уже в кеші
        Assert.Single(rock, t => t.Pending && t.Title == "Smells Like Teen Spirit");         // а цю — качати

        var mixed = MelodyLibrary.Interleave([[rows[0].Track, rows[1].Track], [rock[0], rock[1], rock[2]]]);
        Assert.Equal([rows[0].Track, rock[0], rows[1].Track, rock[1], rock[2]], mixed);

        var pend = new MelodyTrack("", "X", "Y", 0, null, "");
        Assert.Equal(["a", "", "b"], MelodyLibrary.FirstReady([pend, rows[0].Track, rows[1].Track]).Select(t => t.Id));
        Assert.Equal(["a", "", "b"], MelodyLibrary.FirstReady([rows[0].Track, pend, rows[1].Track]).Select(t => t.Id));
    }

    [Fact]
    public void Choose_keeps_order_when_told_not_to_shuffle()
    {
        var list = new List<MelodyTrack> { Songs[0], Songs[1], T("a2", "Океан Ельзи", "Обійми (Live)"), Songs[2] };
        Assert.Equal(["a", "b", "c"], MelodyLibrary.Choose(list, 3, new Random(1), shuffle: false).Select(t => t.Id));
    }

    [Theory]
    [InlineData("Океан Ельзи", "Як ніколи", true)]                      // «і» в назві
    [InlineData("Був'є", "Голова", true)]                               // апостроф в імені
    [InlineData("FIЇNKA", "Афини", true)]                               // «Ї» в латинському імені
    [InlineData("Alena Omargalieva", "Не Пʼяна - Закохана", true)]
    [InlineData("Это Радио", "На мурмулях", false)]                     // «э»
    [InlineData("Abbram", "Kavkazskaya Krov", false)]
    [InlineData("AC/DC", "Highway to Hell", false)]
    [InlineData("Cee-Lo і Jack Black", "Kung Fu Fighting", false)]      // « і » — лише сполучник між виконавцями
    [InlineData("БЕЗ ОБМЕЖЕНЬ", "Якби", true)]                          // зі списку
    [InlineData("Kalush", "Stefania", true)]                             // зі списку, регістр не важить
    [InlineData("Нумер 482", "Триллер", true)]                          // виконавець уже має український трек
    [InlineData("alyona alyona і Jerry Heil", "Teresa & Maria", true)]  // а тут — і список, і вивчений
    [InlineData("DG Leos", "Блатата", false)]
    [InlineData("MILA", "ШО ТИ, ШО ТИ", true)]                          // суто українські слова
    [InlineData("ROMA", "Кава на двох", true)]
    public void Language_detection(string artist, string title, bool ukrainian)
    {
        var lang = new MelodyLanguage(["БЕЗ ОБМЕЖЕНЬ", "KALUSH", "Jerry Heil"]);
        var library = new[]
        {
            T("n1", "Нумер 482", "Добрий ранок, Україно"),
            T("q", artist, title),
        };
        Assert.Equal(ukrainian, lang.Ukrainian(library).Any(t => t.Id == "q"));
    }

    [Fact]
    public void A_russian_title_is_never_ukrainian_even_for_a_known_artist()
    {
        var lang = new MelodyLanguage(["Скрябін"]);
        Assert.Empty(lang.Ukrainian([T("x", "Скрябін", "Мёртвые души")]));
    }

    [Fact]
    public void The_real_artist_list_loads()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) dir = dir.Parent;
        var lang = MelodyLanguage.Load(Path.Combine(dir!.FullName, MelodyLanguage.FileName));
        Assert.Single(lang.Ukrainian([T("k", "Kalush Orchestra", "Stefania")]));
    }

    [Fact]
    public void Everything_that_was_played_counts_equally()
    {
        var list = Enumerable.Range(0, 60).Select(i => new MelodyLibrary.Row(T($"t{i:00}", $"A{i}", $"S{i}"), i < 50 ? (i % 7) + 1 : 0, "")).ToList();
        var pool = MelodyLibrary.Heard(list, 10);
        Assert.Equal(50, pool.Count);                                   // усі, що звучали, а не верхівка
        Assert.DoesNotContain(pool, t => t.Id == "t55");                // жодного разу не грав — не беремо
    }

    [Fact]
    public void Too_few_played_tracks_fall_back_to_the_rest_of_the_cache()
    {
        var list = Enumerable.Range(0, 20).Select(i => new MelodyLibrary.Row(T($"t{i:00}", $"A{i}", $"S{i}"), i < 3 ? 1 : 0, "")).ToList();
        var pool = MelodyLibrary.Heard(list, 10);
        Assert.Equal(20, pool.Count);
        Assert.Equal(["t00", "t01", "t02"], pool.Take(3).Select(t => t.Id));
    }

    [Fact]
    public void Picking_avoids_the_same_song_and_prefers_other_artists()
    {
        var list = new List<MelodyTrack>
        {
            T("1", "Скрябін", "Старі фотографії"),
            T("2", "Скрябін", "Старі фотографії (Official Video)"),
            T("3", "Скрябін", "Люди як кораблі"),
            T("4", "Океан Ельзи", "Обійми"),
        };
        var picked = MelodyLibrary.Choose(list, 2, new Random(1));
        Assert.Equal(2, picked.Count);
        Assert.NotEqual(picked[0].Artist, picked[1].Artist);

        var all = MelodyLibrary.Choose([.. list], 4, new Random(1));
        Assert.Equal(3, all.Count);   // дві копії «Старих фотографій» — одна пісня
    }
}
