using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Хата гончаря: глина як стратегія, знаряддя на стіні, прикраси й дошка купців (ClickerHouse.cs). Основа — у
/// <see cref="ClickerTests"/>, драбина й обпал — у <see cref="ClickerProgressTests"/>, розгін і полиця — у <see cref="ClickerLiveTests"/>.
/// </summary>
public class ClickerHouseTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement House(RoomHarness h) => View(h).GetProperty("house");
    static JsonElement Orders(RoomHarness h) => House(h).GetProperty("orders");
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static long ClickBase(RoomHarness h) => View(h).GetProperty("clickBase").GetInt64();
    static double PerSecond(RoomHarness h) => View(h).GetProperty("perSecond").GetDouble();
    static JsonElement Fall(RoomHarness h) => View(h).GetProperty("fall");
    static int Streak(RoomHarness h) => Fall(h).GetProperty("streak").GetInt32();
    static int Stamps(RoomHarness h) => View(h).GetProperty("stamps").GetInt32();

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Give(RoomHarness h, long pots, long? total = null) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = Math.Max(pots, total ?? pots);
    });

    static void Levels(RoomHarness h, params (string Key, int Level)[] levels) => Patch(h, s =>
    {
        foreach (var (key, level) in levels) s["upgrades"]![key] = level;
    });

    static void FallNow(RoomHarness h) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s["fall"] = new JsonObject
        {
            ["at"] = now.ToString("O"),
            ["until"] = (now + Clicker.FallShown).ToString("O"),
            ["x"] = 40,
        };
    });

    static void GoldenNow(RoomHarness h) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s["golden"] = new JsonObject
        {
            ["at"] = now.ToString("O"),
            ["until"] = (now + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant,
            ["x"] = 10,
            ["y"] = 10,
        };
    });

    static JsonElement Owned(JsonElement list, string key) => list.EnumerateArray().Single(x => x.GetProperty("key").GetString() == key);

    // ---------- глина ----------

    [Fact]
    public void Buying_a_clay_puts_it_on_the_wheel_and_it_has_to_rest_before_the_next()
    {
        var h = Wheel();
        Give(h, 10_000);
        var r = Act(h, "knead", new { kind = "red" });
        Assert.True(r.Ok);
        Assert.Equal(7_000, Pots(h));
        Assert.Equal("red", House(h).GetProperty("clay").GetString());
        Assert.Equal(2, ClickBase(h));                          // клік ×2

        // Замісити назад звичайну одразу не можна — глина відлежується.
        var back = Act(h, "knead", new { kind = "" });
        Assert.False(back.Ok);
        Assert.Contains("відлежується", back.Message);

        h.Clock.AdvanceMs((int)Clicker.ClayRest.TotalMilliseconds + 1000);
        Assert.True(Act(h, "knead", new { kind = "" }).Ok);
        Assert.Equal("", House(h).GetProperty("clay").GetString());
        Assert.Equal(1, ClickBase(h));
        // Куплена лишилась: вдруге платити не треба.
        h.Clock.AdvanceMs((int)Clicker.ClayRest.TotalMilliseconds + 1000);
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        Assert.Equal(7_000, Pots(h));
    }

    [Fact]
    public void White_clay_trades_the_click_for_passive()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));                          // 5 за секунду
        Give(h, 100_000);
        Assert.True(Act(h, "knead", new { kind = "white" }).Ok);
        Assert.Equal(7.5, PerSecond(h), 6);
        Assert.Equal(1, ClickBase(h));                          // 0,5 округлюється до мінімуму — одного глека
        Assert.True(Owned(House(h).GetProperty("clays"), "white").GetProperty("on").GetBoolean());
    }

    [Fact]
    public void Black_clay_makes_jugs_fall_sooner_and_pay_more()
    {
        var h = Wheel();
        Give(h, 1_000_000);
        Assert.True(Act(h, "knead", new { kind = "black" }).Ok);
        FallNow(h);
        Assert.Equal(170, Fall(h).GetProperty("gain").GetInt64());     // (100 кліків) × 1,5 + 20
        Assert.True(Act(h, "grab").Ok);
        var wait = (Fall(h).GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        Assert.InRange(wait, Clicker.FallMinSeconds * 0.6 - 0.01, Clicker.FallMaxSeconds * 0.6 + 0.01);
    }

    [Fact]
    public void Unknown_or_unpaid_clay_is_refused()
    {
        var h = Wheel();
        Assert.False(Act(h, "knead", new { kind = "golden" }).Ok);
        var poor = Act(h, "knead", new { kind = "red" });
        Assert.False(poor.Ok);
        Assert.Contains("Бракує", poor.Message);
        Assert.False(Act(h, "knead", new { kind = "" }).Ok);            // звичайна вже на колі
    }

    [Fact]
    public void The_kiln_puts_plain_clay_back_but_keeps_the_bought_ones()
    {
        var h = Wheel();
        Give(h, 10_000, Clicker.TotalFor(1));
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal("", House(h).GetProperty("clay").GetString());
        Assert.True(Owned(House(h).GetProperty("clays"), "red").GetProperty("owned").GetBoolean());
    }

    // ---------- знаряддя ----------

    [Fact]
    public void A_tool_is_bought_once_and_the_paddle_slows_the_cooling()
    {
        var h = Wheel();
        Give(h, 10_000);
        Assert.True(Act(h, "tool", new { key = "paddle" }).Ok);
        Assert.Equal(5_000, Pots(h));
        Assert.False(Act(h, "tool", new { key = "paddle" }).Ok);
        Assert.True(Owned(House(h).GetProperty("tools"), "paddle").GetProperty("owned").GetBoolean());
        Assert.Equal(Clicker.HeatTau * Clicker.PaddleTau, View(h).GetProperty("heatTau").GetDouble());
        Assert.False(Act(h, "tool", new { key = "hammer" }).Ok);
    }

    [Fact]
    public void The_string_and_the_ribs_feed_the_click()
    {
        var h = Wheel();
        Levels(h, ("wheel", 9), ("apprentice", 200));           // рука 10, пасив 100
        Give(h, 200_000);
        Assert.Equal(10, ClickBase(h));
        Assert.True(Act(h, "tool", new { key = "string" }).Ok);
        Assert.Equal(11, ClickBase(h));                         // +1 % пасиву
        Assert.True(Act(h, "tool", new { key = "ribs" }).Ok);
        Assert.Equal(14, ClickBase(h));                         // 10 × 1,25 + 1 = 13,5 → 14
    }

    [Fact]
    public void The_bucket_and_the_lantern()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        Give(h, 2_000_000);
        Assert.True(Act(h, "tool", new { key = "bucket" }).Ok);
        Assert.Equal(5.5, PerSecond(h), 6);
        Assert.Equal(8, View(h).GetProperty("offlineHours").GetDouble());
        Assert.True(Act(h, "tool", new { key = "lantern" }).Ok);
        Assert.Equal(10, View(h).GetProperty("offlineHours").GetDouble());
    }

    [Fact]
    public void The_sponge_gives_the_falling_jug_more_air()
    {
        var h = Wheel();
        Give(h, 100_000);
        Assert.True(Act(h, "tool", new { key = "sponge" }).Ok);
        FallNow(h);
        Assert.True(Act(h, "grab").Ok);
        var f = Fall(h);
        Assert.Equal(Clicker.FallShownLong, f.GetProperty("until").GetDateTimeOffset() - f.GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public void The_apron_forgives_one_broken_jug_but_not_two_in_a_row()
    {
        var h = Wheel();
        Give(h, 1_000_000);
        Assert.True(Act(h, "tool", new { key = "apron" }).Ok);
        Patch(h, s => s["fallStreak"] = 5);
        var past = (int)(Clicker.FallShown + Clicker.CatchGrace).TotalMilliseconds + 500;

        FallNow(h);
        h.Clock.AdvanceMs(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(5, Streak(h));                             // фартух вибачив

        FallNow(h);
        h.Clock.AdvanceMs(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(0, Streak(h));                             // другий поспіль — ні

        // Спійманий глек повертає фартухові силу.
        FallNow(h);
        Assert.True(Act(h, "grab").Ok);
        Assert.Equal(1, Streak(h));
        FallNow(h);
        h.Clock.AdvanceMs(past);
        Assert.False(Act(h, "grab").Ok);
        Assert.Equal(1, Streak(h));
    }

    [Fact]
    public void The_whistle_hurries_the_golden_jug()
    {
        var h = Wheel();
        Give(h, 3_000_000);
        Assert.True(Act(h, "tool", new { key = "whistle" }).Ok);
        for (var i = 0; i < 3; i++)
        {
            GoldenNow(h);
            Assert.True(Act(h, "catch").Ok);
            var wait = (View(h).GetProperty("golden").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
            Assert.InRange(wait, Clicker.GoldenMinSeconds * Clicker.WhistleEvents - 0.01, Clicker.GoldenMaxSeconds * Clicker.WhistleEvents + 0.01);
        }
    }

    [Fact]
    public void The_iron_adds_a_stamp_to_every_kiln_but_never_makes_one_out_of_nothing()
    {
        var h = Wheel();
        Give(h, 100_000_000, Clicker.TotalFor(2));
        Assert.True(Act(h, "tool", new { key = "iron" }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(3, Stamps(h));                             // 2 за глеки + 1 від тавра
        Assert.False(Act(h, "fire").Ok);                        // без нового клейма тавро не спрацює
        Assert.Equal(3, Stamps(h));
        Assert.True(Owned(House(h).GetProperty("tools"), "iron").GetProperty("owned").GetBoolean());
    }

    // ---------- прикраси ----------

    [Fact]
    public void Decor_adds_two_percent_each_and_stays_after_the_kiln()
    {
        var h = Wheel();
        Give(h, 200_000, Clicker.TotalFor(1));
        Assert.True(Act(h, "adorn", new { key = "towel" }).Ok);
        Assert.True(Act(h, "adorn", new { key = "icon" }).Ok);
        Assert.False(Act(h, "adorn", new { key = "icon" }).Ok);
        Assert.Equal(1.04, View(h).GetProperty("allMult").GetDouble(), 6);

        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(1.04 * 1.02, View(h).GetProperty("allMult").GetDouble(), 6);
        Assert.True(Owned(House(h).GetProperty("decor"), "towel").GetProperty("owned").GetBoolean());
    }

    [Fact]
    public void The_dog_keeps_the_golden_jug_on_the_stage_longer()
    {
        var h = Wheel();
        Give(h, 200_000_000);
        Assert.True(Act(h, "adorn", new { key = "dog" }).Ok);
        GoldenNow(h);
        Assert.True(Act(h, "catch").Ok);
        var g = View(h).GetProperty("golden");
        Assert.Equal(Clicker.GoldenShown + Clicker.DogGuard, g.GetProperty("until").GetDateTimeOffset() - g.GetProperty("at").GetDateTimeOffset());
    }

    // ---------- купці ----------

    [Fact]
    public void The_board_has_three_merchants_and_refreshes_every_four_minutes()
    {
        var h = Wheel();
        var board = Orders(h).GetProperty("board").EnumerateArray().ToList();
        Assert.Equal(Clicker.BoardSize, board.Count);
        Assert.All(board, o => Assert.Equal("invest", o.GetProperty("kind").GetString()));   // розписів ще нема
        Assert.All(board, o => Assert.True(o.GetProperty("pay").GetInt64() > o.GetProperty("need").GetInt64()));
        Assert.Equal(3, board.Select(o => o.GetProperty("id").GetInt32()).Distinct().Count());
        Assert.Equal(h.Clock.UtcNow + Clicker.BoardEvery, Orders(h).GetProperty("refreshAt").GetDateTimeOffset());

        var ids = board.Select(o => o.GetProperty("id").GetInt32()).ToHashSet();
        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        var fresh = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(Clicker.BoardSize, fresh.Count);
        Assert.Empty(fresh.Intersect(ids));
    }

    [Fact]
    public void A_merchant_takes_the_pots_and_returns_with_more()
    {
        var h = Wheel();
        Give(h, 1_000_000);
        var order = Orders(h).GetProperty("board").EnumerateArray().First();
        var id = order.GetProperty("id").GetInt32();
        var need = order.GetProperty("need").GetInt64();
        var pay = order.GetProperty("pay").GetInt64();
        var minutes = order.GetProperty("minutes").GetInt32();
        Assert.Contains(minutes, Clicker.OrderMinutes);
        Assert.Equal((long)(need * (Clicker.InvestBase + minutes / 30.0 * Clicker.InvestPerHalfHour)), pay);

        var r = Act(h, "take", new { id });
        Assert.True(r.Ok);
        Assert.Contains("повернеться", r.Message);
        Assert.Equal(1_000_000 - need, Pots(h));
        Assert.Single(Orders(h).GetProperty("taken").EnumerateArray());
        Assert.Equal(Clicker.BoardSize - 1, Orders(h).GetProperty("board").GetArrayLength());

        // Ще не час — купець у дорозі.
        h.Clock.AdvanceMs(minutes * 60_000 - 5_000);
        Assert.Equal(1_000_000 - need, Pots(h));
        h.Clock.AdvanceMs(10_000);
        Assert.Equal(1_000_000 - need + pay, Pots(h));
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());
        var paid = Orders(h).GetProperty("paid").EnumerateArray().Single();
        Assert.Equal(pay, paid.GetProperty("pay").GetInt64());
        Assert.Equal(id, paid.GetProperty("id").GetInt32());
    }

    [Fact]
    public void A_style_order_pays_at_once_and_only_for_a_style_you_own()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("kosiv"));
        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);   // нова дошка вже бачить розпис
        var order = Orders(h).GetProperty("board").EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "style");
        Assert.Equal("kosiv", order.GetProperty("style").GetString());
        Assert.Equal("Косівська", order.GetProperty("styleName").GetString());
        Assert.True(order.GetProperty("can").GetBoolean());
        var need = order.GetProperty("need").GetInt64();
        var pay = order.GetProperty("pay").GetInt64();
        Assert.Equal((long)(need * Clicker.StyleOrderPay), pay);

        Give(h, need);
        Assert.True(Act(h, "take", new { id = order.GetProperty("id").GetInt32() }).Ok);
        Assert.Equal(pay, Pots(h));
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());

        // Той самий купець, але розпис продали з колекції (правка бази) — не візьме.
        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        var again = Orders(h).GetProperty("board").EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "style");
        Patch(h, s => s["styles"] = new JsonArray());
        Give(h, 10_000_000);
        var r = Act(h, "take", new { id = again.GetProperty("id").GetInt32() });
        Assert.False(r.Ok);
        Assert.Contains("розпису", r.Message);
    }

    [Fact]
    public void No_more_than_three_merchants_on_the_road()
    {
        var h = Wheel();
        Give(h, 100_000_000);
        foreach (var o in Orders(h).GetProperty("board").EnumerateArray().ToList())
            Assert.True(Act(h, "take", new { id = o.GetProperty("id").GetInt32() }).Ok);
        Assert.Equal(Clicker.MaxTaken, Orders(h).GetProperty("taken").GetArrayLength());
        Assert.Equal(0, Orders(h).GetProperty("board").GetArrayLength());

        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        var next = Orders(h).GetProperty("board").EnumerateArray().First();
        var r = Act(h, "take", new { id = next.GetProperty("id").GetInt32() });
        Assert.False(r.Ok);
        Assert.Contains("Три купці", r.Message);
    }

    [Fact]
    public void A_merchant_who_left_cannot_be_hired_and_you_need_the_pots()
    {
        var h = Wheel();
        var order = Orders(h).GetProperty("board").EnumerateArray().First();
        var id = order.GetProperty("id").GetInt32();
        var poor = Act(h, "take", new { id });
        Assert.False(poor.Ok);
        Assert.Contains("Бракує", poor.Message);

        h.Clock.AdvanceMs((int)Clicker.BoardEvery.TotalMilliseconds + 1000);
        Give(h, 100_000_000);
        var gone = Act(h, "take", new { id });
        Assert.False(gone.Ok);
        Assert.Contains("поїхав", gone.Message);
        Assert.False(Act(h, "take", new { id = 9999 }).Ok);
    }

    [Fact]
    public void The_scales_make_every_merchant_pay_a_fifth_more()
    {
        var h = Wheel();
        Give(h, 100_000_000);
        var before = Orders(h).GetProperty("board").EnumerateArray().First();
        var id = before.GetProperty("id").GetInt32();
        var pay = before.GetProperty("pay").GetInt64();
        Assert.True(Act(h, "tool", new { key = "scales" }).Ok);
        var after = Orders(h).GetProperty("board").EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == id);
        Assert.Equal((long)(pay * (1 + Clicker.ScalesBonus)), after.GetProperty("pay").GetInt64());

        var potsBefore = Pots(h);
        Assert.True(Act(h, "take", new { id }).Ok);
        h.Clock.AdvanceMs(31 * 60_000);
        Assert.Equal(potsBefore - after.GetProperty("need").GetInt64() + after.GetProperty("pay").GetInt64(), Pots(h));
    }

    [Fact]
    public void The_kiln_burns_the_merchants_on_the_road_and_hangs_a_new_board()
    {
        var h = Wheel();
        Give(h, 100_000_000, Clicker.TotalFor(1));
        var ids = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.True(Act(h, "take", new { id = ids[0] }).Ok);
        Assert.True(Act(h, "fire").Ok);
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());
        var fresh = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(Clicker.BoardSize, fresh.Count);
        Assert.Empty(fresh.Intersect(ids));
        Assert.Equal(0, Pots(h));
    }

    [Fact]
    public void The_house_survives_save_and_load()
    {
        var h = Wheel();
        Give(h, 100_000_000);
        Assert.True(Act(h, "knead", new { kind = "red" }).Ok);
        Assert.True(Act(h, "tool", new { key = "paddle" }).Ok);
        Assert.True(Act(h, "adorn", new { key = "towel" }).Ok);
        var order = Orders(h).GetProperty("board").EnumerateArray().First();
        Assert.True(Act(h, "take", new { id = order.GetProperty("id").GetInt32() }).Ok);
        var boardIds = Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        var payAt = Orders(h).GetProperty("taken").EnumerateArray().Single().GetProperty("payAt").GetDateTimeOffset();

        Patch(h, _ => { });

        Assert.Equal("red", House(h).GetProperty("clay").GetString());
        Assert.True(Owned(House(h).GetProperty("tools"), "paddle").GetProperty("owned").GetBoolean());
        Assert.True(Owned(House(h).GetProperty("decor"), "towel").GetProperty("owned").GetBoolean());
        Assert.Equal(boardIds, Orders(h).GetProperty("board").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList());
        Assert.Equal(payAt, Orders(h).GetProperty("taken").EnumerateArray().Single().GetProperty("payAt").GetDateTimeOffset());
    }

    [Fact]
    public void Old_saves_without_a_house_get_plain_clay_and_a_fresh_board()
    {
        var h = Wheel();
        lock (h.Room.Sync)
            h.Room.Game.Load("""{"pots":5,"total":5,"upgrades":{"wheel":2},"soldToday":null}""");
        Assert.Equal("", House(h).GetProperty("clay").GetString());
        Assert.Equal(Clicker.BoardSize, Orders(h).GetProperty("board").GetArrayLength());
        Assert.Empty(Orders(h).GetProperty("taken").EnumerateArray());
        Assert.Equal(5, Pots(h));
    }

    [Fact]
    public void Nice_rounds_to_two_significant_digits()
    {
        Assert.Equal(12_000, Clicker.Nice(11_873));
        Assert.Equal(57, Clicker.Nice(56.2));
        Assert.Equal(1_200_000, Clicker.Nice(1_234_567));
        Assert.Equal(0, Clicker.Nice(-3));
    }
}
