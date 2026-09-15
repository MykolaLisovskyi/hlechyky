using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Гончарне коло після «треба п'ять хвилин клацати, щоб щось прокачати»: розгін кола (Маховик), глек, що падає
/// з полиці (Кошик, серія, Кіт на полиці), і те, як усе це переживає збереження. Основа — у <see cref="ClickerTests"/>,
/// драбина й обпал — у <see cref="ClickerProgressTests"/>.
/// </summary>
public class ClickerLiveTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static double Heat(RoomHarness h) => View(h).GetProperty("heat").GetDouble();
    static double Momentum(RoomHarness h) => View(h).GetProperty("momentum").GetDouble();
    static JsonElement Fall(RoomHarness h) => View(h).GetProperty("fall");
    static DateTimeOffset FallAt(RoomHarness h) => Fall(h).GetProperty("at").GetDateTimeOffset();
    static int Streak(RoomHarness h) => Fall(h).GetProperty("streak").GetInt32();

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);

    /// <summary>Пачка людських кліків: голий spin без почерку Око майстра відкидає.</summary>
    static ActResult Spin(RoomHarness h, int n) => Act(h, "spin", PotterHands.Human(n));

    /// <summary>Переписати збережений стан і завантажити назад — набивати рівні кліками нема коли.</summary>
    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Levels(RoomHarness h, params (string Key, int Level)[] levels) => Patch(h, s =>
    {
        foreach (var (key, level) in levels) s["upgrades"]![key] = level;
    });

    /// <summary>Глек летить з полиці просто зараз.</summary>
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

    // ---------- розгін кола ----------

    [Fact]
    public void Without_a_flywheel_every_click_is_worth_exactly_one_however_fast()
    {
        var h = Wheel();
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(12, Pots(h));
        h.Clock.AdvanceMs(1000);
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(24, Pots(h));

        // Коло гріється й без маховика (клієнт малює швидкість), але множник — рівно один.
        Assert.True(Heat(h) > 10);
        Assert.Equal(1, Momentum(h));
        Assert.Equal(1, View(h).GetProperty("momentumMax").GetDouble());
    }

    [Fact]
    public void A_flywheel_turns_fast_clicks_into_more_pots()
    {
        var h = Wheel();
        Levels(h, ("flywheel", 1));
        Assert.Equal(1.5, View(h).GetProperty("momentumMax").GetDouble());

        // Холодне коло: множник беруть на середині пачки — шість гарячих кліків із вісімнадцяти.
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(14, Pots(h));                       // 12 × (1 + 0,5 × 6/18) = 14

        // Через секунду розгін спав до 12·e^(−1/3) ≈ 8,6 і пачка додала ще шість: 12 × 1,4055 = 16.
        h.Clock.AdvanceMs(1000);
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(30, Pots(h));
    }

    [Fact]
    public void A_wheel_kept_hot_hits_the_flywheel_ceiling()
    {
        var h = Wheel();
        Levels(h, ("flywheel", 8));                      // стеля ×5
        for (var i = 0; i < 10; i++)
        {
            Assert.True(Spin(h, 12).Ok);
            h.Clock.AdvanceMs(1000);
        }
        var before = Pots(h);
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(60, Pots(h) - before);              // 12 кліків × 5
        Assert.Equal(5, Momentum(h), 3);
    }

    [Fact]
    public void Momentum_cools_when_the_potter_stops()
    {
        var h = Wheel();
        Levels(h, ("flywheel", 4));
        Assert.True(Spin(h, 12).Ok);
        Assert.True(Heat(h) > 10);
        Assert.True(Momentum(h) > 1.5);

        h.Clock.AdvanceMs(30_000);
        Assert.True(Heat(h) < 0.01);
        Assert.Equal(1, Momentum(h), 3);
    }

    [Fact]
    public void An_empty_batch_does_not_heat_the_wheel()
    {
        // Відро віддало всі дванадцять — друга пачка тієї ж миті не рахується і кола не гріє.
        var h = Wheel();
        Assert.True(Spin(h, 12).Ok);
        var heat = Heat(h);
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(heat, Heat(h), 6);
        Assert.Equal(12, Pots(h));
    }

    [Fact]
    public void Heat_survives_a_reload()
    {
        var h = Wheel();
        Assert.True(Spin(h, 12).Ok);
        Patch(h, _ => { });
        Assert.Equal(12, Heat(h), 3);
    }

    [Fact]
    public void The_view_carries_the_momentum_constants_the_client_mirrors()
    {
        var v = View(Wheel());
        Assert.Equal(Clicker.HeatFull, v.GetProperty("heatFull").GetDouble());
        Assert.Equal(Clicker.HeatTau, v.GetProperty("heatTau").GetDouble());
        Assert.Equal(0, v.GetProperty("heat").GetDouble());
    }

    // ---------- глек з полиці ----------

    [Fact]
    public void A_jug_is_on_the_shelf_from_the_first_second()
    {
        var h = Wheel();
        var f = Fall(h);
        var at = f.GetProperty("at").GetDateTimeOffset();
        var until = f.GetProperty("until").GetDateTimeOffset();
        Assert.InRange((at - h.Clock.UtcNow).TotalSeconds, Clicker.FallMinSeconds, Clicker.FallMaxSeconds);
        Assert.Equal(Clicker.FallShown, until - at);
        Assert.InRange(f.GetProperty("x").GetInt32(), 8, 79);
        Assert.Equal(0, f.GetProperty("streak").GetInt32());
        Assert.Equal(0, View(h).GetProperty("grabbed").GetInt32());
    }

    [Fact]
    public void Grabbing_the_jug_pays_two_minutes_of_passive_and_a_hundred_clicks()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));                   // 5 глеків за секунду, клік — 1
        FallNow(h);
        Assert.Equal(720, Fall(h).GetProperty("gain").GetInt64());   // 5 × 120 + 1 × 100 + 20

        var before = Pots(h);
        var r = Act(h, "grab");
        Assert.True(r.Ok);
        Assert.Contains("Спіймав", r.Message);
        Assert.Equal(720, Pots(h) - before);
        Assert.Equal(1, Streak(h));
        Assert.Equal(1, View(h).GetProperty("grabbed").GetInt32());
    }

    [Fact]
    public void The_basket_and_the_streak_multiply_the_catch()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10), ("basket", 5));    // кошик ×2
        Patch(h, s => s["fallStreak"] = 3);              // серія ×1,3
        FallNow(h);
        var before = Pots(h);
        Assert.True(Act(h, "grab").Ok);
        Assert.Equal(1840, Pots(h) - before);            // (600 + 100) × 2 × 1,3 + 20
        Assert.Equal(4, Streak(h));
    }

    [Fact]
    public void The_streak_bonus_stops_at_ten()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        Patch(h, s => s["fallStreak"] = 25);
        FallNow(h);
        Assert.Equal(1420, Fall(h).GetProperty("gain").GetInt64());  // 700 × 2 + 20
    }

    [Fact]
    public void The_fair_multiplies_the_jug_too()
    {
        var h = Wheel();
        Levels(h, ("apprentice", 10));
        Patch(h, s => s["fairUntil"] = (h.Clock.UtcNow + TimeSpan.FromSeconds(60)).ToString("O"));
        FallNow(h);
        var before = Pots(h);
        Assert.True(Act(h, "grab").Ok);
        Assert.Equal(4920, Pots(h) - before);            // 700 × 7 + 20
    }

    [Fact]
    public void Too_late_and_the_jug_is_shards_and_the_streak_is_gone()
    {
        var h = Wheel();
        Patch(h, s => s["fallStreak"] = 4);
        FallNow(h);
        h.Clock.AdvanceMs((int)(Clicker.FallShown + Clicker.CatchGrace).TotalMilliseconds + 500);

        var r = Act(h, "grab");
        Assert.False(r.Ok);
        Assert.Contains("розбився", r.Message);
        Assert.Equal(0, Streak(h));
        // Розбитий уже переставлено: наступний — від «зараз», а не від кінця старого.
        Assert.InRange((FallAt(h) - h.Clock.UtcNow).TotalSeconds, Clicker.FallMinSeconds, Clicker.FallMaxSeconds);
    }

    [Fact]
    public void A_catch_puts_the_next_jug_on_the_shelf()
    {
        var h = Wheel();
        FallNow(h);
        Assert.True(Act(h, "grab").Ok);
        Assert.InRange((FallAt(h) - h.Clock.UtcNow).TotalSeconds, Clicker.FallMinSeconds, Clicker.FallMaxSeconds);
        // Той самий глек удруге не ловиться.
        Assert.False(Act(h, "grab").Ok);
    }

    [Fact]
    public void The_cat_makes_jugs_fall_sooner()
    {
        var h = Wheel();
        Patch(h, s => s["secrets"] = new JsonArray("cat"));
        for (var i = 0; i < 3; i++)
        {
            FallNow(h);
            Assert.True(Act(h, "grab").Ok);
            Assert.InRange((FallAt(h) - h.Clock.UtcNow).TotalSeconds, Clicker.CatMinSeconds, Clicker.CatMaxSeconds);
        }
    }

    [Fact]
    public void A_hundred_catches_and_ten_in_a_row_are_achievements()
    {
        var h = Wheel();
        Patch(h, s => { s["grabbed"] = 99; s["fallStreak"] = 9; });
        FallNow(h);
        Assert.True(Act(h, "grab").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-grab");
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-streak");
        Assert.Equal(100, View(h).GetProperty("grabbed").GetInt32());
    }

    [Fact]
    public void The_masters_eye_watches_the_shelf_as_well()
    {
        // Перевірка вже назріла: замість глеків — прохання торкнутись полиці, і доки не відповіси, глек не ловиться.
        var h = Wheel();
        Patch(h, s => s["guard"]!["left"] = 0);
        FallNow(h);
        var before = Pots(h);
        var r = Act(h, "grab");
        Assert.True(r.Ok);
        Assert.Contains("Майстер", r.Message);
        Assert.Equal(before, Pots(h));
        Assert.Equal(0, Streak(h));

        var again = Act(h, "grab");
        Assert.False(again.Ok);
        Assert.Contains("Око майстра", again.Message);
    }

    [Fact]
    public void The_shelf_survives_save_and_load()
    {
        var h = Wheel();
        Patch(h, s => { s["grabbed"] = 42; s["fallStreak"] = 7; });
        FallNow(h);
        var at = FallAt(h);
        Patch(h, _ => { });
        Assert.Equal(at, FallAt(h));
        Assert.Equal(7, Streak(h));
        Assert.Equal(42, View(h).GetProperty("grabbed").GetInt32());
    }

    [Fact]
    public void Old_saves_without_a_shelf_get_a_jug_scheduled()
    {
        var h = Wheel();
        lock (h.Room.Sync)
            h.Room.Game.Load("""{"pots":5,"total":5,"upgrades":{"wheel":2},"soldToday":null}""");
        Assert.True(FallAt(h) > h.Clock.UtcNow);
        Assert.Equal(0, Streak(h));
        Assert.Equal(0, Heat(h));
        Assert.Equal(5, Pots(h));
    }

    // ---------- полиця верстатів ----------

    [Fact]
    public void The_flywheel_and_the_basket_are_always_on_the_shelf()
    {
        var ups = View(Wheel()).GetProperty("upgrades");
        var flywheel = ups.GetProperty("flywheel");
        Assert.Equal("skill", flywheel.GetProperty("kind").GetString());
        Assert.Equal(8, flywheel.GetProperty("max").GetInt32());
        Assert.Equal(250, flywheel.GetProperty("price").GetInt64());
        Assert.True(flywheel.GetProperty("open").GetBoolean());
        Assert.Equal(0, flywheel.GetProperty("gain").GetDouble());

        var basket = ups.GetProperty("basket");
        Assert.Equal(10, basket.GetProperty("max").GetInt32());
        Assert.Equal(2_500, basket.GetProperty("price").GetInt64());
        Assert.True(basket.GetProperty("open").GetBoolean());

        // Драбина пасивних верстатів від них не залежить: піч і далі відкривається підмайстром.
        Assert.False(ups.GetProperty("kiln").GetProperty("open").GetBoolean());
    }

    [Fact]
    public void The_flywheel_is_bought_like_any_other_workbench_and_burns_in_the_kiln()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 1_000; s["total"] = Clicker.TotalFor(1); });
        Assert.True(Act(h, "buy", new { key = "flywheel", n = 2 }).Ok);
        Assert.Equal(2, View(h).GetProperty("upgrades").GetProperty("flywheel").GetProperty("level").GetInt32());
        Assert.Equal(2, View(h).GetProperty("momentumMax").GetDouble());

        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(0, View(h).GetProperty("upgrades").GetProperty("flywheel").GetProperty("level").GetInt32());
        Assert.Equal(1, View(h).GetProperty("momentumMax").GetDouble());
    }
}
