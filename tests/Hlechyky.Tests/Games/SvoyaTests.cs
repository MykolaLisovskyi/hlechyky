using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>Пакети для гри без бази: вбудований «Міні» і чужий приватний.</summary>
sealed class FakeSvoyaPacks : ISvoyaPackSource
{
    public readonly Dictionary<string, (SvoyaPack Pack, string Owner)> Packs = [];
    public readonly List<string> Played = [];

    public FakeSvoyaPacks()
    {
        var mini = SvoyaPackTests.Mini();
        SvoyaBuiltin.Stamp(mini, "mini");
        Packs["b_mini"] = (mini, "");
        var secret = SvoyaPackTests.Mini();
        secret.Id = "p_secret";
        Packs["p_secret"] = (secret, "олег");
        var nofinal = SvoyaPackTests.Mini();
        nofinal.Rounds.RemoveAt(1);
        nofinal.Id = "b_nofinal";
        SvoyaBuiltin.Stamp(nofinal, "nofinal");
        Packs["b_nofinal"] = (nofinal, "");
    }

    /// <summary>Вбудований пакет із одним раундом (одна тема, задані типи й ціни) і фіналом на дві теми.</summary>
    public string Add(string id, params (string Type, int Price)[] cells)
    {
        var p = new SvoyaPack { Id = id, Title = id };
        p.Rounds.Add(new SvoyaRound
        {
            Name = "Раунд",
            Themes = [new SvoyaTheme
            {
                Name = "Тема",
                Questions = [.. cells.Select((c, i) => new SvoyaQuestion { Price = c.Price, Type = c.Type, Text = "Питання " + (i + 1), Answer = "відповідь" + (i + 1) })],
            }],
        });
        p.Rounds.Add(new SvoyaRound
        {
            Name = "Фінал",
            Type = SvoyaRound.Final,
            Themes = [new SvoyaTheme { Name = "Ф1", Questions = [new SvoyaQuestion { Text = "Фінал один", Answer = "фінал1" }] },
                      new SvoyaTheme { Name = "Ф2", Questions = [new SvoyaQuestion { Text = "Фінал два", Answer = "фінал2" }] }],
        });
        SvoyaBuiltin.Stamp(p, id);
        Packs[id] = (p, "");
        return id;
    }

    public SvoyaPack? Playable(string id, string hostNick) =>
        Packs.TryGetValue(id, out var p) && (p.Owner == "" || p.Owner == Auth.NickKey(hostNick)) ? p.Pack.Clone() : null;

    public void NotePlayed(string id) => Played.Add(id);
}

/// <summary>Голос, що готовий одразу (або ніколи) і звучить рівно стільки секунд, скільки сказали.</summary>
sealed class FakeSvoyaVoice(double seconds = 2, bool ready = true) : ISvoyaVoice
{
    public bool Ready_ = ready;
    public readonly List<string> Prepared = [];
    public bool Enabled => true;
    public readonly List<string> Urgent = [];
    public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false) => (urgent ? Urgent : Prepared).AddRange(texts);
    public SvoyaClip? Ready(string voice, string text) => Ready_ ? new SvoyaClip($"/tts/{text.Length}.mp3", seconds) : null;
}

/// <summary>«Своя гра» (specs/svoya.md §3): лобі, поле, кнопка, відповідь, розкриття, апеляція, живий ведучий.</summary>
public class SvoyaTests
{
    internal static RoomHarness Table(object? options = null, string[]? nicks = null, ISvoyaVoice? voice = null,
        FakeSvoyaPacks? packs = null, int seed = 5, bool start = true, string pack = "b_mini")
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISvoyaPackSource>(packs ?? new FakeSvoyaPacks());
        if (voice is not null) sc.AddSingleton(voice);
        var h = new RoomHarness("svoya", options, seed, sc.BuildServiceProvider());
        foreach (var n in nicks ?? ["Оля", "Петро"]) h.Join(n);
        if (!start) return h;
        Assert.True(h.Act(0, "pack", new { id = pack }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    internal static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    /// <summary>Тикати, поки фаза не стане <paramref name="phase"/> (або не вийде ліміт — тоді тест упаде).</summary>
    internal static void Until(RoomHarness h, string phase, int max = 4000)
    {
        for (var i = 0; i < max && Phase(h) != phase; i++) h.Tick();
        Assert.Equal(phase, Phase(h));
    }

    internal static int Chooser(RoomHarness h) => h.View(null).GetProperty("chooser").GetInt32();

    internal static int Other(RoomHarness h, int seat) => seat == 0 ? 1 : 0;

    /// <summary>Від старту до читання клітинки (тема, запитання) — від імені того, хто обирає.</summary>
    internal static int Open(RoomHarness h, int theme = 0, int q = 0)
    {
        Until(h, Svoya.Board);
        var c = Chooser(h);
        Assert.True(h.Act(c, "pick", new { theme, q }).Ok);
        Assert.Equal(Svoya.Reading, Phase(h));
        return c;
    }

    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    static bool HasAnswer(JsonElement v) => v.GetProperty("answer").ValueKind != JsonValueKind.Null;

    // ---------- лобі ----------

    [Fact]
    public void Host_picks_a_pack_in_the_lobby_and_everyone_sees_its_themes()
    {
        var h = Table(start: false);
        Assert.Equal("Оберіть пакет", h.Start().Message);
        Assert.Equal("Пакет обирає господар столу", h.Act(1, "pack", new { id = "b_mini" }).Message);
        Assert.False(h.Act(0, "pack", new { id = "p_secret" }).Ok);    // чужий приватний
        Assert.False(h.Act(0, "pack", new { id = "p_nothing" }).Ok);
        Assert.True(h.Act(0, "pack", new { id = "b_mini" }).Ok);

        var v = h.View(1);
        Assert.Equal(Svoya.Lobby, v.GetProperty("phase").GetString());
        Assert.Equal("Міні", v.GetProperty("pack").GetProperty("title").GetString());
        Assert.Equal("Література", v.GetProperty("pack").GetProperty("rounds")[0].GetProperty("themes")[0].GetString());
        Assert.DoesNotContain("Енеїд", v.GetRawText());
        Assert.DoesNotContain("Котляревськ", v.GetRawText());
        Assert.True(h.View(0).GetProperty("me").GetProperty("canChoosePack").GetBoolean());
        Assert.False(v.GetProperty("me").GetProperty("canChoosePack").GetBoolean());
        Assert.True(h.Start().Ok);
    }

    [Fact]
    public void Owner_can_play_a_private_pack()
    {
        var h = Table(nicks: ["Олег", "Петро"], start: false);
        Assert.True(h.Act(0, "pack", new { id = "p_secret" }).Ok);
    }

    [Fact]
    public void Game_starts_with_intro_then_board_and_notes_the_play()
    {
        var packs = new FakeSvoyaPacks();
        var h = Table(packs: packs);
        Assert.Equal(Svoya.Intro, Phase(h));
        Assert.Equal(["b_mini"], packs.Played);
        var v = h.View(null);
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal("Перший раунд", v.GetProperty("roundName").GetString());
        Assert.Contains("Література, Географія", v.GetProperty("say").GetProperty("text").GetString());
        Until(h, Svoya.Board);
        var board = h.View(null).GetProperty("board");
        Assert.Equal(2, board.GetArrayLength());
        Assert.Equal([100, 200], board[0].GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("price").GetInt32()));
        Assert.All(board[1].GetProperty("cells").EnumerateArray(), c => Assert.True(c.GetProperty("open").GetBoolean()));
        Assert.InRange(Chooser(h), 0, 1);
    }

    // ---------- поле ----------

    [Fact]
    public void Only_the_chooser_picks_and_a_played_cell_stays_closed()
    {
        var h = Table();
        Until(h, Svoya.Board);
        var c = Chooser(h);
        Assert.StartsWith("Обирає", h.Act(Other(h, c), "pick", new { theme = 0, q = 0 }).Message);
        Assert.False(h.Act(c, "pick", new { theme = 5, q = 0 }).Ok);
        Assert.True(h.Act(c, "pick", new { theme = 1, q = 1 }).Ok);
        var v = h.View(null);
        Assert.Equal("Найдовша річка України?", v.GetProperty("question").GetProperty("text").GetString());
        Assert.Equal(200, v.GetProperty("question").GetProperty("price").GetInt32());
        Assert.False(v.GetProperty("board")[1].GetProperty("cells")[1].GetProperty("open").GetBoolean());
    }

    [Fact]
    public void Slow_chooser_gets_the_cheapest_open_cell()
    {
        var h = Table();
        Until(h, Svoya.Board);
        h.Clock.AdvanceMs(Svoya.PickMs);
        h.Tick();
        var q = h.View(null).GetProperty("question");
        Assert.Equal(100, q.GetProperty("price").GetInt32());
        Assert.Equal("Література", q.GetProperty("theme").GetString());
    }

    // ---------- кнопка ----------

    [Fact]
    public void Early_buzz_during_reading_answers_straight_away()
    {
        var h = Table();
        var c = Open(h);
        Assert.True(h.View(c).GetProperty("me").GetProperty("canBuzz").GetBoolean());
        Assert.True(h.Act(c, "buzz").Ok);
        var v = h.View(null);
        Assert.Equal(Svoya.Answering, v.GetProperty("phase").GetString());
        Assert.Equal(c, v.GetProperty("answering").GetInt32());
    }

    [Fact]
    public void Without_early_buzz_the_button_opens_after_reading()
    {
        var h = Table(new { early = "off" });
        var c = Open(h);
        Assert.Equal("Ще читають — зачекай", h.Act(c, "buzz").Message);
        Assert.False(h.View(c).GetProperty("me").GetProperty("canBuzz").GetBoolean());
        Until(h, Svoya.Buzz);
        Assert.True(h.Act(c, "buzz").Ok);
    }

    [Fact]
    public void First_press_answers_and_the_rest_are_logged_with_milliseconds()
    {
        var h = Table(nicks: ["Оля", "Петро", "Іра"]);
        Open(h);
        Until(h, Svoya.Buzz);
        h.Clock.AdvanceMs(300);
        Assert.True(h.Act(2, "buzz").Ok);
        h.Clock.AdvanceMs(120);
        Assert.StartsWith("Не встиг — відповідає Іра", h.Act(0, "buzz").Message);
        var presses = h.View(null).GetProperty("presses");
        Assert.Equal(2, presses[0].GetProperty("seat").GetInt32());
        Assert.Equal(300, presses[0].GetProperty("ms").GetInt32());
        Assert.Equal(420, presses[1].GetProperty("ms").GetInt32());
        Assert.Equal(2, h.View(null).GetProperty("answering").GetInt32());
    }

    [Fact]
    public void Nobody_presses_and_the_answer_is_shown_without_points()
    {
        var h = Table();
        Open(h);
        Until(h, Svoya.Buzz);
        h.Clock.AdvanceMs(10_000);
        h.Tick();
        var v = h.View(null);
        Assert.Equal(Svoya.Reveal, v.GetProperty("phase").GetString());
        Assert.Equal("Іван Котляревський", v.GetProperty("answer").GetProperty("text").GetString());
        Assert.Equal("Перша книга сучасною українською", v.GetProperty("answer").GetProperty("comment").GetString());
        Assert.StartsWith("Правильна відповідь — Іван Котляревський", v.GetProperty("say").GetProperty("text").GetString());
        Assert.Equal([0, 0], v.GetProperty("scores").EnumerateArray().Take(2).Select(x => x.GetInt32()));
        Until(h, Svoya.Board);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("question").ValueKind);
    }

    // ---------- відповідь ----------

    [Fact]
    public void Right_answer_scores_and_makes_the_player_choose()
    {
        var h = Table(nicks: ["Оля", "Петро", "Іра"]);
        var c = Open(h);
        var other = c == 2 ? 1 : 2;
        Assert.True(h.Act(other, "buzz").Ok);
        Assert.Equal("Зараз відповідаєш не ти", h.Act(c, "answer", new { text = "Котляревський" }).Message);
        var r = h.Act(other, "answer", new { text = "це Котляревський" });
        Assert.Equal("✅ +100", r.Message);
        Assert.Equal(100, Score(h, other));
        var v = h.View(null);
        Assert.Equal(Svoya.Reveal, v.GetProperty("phase").GetString());
        Assert.Equal(other, v.GetProperty("correct").GetInt32());
        Assert.Contains("Правильно, " + h.NickOf(other), v.GetProperty("say").GetProperty("text").GetString());
        Until(h, Svoya.Board);
        Assert.Equal(other, Chooser(h));
    }

    [Fact]
    public void Wrong_answer_costs_points_and_reopens_the_button_for_the_rest()
    {
        var h = Table();
        var c = Open(h);
        var o = Other(h, c);
        Assert.True(h.Act(c, "buzz").Ok);
        Assert.Equal("❌ −100", h.Act(c, "answer", new { text = "Шевченко" }).Message);
        Assert.Equal(-100, Score(h, c));
        var v = h.View(null);
        Assert.Equal(Svoya.Buzz, v.GetProperty("phase").GetString());
        Assert.Equal([c], v.GetProperty("wrong").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(10_000, v.GetProperty("totalMs").GetInt32());   // час на кнопку знову повний
        Assert.StartsWith("Свою спробу на це запитання", h.Act(c, "buzz").Message);
        Assert.False(h.View(c).GetProperty("me").GetProperty("canBuzz").GetBoolean());
        Assert.True(h.Act(o, "buzz").Ok);
        Assert.True(h.Act(o, "answer", new { text = "котляревский" }).Ok);   // одна помилка на п'ять літер
        Assert.Equal(100, Score(h, o));
    }

    [Fact]
    public void Everyone_wrong_ends_the_question()
    {
        var h = Table();
        var c = Open(h);
        h.Act(c, "buzz");
        h.Act(c, "answer", new { text = "ні" });
        h.Act(Other(h, c), "buzz");
        h.Act(Other(h, c), "answer", new { text = "не знаю" });
        Assert.Equal(Svoya.Reveal, Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("correct").ValueKind);
    }

    [Fact]
    public void Silence_after_the_button_counts_as_wrong()
    {
        var h = Table(new { answer = "10" });
        var c = Open(h);
        h.Act(c, "buzz");
        h.Clock.AdvanceMs(9_600);             // + 250 мс самого тика
        h.Tick();
        Assert.Equal(Svoya.Answering, Phase(h));
        h.Clock.AdvanceMs(400);
        h.Tick();
        Assert.Equal(Svoya.Buzz, Phase(h));
        Assert.Equal(-100, Score(h, c));
        var t = h.View(null).GetProperty("tries");
        Assert.Equal(0, t.GetArrayLength());       // мовчання — не спроба, оскаржувати нема чого
    }

    [Fact]
    public void Nobody_sees_the_answer_before_reveal()
    {
        var h = Table(nicks: ["Оля", "Петро", "Іра"]);
        var c = Open(h);
        foreach (var s in new int?[] { 0, 1, 2, null }) Assert.False(HasAnswer(h.View(s)));
        h.Act(c, "buzz");
        foreach (var s in new int?[] { 0, 1, 2, null })
        {
            Assert.False(HasAnswer(h.View(s)));
            Assert.DoesNotContain("Котляревськ", h.View(s).GetRawText());
        }
        h.Act(c, "answer", new { text = "Котляревський" });
        foreach (var s in new int?[] { 0, 1, 2, null }) Assert.True(HasAnswer(h.View(s)));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("me").ValueKind);
    }

    // ---------- апеляція ----------

    [Fact]
    public void Appeal_accepted_by_the_host_moves_the_points()
    {
        var h = Table(nicks: ["Оля", "Петро"]);
        var c = Open(h, theme: 1, q: 1);                      // Дніпро, 200
        h.Act(c, "buzz");
        h.Act(c, "answer", new { text = "ріка Дніпр" });      // автомат не взяв
        Assert.Equal(-200, Score(h, c));
        h.Clock.AdvanceMs(10_000);
        h.Tick();
        Assert.Equal(Svoya.Reveal, Phase(h));
        Assert.True(h.View(c).GetProperty("me").GetProperty("canAppeal").GetBoolean());
        Assert.False(h.View(Other(h, c)).GetProperty("me").GetProperty("canAppeal").GetBoolean());
        Assert.True(h.Act(c, "appeal").Ok);
        Assert.False(h.Act(c, "appeal").Ok);
        Assert.True(h.View(0).GetProperty("me").GetProperty("canJudge").GetBoolean());
        if (c != 0) Assert.Equal("Судить господар столу", h.Act(c, "judge", new { accept = true }).Message);
        Assert.True(h.Act(0, "judge", new { accept = true }).Ok);
        Assert.Equal(200, Score(h, c));
        Until(h, Svoya.Board);
        Assert.Equal(c, Chooser(h));
    }

    [Fact]
    public void Appeal_rejected_or_ignored_changes_nothing()
    {
        var h = Table();
        var c = Open(h);
        h.Act(c, "buzz");
        h.Act(c, "answer", new { text = "Франко" });
        h.Act(Other(h, c), "buzz");
        h.Act(Other(h, c), "answer", new { text = "Франко" });
        h.Act(c, "appeal");
        Assert.True(h.Act(0, "judge", new { seat = c, accept = false }).Ok);
        Assert.Equal(-100, Score(h, c));
        h.Act(Other(h, c), "appeal");
        Until(h, Svoya.Board);                                 // господар мовчав — апеляція згоріла
        Assert.Equal(-100, Score(h, Other(h, c)));
        Assert.Equal("Нема що судити", h.Act(0, "judge", new { accept = true }).Message);
    }

    [Fact]
    public void Appeal_holds_the_reveal_open_for_a_while()
    {
        var h = Table();
        var c = Open(h);
        h.Act(c, "buzz");
        h.Act(c, "answer", new { text = "Франко" });
        h.Act(Other(h, c), "buzz");
        h.Act(Other(h, c), "answer", new { text = "Котляревський" });
        Assert.Equal(Svoya.Reveal, Phase(h));
        h.Act(c, "appeal");
        h.Clock.AdvanceMs(Svoya.AppealMs - 500);
        h.Tick();
        Assert.Equal(Svoya.Reveal, Phase(h));
    }

    // ---------- раунди й кінець ----------

    /// <summary>Зіграти весь раунд: обирач бере клітинки по черзі, відповідає правильно.</summary>
    static void PlayRound(RoomHarness h, Func<int, int> who)
    {
        var answers = new[] { new[] { "Котляревський", "Шевченко" }, new[] { "Київ", "Дніпро" } };
        for (var t = 0; t < 2; t++)
            for (var q = 0; q < 2; q++)
            {
                Until(h, Svoya.Board);
                h.Act(Chooser(h), "pick", new { theme = t, q });
                var s = who(t * 2 + q);
                Assert.True(h.Act(s, "buzz").Ok);
                h.Act(s, "answer", new { text = answers[t][q] });
            }
    }

    [Fact]
    public void Game_ends_after_the_last_round_with_the_best_score_winning()
    {
        var h = Table(pack: "b_nofinal");
        PlayRound(h, i => i == 3 ? 0 : 1);                  // Петро 100+200+100, Оля 200
        Until(h, Svoya.Done, 400);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Contains("Своя гра: Петро 400, Оля 200 — перемога: Петро", h.Room.Result.Text);
        Assert.Equal(2, h.Scores.Count);
        Assert.Equal(400, h.Room.Result.Scores![1]);
    }

    [Fact]
    public void Rematch_keeps_the_pack()
    {
        var h = Table(pack: "b_nofinal");
        PlayRound(h, _ => 0);
        Until(h, Svoya.Done, 400);
        Assert.True(h.Rematch().Ok);
        Assert.Equal(Svoya.Intro, Phase(h));
        Assert.Equal("b_nofinal", h.View(null).GetProperty("pack").GetProperty("id").GetString());
        Assert.All(h.View(null).GetProperty("scores").EnumerateArray(), s => Assert.Equal(0, s.GetInt32()));
    }

    [Fact]
    public void Leaving_mid_answer_reopens_the_button()
    {
        var h = Table(nicks: ["Оля", "Петро", "Іра"]);
        Open(h);
        Until(h, Svoya.Buzz);
        h.Act(2, "buzz");
        h.Leave("Іра");
        Assert.Equal(Svoya.Buzz, Phase(h));
        Assert.Equal(0, Score(h, 2));
        Assert.Contains(2, h.View(null).GetProperty("left").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Last_player_leaving_ends_the_game()
    {
        var h = Table();
        Until(h, Svoya.Board);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Leave("Оля");
        Assert.Null(h.Rooms.Find(h.RoomId));                 // порожня кімната зникає разом із партією
    }

    [Fact]
    public void Same_seed_same_game()
    {
        string Run()
        {
            var h = Table(seed: 11);
            Until(h, Svoya.Board);
            var c = Chooser(h);
            h.Act(c, "pick", new { theme = 1, q = 0 });
            h.Act(c, "buzz");
            h.Act(c, "answer", new { text = "Київ" });
            return h.View(null).GetRawText();
        }
        Assert.Equal(Run(), Run());
    }

    // ---------- голос ----------

    [Fact]
    public void Ready_voice_sets_the_reading_time()
    {
        var voice = new FakeSvoyaVoice(seconds: 3);
        var h = Table(new { early = "off" }, voice: voice);
        Assert.NotEmpty(voice.Prepared);                                  // раунд попросили озвучити наперед
        Assert.Contains("Хто написав «Енеїду»?", voice.Prepared);
        Open(h);
        var say = h.View(null).GetProperty("say");
        Assert.Equal("Хто написав «Енеїду»?", say.GetProperty("text").GetString());
        Assert.StartsWith("/tts/", say.GetProperty("url").GetString());
        Assert.Equal(3400, h.View(null).GetProperty("totalMs").GetInt32());
    }

    [Fact]
    public void Voice_that_is_not_ready_is_waited_for_then_skipped()
    {
        var voice = new FakeSvoyaVoice(ready: false);
        var h = Table(voice: voice);
        var v = h.View(null);
        Assert.True(v.GetProperty("waiting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("until").ValueKind);
        h.Clock.AdvanceMs(Svoya.VoiceWaitMs - 300);
        h.Tick();
        Assert.True(h.View(null).GetProperty("waiting").GetBoolean());
        h.Tick(2);
        v = h.View(null);
        Assert.False(v.GetProperty("waiting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("say").GetProperty("url").ValueKind);   // браузер прочитає сам
        Assert.Equal(Svoya.Intro, v.GetProperty("phase").GetString());
    }

    [Fact]
    public void No_voice_option_reads_silently()
    {
        var voice = new FakeSvoyaVoice();
        var h = Table(new { voice = "none" }, voice: voice);
        Assert.Empty(voice.Prepared);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("say").GetProperty("url").ValueKind);
    }
}

/// <summary>Живий ведучий (specs/svoya.md §3.0): господар не грає, читає сам і судить.</summary>
public class SvoyaLiveTests
{
    static RoomHarness Live(string[]? nicks = null, string pack = "b_mini") =>
        SvoyaTests.Table(new { host = "live", buzz = "10", answer = "15" }, nicks ?? ["Ведучий", "Оля", "Петро"], pack: pack);

    static string Phase(RoomHarness h) => SvoyaTests.Phase(h);

    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    static int OpenLive(RoomHarness h)
    {
        Assert.True(h.Act(0, "next").Ok);                    // ведучий пропускає вступ
        Assert.Equal(Svoya.Board, Phase(h));
        var c = SvoyaTests.Chooser(h);
        Assert.NotEqual(0, c);
        Assert.True(h.Act(c, "pick", new { theme = 0, q = 0 }).Ok);
        return c;
    }

    [Fact]
    public void Live_host_needs_at_least_one_player()
    {
        var h = SvoyaTests.Table(new { host = "live" }, ["Ведучий"], start: false);
        h.Act(0, "pack", new { id = "b_mini" });
        Assert.Equal("Треба ще хоч одного гравця, крім ведучого", h.Start().Message);
        h.Join("Оля");
        Assert.True(h.Start().Ok);
    }

    [Fact]
    public void Host_is_not_a_player()
    {
        var h = Live();
        Assert.Equal(0, h.View(null).GetProperty("host").GetInt32());
        Assert.Equal("🎙 ведучий", h.Room.Game.SeatName(0));
        OpenLive(h);
        Assert.False(h.View(0).GetProperty("me").GetProperty("canBuzz").GetBoolean());
        Assert.Equal("Ведучий так не ходить", h.Act(0, "buzz").Message);
        Assert.True(h.View(0).GetProperty("me").GetProperty("isHost").GetBoolean());
    }

    [Fact]
    public void Host_sees_the_answer_players_do_not()
    {
        var h = Live();
        OpenLive(h);
        Assert.Equal("Іван Котляревський", h.View(0).GetProperty("answer").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("answer").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(2).GetProperty("answer").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("answer").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("say").ValueKind);   // живий ведучий читає сам
    }

    [Fact]
    public void Host_opens_the_button_and_judges()
    {
        var h = Live();
        var c = OpenLive(h);
        Assert.Equal(Svoya.Reading, Phase(h));
        Assert.True(h.Act(0, "open").Ok);
        Assert.Equal(Svoya.Buzz, Phase(h));
        Assert.True(h.Act(c, "buzz").Ok);
        Assert.Equal("Кажи вголос — ведучий слухає", h.Act(c, "answer", new { text = "x" }).Message);
        Assert.True(h.Act(0, "verdict", new { ok = false }).Ok);
        Assert.Equal(-100, Score(h, c));
        Assert.Equal(Svoya.Buzz, Phase(h));
        var o = c == 1 ? 2 : 1;
        h.Act(o, "buzz");
        Assert.True(h.Act(0, "verdict", new { ok = true }).Ok);
        Assert.Equal(100, Score(h, o));
        Assert.Equal(Svoya.Reveal, Phase(h));
        Assert.True(h.Act(0, "next").Ok);
        Assert.Equal(Svoya.Board, Phase(h));
        Assert.Equal(o, SvoyaTests.Chooser(h));
        Assert.Equal(0, Score(h, 0));
    }

    [Fact]
    public void Live_answer_does_not_time_out_on_its_own()
    {
        var h = Live();
        var c = OpenLive(h);
        h.Act(c, "buzz");
        h.Clock.AdvanceMs(60_000);
        h.Tick(4);
        Assert.Equal(Svoya.Answering, Phase(h));
        Assert.Equal(0, Score(h, c));
    }

    [Fact]
    public void Host_closes_a_question_nobody_knows()
    {
        var h = Live();
        OpenLive(h);
        Assert.True(h.Act(0, "nobody").Ok);
        Assert.Equal(Svoya.Reveal, Phase(h));
        Assert.Equal("Іван Котляревський", h.View(1).GetProperty("answer").GetProperty("text").GetString());
    }

    [Fact]
    public void Pause_freezes_the_clock()
    {
        var h = Live();
        OpenLive(h);
        h.Act(0, "open");
        Assert.True(h.Act(0, "pause").Ok);
        Assert.Equal("Пауза", h.Act(1, "buzz").Message);
        h.Clock.AdvanceMs(60_000);
        h.Tick(4);
        Assert.Equal(Svoya.Buzz, Phase(h));
        Assert.True(h.View(1).GetProperty("paused").GetBoolean());
        Assert.True(h.Act(0, "resume").Ok);
        h.Clock.AdvanceMs(9_000);
        h.Tick();
        Assert.Equal(Svoya.Buzz, Phase(h));
        h.Clock.AdvanceMs(1_500);
        h.Tick();
        Assert.Equal(Svoya.Reveal, Phase(h));
    }

    [Fact]
    public void Host_adjusts_scores_by_hand()
    {
        var h = Live();
        Assert.True(h.Act(0, "adjust", new { seat = 2, delta = 300 }).Ok);
        Assert.True(h.Act(0, "adjust", new { seat = 2, delta = -100 }).Ok);
        Assert.Equal(200, Score(h, 2));
        Assert.False(h.Act(0, "adjust", new { seat = 0, delta = 100 }).Ok);   // собі — ні
        Assert.False(h.Act(1, "adjust", new { seat = 1, delta = 100 }).Ok);   // гравцеві — ні
    }

    [Fact]
    public void Media_only_question_opens_the_button_by_itself()
    {
        var packs = new FakeSvoyaPacks();
        var q = packs.Packs["b_mini"].Pack.Rounds[0].Themes[0].Questions[0];
        q.Text = "";
        q.Media = new SvoyaMedia { Kind = "audio", File = "aaaaaaaaaaaa.mp3", Seconds = 8 };
        var h = SvoyaTests.Table(new { host = "live" }, ["Ведучий", "Оля"], packs: packs);
        OpenLive(h);
        h.Clock.AdvanceMs(8_600);
        h.Tick();
        Assert.Equal(Svoya.Buzz, Phase(h));
    }

    [Fact]
    public void Host_leaving_ends_the_game_without_winners()
    {
        var h = Live();
        OpenLive(h);
        h.Leave("Ведучий");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
        Assert.Contains("ведучий пішов", h.Room.Result.Text);
    }

    [Fact]
    public void Live_game_ends_with_players_only_in_the_result()
    {
        var h = Live(pack: "b_nofinal");
        h.Act(0, "adjust", new { seat = 1, delta = 500 });
        for (var i = 0; i < 4; i++)
        {
            if (Phase(h) == Svoya.Intro) h.Act(0, "next");
            SvoyaTests.Until(h, Svoya.Board);
            h.Act(0, "pick", new { theme = i / 2, q = i % 2 });          // ведучий може обрати й сам
            h.Act(0, "nobody");
            h.Act(0, "next");
        }
        SvoyaTests.Until(h, Svoya.Done, 100);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.False(h.Room.Result.Scores!.ContainsKey(0));
    }
}
