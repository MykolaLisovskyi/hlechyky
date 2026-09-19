using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>Характер ведучого (specs/svoya.md §11): книга фраз, вибір репліки за ситуацією, підсумок партії.</summary>
public sealed class SvoyaHostTests
{
    static readonly SvoyaPhrases Book = SvoyaPhrases.Load(Paths.Resolve("data/svoya/host.json"));

    static RoomHarness Table(FakeSvoyaPacks packs, string pack, string[]? nicks = null, ISvoyaVoice? voice = null, int seed = 5)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISvoyaPackSource>(packs);
        sc.AddSingleton(Book);
        if (voice is not null) sc.AddSingleton(voice);
        var h = new RoomHarness("svoya", null, seed, sc.BuildServiceProvider());
        foreach (var n in nicks ?? ["Оля", "Петро"]) h.Join(n);
        Assert.True(h.Act(0, "pack", new { id = pack }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static string Say(RoomHarness h)
    {
        var say = h.View(null).GetProperty("say");
        return say.ValueKind == JsonValueKind.Null ? "" : say.GetProperty("text").GetString() ?? "";
    }

    /// <summary>Чи репліка — один із варіантів пулу з такими підстановками (без хвоста-коментаря).</summary>
    static void AssertFrom(string key, string said, params (string Key, string Value)[] vars)
    {
        var options = Book.Pool(key).Select(t => SvoyaPhrases.Fill(t, vars)).ToList();
        Assert.True(options.Any(o => said == o || said.StartsWith(o + " ", StringComparison.Ordinal)), $"«{said}» не з пулу {key}");
    }

    /// <summary>Гравець тисне кнопку вже після читання й відповідає.</summary>
    static void Answer(RoomHarness h, int seat, string text)
    {
        SvoyaTests.Until(h, Svoya.Buzz);
        Assert.True(h.Act(seat, "buzz").Ok, h.Reply.Message);
        Assert.True(h.Act(seat, "answer", new { text }).Ok, h.Reply.Message);
    }

    // ---------- книга фраз ----------

    [Fact]
    public void Book_is_valid_and_every_pool_has_choice()
    {
        var (_, problems) = SvoyaPhrases.Parse(File.ReadAllText(Paths.Resolve("data/svoya/host.json")));
        Assert.Empty(problems);
        Assert.All(SvoyaPhrases.Allowed.Keys, k => Assert.True(Book.Count(k) >= 3, $"{k}: замало варіантів"));
        Assert.Contains("Кіт у мішку!", Book.Pure());
        Assert.DoesNotContain(Book.Pure(), t => t.Contains("{nick}"));
    }

    [Fact]
    public void Broken_book_falls_back_to_the_plain_host()
    {
        Assert.Same(SvoyaPhrases.Plain, SvoyaPhrases.Parse("{ nope").Phrases);
        var (p, problems) = SvoyaPhrases.Parse("""{ "right": ["Так, {who}!"], "wrong": ["Ні."], "_note": "x", "unknown": ["?"] }""");
        Assert.Contains(problems, x => x.StartsWith("right:") && x.Contains("{who}"));
        Assert.Contains(problems, x => x.StartsWith("unknown:"));
        Assert.Equal(0, p.Count("right"));
        var last = new Dictionary<string, int>();
        Assert.Equal("Правильно, {nick}! Плюс {sum}.", p.Pick("right", new Random(1), last));   // сухий варіант
        Assert.Equal("Ні.", p.Pick("wrong", new Random(1), last));
        Assert.Equal("", SvoyaPhrases.Plain.Pick("endWin", new Random(1), last));               // сухий ведучий на фініші мовчить
    }

    [Fact]
    public void Pick_never_repeats_the_previous_line_and_reaches_every_one()
    {
        var (p, _) = SvoyaPhrases.Parse("""{ "cat": ["а", "б", "в"] }""");
        var rng = new Random(3);
        var last = new Dictionary<string, int>();
        var seen = new HashSet<string>();
        var prev = "";
        for (var i = 0; i < 60; i++)
        {
            var line = p.Pick("cat", rng, last);
            Assert.NotEqual(prev, line);
            seen.Add(line);
            prev = line;
        }
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void Fill_replaces_placeholders_and_leaves_the_rest()
    {
        Assert.Equal("Так, Оля! Плюс сто. {x}", SvoyaPhrases.Fill("Так, {nick}! Плюс {sum}. {x}", ("nick", "Оля"), ("sum", "сто")));
    }

    // ---------- у грі ----------

    static FakeSvoyaPacks Ladder()
    {
        var packs = new FakeSvoyaPacks();
        packs.Add("b_ladder", ("normal", 100), ("normal", 200), ("normal", 300), ("normal", 400));
        return packs;
    }

    [Fact]
    public void First_right_answer_opens_the_score_then_a_streak_is_noticed()
    {
        var h = Table(Ladder(), "b_ladder");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "відповідь1");
        AssertFrom("rightFirst", Say(h), ("nick", "Оля"), ("sum", "сто"));
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 1);
        Answer(h, 0, "відповідь2");
        AssertFrom("right", Say(h), ("nick", "Оля"), ("sum", "двісті"));                 // друга: ні перша, ні серія, ні найдорожча
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 2);
        Answer(h, 0, "відповідь3");
        AssertFrom("rightStreak", Say(h), ("nick", "Оля"), ("sum", "триста"));            // третя поспіль
    }

    [Fact]
    public void Taking_the_lead_and_the_dearest_cell_have_their_own_lines()
    {
        var h = Table(Ladder(), "b_ladder");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "відповідь1");                                                        // Оля 100
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 1);
        Answer(h, 1, "відповідь2");                                                        // Петро 200 — вийшов уперед
        AssertFrom("rightLead", Say(h), ("nick", "Петро"), ("sum", "двісті"));
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 3);
        Answer(h, 1, "відповідь4");                                                        // найдорожча, лідер і так він
        AssertFrom("rightBig", Say(h), ("nick", "Петро"), ("sum", "чотириста"));
    }

    [Fact]
    public void Wrong_answers_are_judged_by_the_damage()
    {
        var h = Table(Ladder(), "b_ladder");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "ні");                                                                // 0 → −100
        AssertFrom("wrongMinus", Say(h), ("nick", "Оля"), ("sum", "сто"));
        Answer(h, 1, "теж ні");                                                            // Петро теж у мінус — і він останній: «Ні» каже розкриття разом із відповіддю
        Assert.Equal(Svoya.Reveal, SvoyaTests.Phase(h));
        AssertFrom("wrongLast", Say(h), ("nick", "Петро"), ("sum", "сто"), ("answer", "відповідь1"));
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 3);
        Answer(h, 0, "знову ні");                                                          // уже в мінусі, найдорожча
        AssertFrom("wrongBig", Say(h), ("nick", "Оля"), ("sum", "чотириста"));
    }

    [Fact]
    public void Silence_on_the_button_gets_the_timeout_line()
    {
        var h = Table(Ladder(), "b_ladder");
        SvoyaTests.Open(h, 0, 0);
        SvoyaTests.Until(h, Svoya.Buzz);
        Assert.True(h.Act(1, "buzz").Ok);
        h.Clock.AdvanceMs(16_000);
        h.Tick();
        AssertFrom("timeout", Say(h), ("nick", "Петро"), ("sum", "сто"));
    }

    [Fact]
    public void Second_unanswered_question_in_a_row_is_noticed()
    {
        var h = Table(Ladder(), "b_ladder");
        SvoyaTests.Open(h, 0, 0);
        SvoyaTests.Until(h, Svoya.Reveal);
        AssertFrom("nobody", Say(h), ("answer", "відповідь1"));
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 1);
        SvoyaTests.Until(h, Svoya.Reveal);
        AssertFrom("nobodyAgain", Say(h), ("answer", "відповідь2"));
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 2);
        Answer(h, 0, "відповідь3");                                                        // хтось відповів — лічильник скинуто
        SvoyaTests.Until(h, Svoya.Board);
        SvoyaTests.Open(h, 0, 3);
        SvoyaTests.Until(h, Svoya.Reveal);
        AssertFrom("nobody", Say(h), ("answer", "відповідь4"));
    }

    [Fact]
    public void Plain_host_says_the_dry_lines_when_there_is_no_book()
    {
        var h = SvoyaTests.Table(packs: Ladder(), pack: "b_ladder");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "відповідь1");
        Assert.Equal("Правильно, Оля! Плюс сто.", Say(h));
    }

    [Fact]
    public void The_end_is_announced_and_voiced_ahead_of_time()
    {
        var voice = new FakeSvoyaVoice();
        var h = Table(new FakeSvoyaPacks(), "b_nofinal", voice: voice);                    // «Міні» без фіналу: 2×2
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "Котляревський");                                                     // Оля 100, далі всі мовчать
        foreach (var (t, q) in new[] { (0, 1), (1, 0), (1, 1) })
        {
            SvoyaTests.Until(h, Svoya.Board);
            SvoyaTests.Open(h, t, q);
            SvoyaTests.Until(h, Svoya.Reveal);
        }
        var prepared = voice.Urgent.Last();                                                // остання клітинка: підсумок пішов у чергу ще на розкритті
        AssertFrom("endWin", prepared, ("nick", "Оля"), ("sum", "сто"));
        SvoyaTests.Until(h, Svoya.Done);
        Assert.Equal(prepared, Say(h));
        Assert.Equal([0], h.Room.Result!.Winners);
    }

    [Fact]
    public void Final_gets_the_leader_intro_bets_and_verdict_lines()
    {
        var packs = new FakeSvoyaPacks();
        packs.Add("b_one", ("normal", 100));
        var h = Table(packs, "b_one");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "відповідь1");                                                        // Оля 100 — єдина у фіналі
        SvoyaTests.Until(h, Svoya.Strike);
        AssertFrom("introLead", Say(h), ("round", "Фінал"), ("themes", "Ф1, Ф2"), ("nick", "Оля"), ("sum", "сто"));
        Assert.True(h.Act(0, "strike", new { theme = 0 }).Ok);
        AssertFrom("finalBets", Say(h), ("theme", "Ф2"));
        Assert.True(h.Act(0, "bet", new { amount = 50 }).Ok);
        SvoyaTests.Until(h, Svoya.FinalQuestion);
        Assert.True(h.Act(0, "answer", new { text = "фінал2" }).Ok);
        Assert.Equal(Svoya.FinalReveal, SvoyaTests.Phase(h));
        AssertFrom("finalRight", Say(h), ("nick", "Оля"), ("said", "фінал2"), ("sum", "п'ятдесят"));
        SvoyaTests.Until(h, Svoya.Done, 200);
        AssertFrom("endWin", Say(h), ("nick", "Оля"), ("sum", "сто п'ятдесят"));
    }

    [Fact]
    public void Nobody_in_plus_gets_a_consolation_and_a_draw_names_everyone()
    {
        var packs = new FakeSvoyaPacks();
        packs.Add("b_one", ("normal", 100));
        var h = Table(packs, "b_one");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "ні");
        SvoyaTests.Until(h, Svoya.Done);                                                   // без фіналістів — фінал пропущено
        AssertFrom("endNobody", Say(h));

        var draw = Table(new FakeSvoyaPacks(), "b_nofinal");
        SvoyaTests.Open(draw, 0, 0);
        Answer(draw, 0, "Котляревський");                                                  // Оля 100
        SvoyaTests.Until(draw, Svoya.Board);
        SvoyaTests.Open(draw, 1, 0);
        Answer(draw, 1, "Київ");                                                           // Петро 100
        foreach (var (t, q) in new[] { (0, 1), (1, 1) })
        {
            SvoyaTests.Until(draw, Svoya.Board);
            SvoyaTests.Open(draw, t, q);
            SvoyaTests.Until(draw, Svoya.Reveal);
        }
        SvoyaTests.Until(draw, Svoya.Done);
        AssertFrom("endDraw", Say(draw), ("nicks", "Оля і Петро"));
    }
}
