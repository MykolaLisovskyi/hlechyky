using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Ярмарок і люди (пакет B4, ClickerFair.cs): замовлення з вимогами, здача й торг, шана сіл і пільги, гості біля
/// вікна, пригоди з вибором, погода, свята, збереження й обпал.
/// </summary>
public class ClickerFairTests
{
    static readonly DateTimeOffset Thursday = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    static RoomHarness Wheel(int seed = 1, DateTimeOffset? at = null)
    {
        var h = new RoomHarness("clicker", seed: seed);
        if (at is { } t) h.Clock.UtcNow = t;
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Market(RoomHarness h) => View(h).GetProperty("market");
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult Fair(RoomHarness h, object payload) => h.Act(0, "fair", payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static JsonObject Order(RoomHarness h, int id, string village, string ware, int quality, int count, string style = "",
        double mult = 2, int minutes = 30, bool lord = false) => new()
    {
        ["id"] = id, ["village"] = village, ["who"] = 0, ["ware"] = ware, ["style"] = style, ["quality"] = quality,
        ["count"] = count, ["mult"] = mult, ["at"] = h.Clock.UtcNow.ToString("O"),
        ["until"] = (h.Clock.UtcNow + TimeSpan.FromMinutes(minutes)).ToString("O"), ["lord"] = lord, ["sour"] = false,
    };

    /// <summary>Дошка рівно з цих замовлень, а наступне — нескоро (щоб розклад не підкидав нових посеред тесту).</summary>
    static void Board(RoomHarness h, params JsonObject[] orders) => Patch(h, s =>
    {
        var fair = s["fair"]!.AsObject();
        fair["orders"] = new JsonArray(orders.Select(o => (JsonNode)o).ToArray());
        fair["orderNext"] = (h.Clock.UtcNow + TimeSpan.FromHours(5)).ToString("O");
    });

    static void Rep(RoomHarness h, string village, int points) => Patch(h, s =>
    {
        var fair = s["fair"]!.AsObject();
        var rep = fair["rep"] as JsonObject ?? new JsonObject();
        rep[village] = points;
        fair["rep"] = rep;
    });

    static JsonElement? OrderView(RoomHarness h, int id) =>
        Market(h).GetProperty("orders").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(o => o!.Value.GetProperty("id").GetInt32() == id);

    static int RepOf(RoomHarness h, string village) =>
        Market(h).GetProperty("rep").EnumerateArray().First(r => r.GetProperty("key").GetString() == village).GetProperty("pts").GetInt32();

    static long ItemValue(RoomHarness h, string key) =>
        View(h).GetProperty("craft").GetProperty("items").EnumerateArray().First(x => x.GetProperty("key").GetString() == key).GetProperty("value").GetInt64();

    static int ItemN(RoomHarness h, string key) =>
        View(h).GetProperty("craft").GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("key").GetString() == key).Select(x => x.GetProperty("n").GetInt32()).FirstOrDefault();

    static JsonElement WareView(RoomHarness h, string ware) =>
        View(h).GetProperty("craft").GetProperty("wares").EnumerateArray().First(x => x.GetProperty("key").GetString() == ware);

    static void Guest(RoomHarness h, string kind, double inSeconds = 0) => Patch(h, s =>
    {
        var at = h.Clock.UtcNow + TimeSpan.FromSeconds(inSeconds);
        s["fair"]!["guest"] = new JsonObject
        {
            ["kind"] = kind, ["at"] = at.ToString("O"), ["until"] = (at + Clicker.FairGuestShown).ToString("O"), ["x"] = 66, ["y"] = 12,
        };
    });

    static void Event(RoomHarness h, string key, int rollA = 0, int rollB = 0, int id = 7, bool foresight = false) => Patch(h, s =>
    {
        s["fair"]!["event"] = new JsonObject
        {
            ["id"] = id, ["key"] = key, ["at"] = h.Clock.UtcNow.ToString("O"),
            ["until"] = (h.Clock.UtcNow + Clicker.FairEventWait).ToString("O"), ["rollA"] = rollA, ["rollB"] = rollB, ["foresight"] = foresight,
        };
    });

    // ---------- замовлення: розклад і генерація ----------

    [Fact]
    public void A_new_wheel_has_two_simple_orders_for_open_wares()
    {
        var h = Wheel();
        var orders = Market(h).GetProperty("orders").EnumerateArray().ToList();
        Assert.Equal(Clicker.FairBoardMin, orders.Count);
        foreach (var o in orders)
        {
            Assert.Contains(o.GetProperty("ware").GetString(), new[] { "pot", "bowl" });
            Assert.Equal(1, o.GetProperty("q").GetInt32());
            Assert.Equal("", o.GetProperty("style").GetString());
            Assert.InRange(o.GetProperty("n").GetInt32(), 2, 3);
            Assert.Equal(Clicker.FairOrderMult, o.GetProperty("mult").GetDouble());
            var left = o.GetProperty("until").GetDateTimeOffset() - h.Clock.UtcNow;
            Assert.InRange(left.TotalMinutes, Clicker.FairOrderLifeMinMinutes, Clicker.FairOrderLifeMaxMinutes);
            Assert.Equal(0, o.GetProperty("have").GetInt32());
        }
    }

    [Fact]
    public void The_same_seed_gives_the_same_board_guest_and_adventure()
    {
        string Text(int seed)
        {
            var h = Wheel(seed);
            h.Clock.Advance(TimeSpan.FromMinutes(20));
            return Views.Text(Market(h));
        }
        Assert.Equal(Text(7), Text(7));
        Assert.NotEqual(Text(7), Text(8));
    }

    [Fact]
    public void New_orders_come_on_schedule_and_the_board_stops_at_four()
    {
        var h = Wheel();
        var max = 0;
        var grew = false;
        for (var minute = 1; minute <= 24; minute++)
        {
            h.Clock.Advance(60);
            var n = Market(h).GetProperty("orders").GetArrayLength();
            max = Math.Max(max, n);
            if (minute <= 10 && n == 3) grew = true;
        }
        Assert.True(grew, "за десять хвилин мусило прийти третє замовлення");
        Assert.Equal(Clicker.FairBoardMax, max);
    }

    [Fact]
    public void Expired_orders_leave_the_board()
    {
        var h = Wheel();
        var first = Market(h).GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        for (var i = 0; i < 41; i++) { h.Clock.Advance(60); View(h); }
        var now = Market(h).GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.DoesNotContain(now, first.Contains);
        Assert.All(Market(h).GetProperty("orders").EnumerateArray(), o => Assert.True(o.GetProperty("until").GetDateTimeOffset() > h.Clock.UtcNow));
    }

    [Fact]
    public void Coming_back_after_hours_finds_fresh_orders_and_a_note()
    {
        var h = Wheel();
        h.Clock.Advance(TimeSpan.FromHours(3));
        var m = Market(h);
        Assert.True(m.GetProperty("orders").GetArrayLength() >= Clicker.FairBoardMin);
        Assert.All(m.GetProperty("orders").EnumerateArray(), o => Assert.True(o.GetProperty("until").GetDateTimeOffset() > h.Clock.UtcNow));
        Assert.Contains(View(h).GetProperty("away").GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith("📜"));
    }

    [Fact]
    public void A_delivered_order_is_not_replaced_at_once()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "opishnia", "pot", 1, 2));
        Items(h, ("pot||1", 2));
        Assert.True(Fair(h, new { op = "deliver", id = 100 }).Ok);
        h.Clock.Advance(30);
        Assert.Equal(0, Market(h).GetProperty("orders").GetArrayLength());
    }

    [Fact]
    public void Progress_makes_orders_bigger_pickier_and_about_newer_wares()
    {
        var qualities = new HashSet<int>();
        var counts = new HashSet<int>();
        var wares = new HashSet<string>();
        var styled = 0;
        for (var seed = 1; seed <= 30; seed++)
        {
            var h = Wheel(seed);
            Patch(h, s =>
            {
                s["total"] = 2_000_000_000_000;
                s["styles"] = new JsonArray("gavarets", "bubnivka", "kosiv");
                s.Remove("fair");                                    // дошка рахується наново від нового прогресу
            });
            foreach (var o in Market(h).GetProperty("orders").EnumerateArray())
            {
                qualities.Add(o.GetProperty("q").GetInt32());
                counts.Add(o.GetProperty("n").GetInt32());
                wares.Add(o.GetProperty("ware").GetString()!);
                var style = o.GetProperty("style").GetString()!;
                if (style.Length > 0) { styled++; Assert.Contains(style, new[] { "gavarets", "bubnivka", "kosiv" }); }
                Assert.InRange(o.GetProperty("mult").GetDouble(), 2, 2.75);
            }
        }
        Assert.Contains(3, qualities);
        Assert.Contains(2, qualities);
        Assert.True(counts.Max() >= 4);
        Assert.True(styled > 0);
        // Лише останні п'ять відкритих виробів: від барила до лева.
        Assert.All(wares, w => Assert.Contains(w, new[] { "barrel", "tile", "kumanets", "ram", "lion" }));
    }

    // ---------- здача ----------

    [Fact]
    public void Delivering_without_the_wares_is_refused()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "bubnivka", "makitra", 2, 3));
        Assert.Equal("Бракує: є 0 з 3 — макітра", Fair(h, new { op = "deliver", id = 100 }).Message);
        Items(h, ("makitra||1", 5), ("makitra||2", 2));
        Assert.Equal("Бракує: є 2 з 3 — макітра", Fair(h, new { op = "deliver", id = 100 }).Message);
        Assert.Equal(7, ItemN(h, "makitra||1") + ItemN(h, "makitra||2"));
    }

    [Fact]
    public void A_styled_order_wants_exactly_that_style_and_an_open_one_wants_any()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("gavarets", "bubnivka"));
        Board(h, Order(h, 100, "bubnivka", "pot", 1, 2, style: "bubnivka"), Order(h, 101, "kosiv", "pot", 1, 2));
        Items(h, ("pot|gavarets|1", 2));
        Assert.Equal("Бракує: є 0 з 2 — горщик", Fair(h, new { op = "deliver", id = 100 }).Message);
        Assert.True(Fair(h, new { op = "deliver", id = 101 }).Ok);      // «будь-який розпис» бере й гаварецький
        Items(h, ("pot|bubnivka|3", 2));
        Assert.True(Fair(h, new { op = "deliver", id = 100 }).Ok);
    }

    [Fact]
    public void An_expired_or_unknown_order_is_gone()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "kosiv", "pot", 1, 2, minutes: 1));
        Items(h, ("pot||1", 2));
        h.Clock.Advance(61);
        Assert.Equal("Цей замовник уже не чекає — поїхав додому", Fair(h, new { op = "deliver", id = 100 }).Message);
        Assert.Equal("Цей замовник уже не чекає — поїхав додому", Fair(h, new { op = "deliver", id = 999 }).Message);
        Assert.Equal(2, ItemN(h, "pot||1"));
    }

    [Fact]
    public void Delivery_takes_the_worst_suitable_quality_and_pays_by_what_went()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["kiln"] = 50);
        Board(h, Order(h, 100, "gavarets", "bowl", 2, 2, mult: 2.25));
        Items(h, ("bowl||1", 4), ("bowl||2", 1), ("bowl||3", 3));
        var v2 = ItemValue(h, "bowl||2");
        var v3 = ItemValue(h, "bowl||3");
        var before = Pots(h);
        var r = Fair(h, new { op = "deliver", id = 100 });
        Assert.True(r.Ok, r.Message);
        Assert.StartsWith("🤝", r.Message);
        Assert.Equal(before + (long)((v2 + v3) * 2.25), Pots(h));
        Assert.Equal(4, ItemN(h, "bowl||1"));                   // звичайні не годяться — лишились
        Assert.Equal(0, ItemN(h, "bowl||2"));
        Assert.Equal(2, ItemN(h, "bowl||3"));
        Assert.Null(OrderView(h, 100));
        Assert.Equal(2 + 2 + 2, RepOf(h, "gavarets"));          // 2 + кількість + 2 за якість
        Assert.Equal(1, Market(h).GetProperty("delivered").GetInt32());
    }

    [Fact]
    public void The_view_promises_the_pay_and_counts_what_is_in_the_store()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "kosiv", "pot", 1, 3));
        Items(h, ("pot||1", 2), ("pot||2", 5));
        var o = OrderView(h, 100)!.Value;
        Assert.Equal(7, o.GetProperty("have").GetInt32());
        Assert.Equal((long)(ItemValue(h, "pot||1") * 3 * 2.0), o.GetProperty("pay").GetInt64());
        Assert.Equal(2 + 3, o.GetProperty("rep").GetInt32());
    }

    // ---------- торг ----------

    [Fact]
    public void Giving_way_pays_less_and_earns_twice_the_respect()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "vasylkiv", "pot", 1, 2));
        Items(h, ("pot||1", 2));
        var v = ItemValue(h, "pot||1");
        var before = Pots(h);
        var r = Fair(h, new { op = "deliver", id = 100, bid = "down" });
        Assert.StartsWith("🥰", r.Message);
        Assert.Equal(before + (long)(v * 2 * 2.0 * Clicker.FairDownPay), Pots(h));
        Assert.Equal((2 + 2) * 2, RepOf(h, "vasylkiv"));
    }

    [Fact]
    public void Asking_more_is_a_gamble_decided_by_the_server_and_the_same_for_the_same_seed()
    {
        string Try(int seed)
        {
            var h = Wheel(seed);
            Board(h, Order(h, 100, "bubnivka", "pot", 1, 2));
            Items(h, ("pot||1", 2));
            return Fair(h, new { op = "deliver", id = 100, bid = "up" }).Message;
        }
        var outcomes = Enumerable.Range(1, 40).Select(Try).ToList();
        Assert.Contains(outcomes, m => m.StartsWith("💰"));
        Assert.Contains(outcomes, m => m.StartsWith("😤"));
        Assert.Equal(Try(5), Try(5));
        // Без шани шанс 45 %: сорок спроб не можуть бути всі одного кольору, але й не мусять бути навпіл.
        Assert.InRange(outcomes.Count(m => m.StartsWith("💰")), 8, 32);
    }

    [Fact]
    public void A_refusal_keeps_the_wares_offends_a_little_and_still_allows_a_plain_deal()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var h = Wheel(seed);
            Board(h, Order(h, 100, "bubnivka", "pot", 1, 2));
            Rep(h, "bubnivka", 9);                                   // рівень 1 (поріг 8)
            Items(h, ("pot||1", 2));
            var before = Pots(h);
            var r = Fair(h, new { op = "deliver", id = 100, bid = "up" });
            if (!r.Message.StartsWith("😤")) continue;

            Assert.Equal(before, Pots(h));
            Assert.Equal(2, ItemN(h, "pot||1"));
            Assert.True(OrderView(h, 100)!.Value.GetProperty("sour").GetBoolean());
            Assert.Equal(8, RepOf(h, "bubnivka"));
            Assert.Equal("Замовник уже ображений — удруге накидати не вийде", Fair(h, new { op = "deliver", id = 100, bid = "up" }).Message);
            // Друга образа рівня не забирає.
            Assert.True(Fair(h, new { op = "deliver", id = 100 }).Ok);
            Assert.Equal(8 + 4, RepOf(h, "bubnivka"));
            return;
        }
        Assert.Fail("за сорок спроб купець жодного разу не відмовив");
    }

    [Fact]
    public void Respect_raises_the_chance_to_ask_more()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "kosiv", "pot", 1, 2));
        Assert.Equal(Clicker.FairUpChance, OrderView(h, 100)!.Value.GetProperty("chance").GetDouble(), 6);
        Rep(h, "kosiv", 60);                                         // рівень 3
        Rep(h, "sorochyntsi", 220);                                  // рівень 5: +15 %
        Assert.Equal(Math.Min(Clicker.FairUpMax, 0.45 + 0.21 + 0.15), OrderView(h, 100)!.Value.GetProperty("chance").GetDouble(), 6);
    }

    [Fact]
    public void A_strange_bid_or_op_is_refused()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "kosiv", "pot", 1, 2));
        Items(h, ("pot||1", 2));
        Assert.Equal("Так на ярмарку не торгуються", Fair(h, new { op = "deliver", id = 100, bid = "steal" }).Message);
        Assert.Equal("На ярмарку так не роблять", Fair(h, new { op = "dance" }).Message);
    }

    // ---------- шана й пільги ----------

    [Fact]
    public void Respect_levels_follow_the_thresholds()
    {
        Assert.Equal(0, Clicker.FairLevelOf(7));
        Assert.Equal(1, Clicker.FairLevelOf(8));
        Assert.Equal(2, Clicker.FairLevelOf(25));
        Assert.Equal(4, Clicker.FairLevelOf(219));
        Assert.Equal(5, Clicker.FairLevelOf(220));
        Assert.Equal(5, Clicker.FairLevelOf(100_000));
    }

    [Fact]
    public void Every_level_anywhere_is_one_percent_to_everything()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["kiln"] = 10);
        var all = View(h).GetProperty("allMult").GetDouble();
        Rep(h, "opishnia", 25);                                      // 2
        Rep(h, "sorochyntsi", 220);                                  // 5
        Assert.Equal(all * 1.07, View(h).GetProperty("allMult").GetDouble(), 9);
        Assert.Equal(1.07, Market(h).GetProperty("allMult").GetDouble(), 9);
    }

    [Fact]
    public void Village_perks_touch_work_price_and_drying()
    {
        var h = Wheel(at: Thursday);
        Patch(h, s => { s["total"] = 10_000_000_000; s["upgrades"]!["kiln"] = 100; });
        var bowl = WareView(h, "bowl").GetProperty("value").GetInt64();
        var pot = WareView(h, "pot").GetProperty("value").GetInt64();
        var jug = WareView(h, "jug").GetProperty("value").GetInt64();
        var dry = Market(h).GetProperty("dry").GetDouble();

        Rep(h, "opishnia", 60);                                      // 3: −12 % роботи на глечики й куманці
        Assert.Equal(71, WareView(h, "jug").GetProperty("need").GetInt32());
        Assert.Equal(282, WareView(h, "kumanets").GetProperty("need").GetInt32());
        Assert.Equal(40, WareView(h, "pot").GetProperty("need").GetInt32());

        Rep(h, "bubnivka", 25);                                      // 2: миски +10 %
        Rep(h, "gavarets", 8);                                       // 1: горщики +5 %
        // Шість рівнів — ще й +6 % до всього (множить пасив, а з ним і ціну кожного виробу).
        Near(bowl * 1.10 * 1.06, WareView(h, "bowl").GetProperty("value").GetInt64());
        Near(pot * 1.05 * 1.06, WareView(h, "pot").GetProperty("value").GetInt64());
        Near(jug * 1.06, WareView(h, "jug").GetProperty("value").GetInt64());

        Rep(h, "vasylkiv", 120);                                     // 4: −16 % сушіння
        Assert.Equal(dry * 0.84, Market(h).GetProperty("dry").GetDouble(), 9);
    }

    [Fact]
    public void Kosiv_pays_more_for_styled_orders()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("kosiv"));
        Board(h, Order(h, 100, "kosiv", "pot", 1, 2, style: "kosiv", mult: 2.25), Order(h, 101, "kosiv", "pot", 1, 2));
        Rep(h, "kosiv", 120);                                        // 4
        Assert.Equal(2.25 * 1.16, OrderView(h, 100)!.Value.GetProperty("mult").GetDouble(), 9);
        Assert.Equal(2.0, OrderView(h, 101)!.Value.GetProperty("mult").GetDouble(), 9);
    }

    [Fact]
    public void Reaching_the_fifth_level_is_an_achievement()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "opishnia", "pot", 1, 2));
        Rep(h, "opishnia", 218);
        Items(h, ("pot||1", 2));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-rep");
        var r = Fair(h, new { op = "deliver", id = 100 });
        Assert.Contains("⭐ Опішня: шана 5", r.Message);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-rep");
    }

    // ---------- гості ----------

    [Fact]
    public void Guests_come_every_eight_to_twenty_minutes_for_twenty_seconds()
    {
        for (var seed = 1; seed <= 10; seed++)
        {
            var h = Wheel(seed);
            var g = Market(h).GetProperty("guest");
            var at = g.GetProperty("at").GetDateTimeOffset();
            Assert.InRange((at - h.Clock.UtcNow).TotalSeconds, Clicker.FairGuestMinSeconds, Clicker.FairGuestMaxSeconds);
            Assert.Equal(Clicker.FairGuestShown, g.GetProperty("until").GetDateTimeOffset() - at);
            Assert.Contains(g.GetProperty("kind").GetString(), Clicker.FairGuests.Select(x => x.Key));
            Assert.InRange(g.GetProperty("x").GetInt32(), 0, 80);
        }
    }

    [Fact]
    public void A_guest_is_caught_only_in_the_window_with_the_same_grace_as_the_golden_jug()
    {
        var h = Wheel();
        Guest(h, "magpie", inSeconds: 5);
        Assert.Equal("Гість уже пішов далі селом", Fair(h, new { op = "guest" }).Message);
        h.Clock.Advance(4.5);                                        // на пів секунди раніше — запас на годинник
        Assert.StartsWith("🐦", Fair(h, new { op = "guest" }).Message);

        Guest(h, "magpie");
        h.Clock.Advance(Clicker.FairGuestShown.TotalSeconds + 1.5);  // запізнився, але в межах дороги
        Assert.True(Fair(h, new { op = "guest" }).Ok);
        Guest(h, "magpie");
        h.Clock.Advance(Clicker.FairGuestShown.TotalSeconds + 2.5);
        Assert.Equal("Гість уже пішов далі селом", Fair(h, new { op = "guest" }).Message);
    }

    [Fact]
    public void A_caught_guest_leaves_and_the_next_one_is_scheduled()
    {
        var h = Wheel();
        Guest(h, "magpie");
        Assert.True(Fair(h, new { op = "guest" }).Ok);
        Assert.Equal("Гість уже пішов далі селом", Fair(h, new { op = "guest" }).Message);
        var at = Market(h).GetProperty("guest").GetProperty("at").GetDateTimeOffset();
        Assert.True(at >= h.Clock.UtcNow + TimeSpan.FromSeconds(Clicker.FairGuestMinSeconds * 0.9));
        Assert.Equal(1, Market(h).GetProperty("guests").GetInt32());
    }

    [Fact]
    public void The_masters_eye_guards_guests_too()
    {
        var h = Wheel();
        Guest(h, "magpie");
        Patch(h, s => s["guard"]!["left"] = 0);
        Assert.Equal("👁 Майстер хоче глянути на твої руки — торкнись глечиків", Fair(h, new { op = "guest" }).Message);
        Assert.Equal("Спершу Око майстра: покажи, що ти не автоклікер", Fair(h, new { op = "guest" }).Message);
        Assert.Equal(0, Market(h).GetProperty("guests").GetInt32());
        PotterHands.Pass(h);
        Assert.StartsWith("🐦", Fair(h, new { op = "guest" }).Message);   // гість ще біля вікна
    }

    [Fact]
    public void A_caught_guest_costs_the_masters_patience_like_a_golden_jug()
    {
        var h = Wheel();
        Patch(h, s => s["guard"]!["left"] = 1000);
        Guest(h, "magpie");
        Assert.True(Fair(h, new { op = "guest" }).Ok);
        lock (h.Room.Sync)
            Assert.Equal(1000 - ClickerGuard.CatchWeight, JsonNode.Parse(h.Room.Game.Save()!)!["guard"]!["left"]!.GetValue<int>());
    }

    [Fact]
    public void Twenty_guests_are_an_achievement()
    {
        var h = Wheel();
        Patch(h, s => s["fair"]!["guests"] = 19);
        Guest(h, "kobzar");
        Assert.True(Fair(h, new { op = "guest" }).Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-guest");
    }

    [Fact]
    public void The_chumaks_salt_halves_the_work_for_a_minute()
    {
        var h = Wheel();
        Guest(h, "chumak");
        Assert.StartsWith("🐂", Fair(h, new { op = "guest" }).Message);
        Assert.Equal(20, View(h).GetProperty("craft").GetProperty("need").GetInt32());
        Assert.Contains(Market(h).GetProperty("buffs").EnumerateArray(), b => b.GetProperty("src").GetString() == "chumak");
        h.Clock.Advance(61);
        Assert.Equal(40, View(h).GetProperty("craft").GetProperty("need").GetInt32());
    }

    [Fact]
    public void The_magpie_brings_three_minutes_of_passive()
    {
        var h = Wheel();
        Guest(h, "magpie");
        Assert.True(Fair(h, new { op = "guest" }).Ok);
        Assert.Equal(Clicker.FairMagpieClicks, Pots(h));             // голе коло: шістдесят кліків

        var rich = Wheel();
        Patch(rich, s => s["upgrades"]!["kiln"] = 100);
        var passive = View(rich).GetProperty("baseSecond").GetDouble();
        var before = Pots(rich);
        Guest(rich, "magpie");
        Assert.True(Fair(rich, new { op = "guest" }).Ok);
        Assert.Equal(before + (long)(passive * Clicker.FairMagpieSeconds), Pots(rich));
    }

    [Fact]
    public void The_lord_leaves_one_special_order_worth_four_times()
    {
        var h = Wheel();
        Patch(h, s => s["total"] = 50_000_000);
        Guest(h, "lord");
        Assert.StartsWith("🎩", Fair(h, new { op = "guest" }).Message);
        var lord = Market(h).GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("lord").GetBoolean());
        Assert.Equal(Clicker.FairLordMult, lord.GetProperty("mult").GetDouble());
        Assert.True(lord.GetProperty("q").GetInt32() >= 2);
        Assert.Equal(Clicker.FairLordLifeMinutes, (lord.GetProperty("until").GetDateTimeOffset() - h.Clock.UtcNow).TotalMinutes, 3);

        // Другий пан, поки перше замовлення ще на дошці, лише лишає на чай.
        Guest(h, "lord");
        Assert.Contains("на чай", Fair(h, new { op = "guest" }).Message);
        Assert.Single(Market(h).GetProperty("orders").EnumerateArray(), o => o.GetProperty("lord").GetBoolean());
    }

    [Fact]
    public void The_kobzars_song_raises_prices_for_two_minutes()
    {
        var h = Wheel(at: Thursday);
        Patch(h, s => s["upgrades"]!["kiln"] = 100);
        var pot = WareView(h, "pot").GetProperty("value").GetInt64();
        Guest(h, "kobzar");
        Assert.StartsWith("🎶", Fair(h, new { op = "guest" }).Message);
        Near(pot * 1.25, WareView(h, "pot").GetProperty("value").GetInt64());
        h.Clock.Advance(121);
        Near(pot, WareView(h, "pot").GetProperty("value").GetInt64());
    }

    [Fact]
    public void The_fortune_teller_shows_what_the_next_adventure_will_bring()
    {
        var h = Wheel();
        Guest(h, "fortune");
        Assert.StartsWith("🔮", Fair(h, new { op = "guest" }).Message);
        Assert.True(Market(h).GetProperty("foresight").GetBoolean());
        var at = Market(h).GetProperty("eventAt").GetDateTimeOffset();
        h.Clock.UtcNow = at;
        var e = Market(h).GetProperty("event");
        Assert.Equal(2, e.GetProperty("sure").GetArrayLength());
        Assert.False(Market(h).GetProperty("foresight").GetBoolean());
    }

    // ---------- пригоди ----------

    [Fact]
    public void An_adventure_comes_after_half_an_hour_to_an_hour_and_waits_ten_minutes()
    {
        var h = Wheel();
        var at = Market(h).GetProperty("eventAt").GetDateTimeOffset();
        Assert.InRange((at - h.Clock.UtcNow).TotalMinutes, 30, 60);
        Assert.Equal(JsonValueKind.Null, Market(h).GetProperty("event").ValueKind);
        h.Clock.UtcNow = at;
        var e = Market(h).GetProperty("event");
        Assert.Equal(JsonValueKind.Null, e.GetProperty("sure").ValueKind);   // без ворожки наслідків не видно
        Assert.Equal(at + Clicker.FairEventWait, e.GetProperty("until").GetDateTimeOffset());

        h.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var m = Market(h);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("event").ValueKind);
        Assert.InRange((m.GetProperty("eventAt").GetDateTimeOffset() - h.Clock.UtcNow).TotalMinutes, 30, 60);
    }

    [Fact]
    public void Choosing_applies_the_rolled_outcome_and_schedules_the_next_adventure()
    {
        var h = Wheel();
        Event(h, "cat-shelf", rollB: 0);
        Assert.Equal("Ця пригода вже минула", Fair(h, new { op = "choose", id = 8, pick = 0 }).Message);
        Assert.Equal("Обери один із двох варіантів", Fair(h, new { op = "choose", id = 7, pick = 2 }).Message);
        var r = Fair(h, new { op = "choose", id = 7, pick = 1 });
        Assert.StartsWith("🐈 Серед черепків знайшлась загублена монета", r.Message);
        Assert.Contains("+24 глеки", r.Message);                    // голе коло: дві хвилини ≈ 24 кліки
        Assert.Equal(24, Pots(h));
        var m = Market(h);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("event").ValueKind);
        Assert.InRange((m.GetProperty("eventAt").GetDateTimeOffset() - h.Clock.UtcNow).TotalMinutes, 30, 60);
        Assert.Equal("Ця пригода вже минула", Fair(h, new { op = "choose", id = 7, pick = 1 }).Message);
    }

    [Fact]
    public void An_expired_adventure_cannot_be_chosen()
    {
        var h = Wheel();
        Event(h, "goat");
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("Ця пригода вже минула", Fair(h, new { op = "choose", id = 7, pick = 0 }).Message);
    }

    [Fact]
    public void No_adventure_ever_takes_more_than_a_tenth_of_the_pots_or_more_than_one_ware()
    {
        foreach (var ev in Clicker.FairEvents)
            for (var pick = 0; pick < 2; pick++)
            {
                var outcomes = (pick == 0 ? ev.A : ev.B).Outcomes.Length;
                for (var roll = 0; roll < outcomes; roll++)
                {
                    var h = Wheel();
                    Patch(h, s => { s["pots"] = 1_000; s["total"] = 1_000; s["upgrades"]!["tsar"] = 5; });   // пасив величезний, глеків мало
                    Items(h, ("pot||1", 3));
                    Event(h, ev.Key, roll, roll);
                    var r = Fair(h, new { op = "choose", id = 7, pick });
                    Assert.True(r.Ok, ev.Key + ": " + r.Message);
                    var pots = Pots(h);
                    Assert.True(pots >= 900, $"{ev.Key}/{pick}/{roll}: лишилось {pots}");
                    var items = View(h).GetProperty("craft").GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("n").GetInt32());
                    Assert.InRange(items, 2, 5);
                    foreach (var b in Market(h).GetProperty("buffs").EnumerateArray())
                    {
                        Assert.True(b.GetProperty("until").GetDateTimeOffset() <= h.Clock.UtcNow + TimeSpan.FromSeconds(Clicker.FairBuffMaxSeconds));
                        Assert.InRange(b.GetProperty("mult").GetDouble(), 0.5, 1.5);
                    }
                    Assert.All(Market(h).GetProperty("rep").EnumerateArray(), x => Assert.InRange(x.GetProperty("pts").GetInt32(), 0, 3));
                }
            }
    }

    [Fact]
    public void There_are_twenty_adventures_with_two_choices_each_in_the_catalog()
    {
        var h = Wheel();
        var fair = View(h).GetProperty("catalog").GetProperty("fair");
        Assert.True(fair.GetProperty("events").GetArrayLength() >= 20);
        Assert.Equal(6, fair.GetProperty("villages").GetArrayLength());
        Assert.Equal(5, fair.GetProperty("guests").GetArrayLength());
        Assert.All(Clicker.FairEvents, e => Assert.All(new[] { e.A, e.B }, c => Assert.InRange(c.Outcomes.Length, 1, 2)));
        Assert.Equal(Clicker.FairEvents.Length, Clicker.FairEvents.Select(e => e.Key).Distinct().Count());
    }

    // ---------- погода й свята ----------

    static DateTimeOffset Kyiv(int y, int m, int d, int hour = 12) =>
        new DateTimeOffset(y, m, d, hour, 0, 0, TimeSpan.Zero) - Days.Kyiv.GetUtcOffset(new DateTime(y, m, d, hour, 0, 0));

    /// <summary>Ціни — цілі глеки, округлені вниз: множник порівнюємо з допуском на округлення.</summary>
    static void Near(double expected, double actual) =>
        Assert.InRange(actual, expected - Math.Max(2, expected * 0.001), expected + Math.Max(2, expected * 0.001));

    [Fact]
    public void Orthodox_easter_by_meeus()
    {
        Assert.Equal(new DateOnly(2027, 5, 2), Clicker.OrthodoxEaster(2027));
        Assert.Equal(new DateOnly(2026, 4, 12), Clicker.OrthodoxEaster(2026));
        Assert.Equal(new DateOnly(2025, 4, 20), Clicker.OrthodoxEaster(2025));
    }

    [Theory]
    [InlineData(2026, 12, 25, "christmas")]
    [InlineData(2026, 12, 24, "christmas")]
    [InlineData(2027, 1, 7, "christmas")]
    [InlineData(2027, 1, 8, null)]
    [InlineData(2026, 12, 23, null)]
    [InlineData(2027, 5, 2, "easter")]
    [InlineData(2027, 4, 25, "easter")]
    [InlineData(2027, 5, 9, "easter")]
    [InlineData(2027, 5, 10, null)]
    [InlineData(2026, 8, 20, "sorochyntsi")]
    [InlineData(2026, 8, 18, "sorochyntsi")]
    [InlineData(2026, 8, 24, "sorochyntsi")]
    [InlineData(2026, 8, 17, null)]
    [InlineData(2026, 8, 25, null)]
    [InlineData(2026, 10, 14, "pokrova")]
    [InlineData(2026, 11, 30, "pokrova")]
    [InlineData(2026, 10, 13, null)]
    [InlineData(2026, 12, 1, null)]
    public void Holidays_by_the_calendar(int y, int m, int d, string? holiday) =>
        Assert.Equal(holiday, Clicker.FairHolidayOf(new DateOnly(y, m, d)));

    [Fact]
    public void The_sorochyntsi_fair_is_the_second_to_last_week_of_august()
    {
        // Останній тиждень серпня — 25–31, передостанній — 18–24; 20 серпня 2026 — у ньому.
        var days = Enumerable.Range(1, 31).Where(d => Clicker.FairHolidayOf(new DateOnly(2026, 8, d)) == "sorochyntsi").ToList();
        Assert.Equal(7, days.Count);
        Assert.Equal(31 - 13, days[0]);
        Assert.Contains(20, days);
    }

    [Fact]
    public void The_calendar_follows_kyiv_not_utc()
    {
        // 23 грудня 22:30 UTC — це вже 24 грудня за Києвом.
        Assert.Equal("christmas", Clicker.FairCalendar(new DateTimeOffset(2026, 12, 23, 22, 30, 0, TimeSpan.Zero)).Holiday);
        Assert.Null(Clicker.FairCalendar(new DateTimeOffset(2026, 12, 23, 21, 30, 0, TimeSpan.Zero)).Holiday);
        Assert.Equal("2026-12-24", Clicker.FairCalendar(new DateTimeOffset(2026, 12, 23, 22, 30, 0, TimeSpan.Zero)).Day);
    }

    [Fact]
    public void Weather_is_the_same_all_day_and_frost_only_in_cold_months()
    {
        var seen = new Dictionary<string, int>();
        for (var day = new DateOnly(2026, 1, 1); day < new DateOnly(2028, 1, 1); day = day.AddDays(1))
        {
            var morning = Clicker.FairCalendar(Kyiv(day.Year, day.Month, day.Day, 7));
            var evening = Clicker.FairCalendar(Kyiv(day.Year, day.Month, day.Day, 19));
            Assert.Equal(morning.Weather, evening.Weather);
            Assert.Equal(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), morning.Day);
            if (day.Month is >= 4 and <= 10) Assert.NotEqual("frost", morning.Weather);
            seen[morning.Weather] = seen.GetValueOrDefault(morning.Weather) + 1;
        }
        Assert.Equal(4, seen.Count);
        Assert.Equal("autumn", Clicker.FairCalendar(Thursday).Season);
        Assert.Equal("winter", Clicker.FairCalendar(Kyiv(2027, 2, 1)).Season);
        Assert.Equal("spring", Clicker.FairCalendar(Kyiv(2027, 3, 1)).Season);
        Assert.Equal("summer", Clicker.FairCalendar(Kyiv(2027, 8, 31)).Season);
    }

    [Fact]
    public void Demand_follows_the_holiday_and_the_weekend()
    {
        long Value(DateTimeOffset at, string ware)
        {
            var h = Wheel(at: at);
            Patch(h, s => { s["total"] = 10_000_000_000; s["upgrades"]!["kiln"] = 100; });
            return WareView(h, ware).GetProperty("value").GetInt64();
        }
        var plainDay = Kyiv(2026, 12, 10);                           // четвер без свята
        Near(Value(plainDay, "makitra") * 2, Value(Kyiv(2026, 12, 25), "makitra"));
        Assert.Equal(Value(plainDay, "pot"), Value(Kyiv(2026, 12, 25), "pot"));
        Near(Value(Kyiv(2027, 4, 22), "bowl") * 1.5, Value(Kyiv(2027, 4, 29), "bowl"));             // обидва четверги
        Near(Value(Kyiv(2026, 10, 7), "kumanets") * 2, Value(Kyiv(2026, 10, 14), "kumanets"));
        Near(Value(Kyiv(2026, 8, 13), "jug") * 1.5, Value(Kyiv(2026, 8, 20), "jug"));
        Near(Value(Thursday, "pot") * 1.2, Value(Kyiv(2026, 9, 12), "pot"));                         // субота
    }

    [Fact]
    public void The_view_tells_weather_season_and_holiday()
    {
        var h = Wheel(at: Kyiv(2026, 8, 20));
        var m = Market(h);
        Assert.Equal("summer", m.GetProperty("season").GetString());
        Assert.Equal("sorochyntsi", m.GetProperty("holiday").GetProperty("key").GetString());
        Assert.Equal("Сорочинський ярмарок", m.GetProperty("holiday").GetProperty("name").GetString());
        Assert.False(m.GetProperty("weekend").GetBoolean());
        Assert.Contains(m.GetProperty("weather").GetString(), new[] { "sun", "cloud", "rain" });
        Assert.Equal(JsonValueKind.Null, Market(Wheel(at: Thursday)).GetProperty("holiday").ValueKind);
    }

    [Fact]
    public void The_sorochyntsi_fair_brings_guests_sooner()
    {
        double Wait(DateTimeOffset at)
        {
            var h = Wheel(3, at);
            return (Market(h).GetProperty("guest").GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow).TotalSeconds;
        }
        Assert.Equal(Wait(Kyiv(2026, 8, 13)) * Clicker.FairSorochyntsiGuests, Wait(Kyiv(2026, 8, 20)), 1);
    }

    [Fact]
    public void Weather_changes_how_long_the_base_rack_dries()
    {
        // Сорок кліків чотирма пачками: виріб готовий на четвертій, тобто через 3 с після початку.
        double Drying(DateTimeOffset at, out double mult)
        {
            var h = Wheel(at: at);
            mult = Market(h).GetProperty("dry").GetDouble();
            for (var i = 0; i < 4; i++) { Assert.True(Act(h, "spin", PotterHands.Human(10)).Ok); h.Clock.Advance(1); }
            Assert.Equal(mult * Clicker.DryTime.TotalMilliseconds, View(h).GetProperty("craft").GetProperty("dryMs").GetDouble(), 6);
            return (View(h).GetProperty("craft").GetProperty("rack")[0].GetProperty("dryAt").GetDateTimeOffset() - at).TotalSeconds - 3;
        }
        Assert.Equal(Clicker.DryTime.TotalSeconds * Clicker.FairDrySun, Drying(FindDay("sun"), out var sunMult), 3);
        Assert.Equal(Clicker.DryTime.TotalSeconds * Clicker.FairDryRain, Drying(FindDay("rain"), out var rainMult), 3);
        Assert.Equal(Clicker.FairDrySun, sunMult);
        Assert.Equal(Clicker.FairDryRain, rainMult);
    }

    /// <summary>Перший день вересня 2026 з такою погодою (у будній полудень за Києвом).</summary>
    static DateTimeOffset FindDay(string weather)
    {
        for (var d = 1; d <= 30; d++)
        {
            var at = Kyiv(2026, 9, d);
            if (Clicker.FairCalendar(at).Weather == weather) return at;
        }
        throw new InvalidOperationException("у вересні нема " + weather);
    }

    [Fact]
    public void Events_can_speed_up_or_slow_down_drying_within_limits()
    {
        var h = Wheel(at: Thursday);
        var dry = Market(h).GetProperty("dry").GetDouble();
        Event(h, "cold-night");
        Assert.True(Fair(h, new { op = "choose", id = 7, pick = 0 }).Ok);
        Assert.Equal(dry * 0.6, Market(h).GetProperty("dry").GetDouble(), 9);
        h.Clock.Advance(301);
        Assert.Equal(dry, Market(h).GetProperty("dry").GetDouble(), 9);
    }

    // ---------- збереження й обпал ----------

    [Fact]
    public void The_fair_survives_a_reload()
    {
        var h = Wheel();
        Rep(h, "kosiv", 30);
        Guest(h, "chumak");
        Assert.True(Fair(h, new { op = "guest" }).Ok);
        Event(h, "goat", foresight: true);
        var before = Views.Text(Market(h));
        Patch(h, _ => { });
        Assert.Equal(before, Views.Text(Market(h)));
    }

    [Fact]
    public void An_old_save_without_the_fair_starts_from_scratch()
    {
        var h = Wheel();
        Patch(h, s => s.Remove("fair"));
        var m = Market(h);
        Assert.Equal(Clicker.FairBoardMin, m.GetProperty("orders").GetArrayLength());
        Assert.All(m.GetProperty("rep").EnumerateArray(), r => Assert.Equal(0, r.GetProperty("pts").GetInt32()));
        Assert.Equal(1.0, m.GetProperty("allMult").GetDouble());
        Assert.Equal(JsonValueKind.Object, m.GetProperty("guest").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("event").ValueKind);
        Assert.Equal(0, m.GetProperty("guests").GetInt32());
    }

    [Fact]
    public void Broken_fair_rows_in_a_save_are_dropped()
    {
        var h = Wheel();
        Board(h, Order(h, 100, "atlantis", "pot", 1, 2), Order(h, 101, "kosiv", "vase", 1, 2), Order(h, 102, "kosiv", "pot", 9, 2),
            Order(h, 103, "kosiv", "pot", 1, 2, style: "nope"), Order(h, 104, "kosiv", "pot", 1, 2, mult: 99), Order(h, 105, "kosiv", "pot", 1, 2));
        Patch(h, s =>
        {
            s["fair"]!["rep"] = new JsonObject { ["kosiv"] = 5, ["mars"] = 500, ["opishnia"] = -4 };
            s["fair"]!["buffs"] = new JsonArray(new JsonObject { ["kind"] = "work", ["mult"] = 0.01, ["until"] = h.Clock.UtcNow.AddMinutes(1).ToString("O"), ["src"] = "x" });
        });
        var m = Market(h);
        Assert.Equal(new[] { 105 }, m.GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()));
        Assert.Equal(5, RepOf(h, "kosiv"));
        Assert.Equal(0, RepOf(h, "opishnia"));
        Assert.Equal(0, m.GetProperty("buffs").GetArrayLength());
    }

    [Fact]
    public void Firing_burns_the_orders_but_not_the_respect()
    {
        var h = Wheel();
        Patch(h, s => s["total"] = 2_000_000_000);
        Board(h, Order(h, 100, "kosiv", "pot", 1, 2), Order(h, 101, "opishnia", "pot", 1, 2));
        Rep(h, "opishnia", 60);
        Patch(h, s => s["fair"]!["guests"] = 7);
        Assert.True(Act(h, "fire").Ok);
        var m = Market(h);
        var ids = m.GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(Clicker.FairBoardMin, ids.Count);
        Assert.DoesNotContain(100, ids);
        Assert.DoesNotContain(101, ids);
        Assert.Equal(60, RepOf(h, "opishnia"));
        Assert.Equal(7, m.GetProperty("guests").GetInt32());
    }

    [Fact]
    public void The_merchant_board_still_takes_orders_next_to_the_fair()
    {
        var h = Wheel();
        Patch(h, s => s["pots"] = 1_000_000);
        var board = View(h).GetProperty("house").GetProperty("orders").GetProperty("board");
        var invest = board.EnumerateArray().First(o => o.GetProperty("kind").GetString() == "invest");
        Assert.True(Act(h, "take", new { id = invest.GetProperty("id").GetInt32() }).Ok);
        Assert.Equal(Clicker.FairBoardMin, Market(h).GetProperty("orders").GetArrayLength());
    }

    [Fact]
    public void The_view_is_a_pure_function_of_state()
    {
        var h = Wheel();
        h.Clock.Advance(TimeSpan.FromMinutes(45));
        Assert.Equal(Views.Text(View(h)), Views.Text(View(h)));
    }
}
