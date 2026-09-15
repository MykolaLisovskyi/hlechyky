using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Гончарне коло після «нема що робити»: драбина верстатів, віхи, розписний глек, обпал за клейма майстра,
/// родинні секрети, розписи й стеля черепків від клейм. Основа (клік, пасив, прилавок) — у <see cref="ClickerTests"/>.
/// </summary>
public class ClickerProgressTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static long Total(RoomHarness h) => View(h).GetProperty("total").GetInt64();
    static long PerClick(RoomHarness h) => View(h).GetProperty("perClick").GetInt64();
    static double PerSecond(RoomHarness h) => View(h).GetProperty("perSecond").GetDouble();
    static JsonElement Up(RoomHarness h, string key) => View(h).GetProperty("upgrades").GetProperty(key);
    static int LevelOf(RoomHarness h, string key) => Up(h, key).GetProperty("level").GetInt32();
    static bool Open(RoomHarness h, string key) => Up(h, key).GetProperty("open").GetBoolean();
    static double AllMult(RoomHarness h) => View(h).GetProperty("allMult").GetDouble();
    static DateTimeOffset GoldenAt(RoomHarness h) => View(h).GetProperty("golden").GetProperty("at").GetDateTimeOffset();

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);

    /// <summary>Переписати збережений стан і завантажити назад — набивати мільярди кліками нема коли.</summary>
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
        s["total"] = total ?? pots;
    });

    static void Levels(RoomHarness h, params (string Key, int Level)[] levels) => Patch(h, s =>
    {
        foreach (var (key, level) in levels) s["upgrades"]![key] = level;
    });

    /// <summary>Поставити розписний глек потрібного виду просто зараз.</summary>
    static void JugNow(RoomHarness h, Clicker.GoldenKind kind) => Patch(h, s =>
    {
        var now = h.Clock.UtcNow;
        s["golden"] = new JsonObject
        {
            ["at"] = now.ToString("O"),
            ["until"] = (now + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)kind,
            ["x"] = 10,
            ["y"] = 10,
        };
    });

    static void Strings(RoomHarness h, string field, params string[] values) => Patch(h, s =>
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        s[field] = arr;
    });

    // ---------- драбина верстатів ----------

    [Fact]
    public void The_ladder_opens_one_workbench_at_a_time()
    {
        var h = Wheel();
        Assert.True(Open(h, "wheel"));
        Assert.True(Open(h, "clay"));
        Assert.True(Open(h, "apprentice"));
        Assert.False(Open(h, "kiln"));

        Levels(h, ("apprentice", 1));
        Assert.True(Open(h, "kiln"));
        Assert.False(Open(h, "workshop"));

        Levels(h, ("kiln", 1));
        Assert.True(Open(h, "workshop"));
        Assert.False(Open(h, "fair"));
    }

    [Fact]
    public void A_closed_workbench_is_still_sold_to_whoever_can_pay()
    {
        // Полиця ховає картку, щоб не лякати, а не забороняє: правила вирішують глеки, а не екран.
        var h = Wheel();
        Give(h, 100_000);
        Assert.True(Act(h, "buy", new { key = "workshop" }).Ok);
        Assert.Equal(25, PerSecond(h));
    }

    [Fact]
    public void The_new_workbenches_grow_by_fifteen_percent_a_level()
    {
        var workshop = Clicker.Shop.Single(u => u.Key == "workshop");
        Assert.Equal(100_000, workshop.Price(0));
        Assert.Equal(115_000, workshop.Price(1));
        Assert.Equal(132_250, workshop.Price(2));

        // А перші верстати дорожчають, як і дорожчали, — у півтора раза.
        Assert.Equal(23, Clicker.Shop[0].Price(1));
    }

    [Fact]
    public void Every_idle_workbench_up_the_ladder_costs_more_and_earns_more()
    {
        var idle = Clicker.Shop.Where(u => u.Kind == ClickerKind.Idle).ToList();
        for (var i = 1; i < idle.Count; i++)
        {
            Assert.True(idle[i].Base > idle[i - 1].Base, idle[i].Key);
            Assert.True(idle[i].Rate > idle[i - 1].Rate, idle[i].Key);
            Assert.Equal(3, idle[i].Steps.Length);
        }
    }

    [Fact]
    public void A_price_that_does_not_fit_in_long_is_the_ceiling_not_garbage()
    {
        var tsar = Clicker.Shop.Single(u => u.Key == "tsar");
        Assert.Equal(long.MaxValue, tsar.Price(400));
        Assert.Equal(long.MaxValue, tsar.MarkPrice(2));
        Assert.True(tsar.Price(10) > tsar.Price(9));
    }

    [Fact]
    public void Buying_ten_at_once_takes_as_many_levels_as_the_pots_allow()
    {
        var h = Wheel();
        Give(h, 1_000);
        var wheel = Clicker.Shop[0];
        long spent = 0;
        var fits = 0;
        while (spent + wheel.Price(fits) <= 1_000) spent += wheel.Price(fits++);

        var r = Act(h, "buy", new { key = "wheel", n = 10 });

        Assert.True(r.Ok);
        Assert.Equal(8, fits);
        Assert.Equal($"Швидше коло +{fits} — рівень {fits}", r.Message);
        Assert.Equal(fits, LevelOf(h, "wheel"));
        Assert.Equal(1_000 - spent, Pots(h));
    }

    [Fact]
    public void Buying_many_stops_at_the_ceiling_of_good_clay()
    {
        var h = Wheel();
        Give(h, 10_000_000);
        Assert.Equal("Гарна глина +5 — рівень 5", Act(h, "buy", new { key = "clay", n = 1000 }).Message);
        Assert.Equal("Гарна глина: кращої вже не буває", Act(h, "buy", new { key = "clay", n = 3 }).Message);
    }

    [Fact]
    public void Buying_none_or_less_is_refused()
    {
        var h = Wheel();
        Give(h, 1_000);
        Assert.Equal("Скільки купити — хоч один", Act(h, "buy", new { key = "wheel", n = 0 }).Message);
        Assert.Equal(1_000, Pots(h));
    }

    // ---------- віхи ----------

    [Fact]
    public void A_mark_opens_at_its_level_and_doubles_its_workbench()
    {
        var h = Wheel();
        Levels(h, ("kiln", 10));
        Give(h, 10_000_000);
        Assert.Equal(30, PerSecond(h));

        var marks = View(h).GetProperty("marks").EnumerateArray().Select(m => m.GetProperty("key").GetString()).ToList();
        Assert.Contains("kiln:10", marks);
        Assert.DoesNotContain("kiln:25", marks);

        var price = Clicker.Shop.Single(u => u.Key == "kiln").MarkPrice(0);
        Assert.Equal(Clicker.Shop.Single(u => u.Key == "kiln").Price(10) * 8, price);
        Assert.Equal("«Дубові дрова»: Піч ×2", Act(h, "mark", new { key = "kiln:10" }).Message);
        Assert.Equal(60, PerSecond(h));
        Assert.Equal(10_000_000 - price, Pots(h));
        Assert.Equal(1, Up(h, "kiln").GetProperty("marks").GetInt32());

        Assert.Equal("«Дубові дрова» уже є", Act(h, "mark", new { key = "kiln:10" }).Message);
        Assert.Equal("Спершу Піч до рівня 25", Act(h, "mark", new { key = "kiln:25" }).Message);
        Assert.Equal("Такої віхи нема", Act(h, "mark", new { key = "kiln:11" }).Message);
        Assert.DoesNotContain(View(h).GetProperty("marks").EnumerateArray(), m => m.GetProperty("key").GetString() == "kiln:10");
    }

    [Fact]
    public void The_wheel_marks_double_the_click_and_then_pour_the_passive_into_it()
    {
        var h = Wheel();
        Levels(h, ("wheel", 25), ("workshop", 100));   // клік 26, пасив 2 500 за секунду
        Give(h, 1_000_000_000);
        Assert.Equal(26, PerClick(h));

        Assert.True(Act(h, "mark", new { key = "wheel:10" }).Ok);
        Assert.Equal(52, PerClick(h));

        Assert.True(Act(h, "mark", new { key = "wheel:25" }).Ok);
        Assert.Equal(52 + 25, PerClick(h));             // +1 % від 2 500
    }

    // ---------- розписний глек ----------

    [Fact]
    public void The_next_painted_jug_is_scheduled_minutes_ahead_and_inside_the_stage()
    {
        var h = Wheel();
        var v = View(h).GetProperty("golden");
        var wait = v.GetProperty("at").GetDateTimeOffset() - h.Clock.UtcNow;

        Assert.InRange(wait.TotalSeconds, Clicker.GoldenMinSeconds, Clicker.GoldenMaxSeconds);
        Assert.Equal(Clicker.GoldenShown, v.GetProperty("until").GetDateTimeOffset() - v.GetProperty("at").GetDateTimeOffset());
        Assert.InRange(v.GetProperty("x").GetInt32(), 0, 90);
        Assert.InRange(v.GetProperty("y").GetInt32(), 0, 90);
    }

    [Fact]
    public void A_painted_jug_is_caught_only_while_it_stands_on_the_wheel()
    {
        var h = Wheel();
        var at = GoldenAt(h);

        h.Clock.UtcNow = at - TimeSpan.FromSeconds(5);
        Assert.Equal("Розписний глек уже втік", Act(h, "catch").Message);

        h.Clock.UtcNow = at + TimeSpan.FromSeconds(3);
        Assert.True(Act(h, "catch").Ok);
        Assert.Equal(1, View(h).GetProperty("caught").GetInt32());

        // Той самий глек удруге не ловиться: на його місці вже розклад наступного.
        Assert.Equal("Розписний глек уже втік", Act(h, "catch").Message);
        Assert.True(GoldenAt(h) > h.Clock.UtcNow);
    }

    [Fact]
    public void A_click_that_left_in_the_last_moment_still_counts()
    {
        var h = Wheel();
        var at = GoldenAt(h);
        h.Clock.UtcNow = at + Clicker.GoldenShown + TimeSpan.FromSeconds(1.5);
        Assert.True(Act(h, "catch").Ok);
    }

    [Fact]
    public void A_missed_jug_is_replaced_by_the_next_one_on_a_look()
    {
        var h = Wheel();
        var at = GoldenAt(h);
        h.Clock.UtcNow = at + TimeSpan.FromMinutes(1);

        Assert.True(Act(h, "look").Ok);
        Assert.True(GoldenAt(h) > h.Clock.UtcNow);
        Assert.Equal(0, View(h).GetProperty("caught").GetInt32());
    }

    [Fact]
    public void The_fair_multiplies_the_passive_only_while_it_lasts()
    {
        var h = Wheel();
        Levels(h, ("kiln", 1));
        JugNow(h, Clicker.GoldenKind.Fair);

        Assert.Equal("🎪 Ярмарок! Усе ×7 на 66 с", Act(h, "catch").Message);
        Assert.Equal(21, PerSecond(h));
        Assert.Equal(0, Pots(h));

        h.Clock.Advance(100);
        Assert.Equal(66 * 3 * 7 + 34 * 3, Pots(h));
        Assert.Equal(3, PerSecond(h));
    }

    [Fact]
    public void Inspiration_makes_every_click_count_twenty_five_times()
    {
        var h = Wheel();
        JugNow(h, Clicker.GoldenKind.Inspire);

        Assert.Equal("✨ Натхнення! Клік ×25 на 15 с", Act(h, "catch").Message);
        Assert.True(Act(h, "spin", PotterHands.Human(1)).Ok);
        Assert.Equal(25, Pots(h));

        h.Clock.Advance(16);
        Act(h, "spin", PotterHands.Human(1));
        Assert.Equal(26, Pots(h));
    }

    [Fact]
    public void The_merchant_pays_a_share_of_the_pile_but_not_more_than_a_quarter_hour_of_work()
    {
        var h = Wheel();
        Levels(h, ("kiln", 1));                         // 3 глеки за секунду: чверть години — 2 700
        Give(h, 1_000_000);
        JugNow(h, Clicker.GoldenKind.Merchant);

        Assert.Equal("🧺 Щедрий купець: +2 713 глеків", Plain(Act(h, "catch").Message));
        Assert.Equal(1_000_000 + 2_713, Pots(h));
        Assert.Equal(1_000_000 + 2_713, Total(h));
    }

    [Fact]
    public void A_merchant_for_a_beginner_still_brings_something()
    {
        var h = Wheel();
        JugNow(h, Clicker.GoldenKind.Merchant);
        Assert.True(Act(h, "catch").Ok);
        Assert.Equal(13, Pots(h));
    }

    [Fact]
    public void The_fiftieth_jug_unlocks_the_achievement()
    {
        var h = Wheel();
        Patch(h, s => s["caught"] = 49);
        JugNow(h, Clicker.GoldenKind.Merchant);

        Act(h, "catch");

        var award = Assert.Single(h.Awards);
        Assert.Equal("ach:potter-golden", award.Reason);
        Assert.Equal(0, award.Shards);
    }

    [Fact]
    public void The_long_fair_secret_doubles_the_bonus_time()
    {
        var h = Wheel();
        Strings(h, "secrets", "longfair");
        Patch(h, s => s["stamps"] = 20);
        JugNow(h, Clicker.GoldenKind.Inspire);
        Assert.Equal("✨ Натхнення! Клік ×25 на 30 с", Act(h, "catch").Message);
    }

    [Fact]
    public void The_omen_brings_the_jugs_sooner()
    {
        var h = Wheel();
        Strings(h, "secrets", "omen");
        Patch(h, s => s["stamps"] = 5);
        h.Clock.UtcNow = GoldenAt(h) + TimeSpan.FromMinutes(1);
        Act(h, "look");

        var wait = GoldenAt(h) - h.Clock.UtcNow;
        Assert.InRange(wait.TotalSeconds, Clicker.OmenMinSeconds, Clicker.OmenMaxSeconds);
    }

    // ---------- обпал ----------

    [Fact]
    public void Firing_needs_at_least_one_stamp()
    {
        var h = Wheel();
        Give(h, 999_999_999);

        Assert.Equal("Ще рано: наступне клеймо — на 1 млрд глеків за весь час", Act(h, "fire").Message);
        Assert.Equal(999_999_999, Pots(h));
        Assert.Equal(1, View(h).GetProperty("nextStampAt").GetInt64() / 1_000_000_000);
    }

    [Fact]
    public void Stamps_follow_the_square_root_of_the_lifetime_total()
    {
        Assert.Equal(0, Clicker.StampsFor(0));
        Assert.Equal(0, Clicker.StampsFor(999_999_999));
        Assert.Equal(1, Clicker.StampsFor(1_000_000_000));
        Assert.Equal(2, Clicker.StampsFor(4_000_000_000));
        Assert.Equal(10, Clicker.StampsFor(100_000_000_000));
        Assert.Equal(4_000_000_000, Clicker.TotalFor(2));
    }

    [Fact]
    public void Firing_burns_the_workshop_but_keeps_the_total_the_styles_and_the_stamps()
    {
        var h = Wheel();
        Levels(h, ("kiln", 10), ("wheel", 12), ("clay", 3), ("workshop", 4));
        Strings(h, "marks", "kiln:10");
        Strings(h, "styles", "gavarets");
        Patch(h, s => s["wear"] = "gavarets");
        Give(h, 4_000_000_000);
        Assert.Equal(2, View(h).GetProperty("stampsReady").GetInt32());

        Assert.Equal("🔥 Обпал! +2 клейма — тепер +4 % до всього", Act(h, "fire").Message);

        Assert.Equal(0, Pots(h));
        Assert.Equal(4_000_000_000, Total(h));
        foreach (var up in Clicker.Shop) Assert.Equal(0, LevelOf(h, up.Key));
        Assert.Equal(0, Up(h, "kiln").GetProperty("marks").GetInt32());
        Assert.Equal(2, View(h).GetProperty("stamps").GetInt32());
        Assert.Equal(1, View(h).GetProperty("firings").GetInt32());
        Assert.Equal(1.05 * 1.04, AllMult(h), 9);
        Assert.Equal("gavarets", View(h).GetProperty("wear").GetString());
        Assert.Equal("ach:potter-fire", Assert.Single(h.Awards).Reason);

        Assert.StartsWith("Ще рано", Act(h, "fire").Message);
    }

    [Fact]
    public void An_early_firing_loses_nothing_because_stamps_count_the_whole_lifetime()
    {
        var h = Wheel();
        Give(h, 1_000_000_000);
        Assert.Equal("🔥 Обпал! +1 клеймо — тепер +2 % до всього", Act(h, "fire").Message);

        Give(h, 0, total: 9_000_000_000);
        Assert.Equal("🔥 Обпал! +2 клейма — тепер +6 % до всього", Act(h, "fire").Message);
        Assert.Equal(3, View(h).GetProperty("stamps").GetInt32());
        Assert.Single(h.Awards);                         // «Перший обпал» — один раз
    }

    [Fact]
    public void Stamps_make_every_pot_worth_more()
    {
        var h = Wheel();
        Levels(h, ("kiln", 10));
        Patch(h, s => s["stamps"] = 50);                // +100 %
        Assert.Equal(60, PerSecond(h));
    }

    [Fact]
    public void Secrets_are_bought_with_free_stamps_and_stamps_keep_their_bonus()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 10);

        Assert.StartsWith("🤫 Довга ніч", Act(h, "secret", new { key = "night" }).Message);
        Assert.Equal(7, View(h).GetProperty("stampsFree").GetInt32());
        Assert.True(Act(h, "secret", new { key = "omen" }).Ok);
        Assert.Equal("Бракує клейм: треба ще 6", Act(h, "secret", new { key = "kin" }).Message);
        Assert.Equal("«Довга ніч» уже знаєш", Act(h, "secret", new { key = "night" }).Message);
        Assert.Equal("Такого секрету в родині нема", Act(h, "secret", new { key = "чари" }).Message);

        Assert.Equal(2, View(h).GetProperty("stampsFree").GetInt32());
        Assert.Equal(1.2, AllMult(h), 9);               // витрачені клейма бонусу не губують
        Assert.Equal(12, View(h).GetProperty("offlineHours").GetDouble());
    }

    [Fact]
    public void The_long_night_pays_twelve_hours_away()
    {
        var h = Wheel();
        Levels(h, ("kiln", 1));
        Patch(h, s => s["stamps"] = 3);
        Strings(h, "secrets", "night");
        Give(h, 0);

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(ToLong(12 * 3600 * 3 * 1.06), Pots(h));
    }

    [Fact]
    public void Kin_recipe_and_memory_survive_the_firing()
    {
        var h = Wheel();
        Levels(h, ("clay", 5), ("kiln", 30));
        Strings(h, "marks", "kiln:10", "kiln:25");
        Patch(h, s => s["stamps"] = 68);
        Strings(h, "secrets", "kin", "recipe", "memory");
        Give(h, 0, total: 5_000_000_000_000);           // √5000 = 70 клейм за весь час — ще +2

        Assert.True(Act(h, "fire").Ok);

        Assert.Equal(Clicker.KinLevels, LevelOf(h, "wheel"));
        Assert.Equal(Clicker.KinLevels, LevelOf(h, "apprentice"));
        Assert.Equal(Clicker.KinLevels, LevelOf(h, "kiln"));
        Assert.Equal(5, LevelOf(h, "clay"));
        Assert.Equal(2, Up(h, "kiln").GetProperty("marks").GetInt32());
    }

    [Fact]
    public void The_seal_makes_every_stamp_worth_three_percent()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 100);
        Strings(h, "secrets", "seal");
        Assert.Equal(4.0, AllMult(h), 9);
    }

    [Fact]
    public void Stamps_raise_the_daily_cap_and_tell_the_economy_the_new_number()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 35);
        Give(h, 10_000);

        Assert.Equal(23, View(h).GetProperty("cap").GetInt32());
        Assert.Equal(3, View(h).GetProperty("stampCap").GetInt32());
        Assert.True(Act(h, "sell", new { pots = 2_300 }).Ok);

        var award = Assert.Single(h.Awards);
        Assert.Equal("clicker:23", award.Reason);
        Assert.Equal(23, award.Shards);
        Assert.Equal("Сьогодні черепки скінчились, приходь завтра", Act(h, "sell", new { pots = 100 }).Message);
    }

    [Fact]
    public void Stamps_add_at_most_twenty_shards_a_day()
    {
        var h = Wheel();
        Patch(h, s => s["stamps"] = 5_000);
        Assert.Equal(40, View(h).GetProperty("cap").GetInt32());
    }

    /// <summary>Кімната з налаштуваннями економіки, як на проді.</summary>
    static RoomHarness WheelWith(EconomyOptions options)
    {
        var h = new RoomHarness("clicker", services: RoomHarness.WithService<Microsoft.Extensions.Options.IOptionsMonitor<EconomyOptions>>(
            new FixedOptions<EconomyOptions>(options)));
        h.Solo("Оля");
        return h;
    }

    [Fact]
    public void The_game_never_promises_more_than_the_economy_will_pay()
    {
        // Інакше глеки списались би, лічильник дня виріс, а економіка відмовила б усю виплату як «понад стелю».
        var h = WheelWith(new EconomyOptions { ClickerDailyCap = 25, ClickerDailyCapMax = 40 });
        Patch(h, s => s["stamps"] = 200);
        Give(h, 10_000);

        Assert.Equal(40, View(h).GetProperty("cap").GetInt32());
        Assert.Equal(15, View(h).GetProperty("stampCap").GetInt32());
        Assert.Equal("Сьогодні лишилось 40 — більше не візьму", Act(h, "sell", new { pots = 4_500 }).Message);
        Assert.True(Act(h, "sell", new { pots = 4_000 }).Ok);
        Assert.Equal("clicker:40", Assert.Single(h.Awards).Reason);
    }

    [Fact]
    public void Stamps_do_not_open_a_counter_the_settings_closed()
    {
        var h = WheelWith(new EconomyOptions { ClickerDailyCap = 0 });
        Patch(h, s => s["stamps"] = 200);
        Give(h, 10_000);

        Assert.Equal(0, View(h).GetProperty("cap").GetInt32());
        Assert.Equal("Сьогодні черепки скінчились, приходь завтра", Act(h, "sell", new { pots = 100 }).Message);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void The_boost_on_a_card_is_the_real_multiplier()
    {
        var h = Wheel();
        Levels(h, ("wheel", 50), ("kiln", 50));
        Strings(h, "marks", "wheel:10", "wheel:25", "wheel:50", "kiln:10", "kiln:25", "kiln:50");

        Assert.Equal(2, Up(h, "wheel").GetProperty("boost").GetDouble());   // решта віх кола — відсоток пасиву
        Assert.Equal(8, Up(h, "kiln").GetProperty("boost").GetDouble());
        Assert.Equal(1, Up(h, "workshop").GetProperty("boost").GetDouble());
    }

    // ---------- розписи ----------

    [Fact]
    public void A_style_adds_five_percent_and_goes_straight_onto_the_wheel()
    {
        var h = Wheel();
        Give(h, 1_500_000);

        Assert.Equal("🎨 Гаварецька чорнодимлена — +5 % до всього", Act(h, "paint", new { key = "gavarets" }).Message);
        Assert.Equal(500_000, Pots(h));
        Assert.Equal(1.05, AllMult(h), 9);
        Assert.Equal("gavarets", View(h).GetProperty("wear").GetString());

        Assert.Equal("Гаварецька чорнодимлена уже в колекції", Act(h, "paint", new { key = "gavarets" }).Message);
        Assert.StartsWith("Бракує глеків: треба ще", Act(h, "paint", new { key = "vasylkiv" }).Message);
        Assert.Equal("Такого розпису нема", Act(h, "paint", new { key = "хохлома" }).Message);

        Assert.True(Act(h, "wear", new { key = "" }).Ok);
        Assert.Equal("", View(h).GetProperty("wear").GetString());
        Assert.Equal("Цього розпису ще нема в колекції", Act(h, "wear", new { key = "trypillia" }).Message);
    }

    [Fact]
    public void The_whole_collection_makes_a_museum_at_home()
    {
        var h = Wheel();
        Strings(h, "styles", Clicker.Styles.SkipLast(1).Select(s => s.Key).ToArray());
        Give(h, long.MaxValue / 2);

        Assert.True(Act(h, "paint", new { key = "trypillia" }).Ok);
        Assert.Equal("ach:potter-museum", Assert.Single(h.Awards).Reason);
        Assert.Equal(1 + 0.05 * Clicker.Styles.Length, AllMult(h), 9);
    }

    // ---------- збереження, стелі, вид ----------

    [Fact]
    public void A_save_from_before_the_update_loads_as_is()
    {
        // Справжній стан з проду 15 вересня: ні віх, ні клейм, ні розписного глека в ньому ще нема.
        var h = Wheel();
        h.Room.Game.Load("""
            {"pots":1093231,"total":50101180,"carry":0.3,"lastSync":"2026-09-10T12:00:00+00:00",
             "upgrades":{"wheel":29,"apprentice":27,"kiln":24,"clay":5},
             "soldToday":{"day":"2026-09-10","shards":20},"clicks":{"tokens":7,"at":"2026-09-10T12:00:00+00:00"}}
            """);

        Assert.Equal(1_093_231, Pots(h));
        Assert.Equal(29, LevelOf(h, "wheel"));
        Assert.Equal(0, LevelOf(h, "workshop"));
        Assert.True(Open(h, "workshop"));
        Assert.Equal(92, PerClick(h));
        Assert.Equal(260.9, PerSecond(h), 1);
        Assert.Equal(0, View(h).GetProperty("stamps").GetInt32());
        Assert.True(GoldenAt(h) > h.Clock.UtcNow);
        Assert.Equal(20, View(h).GetProperty("cap").GetInt32());
    }

    [Fact]
    public void Load_of_Save_keeps_everything_the_update_added()
    {
        var h = Wheel();
        Levels(h, ("kiln", 12), ("workshop", 3));
        Strings(h, "marks", "kiln:10");
        Patch(h, s => s["stamps"] = 12);
        Strings(h, "secrets", "night");
        Strings(h, "styles", "gavarets", "kosiv");
        Patch(h, s => s["wear"] = "kosiv");
        JugNow(h, Clicker.GoldenKind.Fair);
        Act(h, "catch");
        h.Clock.Advance(5);
        Act(h, "spin", PotterHands.Human(1));
        var json = h.Room.Game.Save()!;
        var before = Views.Text(h.Room.Game.View(0));

        var again = Wheel();
        again.Clock.UtcNow = h.Clock.UtcNow;
        again.Room.Game.Load(json);

        Assert.Equal(before, Views.Text(again.Room.Game.View(0)));
    }

    [Fact]
    public void A_forged_save_cannot_invent_marks_secrets_or_styles()
    {
        var h = Wheel();
        Levels(h, ("kiln", 10));
        Strings(h, "marks", "kiln:7", "лазер:1");
        Strings(h, "secrets", "god-mode");
        Strings(h, "styles", "золото");
        Patch(h, s => s["wear"] = "золото");

        Assert.Equal(30, PerSecond(h));
        Assert.Equal(1.0, AllMult(h), 9);
        Assert.Equal("", View(h).GetProperty("wear").GetString());
    }

    [Fact]
    public void A_mountain_of_pots_stops_at_the_ceiling_instead_of_wrapping()
    {
        var h = Wheel();
        Give(h, long.MaxValue - 5);
        Act(h, "spin", PotterHands.Human(12));

        Assert.Equal(long.MaxValue, Pots(h));
        Assert.Equal(long.MaxValue, Total(h));
    }

    [Fact]
    public void A_million_reaches_the_table_at_once()
    {
        var h = Wheel();
        Give(h, 999_990);
        Act(h, "spin", PotterHands.Human(1));
        Assert.Single(h.Scores);
        Act(h, "spin", PotterHands.Human(11));
        Assert.Equal(2, h.Scores.Count);
        Assert.Equal(1_000_002, h.Scores.Last().Score);
    }

    [Theory]
    [InlineData(4, "4")]
    [InlineData(1_093_232, "1,09 млн")]
    [InlineData(50_101_180, "50,1 млн")]
    [InlineData(999_000_000, "999 млн")]
    [InlineData(2_500_000_000, "2,5 млрд")]
    [InlineData(5_000_000_000_000_000, "5 квдрлн")]
    public void Big_numbers_are_short(long n, string text)
    {
        Assert.Equal(text, Clicker.Short(n));
    }

    [Fact]
    public void Numbers_below_a_million_keep_every_digit()
    {
        Assert.Equal("999 999", Plain(Clicker.Short(999_999)));
    }

    static long ToLong(double v) => (long)Math.Floor(v);

    /// <summary>Розділювач тисяч в uk-UA — нерозривний пробіл; у тексті тесту пишемо звичайний.</summary>
    static string Plain(string s) => s.Replace('\u00a0', ' ').Replace('\u202f', ' ');
}
