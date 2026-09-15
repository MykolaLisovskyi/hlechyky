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
    public void The_bank_holds_at_least_a_hundred_and_fifty_questions()
    {
        Assert.True(SkilkyBank.All.Count >= 150, $"у банку лише {SkilkyBank.All.Count} запитань");
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
    [InlineData(102.5, 3)]
    [InlineData(110, 3)]
    [InlineData(90, 3)]
    [InlineData(111, 2)]
    [InlineData(125, 2)]
    [InlineData(75, 2)]
    [InlineData(126, 1)]
    [InlineData(74, 1)]
    [InlineData(200, 1)]      // удвічі більше…
    [InlineData(50, 1)]       // …і вдвічі менше — однаково далеко
    [InlineData(201, 0)]
    [InlineData(49, 0)]
    [InlineData(0, 0)]
    [InlineData(-100, 0)]
    public void Accuracy_is_measured_as_a_share_of_the_answer(double guess, int points)
    {
        Assert.Equal(points, Skilky.Accuracy(guess, 100, years: false));
    }

    [Theory]
    [InlineData(1991, 5)]
    [InlineData(1994, 3)]
    [InlineData(1988, 3)]
    [InlineData(2001, 2)]
    [InlineData(1981, 2)]
    [InlineData(2041, 1)]
    [InlineData(1941, 1)]
    [InlineData(2042, 0)]
    [InlineData(1992, 3)]     // рік — це або точно, або вже ні
    public void Years_are_measured_in_years_not_in_percent(double guess, int points)
    {
        Assert.Equal(points, Skilky.Accuracy(guess, 1991, years: true));
    }

    [Fact]
    public void Tier_edges_hold_even_where_double_rounds_past_them()
    {
        // 2,794 проти 2,54 — рівно 10 %, але в double це 0.10000000000000009: без допуску було б 2, а не 3.
        Assert.Equal(3, Skilky.Accuracy(2.794, 2.54, years: false));
        Assert.Equal(5, Skilky.Accuracy(36.6 * 1.02, 36.6, years: false));
    }

    [Fact]
    public void Everyone_scores_for_accuracy_and_the_closest_takes_a_bonus()
    {
        var h = Asking(5, q => !Years(q));
        var c = Correct(h);
        h.Act(0, "answer", new { value = c * 1.05 });
        h.Act(1, "answer", new { value = c * 0.8 });
        h.Act(2, "answer", new { value = c * 1.5 });
        h.Act(3, "answer", new { value = c * 5 });
        h.Act(4, "answer", new { value = c * 1.09 });
        h.Tick(1);

        var rows = h.View(0).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(new[] { 0, 4, 1, 2, 3 }, rows.Select(r => r.GetProperty("seat").GetInt32()).ToArray());
        Assert.Equal(new[] { 3 + Skilky.BestBonus, 3, 2, 1, 0 }, rows.Select(r => r.GetProperty("points").GetInt32()).ToArray());
        Assert.Equal(new[] { Skilky.BestBonus, 0, 0, 0, 0 }, rows.Select(r => r.GetProperty("bonus").GetInt32()).ToArray());
        Assert.Equal(new[] { 5L, 2L, 1L, 0L, 3L }, Enumerable.Range(0, 5).Select(s => Score(h, s)).ToArray());
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
    public void In_a_duel_a_wild_guess_earns_nothing_even_against_a_wilder_one()
    {
        // Удвох за старими місцями 3/2 той, хто промазав, однаково брав очки. Тепер — ні: мимо обидва —
        // і бонус найближчому теж не світить, бо хвалити нема за що.
        var h = Asking(2, q => !Years(q));
        var c = Correct(h);
        h.Act(0, "answer", new { value = c * 3 });
        h.Act(1, "answer", new { value = c * 10 });
        h.Tick(1);

        Assert.Equal(0, Score(h, 0));
        Assert.Equal(0, Score(h, 1));
        Assert.Contains(h.Outbox.OfType<DjSays>(), s => s.Text.Contains("Оля"));
    }

    [Fact]
    public void In_a_duel_a_close_guess_beats_a_wide_one_by_more_than_a_point()
    {
        var h = Asking(2, q => !Years(q));
        var c = Correct(h);
        h.Act(0, "answer", new { value = c });
        h.Act(1, "answer", new { value = c * 1.6 });
        h.Tick(1);

        Assert.Equal(Perfect, Score(h, 0));
        Assert.Equal(1, Score(h, 1));
    }

    [Fact]
    public void Equal_distance_means_an_equal_bonus()
    {
        var h = Asking(3, q => !Years(q));
        var c = Correct(h);
        h.Act(0, "answer", new { value = c * 0.95 });
        h.Act(1, "answer", new { value = c * 1.05 });
        h.Act(2, "answer", new { value = c * 1.2 });
        h.Tick(1);

        Assert.Equal(3 + Skilky.BestBonus, Score(h, 0));
        Assert.Equal(3 + Skilky.BestBonus, Score(h, 1));
        Assert.Equal(2, Score(h, 2));
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
        // Однакова відстань у кожному раунді. Пів одиниці, а не одиниця: найменша відповідь у банку 1,852, і
        // «на одиницю менше» там уже за межею «удвічі», а «на одиницю більше» — ще ні.
        PlayAll(h, (0, -0.5), (1, 0.5));

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
    public void When_only_one_is_left_the_match_is_over()
    {
        var h = Table(2);
        Until(h, Skilky.PhaseAsk);
        h.Leave("Петро");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("розійшлись", h.Room.Result.Text);
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
        var seen = false;
        for (var seed = 1; seed <= 40 && !seen; seed++)
            seen = AskedQuestions(Table(2, seed: seed, services: services)).Any(dynamic.Contains);

        Assert.True(seen, "жива база — а динамічні запитання так і не трапились");
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
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(Skilky.MaxSeats, game.MaxPlayers);
        Assert.Equal("skilky", game.Module);
        Assert.True(game.HasCss);
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
