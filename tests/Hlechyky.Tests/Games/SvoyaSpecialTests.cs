using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Кіт у мішку, аукціон і фінал «Своєї гри» (specs/svoya.md §3.2) — по правилу на тест.</summary>
public class SvoyaSpecialTests
{
    static string Phase(RoomHarness h) => SvoyaTests.Phase(h);
    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();
    static int Chooser(RoomHarness h) => SvoyaTests.Chooser(h);
    static JsonElement V(RoomHarness h, int? seat = null) => h.View(seat);
    static JsonElement Me(RoomHarness h, int seat) => h.View(seat).GetProperty("me").GetProperty("special");

    /// <summary>Стіл на пакеті з однією темою; <paramref name="nicks"/> сідають по черзі.</summary>
    static RoomHarness Table(FakeSvoyaPacks packs, string pack, string[]? nicks = null, object? options = null, int seed = 5) =>
        SvoyaTests.Table(options, nicks ?? ["Оля", "Петро", "Іра"], packs: packs, pack: pack, seed: seed);

    /// <summary>Хто обирає — відкриває клітинку q.</summary>
    static void Pick(RoomHarness h, int q)
    {
        SvoyaTests.Until(h, Svoya.Board);
        Assert.True(h.Act(Chooser(h), "pick", new { theme = 0, q }).Ok);
    }

    /// <summary>Звичайна клітинка: <paramref name="seat"/> тисне й відповідає правильно.</summary>
    static void Win(RoomHarness h, int q, int seat)
    {
        Pick(h, q);
        Assert.True(h.Act(seat, "buzz").Ok, h.Reply.Message);
        Assert.Equal($"✅ +{V(h).GetProperty("question").GetProperty("price").GetInt32()}", h.Act(seat, "answer", new { text = "відповідь" + (q + 1) }).Message);
    }

    // =========================================================================================
    // Кіт у мішку
    // =========================================================================================

    [Fact]
    public void Cat_goes_to_another_player_who_answers_without_the_button()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_cat", ("normal", 100), ("cat", 200));
        packs.Packs[id].Pack.Rounds[0].Themes[0].Questions[1].CatPrice = 500;
        var h = Table(packs, id);
        Win(h, 0, 1);                                              // Петро обирає далі
        Pick(h, 1);
        Assert.Equal(Svoya.Cat, Phase(h));
        Assert.Equal("Кіт у мішку!", V(h).GetProperty("say").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("question").ValueKind);   // що в мішку — поки не видно
        Assert.True(Me(h, 1).GetProperty("canGive").GetBoolean());
        Assert.False(Me(h, 0).GetProperty("canGive").GetBoolean());
        Assert.Equal("Кота передає той, хто його відкрив", h.Act(0, "give", new { seat = 2 }).Message);
        Assert.Equal("Кота треба віддати комусь іншому", h.Act(1, "give", new { seat = 1 }).Message);
        Assert.True(h.Act(1, "give", new { seat = 2 }).Ok);

        var v = V(h);
        Assert.Equal(Svoya.Reading, v.GetProperty("phase").GetString());
        Assert.Equal(500, v.GetProperty("question").GetProperty("price").GetInt32());
        Assert.Equal(2, v.GetProperty("solo").GetInt32());
        Assert.Equal("Це запитання — лише для одного гравця", h.Act(0, "buzz").Message);
        Assert.False(h.View(0).GetProperty("me").GetProperty("canBuzz").GetBoolean());
        SvoyaTests.Until(h, Svoya.Answering);
        Assert.Equal(2, V(h).GetProperty("answering").GetInt32());
        Assert.Equal("✅ +500", h.Act(2, "answer", new { text = "відповідь2" }).Message);
        Assert.Equal(500, Score(h, 2));
        SvoyaTests.Until(h, Svoya.Strike);                         // поле порожнє — фінал
    }

    [Fact]
    public void Cat_without_a_price_lets_the_receiver_pick_min_or_max()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_cat2", ("normal", 100), ("normal", 300), ("cat", 200));
        var h = Table(packs, id);
        Win(h, 0, 0);
        Pick(h, 2);
        h.Act(0, "give", new { seat = 1 });
        var cat = V(h).GetProperty("cat");
        Assert.True(cat.GetProperty("choosing").GetBoolean());
        Assert.Equal(100, cat.GetProperty("min").GetInt32());
        Assert.Equal(300, cat.GetProperty("max").GetInt32());
        Assert.True(Me(h, 1).GetProperty("canCatPrice").GetBoolean());
        Assert.False(h.Act(2, "catPrice", new { max = true }).Ok);
        Assert.True(h.Act(1, "catPrice", new { max = true }).Ok);
        Assert.Equal(300, V(h).GetProperty("question").GetProperty("price").GetInt32());
    }

    [Fact]
    public void Wrong_answer_on_a_cat_closes_the_question()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_cat3", ("normal", 100), ("cat", 200), ("normal", 300));
        packs.Packs[id].Pack.Rounds[0].Themes[0].Questions[1].CatPrice = 200;
        var h = Table(packs, id);
        Win(h, 0, 0);
        Pick(h, 1);
        h.Act(0, "give", new { seat = 2 });
        SvoyaTests.Until(h, Svoya.Answering);
        Assert.Equal("❌ −200", h.Act(2, "answer", new { text = "не знаю" }).Message);
        Assert.Equal(-200, Score(h, 2));
        Assert.Equal(Svoya.Reveal, Phase(h));                      // кнопки для решти нема
    }

    [Fact]
    public void Slow_cat_owner_gives_to_a_random_other_and_slow_receiver_gets_the_min()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_cat4", ("normal", 100), ("cat", 200), ("normal", 400));
        var h = Table(packs, id);
        Win(h, 0, 0);
        Pick(h, 1);
        h.Clock.AdvanceMs(Svoya.CatMs + 3000);
        h.Tick();
        var to = V(h).GetProperty("cat").GetProperty("to").GetInt32();
        Assert.NotEqual(0, to);
        h.Clock.AdvanceMs(Svoya.CatMs + 3000);
        h.Tick();
        Assert.Equal(100, V(h).GetProperty("question").GetProperty("price").GetInt32());
    }

    [Fact]
    public void Lonely_player_keeps_the_cat()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_cat5", ("cat", 100));
        packs.Packs[id].Pack.Rounds[0].Themes[0].Questions[0].CatPrice = 100;
        var h = Table(packs, id, ["Оля"]);
        Pick(h, 0);
        Assert.Equal(Svoya.Reading, Phase(h));
        Assert.Equal(0, V(h).GetProperty("solo").GetInt32());
    }

    // =========================================================================================
    // Аукціон
    // =========================================================================================

    /// <summary>Оля 300, Петро 500, Іра 0; обирає Петро; аукціон за 100.</summary>
    static RoomHarness AuctionTable()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_auc", ("normal", 300), ("normal", 500), ("auction", 100));
        var h = Table(packs, id);
        Win(h, 0, 0);
        Win(h, 1, 1);
        Pick(h, 2);
        return h;
    }

    [Fact]
    public void Auction_goes_round_from_the_chooser_and_the_top_bid_answers()
    {
        var h = AuctionTable();
        var a = V(h).GetProperty("auction");
        Assert.Equal(Svoya.Auction, Phase(h));
        Assert.Equal("Аукціон!", V(h).GetProperty("say").GetProperty("text").GetString());
        Assert.Equal(100, a.GetProperty("nominal").GetInt32());
        Assert.Equal(1, a.GetProperty("turn").GetInt32());          // починає обирач
        Assert.Equal("Зараз торгується Петро", h.Act(0, "bid", new { amount = 200 }).Message);
        Assert.Equal("Щонайменше 100", h.Act(1, "bid", new { amount = 50 }).Message);
        Assert.True(h.Act(1, "bid", new { amount = 200 }).Ok);
        // Іра з нулем не торгується — хід одразу в Олі
        Assert.Equal(0, V(h).GetProperty("auction").GetProperty("turn").GetInt32());
        Assert.Equal("Щонайменше 300", h.Act(0, "bid", new { amount = 250 }).Message);
        Assert.True(h.Act(0, "pass").Ok);
        // лишився сам Петро зі ставкою — торги скінчились
        Assert.Equal(Svoya.Reading, Phase(h));
        Assert.Equal(1, V(h).GetProperty("solo").GetInt32());
        Assert.Equal(200, V(h).GetProperty("question").GetProperty("price").GetInt32());
        SvoyaTests.Until(h, Svoya.Answering);
        h.Act(1, "answer", new { text = "ні" });
        Assert.Equal(300, Score(h, 1));
    }

    [Fact]
    public void All_in_is_beaten_only_by_a_bigger_all_in()
    {
        var h = AuctionTable();
        h.Act(1, "bid", new { amount = 200 });
        Assert.Equal("Ва-банк: 300", h.Act(0, "allin").Message);  // Оля: усі свої 300
        var a = V(h).GetProperty("auction");
        Assert.True(a.GetProperty("allIn").GetBoolean());
        Assert.Equal(1, a.GetProperty("turn").GetInt32());
        Assert.False(Me(h, 1).GetProperty("canBid").GetBoolean());
        Assert.True(Me(h, 1).GetProperty("canAllIn").GetBoolean());
        Assert.Equal("Після ва-банку — лише більший ва-банк", h.Act(1, "bid", new { amount = 400 }).Message);
        Assert.Equal("Ва-банк: 500", h.Act(1, "allin").Message);
        // Олиних 300 на 500 не вистачає — торги закінчено
        Assert.Equal(Svoya.Reading, Phase(h));
        Assert.Equal(500, V(h).GetProperty("question").GetProperty("price").GetInt32());
        SvoyaTests.Until(h, Svoya.Answering);
        h.Act(1, "answer", new { text = "відповідь3" });
        Assert.Equal(1000, Score(h, 1));
    }

    [Fact]
    public void Everyone_passing_gives_the_question_to_the_chooser_at_nominal()
    {
        var h = AuctionTable();
        h.Act(1, "pass");
        h.Act(0, "pass");
        Assert.Equal(Svoya.Reading, Phase(h));
        Assert.Equal(1, V(h).GetProperty("solo").GetInt32());
        Assert.Equal(100, V(h).GetProperty("question").GetProperty("price").GetInt32());
    }

    [Fact]
    public void Silent_bidder_passes_on_timeout()
    {
        var h = AuctionTable();
        h.Clock.AdvanceMs(Svoya.AuctionTurnMs + 5_000);
        h.Tick();
        Assert.Contains(1, V(h).GetProperty("auction").GetProperty("passed").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(0, V(h).GetProperty("auction").GetProperty("turn").GetInt32());
    }

    [Fact]
    public void Nobody_rich_enough_means_no_bidding_at_all()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_auc2", ("normal", 100), ("auction", 1000));
        var h = Table(packs, id);
        Win(h, 0, 2);
        Pick(h, 1);
        Assert.Equal(Svoya.Reading, Phase(h));                     // одразу до обирача за номіналом
        Assert.Equal(2, V(h).GetProperty("solo").GetInt32());
        Assert.Equal(1000, V(h).GetProperty("question").GetProperty("price").GetInt32());
    }

    [Fact]
    public void Live_host_passes_for_a_stuck_bidder()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_auc3", ("auction", 100));
        var h = SvoyaTests.Table(new { host = "live" }, ["Ведучий", "Оля", "Петро"], packs: packs, pack: id);
        h.Act(0, "adjust", new { seat = 1, delta = 500 });
        h.Act(0, "adjust", new { seat = 2, delta = 500 });
        h.Act(0, "next");
        h.Act(0, "pick", new { theme = 0, q = 0 });
        Assert.Equal(Svoya.Auction, Phase(h));
        var turn = V(h).GetProperty("auction").GetProperty("turn").GetInt32();
        Assert.True(h.Act(0, "passFor", new { seat = turn }).Ok);
        Assert.NotEqual(turn, V(h).GetProperty("auction").GetProperty("turn").GetInt32());
    }

    // =========================================================================================
    // Фінал
    // =========================================================================================

    /// <summary>Оля 300, Петро 300, Іра −200 — у фіналі лише Оля й Петро.</summary>
    static RoomHarness FinalTable(object? options = null, string[]? nicks = null)
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_fin", ("normal", 100), ("normal", 300), ("normal", 200));
        var h = Table(packs, id, nicks, options);
        Win(h, 0, 0);
        Win(h, 1, 1);
        Pick(h, 2);
        h.Act(2, "buzz");
        h.Act(2, "answer", new { text = "ні" });                    // Іра −200
        h.Act(0, "buzz");
        h.Act(0, "answer", new { text = "відповідь3" });            // Оля 100 + 200 = 300, Петро 300, Іра −200
        return h;
    }

    [Fact]
    public void Only_players_in_plus_reach_the_final_and_strike_from_the_lowest()
    {
        var h = FinalTable();
        SvoyaTests.Until(h, Svoya.Strike);
        var f = V(h).GetProperty("final");
        Assert.Equal([0, 1], f.GetProperty("finalists").EnumerateArray().Select(x => x.GetInt32()).Order());
        Assert.DoesNotContain(2, f.GetProperty("finalists").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(["Ф1", "Ф2"], f.GetProperty("themes").EnumerateArray().Select(x => x.GetString()));
        // Оля 300, Петро 300 — рівно; тоді від меншого місця
        var turn = f.GetProperty("turn").GetInt32();
        Assert.Equal(0, turn);
        Assert.True(Me(h, 0).GetProperty("canStrike").GetBoolean());
        Assert.Equal("Викреслює Оля", h.Act(1, "strike", new { theme = 0 }).Message);
        Assert.True(h.Act(0, "strike", new { theme = 0 }).Ok);
        Assert.Equal(Svoya.Bet, Phase(h));                          // лишилась одна тема
        Assert.Equal(1, V(h).GetProperty("final").GetProperty("theme").GetInt32());
        Assert.Equal("Іра", h.NickOf(2));
        Assert.Equal("У фіналі грають ті, хто в плюсі", h.Act(2, "bet", new { amount = 1 }).Message);
    }

    [Fact]
    public void Bets_and_answers_stay_secret_until_reveal()
    {
        var h = FinalTable();
        SvoyaTests.Until(h, Svoya.Strike);
        h.Act(0, "strike", new { theme = 0 });
        Assert.Equal("Ставка від 1 до 300", h.Act(0, "bet", new { amount = 301 }).Message);
        Assert.True(h.Act(0, "bet", new { amount = 250 }).Ok);
        Assert.Equal(250, Me(h, 0).GetProperty("bet").GetInt32());
        var other = V(h, 1).GetProperty("final");
        Assert.Equal([0], other.GetProperty("betted").EnumerateArray().Select(x => x.GetInt32()));
        Assert.DoesNotContain("250", other.GetProperty("rows").GetRawText());
        Assert.DoesNotContain("250", V(h).GetProperty("final").GetRawText());

        h.Act(1, "bet", new { amount = 100 });                     // усі поставили — питання
        Assert.Equal(Svoya.FinalQuestion, Phase(h));
        Assert.Equal("Фінал два", V(h).GetProperty("question").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, V(h, 0).GetProperty("answer").ValueKind);
        h.Act(0, "answer", new { text = "фінал2" });
        Assert.DoesNotContain("фінал2", V(h, 1).GetRawText());
        Assert.DoesNotContain("фінал2", V(h).GetRawText());
        Assert.Equal("фінал2", Me(h, 0).GetProperty("answer").GetString());
    }

    [Fact]
    public void Final_reveals_one_by_one_from_the_lowest_and_moves_the_bets()
    {
        var h = FinalTable();
        SvoyaTests.Until(h, Svoya.Strike);
        h.Act(0, "strike", new { theme = 0 });
        h.Act(0, "bet", new { amount = 250 });
        h.Act(1, "bet", new { amount = 100 });
        h.Act(0, "answer", new { text = "фінал2" });
        h.Act(1, "answer", new { text = "зовсім не те" });        // обидва відповіли — розкриття одразу
        Assert.Equal(Svoya.FinalReveal, Phase(h));
        var f = V(h).GetProperty("final");
        Assert.Equal(1, f.GetProperty("revealed").GetArrayLength());
        var first = f.GetProperty("revealed")[0].GetInt32();
        Assert.Equal("фінал2", V(h).GetProperty("answer").GetProperty("text").GetString());
        SvoyaTests.Until(h, Svoya.Done, 200);
        Assert.Equal(550, Score(h, 0));
        Assert.Equal(200, Score(h, 1));
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains(first, new[] { 0, 1 });
        var rows = V(h).GetProperty("final").GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());                    // після кінця видно всіх
    }

    [Fact]
    public void Slow_finalists_bet_one_and_answer_nothing()
    {
        var h = FinalTable();
        SvoyaTests.Until(h, Svoya.Strike);
        h.Clock.AdvanceMs(Svoya.StrikeMs + 5_000);
        h.Tick();
        Assert.Equal(Svoya.Bet, Phase(h));                          // тему викреслено навмання
        h.Clock.AdvanceMs(Svoya.BetMs);
        h.Tick();
        Assert.Equal(Svoya.FinalQuestion, Phase(h));
        SvoyaTests.Until(h, Svoya.Done, 1000);
        Assert.Equal(299, Score(h, 0));                             // ставка 1, відповіді нема
        Assert.Equal(299, Score(h, 1));
    }

    [Fact]
    public void No_one_in_plus_skips_the_final()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_nofin", ("normal", 100));
        var h = Table(packs, id);
        Pick(h, 0);
        h.Act(0, "buzz");
        h.Act(0, "answer", new { text = "ні" });
        SvoyaTests.Until(h, Svoya.Done, 400);
        Assert.Empty(h.Room.Result!.Winners);
    }

    [Fact]
    public void Live_host_judges_every_final_answer_before_reveal()
    {
        var packs = new FakeSvoyaPacks();
        var id = packs.Add("b_livefin", ("normal", 100));
        var h = SvoyaTests.Table(new { host = "live" }, ["Ведучий", "Оля", "Петро"], packs: packs, pack: id);
        h.Act(0, "adjust", new { seat = 1, delta = 300 });
        h.Act(0, "adjust", new { seat = 2, delta = 200 });
        h.Act(0, "next");
        h.Act(0, "pick", new { theme = 0, q = 0 });
        h.Act(0, "nobody");
        h.Act(0, "next");
        Assert.Equal(Svoya.Strike, Phase(h));
        Assert.Equal(2, V(h).GetProperty("final").GetProperty("turn").GetInt32());   // Петро з меншим рахунком
        Assert.True(h.Act(0, "strike", new { theme = 1 }).Ok);     // ведучий може й за нього
        h.Act(1, "bet", new { amount = 300 });
        h.Act(2, "bet", new { amount = 50 });
        h.Act(1, "answer", new { text = "Фінал-1!" });
        h.Act(2, "answer", new { text = "щось" });
        h.Clock.AdvanceMs(Svoya.FinalAnswerMs + 10_000);
        h.Tick();
        Assert.Equal(Svoya.FinalJudge, Phase(h));
        var hostRows = V(h, 0).GetProperty("final").GetProperty("rows");
        Assert.Equal(2, hostRows.GetArrayLength());                 // ведучий бачить усі відповіді
        Assert.Equal(1, V(h, 1).GetProperty("final").GetProperty("rows").GetArrayLength());
        Assert.Equal("Спершу оціни кожну відповідь", h.Act(0, "next").Message);
        h.Act(0, "finalVerdict", new { seat = 1, ok = true });
        h.Act(0, "finalVerdict", new { seat = 2, ok = false });
        Assert.True(h.Act(0, "next").Ok);
        SvoyaTests.Until(h, Svoya.Done, 200);
        Assert.Equal(600, Score(h, 1));
        Assert.Equal(150, Score(h, 2));
        Assert.Equal([1], h.Room.Result!.Winners);
    }

    [Fact]
    public void Top_bidder_leaving_drops_the_bid_and_the_auction_goes_on()
    {
        var h = AuctionTable();
        h.Act(1, "bid", new { amount = 200 });
        h.Leave("Петро");
        Assert.Equal(Svoya.Auction, Phase(h));
        var a = V(h).GetProperty("auction");
        Assert.Equal(JsonValueKind.Null, a.GetProperty("holder").ValueKind);
        Assert.Equal(0, a.GetProperty("turn").GetInt32());
        h.Act(0, "pass");
        Assert.Equal(Svoya.Reading, Phase(h));
    }

    [Fact]
    public void Finalist_leaving_during_bets_does_not_stall_the_final()
    {
        var h = FinalTable();
        SvoyaTests.Until(h, Svoya.Strike);
        h.Act(0, "strike", new { theme = 0 });
        h.Act(0, "bet", new { amount = 10 });
        h.Leave("Петро");
        Assert.Equal(Svoya.FinalQuestion, Phase(h));
        h.Act(0, "answer", new { text = "фінал2" });
        SvoyaTests.Until(h, Svoya.Done, 200);
        Assert.Equal(310, Score(h, 0));
    }
}
