using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Скільки?»: банк запитань, статистика радіо і сама партія — фази, очки, приховані числа й кінець
/// (TESTING.md §4). Партія живе від тика, тому майже кожен тест тут — це «прокрути годинник і подивись».
/// </summary>
public class SkilkyTests
{
    /// <summary>Стеля тиків у циклах очікування: партія на п'ять запитань не триває довше за це.</summary>
    const int MaxTicks = 400;

    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан",
        "Леся", "Остап", "Ніна", "Юрко", "Даша", "Тарас"];

    static RoomHarness Table(int players = 3, int seed = 42, IServiceProvider? services = null)
    {
        var h = new RoomHarness("skilky", seed: seed, services: services);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static int Round(RoomHarness h) => h.View(null).GetProperty("round").GetInt32();
    static long Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt64();

    /// <summary>Тикати, поки не настане потрібна фаза (або поки партія не скінчиться).</summary>
    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick(1);
    }

    /// <summary>Максимум за одне запитання: в яблучко й найближчий.</summary>
    const int Perfect = Skilky.Bullseye + Skilky.BestBonus;

    /// <summary>Запитання, яке зараз на столі: беремо з того самого банку.</summary>
    static SkilkyQuestion Question(RoomHarness h)
    {
        var text = h.View(null).GetProperty("question").GetString();
        return SkilkyBank.All.First(q => q.Q == text);
    }

    /// <summary>Правильна відповідь на запитання, яке зараз на столі.</summary>
    static double Correct(RoomHarness h) => Question(h).A!.Value;

    /// <summary>
    /// Стіл, на якому перше запитання підходить тесту (роки чи звичайне число): перебираємо сіди, доки таке
    /// не випаде. Повертає стіл уже у фазі відповіді.
    /// </summary>
    static RoomHarness Asking(int players, Func<SkilkyQuestion, bool> fits)
    {
        for (var seed = 1; seed <= 500; seed++)
        {
            var h = Table(players, seed);
            Until(h, Skilky.PhaseAsk);
            if (fits(Question(h))) return h;
        }
        throw new InvalidOperationException("жоден сід не дав потрібного запитання");
    }

    static bool Years(SkilkyQuestion q) => q.Unit == "рік";

    /// <summary>Дочекатись запитання і роздати числа: зсув задається від правильної відповіді.</summary>
    static double Answer(RoomHarness h, params (int Seat, double Offset)[] offsets)
    {
        Until(h, Skilky.PhaseAsk);
        var correct = Correct(h);
        foreach (var (seat, offset) in offsets) h.Act(seat, "answer", new { value = correct + offset });
        return correct;
    }

    /// <summary>Догортати поточне запитання до кінця розкриття (далі вже наступне або кінець партії).</summary>
    static void Close(RoomHarness h)
    {
        Until(h, Skilky.PhaseReveal);
        for (var i = 0; i < Skilky.RevealSeconds + 2
            && h.Room.Status == RoomStatus.Playing && Phase(h) == Skilky.PhaseReveal; i++) h.Tick(1);
    }

    /// <summary>Зіграти всю партію, роздаючи ті самі зсуви на кожне запитання.</summary>
    static void PlayAll(RoomHarness h, params (int Seat, double Offset)[] offsets)
    {
        for (var q = 0; q < Skilky.Questions && h.Room.Status == RoomStatus.Playing; q++)
        {
            Answer(h, offsets);
            Close(h);
        }
    }

    /// <summary>Тексти всіх запитань партії, зіграної мовчки.</summary>
    static List<string> AskedQuestions(RoomHarness h)
    {
        var list = new List<string>();
        for (var q = 0; q < Skilky.Questions && h.Room.Status == RoomStatus.Playing; q++)
        {
            Until(h, Skilky.PhaseAsk);
            if (h.Room.Status != RoomStatus.Playing) break;
            list.Add(h.View(null).GetProperty("question").GetString()!);
            Close(h);
        }
        return list;
    }

    // ---------- банк ----------

    [Fact]
    public void The_bank_holds_at_least_a_thousand_questions()
    {
        Assert.True(SkilkyBank.All.Count >= 1000, $"у банку лише {SkilkyBank.All.Count} запитань");
    }

    [Fact]
    public void Every_written_answer_is_a_playable_number()
    {
        Assert.All(SkilkyBank.All.Where(q => !q.IsDynamic), q =>
        {
            Assert.EndsWith("?", q.Q);
            Assert.True(q.A is > 0 and <= 1e12, $"«{q.Q}»: відповідь {q.A} гра не зарахує");
            Assert.True(q.Unit is null || q.Unit.Trim().Length > 0, $"«{q.Q}»: порожня одиниця");
            // «рік» вмикає шкалу в роках — тож це мусить бути справжній календарний рік.
            if (q.Unit == "рік") Assert.True(q.A == Math.Round(q.A!.Value) && q.A is >= 1 and <= 2100, $"«{q.Q}»: {q.A} — не рік");
        });
    }

    [Fact]
    public void Every_question_has_text_and_either_a_number_or_a_dynamic_key()
    {
        Assert.All(SkilkyBank.All, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Q));
            Assert.True(q.A is not null ^ q.IsDynamic, $"«{q.Q}»: має бути або a, або dyn");
        });
    }

    [Fact]
    public void No_question_is_asked_twice_in_the_bank()
    {
        var twice = SkilkyBank.All.GroupBy(q => q.Q, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(twice);
    }

    [Fact]
    public void Dynamic_questions_use_keys_the_stats_know()
    {
        var dyn = SkilkyBank.All.Where(q => q.IsDynamic).Select(q => q.Dyn!).ToList();
        Assert.NotEmpty(dyn);
        Assert.All(dyn, key => Assert.Contains(key, SkilkyStats.Keys));
    }

    [Fact]
    public void A_missing_or_broken_bank_file_gives_an_empty_bank_and_not_a_crash()
    {
        Assert.Empty(SkilkyBank.Load(Path.Combine(Path.GetTempPath(), "skilky-" + Guid.NewGuid().ToString("N") + ".json")));

        var broken = Path.Combine(Path.GetTempPath(), "skilky-broken-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(broken, "{ це не json");
        try { Assert.Empty(SkilkyBank.Load(broken)); }
        finally { File.Delete(broken); }
    }

    // ---------- статистика радіо ----------

    [Fact]
    public void On_an_empty_database_every_dynamic_answer_is_zero()
    {
        using var temp = new TempDb();
        var stats = new SkilkyStats(temp.Db, new FakeClock());
        Assert.All(SkilkyStats.Keys, key => Assert.Equal(0, stats.Value(key)));
    }

    [Fact]
    public void Without_a_database_the_stats_stay_silent()
    {
        var stats = new SkilkyStats(null, new FakeClock());
        Assert.All(SkilkyStats.Keys, key => Assert.Equal(0, stats.Value(key)));
        Assert.Equal(0, stats.Value("такого ключа нема"));
    }

    [Fact]
    public void The_stats_count_what_the_radio_actually_did()
    {
        using var temp = new TempDb();
        // Db штампує рядки справжнім UtcNow, тож вікна «за тиждень» рахуємо від того самого моменту.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        var db = temp.Db;

        db.UpsertTrack(new TrackInfo("t1", "Пісня", "Гурт", 180, null, "https://x/1", null));
        db.UpsertTrack(new TrackInfo(VoiceService.Prefix + "abc", "Голосове", "Оля", 12, null, "https://x/v", null));
        db.StartPlay("t1", "user", "Оля", null, null);
        db.StartPlay("t1", "user", "Оля", null, null);
        db.StartPlay("t1", "user", "Петро", null, null);
        db.ToggleLike("t1", "Оля");
        db.AddChat("Оля", "привіт", "chat");
        db.AddChat("Глечики", "хтось сів грати", "system");

        var stats = new SkilkyStats(db, clock);
        Assert.Equal(3, stats.Value("plays7d"));
        Assert.Equal(3, stats.Value("plays30d"));
        Assert.Equal(1, stats.Value("likesTotal"));
        Assert.Equal(1, stats.Value("tracksTotal"));    // голосове треком не рахується
        Assert.Equal(1, stats.Value("voiceTotal"));
        Assert.Equal(1, stats.Value("chatTotal"));      // системний рядок — не балачки
        Assert.Equal(9, stats.Value("minutesPlayed30d"));
        Assert.Equal(2, stats.Value("topRequesterCount7d"));
    }

    [Fact]
    public void Old_plays_fall_out_of_the_weekly_window()
    {
        using var temp = new TempDb();
        temp.Db.UpsertTrack(new TrackInfo("t1", "Пісня", "Гурт", 60, null, "https://x/1", null));
        temp.Db.StartPlay("t1", "user", "Оля", null, null);
        temp.Db.ToggleLike("t1", "Оля");

        // Годинник тесту стоїть у майбутньому щодо запису — для вікон «за тиждень» і «за місяць»
        // цей програш уже старий, а от лайки й треки вікон не мають і рахуються завжди.
        var later = new FakeClock { UtcNow = DateTimeOffset.UtcNow.AddDays(40) };
        var stats = new SkilkyStats(temp.Db, later);
        Assert.Equal(0, stats.Value("plays7d"));
        Assert.Equal(0, stats.Value("plays30d"));
        Assert.Equal(0, stats.Value("minutesPlayed30d"));
        Assert.Equal(0, stats.Value("topRequesterCount7d"));
        Assert.Equal(1, stats.Value("likesTotal"));
        Assert.Equal(1, stats.Value("tracksTotal"));
    }

    [Fact]
    public void A_counted_number_lives_a_few_minutes_and_only_then_goes_back_to_the_database()
    {
        using var temp = new TempDb();
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        temp.Db.UpsertTrack(new TrackInfo("t1", "Пісня", "Гурт", 60, null, "https://x/1", null));

        var stats = new SkilkyStats(temp.Db, clock);
        Assert.Equal(1, stats.Value("tracksTotal"));

        // «Ще раз» за столом трапляється часто, а вісім COUNT(*) по всій базі — ні до чого:
        // поки кеш свіжий, у базу не ходимо, навіть якщо там уже щось змінилось.
        temp.Db.UpsertTrack(new TrackInfo("t2", "Друга", "Гурт", 60, null, "https://x/2", null));
        Assert.Equal(1, stats.Value("tracksTotal"));

        clock.Advance(SkilkyStats.Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(2, stats.Value("tracksTotal"));
    }

    // ---------- кімната й фази ----------

    [Fact]
    public void The_table_waits_for_the_host_and_only_then_asks()
    {
        var h = new RoomHarness("skilky");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal(Skilky.PhaseBetween, Phase(h));

        Assert.True(h.Start().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(Skilky.PhaseBetween, Phase(h));
        Assert.Equal("", h.View(0).GetProperty("question").GetString());   // у паузі запитання ще ніхто не бачить

        h.Tick(Skilky.BetweenSeconds);
        Assert.Equal(Skilky.PhaseAsk, Phase(h));
        Assert.NotEqual("", h.View(0).GetProperty("question").GetString());
        Assert.Equal(1, Round(h));
        Assert.Equal(Skilky.Questions, h.View(0).GetProperty("of").GetInt32());
    }

    [Fact]
    public void An_answer_is_taken_and_can_be_changed_until_the_deadline()
    {
        var h = Table();
        Until(h, Skilky.PhaseAsk);

        Assert.True(h.Act(0, "answer", new { value = 10 }).Ok);
        Assert.Equal(10, h.View(0).GetProperty("my").GetDouble());

        Assert.True(h.Act(0, "answer", new { value = 20 }).Ok);
        Assert.Equal(20, h.View(0).GetProperty("my").GetDouble());
        Assert.True(h.View(0).GetProperty("answered")[0].GetBoolean());
        Assert.False(h.View(0).GetProperty("answered")[1].GetBoolean());
    }

    [Fact]
    public void A_number_typed_with_spaces_and_a_comma_is_still_a_number()
    {
        var h = Table();
        Until(h, Skilky.PhaseAsk);

        Assert.True(h.Act(0, "answer", new { value = "10 000" }).Ok);
        Assert.Equal(10000, h.View(0).GetProperty("my").GetDouble());

        Assert.True(h.Act(0, "answer", new { value = "2,54" }).Ok);
        Assert.Equal(2.54, h.View(0).GetProperty("my").GetDouble(), 6);
        // Набирали з комою — з комою й підтверджуємо: крапка в українському тексті ріже око.
        Assert.Equal("Записав: 2,54", h.Reply.Message);
    }

    [Fact]
    public void Anything_that_is_not_a_number_is_refused_and_the_table_stays_as_it_was()
    {
        var h = Table();
        Until(h, Skilky.PhaseAsk);
        var before = Views.Text(h.Room.Game.View(0));

        Assert.False(h.Act(0, "answer", new { value = "багато" }).Ok);
        Assert.False(h.Act(0, "answer", new { value = "" }).Ok);
        Assert.False(h.Act(0, "тиснути", new { value = 5 }).Ok);
        Assert.Equal("Тут так не ходять", h.Reply.Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(0)));
    }

    [Fact]
    public void There_is_nothing_to_answer_before_the_question_and_nothing_after_the_deadline()
    {
        var h = Table();
        Assert.Equal(Skilky.PhaseBetween, Phase(h));
        Assert.False(h.Act(0, "answer", new { value = 5 }).Ok);
        Assert.Equal("Зачекай на запитання", h.Reply.Message);

        Until(h, Skilky.PhaseAsk);
        // Годинник переводимо без тика: фаза ще «ask», але час на неї вже вийшов.
        h.Clock.Advance(TimeSpan.FromSeconds(Skilky.AskSeconds + 1));
        Assert.False(h.Act(0, "answer", new { value = 5 }).Ok);
        Assert.Equal("Час вийшов", h.Reply.Message);
    }

    [Fact]
    public void When_everyone_has_answered_the_reveal_comes_without_waiting()
    {
        var h = Table();
        Answer(h, (0, 0), (1, 5), (2, -5));
        h.Tick(1);

        Assert.Equal(Skilky.PhaseReveal, Phase(h));
        Assert.Equal(3, h.View(0).GetProperty("reveal").GetProperty("rows").GetArrayLength());
    }

    // ---------- очки ----------

    [Theory]
    [InlineData(100, 5)]
    [InlineData(102, 5)]
    [InlineData(98, 5)]
    [InlineData(102.5, 4)]
    [InlineData(110, 4)]
    [InlineData(90, 4)]
    [InlineData(111, 3)]
    [InlineData(125, 3)]
    [InlineData(75, 3)]
    [InlineData(126, 2)]
    [InlineData(150, 2)]
    [InlineData(50, 2)]       // удвічі менше — це ще 50 %…
    [InlineData(151, 1)]
    [InlineData(200, 1)]      // …а вдвічі більше — вже лише «до двох разів»
    [InlineData(201, 0)]
    [InlineData(49, 0)]
    [InlineData(0, 0)]
    [InlineData(-100, 0)]
    public void Accuracy_is_measured_as_a_share_of_the_answer(double guess, int points)
    {
        Assert.Equal(points, Skilky.Accuracy(guess, 100, years: false));
    }

    [Theory]
    [InlineData(9, 9, 5)]
    [InlineData(8, 9, 4)]     // 11 % — а для людини «майже»: промах на одиницю не дає менше за 4
    [InlineData(10, 9, 4)]
    [InlineData(7, 9, 3)]
    [InlineData(11, 9, 3)]
    [InlineData(5, 9, 2)]
    [InlineData(15, 9, 1)]
    [InlineData(19, 9, 0)]
    [InlineData(3, 2, 4)]
    [InlineData(1, 2, 4)]
    [InlineData(4, 2, 1)]
    [InlineData(1, 1.852, 2)] // на дробовій відповіді одиниця — пів відповіді, правило не діє
    public void Off_by_one_on_a_small_whole_answer_is_almost_there(double guess, double target, int points)
    {
        Assert.Equal(points, Skilky.Accuracy(guess, target, years: false));
    }

    [Theory]
    [InlineData(1991, 5)]
    [InlineData(1990, 4)]
    [InlineData(1993, 4)]
    [InlineData(1994, 3)]
    [InlineData(1986, 3)]
    [InlineData(1997, 2)]
    [InlineData(2006, 2)]
    [InlineData(2007, 1)]
    [InlineData(2041, 1)]
    [InlineData(2042, 0)]
    public void Years_are_measured_in_years_not_in_percent(double guess, int points)
    {
        Assert.Equal(points, Skilky.Accuracy(guess, 1991, years: true));
    }

    [Theory]
    [InlineData(13_800_000_000, 13.8, "млрд років", 13.8)]   // написав повне число — це те саме
    [InlineData(150_000_000, 150, "млн км", 150)]
    [InlineData(357_000, 357, "тис. км²", 357)]
    [InlineData(1200, 357, "тис. км²", 1200)]                // а тут людина справді мала на увазі 1200 тисяч
    [InlineData(200, 150, "млн км", 200)]                    // менше за множник — завжди як є
    [InlineData(5000, 4, "кг", 5000)]                        // без множника в одиниці нічого не чіпаємо
    public void A_full_number_is_read_in_the_units_of_the_question(double guess, double target, string unit, double expected)
    {
        Assert.Equal(expected, Skilky.InUnits(guess, target, unit), 9);
    }

    [Fact]
    public void Tier_edges_hold_even_where_double_rounds_past_them()
    {
        // 2,794 проти 2,54 — рівно 10 %, але в double це 0.10000000000000009: без допуску було б 3, а не 4.
        Assert.Equal(4, Skilky.Accuracy(2.794, 2.54, years: false));
        Assert.Equal(5, Skilky.Accuracy(36.6 * 1.02, 36.6, years: false));
    }

    /// <summary>Звичайне запитання з відповіддю не меншою за 100: там промах на одиницю не втручається в шкалу.</summary>
    static bool Big(SkilkyQuestion q) => !Years(q) && q.A >= 100;

    static int[] Column(RoomHarness h, string name) => [.. h.View(0).GetProperty("reveal").GetProperty("rows")
        .EnumerateArray().Select(r => r.GetProperty(name).GetInt32())];

    [Fact]
    public void Everyone_scores_for_accuracy_and_the_closest_takes_a_bonus()
    {
        var h = Asking(5, Big);
        var c = Correct(h);
        h.Act(0, "answer", new { value = c * 1.05 });
        h.Act(1, "answer", new { value = c * 0.8 });
        h.Act(2, "answer", new { value = c * 1.6 });
        h.Act(3, "answer", new { value = c * 5 });
        h.Act(4, "answer", new { value = c * 1.09 });
        h.Tick(1);

        Assert.Equal(new[] { 0, 4, 1, 2, 3 }, Column(h, "seat"));
        Assert.Equal(new[] { 4, 4, 3, 1, 0 }, Column(h, "accuracy"));
        Assert.Equal(new[] { Skilky.BestBonus, 0, 0, 0, 0 }, Column(h, "bonus"));
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, Column(h, "fast"));
        Assert.Equal(new[] { 4 + Skilky.BestBonus, 4, 3, 1, 0 }, Column(h, "points"));
        Assert.Equal(new[] { 5L, 3L, 1L, 0L, 4L }, Enumerable.Range(0, 5).Select(s => Score(h, s)).ToArray());
        Assert.False(h.View(0).GetProperty("reveal").GetProperty("years").GetBoolean());
    }

    [Fact]
    public void A_year_question_is_scored_in_years()
    {
        var h = Asking(3, Years);
        var c = Correct(h);
        h.Act(0, "answer", new { value = c });
        h.Act(1, "answer", new { value = c - 8 });
        h.Act(2, "answer", new { value = c + 60 });
        h.Tick(1);

        Assert.Equal(Perfect, Score(h, 0));
        Assert.Equal(2, Score(h, 1));
        Assert.Equal(0, Score(h, 2));
        Assert.True(h.View(0).GetProperty("reveal").GetProperty("years").GetBoolean());
    }

    [Fact]
    public void When_everyone_is_way_off_the_closest_still_takes_one_point()
    {
        var h = Asking(2, Big);
        var c = Correct(h);
        h.Act(0, "answer", new { value = c * 3 });
        h.Act(1, "answer", new { value = c * 10 });
        h.Tick(1);

        Assert.Equal(Skilky.BestBonus, Score(h, 0));
        Assert.Equal(0, Score(h, 1));
        Assert.Contains(h.Outbox.OfType<DjSays>(), s => s.Text.Contains("Оля"));
    }

    [Fact]
    public void In_a_duel_off_by_one_is_close_to_exact()
    {
        // Той самий випадок, на який скаржились: правильна 9, один пише 9, другий 8. Було 7 : 2.
        var h = Asking(2, q => !Years(q) && q.A is >= 3 and <= 30 && q.A == Math.Round(q.A.Value));
        var c = Correct(h);
        h.Act(0, "answer", new { value = c });
        h.Act(1, "answer", new { value = c - 1 });
        h.Tick(1);

        Assert.Equal(Skilky.Bullseye + Skilky.BestBonus, Score(h, 0));
        Assert.Equal(Skilky.OffByOne, Score(h, 1));
    }

    [Fact]
    public void Equal_distance_means_an_equal_bonus()
    {
        var h = Asking(3, Big);
        var c = Correct(h);
        h.Act(0, "answer", new { value = c * 0.95 });
        h.Act(1, "answer", new { value = c * 1.05 });
        h.Act(2, "answer", new { value = c * 1.2 });
        h.Tick(1);

        Assert.Equal(4 + Skilky.BestBonus, Score(h, 0));
        Assert.Equal(4 + Skilky.BestBonus, Score(h, 1));
        Assert.Equal(3, Score(h, 2));
    }

    [Fact]
    public void At_equal_distance_whoever_answered_a_second_earlier_takes_one_more()
    {
        var h = Asking(2, Big);
        var c = Correct(h);
        h.Act(1, "answer", new { value = c });
        h.Clock.Advance(Skilky.SpeedGap);
        h.Act(0, "answer", new { value = c });
        h.Tick(1);

        Assert.Equal(new[] { 1, 0 }, Column(h, "seat"));          // швидший стоїть першим у своїй групі
        Assert.Equal(new[] { Skilky.SpeedBonus, 0 }, Column(h, "fast"));
        Assert.Equal(Perfect + Skilky.SpeedBonus, Score(h, 1));
        Assert.Equal(Perfect, Score(h, 0));
    }

    [Fact]
    public void Less_than_a_second_apart_is_not_faster()
    {
        var h = Asking(2, Big);
        var c = Correct(h);
        h.Act(0, "answer", new { value = c * 0.9 });
        h.Clock.Advance(TimeSpan.FromMilliseconds(900));
        h.Act(1, "answer", new { value = c * 1.1 });
        h.Tick(1);

        Assert.Equal(Score(h, 0), Score(h, 1));
        Assert.Equal(new[] { 0, 0 }, Column(h, "fast"));
    }

    [Fact]
    public void The_speed_bonus_works_below_the_top_too_and_counts_the_last_change()
    {
        var h = Asking(3, Big);
        var c = Correct(h);
        h.Act(1, "answer", new { value = c * 0.8 });
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Act(2, "answer", new { value = c * 1.2 });
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Act(1, "answer", new { value = c * 0.8 });   // передумав — і тепер відповів пізніше за сусіда
        h.Act(0, "answer", new { value = c });
        h.Tick(1);

        Assert.Equal(Perfect, Score(h, 0));
        Assert.Equal(3 + Skilky.SpeedBonus, Score(h, 2));
        Assert.Equal(3, Score(h, 1));
    }

    [Fact]
    public void Equal_distance_counts_as_equal_even_when_double_says_otherwise()
    {
        // Пастка живе на дробових цілях: |36.4 - 36.6| і |36.8 - 36.6| математично однакові, а в double —
        // 0.20000000000000284 і 0.19999999999999574. Шукаємо сід, на якому випадає саме таке запитання:
        // на цілій відповіді промах ±x рахується точно, і порівняння «в лоб» помилки не показує.
        RoomHarness? h = null;
        var off = 0.0;
        for (var seed = 1; seed <= 300 && h is null; seed++)
        {
            var t = Table(3, seed);
            Until(t, Skilky.PhaseAsk);
            var c = Correct(t);
            var d = Enumerable.Range(1, 99).Select(i => i / 100.0)
                .FirstOrDefault(x => Math.Abs(c - x - c) != Math.Abs(c + x - c));
            if (d != 0) { h = t; off = d; }
        }
        Assert.NotNull(h);

        var correct = Correct(h);
        h.Act(0, "answer", new { value = correct - off });
        h.Act(1, "answer", new { value = correct + off });
        h.Act(2, "answer", new { value = correct + 5 });
        h.Tick(1);

        // На екрані в обох однакова різниця — отже, найближчі обидва і бонус беруть обидва.
        Assert.Equal(Score(h, 0), Score(h, 1));
        Assert.Equal(Skilky.Accuracy(correct - off, correct, Years(Question(h))) + Skilky.BestBonus, Score(h, 0));
        Assert.True(Score(h, 2) <= Score(h, 0) - Skilky.BestBonus);   // далі за них — і без бонусу
    }

    [Fact]
    public void Whoever_stayed_silent_gets_nothing_and_is_not_in_the_table()
    {
        var h = Table(3);
        Answer(h, (0, 0), (1, 3));
        h.Tick(Skilky.AskSeconds);

        Assert.Equal(Skilky.PhaseReveal, Phase(h));
        var rows = h.View(0).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(2, rows.Select(r => r.GetProperty("seat").GetInt32()));
        Assert.Equal(0, Score(h, 2));
    }

    // ---------- кінець партії ----------

    [Fact]
    public void Five_questions_and_the_sharpest_eye_takes_the_room()
    {
        var h = Table(3);
        PlayAll(h, (0, 0), (1, 10), (2, 100));

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(Skilky.PhaseDone, Phase(h));
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.False(h.Room.Result.Draw);
        Assert.Equal(Skilky.Questions * Perfect, Score(h, 0));
        Assert.True(Score(h, 1) < Score(h, 0));
        Assert.True(Score(h, 2) <= Score(h, 1));   // далі від правди — не більше очок
        Assert.Contains("Скільки?:", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Single(h.Finished);
        Assert.Equal(Skilky.Questions * Perfect, h.Finished[0].Result.Scores![0]);
    }

    [Fact]
    public void An_even_match_ends_with_two_winners()
    {
        var h = Table(2);
        // Однакова відстань у кожному раунді — у частках від відповіді, а не в одиницях: у банку є відповіді,
        // менші за одиницю, і там «на пів менше» вже за межею «удвічі», а «на пів більше» — ще ні.
        for (var q = 0; q < Skilky.Questions && h.Room.Status == RoomStatus.Playing; q++)
        {
            Until(h, Skilky.PhaseAsk);
            var c = Correct(h);
            h.Act(0, "answer", new { value = c * 0.99 });
            h.Act(1, "answer", new { value = c * 1.01 });
            Close(h);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0, 1], h.Room.Result!.Winners);
        Assert.Equal(Score(h, 0), Score(h, 1));
    }

    [Fact]
    public void A_match_where_nobody_named_a_number_ends_in_a_draw()
    {
        var h = Table(2);
        for (var q = 0; q < Skilky.Questions && h.Room.Status == RoomStatus.Playing; q++) Close(h);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Empty(h.Room.Result.Winners);
        Assert.Contains("ніхто нічого не вгадав", h.Room.Result.Text);
    }

    [Fact]
    public void Uncle_Hlek_says_a_word_before_every_reveal()
    {
        var h = Table(2);
        PlayAll(h, (0, 2), (1, 7));

        var said = h.Outbox.OfType<DjSays>().ToList();
        Assert.Equal(Skilky.Questions, said.Count);
        Assert.All(said, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        Assert.Contains(said, s => s.Text.Contains("Оля"));
    }

    [Fact]
    public void Rematch_starts_a_clean_match_with_the_seats_turned_around()
    {
        var h = Table(2);
        PlayAll(h, (0, 0), (1, 50));
        Assert.Equal(Skilky.Questions * Perfect, Score(h, 0));

        Assert.True(h.Rematch("Оля").Ok);
        Assert.Equal("Оля", h.Room.Seats[1]);          // місця обернулись, як і всюди в каркасі
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(Skilky.PhaseBetween, Phase(h));
        Assert.Equal(1, Round(h));
        Assert.Equal(0, Score(h, 0));
        Assert.Equal(0, Score(h, 1));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
    }

    [Fact]
    public void A_party_survives_one_player_walking_out()
    {
        var h = Table(3);
        Answer(h, (0, 0), (1, 5));
        h.Leave("Ганна");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(h.View(0).GetProperty("answered")[2].GetBoolean());
        h.Tick(1);
        Assert.Equal(Skilky.PhaseReveal, Phase(h));   // лишились двоє, обидва відповіли — розкриваємо
    }

    [Fact]
    public void A_player_left_alone_plays_on()
    {
        var h = Table(2);
        Until(h, Skilky.PhaseAsk);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var c = Correct(h);
        h.Act(0, "answer", new { value = c });
        h.Tick(1);
        Assert.Equal(Skilky.PhaseReveal, Phase(h));
        Assert.Equal(Skilky.Bullseye, Score(h, 0));   // сам — без бонусу «найближчому»
    }

    [Fact]
    public void When_everyone_leaves_the_match_is_over()
    {
        var h = Table(2);
        Until(h, Skilky.PhaseAsk);
        h.Leave("Петро");
        h.Leave("Оля");

        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.Contains("розійшлись", h.Finished.Single().Result.Text);
    }

    // ---------- соло, налаштування й черепки ----------

    [Fact]
    public void One_player_can_start_alone_and_play_a_whole_match()
    {
        var h = new RoomHarness("skilky");
        h.Join("Оля");
        Assert.Contains("самому", h.Reply.Message);
        Assert.True(h.Start().Ok);

        PlayAll(h, (0, 0));

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        // Самому бонус «найближчому» не дається: лише точність.
        Assert.Equal(Skilky.Questions * Skilky.Bullseye, Score(h, 0));
        var award = Assert.Single(h.Awards);
        Assert.Equal((Skilky.Questions * Skilky.Bullseye) / Skilky.PointsPerShard, award.Shards);
        Assert.Equal("points:1", award.Reason);
    }

    [Fact]
    public void Everyone_who_played_to_the_end_gets_shards_for_points_and_a_rematch_pays_again()
    {
        var h = Table(2);
        PlayAll(h, (0, 0), (1, 50));

        foreach (var seat in new[] { 0, 1 })
        {
            var shards = Skilky.Shards(Score(h, seat));
            var paid = h.Awards.Where(a => a.Nick == h.NickOf(seat)).ToList();
            if (shards == 0) Assert.Empty(paid);
            else Assert.Equal(shards, Assert.Single(paid).Shards);
        }
        Assert.Equal(Skilky.Questions * Perfect / Skilky.PointsPerShard, h.Awards.Single(a => a.Nick == "Оля").Shards);

        Assert.True(h.Rematch("Оля").Ok);
        PlayAll(h, (0, 0), (1, 0));
        Assert.Contains(h.Awards, a => a.Reason == "points:2");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(34, 6)]
    [InlineData(105, 21)]
    public void Five_points_make_a_shard(long points, int shards)
    {
        Assert.Equal(shards, Skilky.Shards(points));
    }

    [Fact]
    public void The_host_picks_questions_time_and_topic()
    {
        var h = new RoomHarness("skilky", options: new { questions = "3", seconds = "15", topic = "ukraine" });
        h.Join("Оля");
        h.Join("Петро");
        h.Start();

        Until(h, Skilky.PhaseAsk);
        Assert.Equal(3, h.View(0).GetProperty("of").GetInt32());
        Assert.Equal(15, h.View(0).GetProperty("seconds").GetInt32());
        h.Tick(15);
        Assert.Equal(Skilky.PhaseReveal, Phase(h));   // 15 секунд, а не 30

        var asked = new List<SkilkyQuestion>();
        var fresh = new RoomHarness("skilky", seed: 11, options: new { questions = "15", topic = "ukraine" });
        fresh.Join("Оля");
        fresh.Start();
        while (fresh.Room.Status == RoomStatus.Playing)
        {
            Until(fresh, Skilky.PhaseAsk);
            if (fresh.Room.Status != RoomStatus.Playing) break;
            asked.Add(Question(fresh));
            fresh.Act(0, "answer", new { value = 1 });
            Close(fresh);
        }
        Assert.Equal(15, asked.Count);
        Assert.All(asked, q => Assert.Equal("ukraine", q.Topic));
    }

    [Fact]
    public void The_host_may_pick_several_topics()
    {
        var h = new RoomHarness("skilky", seed: 11, options: new { questions = "15", topic = "science,world" });
        h.Join("Оля");
        Assert.Equal("science,world", h.Room.Options["topic"]);
        h.Start();

        var asked = new List<SkilkyQuestion>();
        while (h.Room.Status == RoomStatus.Playing)
        {
            Until(h, Skilky.PhaseAsk);
            if (h.Room.Status != RoomStatus.Playing) break;
            asked.Add(Question(h));
            h.Act(0, "answer", new { value = 1 });
            Close(h);
        }
        Assert.Equal(15, asked.Count);
        Assert.All(asked, q => Assert.Contains(q.Topic, new[] { "science", "world" }));
        // Обидві теми справді йдуть у партію, а не лише перша зі списку.
        Assert.Equal(2, asked.Select(q => q.Topic).Distinct().Count());
    }

    [Theory]
    [InlineData("world,космос,ukraine,world", "ukraine,world")]   // лише знайомі, без повторів, у порядку паспорта
    [InlineData(" tech , culture ", "culture,tech")]
    [InlineData("ukraine,all", "all")]                              // «усі» перемагають решту
    [InlineData("radio", "all")]                                    // радіо окремою темою не обирають
    [InlineData("космос", "all")]
    [InlineData("", "all")]
    [InlineData(",,", "all")]
    public void Several_topics_are_cleaned_up_by_the_platform(string sent, string kept)
    {
        var h = new RoomHarness("skilky", options: new { topic = sent });
        h.Join("Оля");
        Assert.Equal(kept, h.Room.Options["topic"]);
    }

    [Fact]
    public void Topics_from_the_option_become_a_set_and_all_means_any()
    {
        Assert.Null(SkilkyTopics.Parse(null));
        Assert.Null(SkilkyTopics.Parse("all"));
        Assert.Null(SkilkyTopics.Parse("science,all"));
        Assert.Null(SkilkyTopics.Parse("radio"));
        Assert.Equal(["science", "ukraine"], SkilkyTopics.Parse("science,ukraine")!.Order());

        var radio = new SkilkyQuestion { Q = "Скільки треків?", Dyn = "plays7d", Topic = SkilkyTopics.Radio };
        var ukraine = new SkilkyQuestion { Q = "Скільки областей?", A = 24, Topic = "ukraine" };
        Assert.True(SkilkyTopics.Fits(radio, null));
        Assert.False(SkilkyTopics.Fits(radio, SkilkyTopics.Parse("ukraine,science")));
        Assert.True(SkilkyTopics.Fits(ukraine, SkilkyTopics.Parse("ukraine,science")));
    }

    [Fact]
    public void Several_topics_may_come_from_the_wire_as_an_array()
    {
        const string wire = "{\"questions\":\"3\",\"topic\":[\"tech\",\"ukraine\"]}";
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("tech,ukraine", RoomOptions.From(raw)!["topic"]);
    }

    [Fact]
    public void Options_that_are_not_on_the_list_fall_back_to_defaults()
    {
        var h = new RoomHarness("skilky", options: new { questions = "99", seconds = "1", topic = "космос" });
        h.Join("Оля");
        h.Start();
        Until(h, Skilky.PhaseAsk);

        Assert.Equal(Skilky.Questions, h.View(0).GetProperty("of").GetInt32());
        Assert.Equal(Skilky.AskSeconds, h.View(0).GetProperty("seconds").GetInt32());
    }

    [Fact]
    public void Every_question_has_a_topic_and_only_radio_questions_are_about_the_radio()
    {
        var known = SkilkyTopics.All.Select(t => t.Key).Where(k => k != SkilkyTopics.Any).ToHashSet();
        Assert.All(SkilkyBank.All, q =>
        {
            if (q.IsDynamic) Assert.Equal(SkilkyTopics.Radio, q.Topic);
            else Assert.Contains(q.Topic!, known);
        });
        // Кожну тему є з чого грати навіть на п'ятнадцять запитань по кілька разів.
        Assert.All(known, k => Assert.True(SkilkyBank.All.Count(q => q.Topic == k) >= 150, k));
    }

    // ---------- приховане, види й кадри ----------

    [Fact]
    public void Someone_elses_number_is_nowhere_to_be_seen_while_the_question_is_open()
    {
        var h = Table(2);
        Until(h, Skilky.PhaseAsk);
        h.Act(0, "answer", new { value = 123456 });

        var mine = Views.Text(h.Room.Game.View(0));
        var theirs = Views.Text(h.Room.Game.View(1));
        var watcher = Views.Text(h.Room.Game.View(null));

        Assert.Contains("123456", mine);
        Assert.DoesNotContain("123456", theirs);
        Assert.DoesNotContain("123456", watcher);
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("my").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("reveal").ValueKind);
        Assert.True(h.View(1).GetProperty("answered")[0].GetBoolean());   // видно лише, що вже відповів
    }

    [Fact]
    public void After_the_reveal_every_number_is_on_the_table()
    {
        var h = Table(2);
        Answer(h, (0, 0), (1, 7));
        h.Tick(1);

        var theirs = Views.Text(h.Room.Game.View(1));
        Assert.Contains("\"reveal\"", theirs);
        var rows = h.View(1).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(7, rows[1].GetProperty("diff").GetDouble());
    }

    [Fact]
    public void The_view_has_the_shape_the_spec_promises()
    {
        var h = Table(2);
        Until(h, Skilky.PhaseAsk);
        var v = h.View(0);

        foreach (var name in new[] { "round", "of", "phase", "question", "unit", "endsAt",
            "answered", "my", "reveal", "scores", "result" })
            Assert.True(Views.Has(v, name), name);

        Assert.Equal(Skilky.MaxSeats, v.GetProperty("answered").GetArrayLength());
        Assert.Equal(Skilky.MaxSeats, v.GetProperty("scores").GetArrayLength());
        Assert.Equal(JsonValueKind.String, v.GetProperty("endsAt").ValueKind);
        Assert.True(DateTimeOffset.TryParse(v.GetProperty("endsAt").GetString(), out _));
    }

    [Fact]
    public void The_frame_is_compact_and_carries_nothing_secret()
    {
        var h = Table(2);
        Until(h, Skilky.PhaseAsk);
        h.Act(0, "answer", new { value = 987654 });
        h.Tick(1);

        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        foreach (var name in new[] { "round", "of", "phase", "endsAt", "answered", "scores" })
            Assert.True(Views.Has(frame, name), name);
        Assert.False(Views.Has(frame, "my"));
        Assert.False(Views.Has(frame, "question"));
        Assert.DoesNotContain("987654", frame.ToString());
        Assert.True(frame.GetProperty("answered")[0].GetBoolean());
    }

    [Fact]
    public void A_tick_that_changes_nothing_sends_only_a_frame()
    {
        var h = Table(2);
        Until(h, Skilky.PhaseAsk);
        var views = h.Outbox.OfType<RoomViews>().Count();
        var frames = h.Outbox.OfType<RoomFrame>().Count();

        h.Tick(1);
        Assert.Equal(views, h.Outbox.OfType<RoomViews>().Count());
        Assert.Equal(frames + 1, h.Outbox.OfType<RoomFrame>().Count());

        // А от чиєсь число — це вже привід розіслати види: у кожного вони свої.
        h.Act(0, "answer", new { value = 1 });
        h.Tick(1);
        Assert.Equal(views + 1, h.Outbox.OfType<RoomViews>().Count());
    }

    // ---------- вибір запитань ----------

    [Fact]
    public void A_match_never_asks_the_same_question_twice()
    {
        var asked = AskedQuestions(Table(2));
        Assert.Equal(Skilky.Questions, asked.Count);
        Assert.Equal(Skilky.Questions, asked.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_same_seed_asks_the_same_questions()
    {
        Assert.Equal(AskedQuestions(Table(2, seed: 7)), AskedQuestions(Table(2, seed: 7)));
        Assert.NotEqual(AskedQuestions(Table(2, seed: 7)), AskedQuestions(Table(2, seed: 8)));
    }

    [Fact]
    public void Without_a_database_only_the_questions_with_a_written_answer_are_asked()
    {
        var dynamic = SkilkyBank.All.Where(q => q.IsDynamic).Select(q => q.Q).ToHashSet(StringComparer.Ordinal);
        for (var seed = 1; seed <= 8; seed++)
            Assert.All(AskedQuestions(Table(2, seed: seed)), q => Assert.DoesNotContain(q, dynamic));
    }

    [Fact]
    public void With_a_lively_database_a_dynamic_question_can_come_up()
    {
        using var temp = new TempDb();
        temp.Db.UpsertTrack(new TrackInfo("t1", "Пісня", "Гурт", 180, null, "https://x/1", null));
        for (var i = 0; i < 40; i++) temp.Db.StartPlay("t1", "user", "Оля", null, null);
        var services = new ServiceCollection().AddSingleton(temp.Db).BuildServiceProvider();

        var dynamic = SkilkyBank.All.Where(q => q.IsDynamic).Select(q => q.Q).ToHashSet(StringComparer.Ordinal);
        // Банк великий, і на випадкову партію динамічне випадає рідко. Тож хай Оля й Петро «бачили» всі
        // статичні: пам'ять тоді мусить віддати саме динамічні, з ненульовою відповіддю на цій базі.
        new SkilkySeen(temp.Db).Mark([SkilkySeen.NickKey("Оля"), SkilkySeen.NickKey("Петро")],
            SkilkyBank.All.Where(q => !q.IsDynamic), DateTimeOffset.UtcNow.AddDays(-1));

        var asked = AskedQuestions(Table(2, seed: 1, services: services));
        Assert.NotEmpty(asked);
        Assert.All(asked, q => Assert.Contains(q, dynamic));
    }

    // ---------- пам'ять: хто що вже бачив ----------

    static IServiceProvider WithDb(TempDb temp) => new ServiceCollection().AddSingleton(temp.Db).BuildServiceProvider();

    /// <summary>Стіл із заданими ніками — щоб різні партії сідали тими самими або іншими людьми.</summary>
    static RoomHarness TableOf(string[] nicks, int seed, IServiceProvider? services = null)
    {
        var h = new RoomHarness("skilky", seed: seed, services: services);
        foreach (var nick in nicks) h.Join(nick);
        h.Start();
        return h;
    }

    /// <summary>Зіграти партію швидко: усі відповідають одразу. Повертає тексти запитань.</summary>
    static List<string> PlayQuick(RoomHarness h)
    {
        var list = new List<string>();
        while (h.Room.Status == RoomStatus.Playing)
        {
            Until(h, Skilky.PhaseAsk);
            if (h.Room.Status != RoomStatus.Playing) break;
            list.Add(h.View(null).GetProperty("question").GetString()!);
            var c = Correct(h);
            for (var s = 0; s < h.Room.Seats.Length; s++)
                if (h.Room.Seats[s] is not null) h.Act(s, "answer", new { value = c });
            Close(h);
        }
        return list;
    }

    [Fact]
    public void The_same_friends_do_not_see_a_question_twice_while_fresh_ones_remain()
    {
        using var temp = new TempDb();
        var services = WithDb(temp);
        var all = new List<string>();
        for (var match = 0; match < 12; match++)
            all.AddRange(PlayQuick(TableOf(["Оля", "Петро"], seed: 100 + match, services)));

        Assert.Equal(12 * Skilky.Questions, all.Count);
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Memory_is_per_player()
    {
        using var temp = new TempDb();
        var services = WithDb(temp);
        var olya = PlayQuick(TableOf(["Оля", "Петро"], seed: 5, services));

        // Оля сідає з іншою людиною — її запитань там уже не буде.
        var again = PlayQuick(TableOf(["Ганна", "Оля"], seed: 5, services));
        Assert.Empty(again.Intersect(olya));

        // А ті, хто ще нічого не бачив, отримують рівно те, що дало б саме тасування, без жодної пам'яті.
        Assert.Equal(PlayQuick(TableOf(["Іван", "Марта"], seed: 9)),
            PlayQuick(TableOf(["Іван", "Марта"], seed: 9, services)));
    }

    [Fact]
    public void A_question_counts_as_seen_only_once_it_is_on_screen()
    {
        using var temp = new TempDb();
        var seen = new SkilkySeen(temp.Db);
        var nicks = new[] { SkilkySeen.NickKey("Оля") };
        var h = TableOf(["Оля", "Петро"], seed: 3, WithDb(temp));

        Assert.Equal(Skilky.PhaseBetween, Phase(h));
        Assert.Empty(seen.LastSeen(nicks));        // п'ять запитань вибрано, але ще жодного не показано

        Until(h, Skilky.PhaseAsk);
        var only = Assert.Single(seen.LastSeen(nicks));
        Assert.Equal(Question(h).Key, only.Key);
        Assert.Equal(h.Clock.UtcNow, only.Value);
    }

    [Fact]
    public void A_nick_is_remembered_regardless_of_case_and_spaces()
    {
        using var temp = new TempDb();
        var seen = new SkilkySeen(temp.Db);
        var q = SkilkyBank.All[0];
        var at = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        seen.Mark([SkilkySeen.NickKey("Оля")], q, at);

        Assert.Equal(at, seen.LastSeen([SkilkySeen.NickKey("  ОЛЯ ")])[q.Key]);
        Assert.Empty(seen.LastSeen([SkilkySeen.NickKey("Петро")]));

        // Бачив ще раз пізніше — пам'ятаємо свіжіший раз.
        seen.Mark([SkilkySeen.NickKey("оля")], q, at.AddDays(3));
        Assert.Equal(at.AddDays(3), seen.LastSeen([SkilkySeen.NickKey("Оля")])[q.Key]);
    }

    [Fact]
    public void Freshest_takes_the_unseen_first_then_the_longest_ago_seen()
    {
        var at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var last = new Dictionary<string, DateTimeOffset>
        {
            ["a"] = at.AddDays(5), ["b"] = at.AddDays(1), ["d"] = at.AddDays(3),
        };
        // Порядок «тасування» зберігається серед однаково свіжих: c і e обидва не бачені.
        Assert.Equal(new[] { "e", "c", "b", "d" }, SkilkySeen.Freshest(new[] { "e", "a", "b", "c", "d" }, x => x, last, 4));
        Assert.Equal(2, SkilkySeen.Freshest(new[] { "a", "b" }, x => x, last, 5).Count);
    }

    [Fact]
    public void Without_a_database_the_memory_stays_silent()
    {
        var seen = new SkilkySeen(null);
        seen.Mark(["оля"], SkilkyBank.All[0], DateTimeOffset.UtcNow);
        Assert.Empty(seen.LastSeen(["оля"]));
    }

    [Fact]
    public void Every_question_in_the_bank_has_its_own_key()
    {
        Assert.Equal(SkilkyBank.All.Count, SkilkyBank.All.Select(q => q.Key).Distinct(StringComparer.Ordinal).Count());
    }

    // ---------- каталог і продуктивність ----------

    [Fact]
    public void Skilky_is_in_the_catalog_as_a_party_game()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "skilky");
        Assert.Equal("party", game.Group);
        Assert.Equal("byHost", game.Start);
        Assert.True(game.Hidden);
        Assert.False(game.Rated);
        Assert.Equal(1000, game.TickMs);
        Assert.Equal(1, game.MinPlayers);                 // можна й самому
        Assert.Equal(Skilky.MaxSeats, game.MaxPlayers);
        Assert.Equal("skilky", game.Module);
        Assert.True(game.HasCss);
        Assert.Equal(["questions", "seconds", "topic"], game.Options.Select(o => o.Key));
        Assert.Equal("5", game.Options[0].Default);
        Assert.Equal(["3", "5", "7", "10", "15"], game.Options[0].Values.Select(v => v[0]));
        Assert.Equal([false, false, true], game.Options.Select(o => o.Multi));   // теми — можна кілька
        Assert.Equal("all", game.Options[2].Default);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_a_full_table_are_instant()
    {
        var h = Table(Skilky.MaxSeats);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) h.Rematch();
            if (Phase(h) == Skilky.PhaseAsk)
                for (var s = 0; s < Skilky.MaxSeats; s++) h.Act(s, "answer", new { value = 100 + s + i });
            h.Tick(1);
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }
}
