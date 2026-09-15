using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Горно й розпис (пакет B2, ClickerKiln.cs): стани горна, місця, сухі сирці, модель жару й таймлайн, якість і
/// тріщини, солома, підмайстер-палій, техніки розпису й краса, комора й базар, Око майстра, F5, обпал, ачівка.
/// </summary>
public class ClickerKilnTests
{
    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Kiln(RoomHarness h) => View(h).GetProperty("kiln");
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");
    static string State(RoomHarness h) => Kiln(h).GetProperty("state").GetString()!;
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static int StoreCount(RoomHarness h) => Craft(h).GetProperty("items").EnumerateArray().Sum(i => i.GetProperty("n").GetInt32());

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult K(RoomHarness h, object payload) => Act(h, "kiln", payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonNode SaveNode(RoomHarness h)
    {
        lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!;
    }

    /// <summary>Сушарня: <paramref name="dry"/> сухих і <paramref name="wet"/> мокрих сирців.</summary>
    static void Rack(RoomHarness h, int dry, int wet = 0, string ware = "pot") => Patch(h, s =>
    {
        var rack = new JsonArray();
        var now = h.Clock.UtcNow;
        for (var i = 0; i < dry; i++) rack.Add(new JsonObject { ["ware"] = ware, ["clay"] = "", ["dryAt"] = now.AddMinutes(-1).ToString("O") });
        for (var i = 0; i < wet; i++) rack.Add(new JsonObject { ["ware"] = ware, ["clay"] = "", ["dryAt"] = now.AddMinutes(1).ToString("O") });
        s["craft"]!["rack"] = rack;
    });

    static object Timeline(IEnumerable<(int Ms, int Act)> acts) => new { op = "open", t = acts.Select(a => new[] { a.Ms, a.Act }).ToArray() };

    /// <summary>
    /// Умілий палій: кожні 300 мс дивиться, яким буде жар за 2 с без нових дій (тією самою моделлю), і
    /// прикриває заслінку перед перегрівом, відкриває її при недогріві й підкидає, коли жар осідає.
    /// </summary>
    static List<(int Ms, int Act)> Stoker(int seed)
    {
        var acts = new List<(int Ms, int Act)>();
        var open = true;
        var last = -10_000;
        for (var i = 1; i < KilnHeat.Steps - 3 && acts.Count < KilnHeat.MaxActs; i++)
        {
            if (i * 100 - last < 300) continue;
            double fuel = 0, future = 0;
            KilnHeat.Run(seed, acts, (step, T, F, _) =>
            {
                if (step == i - 1) fuel = F;
                if (step == Math.Min(KilnHeat.Steps - 1, i + 20)) future = T;
            });
            var n = i + 21;
            var hi = n < KilnHeat.WarmSteps ? KilnHeat.Hi0 + (KilnHeat.Hi - KilnHeat.Hi0) * n / KilnHeat.WarmSteps : KilnHeat.Hi;
            var lo = n < KilnHeat.WarmSteps ? hi - 150 : KilnHeat.Lo;
            int? act = null;
            if (future > hi - 25 && open) act = KilnHeat.Close;
            else if (future < lo + 40 && !open) act = KilnHeat.Open;
            else if (future < lo + 50 && fuel < KilnHeat.FuelMax - 0.4) act = KilnHeat.Stoke;
            if (act is not { } a) continue;
            acts.Add((i * 100 + 50, a));
            if (a == KilnHeat.Close) open = false;
            if (a == KilnHeat.Open) open = true;
            last = i * 100;
        }
        return acts;
    }

    static List<(int, int)> Spam() => Enumerable.Range(0, 75).Select(i => (i * 400, KilnHeat.Stoke)).ToList();

    /// <summary>Розпалити вручну, дочекатись кінця й відкрити з таймлайном (типово — умілий палій).</summary>
    static ActResult Burn(RoomHarness h, Func<int, List<(int, int)>>? how = null, bool straw = false)
    {
        var lit = K(h, new { op = "light", straw });
        Assert.True(lit.Ok, lit.Message);
        var seed = Kiln(h).GetProperty("seed").GetInt32();
        h.Clock.Advance(KilnHeat.Steps / 10.0 + 0.2);
        return K(h, Timeline((how ?? Stoker)(seed)));
    }

    static void Cool(RoomHarness h) => h.Clock.Advance(Clicker.KilnCool + TimeSpan.FromSeconds(1));

    // ---------- стани, місця, сушарня ----------

    [Fact]
    public void A_fresh_kiln_is_cold_empty_and_has_six_slots()
    {
        var h = Wheel();
        var k = Kiln(h);
        Assert.Equal("cold", k.GetProperty("state").GetString());
        Assert.Equal(Clicker.KilnSlotsBase, k.GetProperty("slots").GetInt32());
        Assert.Equal(0, k.GetProperty("batch").GetArrayLength());
        Assert.Equal(0, k.GetProperty("straw").GetInt32());
        Assert.Equal(JsonValueKind.Null, k.GetProperty("litAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, k.GetProperty("last").ValueKind);
        Assert.Equal(new[] { "rizh" }, k.GetProperty("techs").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(9, 6)]
    [InlineData(10, 7)]
    [InlineData(45, 10)]
    [InlineData(180, 24)]
    [InlineData(5000, 24)]
    public void Slots_grow_with_the_kiln_upgrade_up_to_the_ceiling(int level, int slots)
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["kiln"] = level);
        Assert.Equal(slots, Kiln(h).GetProperty("slots").GetInt32());
    }

    [Fact]
    public void Loading_takes_only_dry_wares_from_the_rack()
    {
        var h = Wheel();
        Rack(h, dry: 3, wet: 2);
        Assert.Equal(3, Kiln(h).GetProperty("dry").GetInt32());
        var r = K(h, new { op = "load" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, Kiln(h).GetProperty("batch").GetArrayLength());
        Assert.Equal("loaded", State(h));
        var rack = Craft(h).GetProperty("rack");
        Assert.Equal(2, rack.GetArrayLength());
        Assert.All(rack.EnumerateArray(), x => Assert.True(x.GetProperty("dryAt").GetDateTimeOffset() > h.Clock.UtcNow));
        Assert.Equal("Сирці ще мокрі — зачекай, поки висохнуть", K(h, new { op = "load" }).Message);
    }

    [Fact]
    public void Loading_an_empty_rack_says_to_form_something_first()
    {
        var h = Wheel();
        Assert.Equal("Сушарня порожня — спершу виліпи щось на колі", K(h, new { op = "load" }).Message);
        Assert.Equal("Горно порожнє — спершу виліпи й висуши щось", K(h, new { op = "light" }).Message);
    }

    [Fact]
    public void The_kiln_takes_no_more_than_its_slots_and_n_limits_a_load()
    {
        var h = Wheel();
        Rack(h, dry: 10);
        Assert.True(K(h, new { op = "load", n = 2 }).Ok);
        Assert.Equal(2, Kiln(h).GetProperty("batch").GetArrayLength());
        Assert.True(K(h, new { op = "load" }).Ok);
        Assert.Equal(6, Kiln(h).GetProperty("batch").GetArrayLength());
        Assert.Equal(4, Craft(h).GetProperty("rack").GetArrayLength());
        Assert.Equal("Горно повне: 6 з 6", K(h, new { op = "load" }).Message);
    }

    [Fact]
    public void Lighting_an_empty_kiln_takes_the_dry_wares_itself()
    {
        var h = Wheel();
        Rack(h, dry: 4, wet: 1, ware: "bowl");
        var r = K(h, new { op = "light" });
        Assert.True(r.Ok, r.Message);
        var k = Kiln(h);
        Assert.Equal("burning", k.GetProperty("state").GetString());
        Assert.Equal(4, k.GetProperty("batch").GetArrayLength());
        Assert.NotEqual(0, k.GetProperty("seed").GetInt32());
        Assert.Equal(h.Clock.UtcNow, k.GetProperty("litAt").GetDateTimeOffset());
    }

    [Fact]
    public void A_burning_kiln_refuses_loading_lighting_painting_and_an_early_open()
    {
        var h = Wheel();
        Rack(h, dry: 8);
        Assert.True(K(h, new { op = "load", n = 6 }).Ok);
        Assert.True(K(h, new { op = "light" }).Ok);
        Assert.Equal("Горно палає — спершу відкрий його", K(h, new { op = "load" }).Message);
        Assert.Equal("Горно вже палає", K(h, new { op = "light" }).Message);
        Assert.Equal("Розписують до обпалу — горно вже палає", K(h, new { op = "paint", style = "", tech = "rizh" }).Message);
        h.Clock.Advance(20);
        Assert.Equal("Ще палає: 10 с", K(h, Timeline([])).Message);
        Assert.Equal("burning", State(h));
    }

    [Fact]
    public void Opening_a_cold_kiln_and_unknown_ops_are_refused()
    {
        var h = Wheel();
        Assert.Equal("Горно не палає — нема чого відкривати", K(h, Timeline([])).Message);
        Assert.Equal("Біля горна так не роблять", K(h, new { op = "dance" }).Message);
    }

    // ---------- підмайстер-палій ----------

    [Fact]
    public void The_helper_fires_quality_one_without_cracks_and_opens_by_himself()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light", helper = true, straw = true }).Ok);
        Assert.Equal("Горно палить підмайстер — сам і відкриє", K(h, Timeline([])).Message);
        Assert.Equal(0, Kiln(h).GetProperty("seed").GetInt32());
        h.Clock.Advance(29);
        Assert.Equal("burning", State(h));
        h.Clock.Advance(1);

        var k = Kiln(h);
        Assert.Equal("cooling", k.GetProperty("state").GetString());
        Assert.Equal(0, k.GetProperty("batch").GetArrayLength());
        var last = k.GetProperty("last");
        Assert.True(last.GetProperty("helper").GetBoolean());
        Assert.Equal(6, last.GetProperty("items").GetArrayLength());
        Assert.All(last.GetProperty("items").EnumerateArray(), i => Assert.Equal(1, i[1].GetInt32()));
        var items = Craft(h).GetProperty("items");
        Assert.Equal("pot||1", items[0].GetProperty("key").GetString());
        Assert.Equal(6, items[0].GetProperty("n").GetInt32());
        // AddFired: майстерність і альбом бачать кожен обпалений виріб.
        Assert.Equal(6, Craft(h).GetProperty("fired").GetInt64());
        Assert.Equal(0, k.GetProperty("straw").GetInt32());                   // солома підмайстрові не потрібна
    }

    [Fact]
    public void A_hot_kiln_cools_for_a_minute_but_can_be_loaded_meanwhile()
    {
        var h = Wheel();
        Rack(h, dry: 12);
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(31);
        Assert.StartsWith("Горно ще гаряче", K(h, new { op = "light" }).Message);
        Assert.True(K(h, new { op = "load" }).Ok);
        Assert.Equal("cooling", State(h));
        h.Clock.Advance(60);
        Assert.Equal("loaded", State(h));
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
    }

    [Fact]
    public void An_abandoned_manual_kiln_is_opened_by_the_helper_after_a_quarter_hour()
    {
        var h = Wheel();
        Rack(h, dry: 5);
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(Clicker.KilnBurn + Clicker.KilnAbandon - TimeSpan.FromSeconds(1));
        Assert.Equal("burning", State(h));
        h.Clock.Advance(2);
        var last = Kiln(h).GetProperty("last");
        Assert.True(last.GetProperty("helper").GetBoolean());
        Assert.Equal(5, StoreCount(h));
    }

    [Fact]
    public void Coming_back_to_a_kiln_opened_by_the_helper_is_in_the_away_notes()
    {
        var h = Wheel();
        Rack(h, dry: 3);
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        var notes = View(h).GetProperty("away").GetProperty("notes").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains(notes, n => n!.StartsWith("🔥 Підмайстер відкрив горно: 3 вироби"));
    }

    // ---------- модель жару ----------

    [Fact]
    public void The_heat_model_is_deterministic_and_depends_on_the_seed()
    {
        var acts = Stoker(777);
        var a = KilnHeat.Run(777, acts);
        var b = KilnHeat.Run(777, acts);
        Assert.Equal(a, b);
        Assert.NotEqual(KilnHeat.Gusts(777), KilnHeat.Gusts(778));
        Assert.NotEqual(KilnHeat.Logs(777), KilnHeat.Logs(778));
        Assert.All(KilnHeat.Logs(5), l => Assert.InRange(l, 60, 140));
        Assert.All(KilnHeat.Gusts(5), g => { Assert.InRange(g.From, 40, 289); Assert.True(g.To > g.From && g.To <= KilnHeat.Steps); });
    }

    /// <summary>Контрольний вектор: ці самі числа мусить дати clicker-kiln.js (перевірено в браузері, див. md).</summary>
    [Fact]
    public void The_heat_model_gives_the_reference_numbers_the_client_repeats()
    {
        List<(int, int)> acts = [(300, 0), (900, 0), (4000, 0), (7200, 2), (9100, 0), (12000, 1), (13500, 0), (16800, 0), (19000, 2), (21000, 0), (23000, 1), (25500, 0), (28000, 0)];
        var r = KilnHeat.Run(12345, acts);
        Assert.Equal(ReferenceHeat, r.Heat, 12);
        Assert.Equal(ReferenceOver, r.Over, 9);
        Assert.Equal(ReferenceTemp, r.Temp, 9);
        Assert.Equal(9, r.Logs);
    }

    const double ReferenceHeat = 0.4581243533354738, ReferenceOver = 1349.966285628821, ReferenceTemp = 1116.5757326445048;

    [Fact]
    public void Doing_nothing_leaves_the_kiln_cold_and_spamming_wood_overheats_it()
    {
        var idle = KilnHeat.Run(42, []);
        Assert.Equal(0, idle.Heat);
        Assert.Equal(0, idle.Over);
        Assert.True(idle.Temp < KilnHeat.Lo);

        var spam = KilnHeat.Run(42, Spam());
        Assert.True(spam.Over > 5000, $"over {spam.Over}");
        Assert.Equal(KilnHeat.CrackMax, KilnHeat.CrackChance(spam.Over));
        Assert.True(spam.Heat < 0.1);
        // Стеля дров: більше, ніж уміщає топка, не буває.
        var fuel = 0.0;
        KilnHeat.Run(42, Spam(), (_, _, f, _) => fuel = Math.Max(fuel, f));
        Assert.True(fuel <= KilnHeat.FuelMax);
    }

    [Fact]
    public void A_skilled_stoker_keeps_the_heat_in_the_green_band()
    {
        foreach (var seed in new[] { 1, 99, 4242, 31337, 777777 })
        {
            var acts = Stoker(seed);
            var r = KilnHeat.Run(seed, acts);
            Assert.True(r.Heat > 0.8, $"seed {seed}: heat {r.Heat}");
            Assert.True(KilnHeat.CrackChance(r.Over) < 0.07, $"seed {seed}: over {r.Over}");
            Assert.True(acts.Count <= KilnHeat.MaxActs);
        }
    }

    [Fact]
    public void A_blind_rhythm_is_worse_than_watching_the_thermometer()
    {
        var blind = 0.0;
        var skilled = 0.0;
        for (var seed = 1; seed <= 20; seed++)
        {
            blind += KilnHeat.Run(seed, Enumerable.Range(0, 7).Select(i => (i * 4000 + 50, KilnHeat.Stoke)).ToList()).Heat;
            skilled += KilnHeat.Run(seed, Stoker(seed)).Heat;
        }
        Assert.True(blind / 20 < 0.7, $"blind {blind / 20}");
        Assert.True(skilled / 20 > blind / 20 + 0.2, $"skilled {skilled / 20} blind {blind / 20}");
    }

    [Fact]
    public void Heat_and_crack_chance_stay_in_bounds()
    {
        var rnd = new Random(5);
        for (var n = 0; n < 60; n++)
        {
            var ms = 0;
            var acts = new List<(int, int)>();
            while (acts.Count < rnd.Next(0, 80) && ms < 29_900) { ms += rnd.Next(1, 900); if (ms < 30_000) acts.Add((ms, rnd.Next(3))); }
            var r = KilnHeat.Run(rnd.Next(), acts);
            Assert.InRange(r.Heat, 0, 1);
            Assert.True(r.Over >= 0);
            Assert.InRange(KilnHeat.CrackChance(r.Over), 0, KilnHeat.CrackMax);
            Assert.True(r.Temp >= KilnHeat.Amb - 1e-9);
        }
        Assert.Equal(0, KilnHeat.CrackChance(KilnHeat.CrackFree));
        Assert.Equal(0.1, KilnHeat.CrackChance(KilnHeat.CrackFree + 300), 9);
    }

    [Theory]
    [InlineData(1.0, 100, 0.29, 3)]
    [InlineData(1.0, 100, 0.31, 2)]
    [InlineData(1.0, 100, 0.69, 2)]
    [InlineData(1.0, 100, 0.71, 1)]
    [InlineData(1.0, 0, 0.10, 3)]
    [InlineData(1.0, 0, 0.11, 2)]
    [InlineData(1.0, 0, 0.35, 1)]
    [InlineData(0.0, 100, 0.0, 1)]
    public void Quality_follows_heat_beauty_and_the_roll(double heat, int beauty, double u, int q)
    {
        Assert.Equal(q, KilnHeat.Quality(heat, beauty, u));
    }

    [Fact]
    public void Beauty_raises_the_chance_of_good_quality_but_not_by_times()
    {
        static double Mult(double heat, int beauty)
        {
            var sum = 0.0;
            for (var i = 0; i < 10_000; i++) sum += Clicker.QualityMult[KilnHeat.Quality(heat, beauty, (i + 0.5) / 10_000)];
            return sum / 10_000;
        }
        Assert.Equal(1, Mult(0, 100), 6);
        Assert.InRange(Mult(1, 0), 1.30, 1.34);
        Assert.InRange(Mult(1, 100), 1.70, 1.74);
        Assert.InRange(Mult(0.5, 0), 1.10, 1.16);
        Assert.True(Mult(0.9, 60) > Mult(0.9, 0));
    }

    // ---------- таймлайн ----------

    [Fact]
    public void The_timeline_is_checked_and_a_bad_one_keeps_the_kiln_burning()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(31);
        Assert.Equal("Забагато дій: не більше 80 за обпал", K(h, Timeline(Enumerable.Range(0, 81).Select(i => (i * 100, 0)))).Message);
        Assert.Equal("Записи палія поза обпалом — оновися й відкрий ще раз", K(h, Timeline([(30_000, 0)])).Message);
        Assert.Equal("Записи палія поза обпалом — оновися й відкрий ще раз", K(h, Timeline([(-5, 0)])).Message);
        Assert.Equal("Записи палія поза обпалом — оновися й відкрий ще раз", K(h, Timeline([(100, 3)])).Message);
        Assert.Equal("Записи палія переплутані — оновися й відкрий ще раз", K(h, Timeline([(500, 0), (500, 1)])).Message);
        Assert.Equal("Записи палія переплутані — оновися й відкрий ще раз", K(h, Timeline([(900, 0), (400, 1)])).Message);
        Assert.Equal("Записи палія зіпсовані — оновися й відкрий ще раз", K(h, new { op = "open", t = new object[] { new object[] { "a", 0 } } }).Message);
        Assert.Equal("Записи палія зіпсовані — оновися й відкрий ще раз", K(h, new { op = "open" }).Message);
        Assert.Equal("burning", State(h));
        Assert.True(K(h, Timeline([(200, 0)])).Ok);
        Assert.Equal("cooling", State(h));
    }

    [Fact]
    public void Opening_needs_the_whole_burn_with_only_a_ping_of_grace()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.AdvanceMs(29_400);
        Assert.StartsWith("Ще палає", K(h, Timeline([])).Message);
        h.Clock.AdvanceMs(200);
        Assert.True(K(h, Timeline([])).Ok);
    }

    [Fact]
    public void An_empty_timeline_means_an_underfired_batch_of_plain_quality_without_cracks()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        var r = Burn(h, _ => []);
        Assert.True(r.Ok, r.Message);
        var last = Kiln(h).GetProperty("last");
        Assert.False(last.GetProperty("helper").GetBoolean());
        Assert.Equal(0, last.GetProperty("heat").GetInt32());
        Assert.All(last.GetProperty("items").EnumerateArray(), i => Assert.Equal(1, i[1].GetInt32()));
        Assert.Equal(6, StoreCount(h));
    }

    // ---------- якість, тріщини, солома ----------

    [Fact]
    public void A_skilled_firing_gives_better_wares_and_every_whole_one_is_counted()
    {
        var h = Wheel(seed: 3);
        Patch(h, s => s["upgrades"]!["kiln"] = 180);
        var q = new int[4];
        for (var b = 0; b < 4; b++)
        {
            Rack(h, dry: 24);
            var r = Burn(h);
            Assert.True(r.Ok, r.Message);
            Assert.StartsWith("🔥 Горно відкрите: ", r.Message);
            var last = Kiln(h).GetProperty("last");
            Assert.True(last.GetProperty("heat").GetInt32() > 75);
            foreach (var i in last.GetProperty("items").EnumerateArray()) q[i[1].GetInt32()]++;
            Cool(h);
        }
        Assert.True(q[0] <= 96 * 0.08, string.Join(",", q));
        Assert.True(q[2] + q[3] > 96 * 0.2, string.Join(",", q));
        Assert.True(q[1] > q[3], string.Join(",", q));
        Assert.Equal(96 - q[0], StoreCount(h));
        Assert.Equal(96 - q[0], Craft(h).GetProperty("fired").GetInt64());
        var byQuality = Craft(h).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("n").GetInt32());
        Assert.Equal(q[3], byQuality.GetValueOrDefault("pot||3"));
    }

    [Fact]
    public void Overheating_cracks_wares_they_skip_the_store_and_leave_shards()
    {
        var h = Wheel(seed: 7);
        Patch(h, s => s["upgrades"]!["kiln"] = 180);
        Rack(h, dry: 24);
        var pot = Craft(h).GetProperty("wares")[0].GetProperty("value").GetInt64();
        Assert.True(Burn(h, _ => Spam()).Ok);
        var last = Kiln(h).GetProperty("last");
        var cracked = last.GetProperty("items").EnumerateArray().Count(i => i[1].GetInt32() == 0);
        Assert.InRange(cracked, 5, 20);
        Assert.Equal(24 - cracked, StoreCount(h));
        Assert.Equal(24 - cracked, Craft(h).GetProperty("fired").GetInt64());
        // Черепки — 15 % ціни простого звичайного горщика за кожен тріснутий.
        Assert.Equal((long)(pot * Clicker.ShardShare) * cracked, last.GetProperty("shards").GetInt64());
    }

    [Fact]
    public void Straw_insures_against_cracks()
    {
        int Cracks(bool straw)
        {
            var h = Wheel(seed: 11);
            Patch(h, s => { s["upgrades"]!["kiln"] = 180; s["pots"] = 100_000; s["total"] = 100_000; });
            Assert.True(K(h, new { op = "straw", n = 5 }).Ok);
            var total = 0;
            for (var b = 0; b < 4; b++)
            {
                Rack(h, dry: 24);
                Assert.True(Burn(h, _ => Spam(), straw).Ok);
                total += Kiln(h).GetProperty("last").GetProperty("items").EnumerateArray().Count(i => i[1].GetInt32() == 0);
                Assert.Equal(straw, Kiln(h).GetProperty("last").GetProperty("straw").GetBoolean());
                Cool(h);
            }
            Assert.Equal(straw ? 1 : 5, Kiln(h).GetProperty("straw").GetInt32());
            return total;
        }
        var bare = Cracks(false);
        var covered = Cracks(true);
        Assert.InRange(bare, 30, 66);                    // очікування 48
        Assert.True(covered < bare / 2, $"з соломою {covered}, без {bare}");
    }

    [Fact]
    public void Straw_costs_two_plain_pots_and_the_barn_holds_twenty()
    {
        var h = Wheel();
        Assert.Equal(40, Kiln(h).GetProperty("strawPrice").GetInt64());      // голе коло: горщик — 20 глеків
        Assert.Equal("Бракує глеків: в'язка коштує 40", K(h, new { op = "straw" }).Message);
        Assert.Equal("Скільки в'язок — хоч одну", K(h, new { op = "straw", n = 0 }).Message);
        Patch(h, s => { s["pots"] = 1_000; s["total"] = 1_000; });
        var r = K(h, new { op = "straw", n = 30 });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(20, Kiln(h).GetProperty("straw").GetInt32());
        Assert.Equal(200, Pots(h));
        Assert.Equal("Клуня повна: 20 в'язок", K(h, new { op = "straw" }).Message);

        // Ціна росте з грою: дві ціни простого звичайного горщика, хай скільки там пасиву.
        Patch(h, s => s["upgrades"]!["kiln"] = 2000);
        var pot = Craft(h).GetProperty("wares")[0].GetProperty("value").GetInt64();
        Assert.True(pot > 20);
        Assert.Equal(pot * 2, Kiln(h).GetProperty("strawPrice").GetInt64());
    }

    [Fact]
    public void Lighting_with_straw_needs_straw()
    {
        var h = Wheel();
        Rack(h, dry: 3);
        Assert.Equal("Соломи нема — купи в'язку або пали без неї", K(h, new { op = "light", straw = true }).Message);
        Assert.Equal("cold", State(h));
    }

    [Fact]
    public void An_overflowing_store_sells_the_rest_at_the_bazaar()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["items"] = new JsonObject { ["bowl||1"] = 198 });
        Rack(h, dry: 5);
        var before = Pots(h);
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(30);
        var last = Kiln(h).GetProperty("last");
        Assert.Equal(60, last.GetProperty("sold").GetInt64());               // три горщики по 20
        Assert.Equal(before + 60, Pots(h));
        Assert.Equal(Clicker.StoreCap, StoreCount(h));
        Assert.Equal(5, Craft(h).GetProperty("fired").GetInt64());
    }

    // ---------- Око майстра ----------

    [Fact]
    public void The_master_may_look_before_the_kiln_opens_and_the_batch_waits()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light" }).Ok);
        var seed = Kiln(h).GetProperty("seed").GetInt32();
        h.Clock.Advance(31);
        Patch(h, s => s["guard"]!["left"] = 0);
        var r = K(h, Timeline(Stoker(seed)));
        Assert.True(r.Ok);
        Assert.StartsWith("👁 Майстер хоче глянути", r.Message);
        Assert.Equal("burning", State(h));
        Assert.Equal(6, Kiln(h).GetProperty("batch").GetArrayLength());
        Assert.Equal("Спершу Око майстра: покажи, що ти не автоклікер", K(h, Timeline(Stoker(seed))).Message);

        Assert.True(PotterHands.Pass(h).Ok);
        var left = SaveNode(h)["guard"]!["left"]!.GetValue<int>();
        var open = K(h, Timeline(Stoker(seed)));
        Assert.True(open.Ok, open.Message);
        Assert.Equal("cooling", State(h));
        Assert.Equal(left - Clicker.KilnGuardWeight, SaveNode(h)["guard"]!["left"]!.GetValue<int>());
    }

    [Fact]
    public void The_helper_is_not_a_skill_and_the_master_does_not_count_it()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        var left = SaveNode(h)["guard"]!["left"]!.GetValue<int>();
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(31);
        Assert.Equal("cooling", State(h));
        Assert.Equal(left, SaveNode(h)["guard"]!["left"]!.GetValue<int>());
    }

    // ---------- розпис ----------

    static JsonElement Pattern(RoomHarness h) => Kiln(h).GetProperty("pattern");

    static object PathOf(IEnumerable<double[]> pts) =>
        new { op = "decor", path = KilnPaint.Encode(pts.Select(p => new KilnPaint.Pt(p[0], p[1], p[2], p.Length > 3 && p[3] >= 1))) };

    /// <summary>Рука, що веде по візерунку: трохи нерівний крок часу й дрібний тремор.</summary>
    static List<double[]> Ideal(JsonElement pattern, int seed = 42)
    {
        var rnd = new Random(seed);
        var tech = pattern.GetProperty("tech").GetString();
        var s = pattern.GetProperty("shape");
        var pts = new List<double[]>();
        double ms = 0;
        double Tick() => ms += 14 + rnd.Next(6) + rnd.NextDouble();
        double Shake() => rnd.NextDouble() * 3 - 1.5;
        switch (tech)
        {
            case "rizh":
            {
                int r0 = s.GetProperty("r0").GetInt32(), amp = s.GetProperty("amp").GetInt32(), k = s.GetProperty("k").GetInt32();
                double ph = s.GetProperty("phase").GetInt32() * Math.PI / 180, period = s.GetProperty("period").GetInt32(), dir = s.GetProperty("dir").GetInt32();
                var beta = -Math.PI / 2;
                while (ms < period + 400)
                {
                    var theta = beta - dir * 2 * Math.PI * ms / period;
                    var r = r0 + amp * Math.Sin(k * theta + ph);
                    pts.Add([ms, 500 + r * Math.Cos(beta) + Shake(), 500 + r * Math.Sin(beta) + Shake(), pts.Count == 0 ? 1 : 0]);
                    Tick();
                }
                break;
            }
            case "ryt":
            {
                int r0 = s.GetProperty("r0").GetInt32(), amp = s.GetProperty("amp").GetInt32(), k = s.GetProperty("k").GetInt32();
                var ph = s.GetProperty("phase").GetInt32() * Math.PI / 180;
                for (var th = 0.0; th < 2 * Math.PI + 0.05; th += 2 * Math.PI / 260)
                {
                    var r = r0 + amp * Math.Cos(k * th + ph);
                    pts.Add([ms, 500 + r * Math.Cos(th) + Shake(), 500 + r * Math.Sin(th) + Shake(), pts.Count == 0 ? 1 : 0]);
                    Tick();
                }
                break;
            }
            case "flyand":
                foreach (var m in s.GetProperty("marks").EnumerateArray())
                {
                    int x = m[0].GetInt32(), d = m[1].GetInt32();
                    for (var i = 0; i <= 24; i++)
                    {
                        var y = d == 1 ? 260 + 20 * i : 740 - 20 * i;
                        pts.Add([ms, x + Shake(), y, i == 0 ? 1 : 0]);
                        Tick();
                    }
                    ms += 180;
                }
                break;
            case "marble":
                foreach (var m in s.GetProperty("drops").EnumerateArray())
                {
                    for (var i = 0; i < 3; i++) { pts.Add([ms, m[0].GetInt32() + Shake(), m[1].GetInt32() + Shake(), i == 0 ? 1 : 0]); Tick(); }
                    ms += 260;
                }
                for (var i = 0; i <= 36; i++)
                {
                    var a = 2.2 * Math.PI * i / 36;
                    pts.Add([ms, 500 + 300 * Math.Cos(a), 500 + 300 * Math.Sin(a), i == 0 ? 1 : 0]);
                    ms += 14 + rnd.NextDouble() * 3;
                }
                break;
            default:
            {
                int top = s.GetProperty("top").GetInt32(), bottom = s.GetProperty("bottom").GetInt32();
                var stripes = s.GetProperty("stripes").EnumerateArray().Select(x => (X: x[0].GetInt32(), W: x[1].GetInt32())).ToList();
                var rows = Enumerable.Range(0, 25).Where(r => r * 40 + 20 >= top && r * 40 + 20 <= bottom).ToList();
                var cols = Enumerable.Range(0, 25).Where(c => stripes.Any(st => Math.Abs(c * 40 + 20 - st.X) * 2 <= st.W)).ToList();
                double y0 = rows.First() * 40 + 5, y1 = rows.Last() * 40 + 35;
                foreach (var c in cols)
                {
                    var first = true;
                    for (var pass = 0; pass < 4; pass++)
                        for (var y = pass % 2 == 0 ? y0 : y1; pass % 2 == 0 ? y <= y1 : y >= y0; y += pass % 2 == 0 ? 40 : -40)
                        {
                            pts.Add([ms, c * 40 + 20 + Shake(), y, first ? 1 : 0]);
                            first = false;
                            Tick();
                        }
                    ms += 150;
                }
                break;
            }
        }
        return pts;
    }

    /// <summary>Почати розпис, «намалювати» (годинник іде стільки, скільки тривала траєкторія) і здати.</summary>
    static ActResult Paint(RoomHarness h, string tech, Func<JsonElement, List<double[]>> draw, string style = "")
    {
        var start = K(h, new { op = "paint", style, tech });
        Assert.True(start.Ok, start.Message);
        var pts = draw(Pattern(h));
        h.Clock.AdvanceMs((int)(pts[^1][0] - pts[0][0]) + 50);
        return K(h, PathOf(pts));
    }

    static void OpenAllTechniques(RoomHarness h) => Patch(h, s =>
    {
        s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 1000 };
        s["styles"] = new JsonArray("gavarets", "kosiv");
    });

    [Fact]
    public void Techniques_open_with_fired_wares_and_home_styles()
    {
        var h = Wheel();
        Assert.Equal("Фляндрування відкриється після 10 обпалених виробів", K(h, new { op = "paint", style = "", tech = "flyand" }).Message);
        Assert.Equal("Такої техніки розпису нема", K(h, new { op = "paint", style = "", tech = "petryk" }).Message);
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 60 });
        Assert.Equal(new[] { "rizh", "flyand", "marble" }, Kiln(h).GetProperty("techs").EnumerateArray().Select(x => x.GetString()));
        Patch(h, s => s["styles"] = new JsonArray("kosiv"));
        Assert.Contains("ryt", Kiln(h).GetProperty("techs").EnumerateArray().Select(x => x.GetString()));
        Patch(h, s => { s["styles"] = new JsonArray("gavarets"); s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 150 }; });
        Assert.Equal(new[] { "rizh", "flyand", "marble", "losk" }, Kiln(h).GetProperty("techs").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Only_a_style_from_the_collection_can_be_painted()
    {
        var h = Wheel();
        Assert.Equal("Цього розпису ще нема в колекції", K(h, new { op = "paint", style = "kosiv" }).Message);
        Assert.Equal("Такого розпису нема", K(h, new { op = "paint", style = "gzhel" }).Message);
        Patch(h, s => s["styles"] = new JsonArray("kosiv"));
        Assert.True(K(h, new { op = "paint", style = "kosiv" }).Ok);
        var k = Kiln(h);
        Assert.Equal("kosiv", k.GetProperty("style").GetString());
        Assert.Equal(0, k.GetProperty("beauty").GetInt32());
        Assert.Equal(JsonValueKind.Null, k.GetProperty("pattern").ValueKind);
    }

    [Theory]
    [InlineData("rizh")]
    [InlineData("ryt")]
    [InlineData("flyand")]
    [InlineData("marble")]
    [InlineData("losk")]
    public void A_careful_hand_paints_beautifully(string tech)
    {
        var h = Wheel();
        OpenAllTechniques(h);
        var r = Paint(h, tech, p => Ideal(p));
        Assert.True(r.Ok, r.Message);
        var beauty = Kiln(h).GetProperty("beauty").GetInt32();
        Assert.True(beauty >= 80, $"{tech}: {beauty}");
        Assert.Equal(JsonValueKind.Null, Pattern(h).ValueKind);                // візерунок здано
        Assert.Equal("Спершу обери техніку розпису", K(h, PathOf([[0, 1, 1]])).Message);
    }

    [Theory]
    [InlineData("rizh")]
    [InlineData("ryt")]
    [InlineData("flyand")]
    [InlineData("marble")]
    [InlineData("losk")]
    public void A_careless_scribble_is_not_beautiful(string tech)
    {
        var h = Wheel();
        OpenAllTechniques(h);
        // Кружляння в кутку полотна, далеко від будь-якого візерунка.
        var rnd = new Random(9);
        var r = Paint(h, tech, _ => Enumerable.Range(0, 200).Select(i => new double[] { i * 17 + rnd.NextDouble() * 4, 60 + 40 * Math.Cos(i / 5.0) + rnd.NextDouble(), 60 + 40 * Math.Sin(i / 5.0), i == 0 ? 1 : 0 }).ToList());
        Assert.True(r.Ok, r.Message);
        Assert.True(Kiln(h).GetProperty("beauty").GetInt32() <= 15, $"{tech}: {Kiln(h).GetProperty("beauty").GetInt32()}");
    }

    [Fact]
    public void Polishing_outside_the_stripes_spoils_the_polish()
    {
        var h = Wheel();
        OpenAllTechniques(h);
        Assert.True(K(h, new { op = "paint", style = "", tech = "losk" }).Ok);
        var pattern = Pattern(h);
        var good = Ideal(pattern);
        // Та сама рука, але зсунута на 70 — між смугами й по них навскіс.
        var bad = good.Select(p => new[] { p[0], p[1] + 70, p[2], p[3] }).ToList();
        h.Clock.Advance(20);
        Assert.True(K(h, PathOf(bad)).Ok);
        Assert.True(Kiln(h).GetProperty("beauty").GetInt32() < 60, $"{Kiln(h).GetProperty("beauty").GetInt32()}");
    }

    [Fact]
    public void The_home_technique_of_a_style_adds_ten_beauty()
    {
        var h = Wheel();
        OpenAllTechniques(h);
        var r = Paint(h, "ryt", _ => Enumerable.Range(0, 100).Select(i => new double[] { i * 20 + i % 3, 20 + i % 7, 20, i == 0 ? 1 : 0 }).ToList(), style: "kosiv");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Clicker.HomeBonus, Kiln(h).GetProperty("beauty").GetInt32());
        Assert.EndsWith("(рідний осередок +10)", r.Message);
    }

    [Fact]
    public void An_implausible_hand_is_refused_and_can_try_again()
    {
        var h = Wheel();
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        h.Clock.Advance(40);
        // Замало точок, закоротко, робот із рівним кроком і рівною швидкістю, сміття.
        Assert.Equal("Закоротко: проведи довше", K(h, PathOf(Enumerable.Range(0, 5).Select(i => new double[] { i * 400, 500, 200 + i }))).Message);
        Assert.Equal("Закоротко: розпис — хоч півтори секунди роботи", K(h, PathOf(Enumerable.Range(0, 40).Select(i => new double[] { i * 20 + i % 3, 500 + i, 200 }))).Message);
        Assert.Equal("Рука так рівно не ходить — спробуй ще раз", K(h, PathOf(Enumerable.Range(0, 200).Select(i => new double[] { i * 16, 300 + i * 2, 200 }))).Message);
        Assert.Equal("Слід пензля прийшов зіпсований — спробуй ще раз", K(h, new { op = "decor", path = new[] { 16, 1, 1, 17 } }).Message);
        Assert.Equal("Слід пензля прийшов зіпсований — спробуй ще раз", K(h, new { op = "decor", path = new object[] { 1, "x", 3 } }).Message);
        Assert.Equal("Слід пензля прийшов зіпсований — спробуй ще раз", K(h, new { op = "decor", path = new[] { 16, 1.5, 3 } }).Message);
        Assert.Equal("Слід пензля прийшов зіпсований — спробуй ще раз", K(h, PathOf(Enumerable.Range(0, KilnPaint.MaxPoints + 1).Select(i => new double[] { i * 16 + i % 3, i % 1000, 0 }))).Message);
        // Живу руку після всього цього приймають.
        Assert.True(K(h, PathOf(Ideal(Pattern(h)))).Ok);
    }

    [Fact]
    public void A_long_path_cannot_arrive_right_after_the_pattern()
    {
        var h = Wheel();
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        var pts = Ideal(Pattern(h));
        h.Clock.Advance(1);
        Assert.Equal("Так швидко не малюють — спробуй ще раз", K(h, PathOf(pts)).Message);
    }

    [Fact]
    public void A_pattern_dries_after_five_minutes()
    {
        var h = Wheel();
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        var pts = Ideal(Pattern(h));
        h.Clock.Advance(Clicker.PatternLife + TimeSpan.FromSeconds(1));
        Assert.Equal(JsonValueKind.Null, Pattern(h).ValueKind);
        Assert.Equal("Візерунок підсох — почни розпис знову", K(h, PathOf(pts)).Message);
        Assert.Equal("Спершу обери техніку розпису", K(h, PathOf(pts)).Message);
    }

    [Fact]
    public void Decor_without_a_pattern_is_refused()
    {
        var h = Wheel();
        Assert.Equal("Спершу обери техніку розпису", K(h, PathOf([[0, 1, 1]])).Message);
    }

    [Fact]
    public void The_pattern_in_the_view_is_what_to_draw_and_not_the_seed()
    {
        var h = Wheel();
        Assert.True(K(h, new { op = "paint", style = "", tech = "rizh" }).Ok);
        var p = Pattern(h);
        Assert.Equal("rizh", p.GetProperty("tech").GetString());
        Assert.Equal(h.Clock.UtcNow, p.GetProperty("at").GetDateTimeOffset());
        Assert.True(p.GetProperty("shape").TryGetProperty("r0", out _));
        Assert.DoesNotContain("seed", p.GetRawText(), StringComparison.OrdinalIgnoreCase);
        // Зерно лежить у збереженні — і та сама мінігра після F5 малює той самий візерунок.
        Assert.NotEqual(0, SaveNode(h)["kiln"]!["paintSeed"]!.GetValue<int>());
        var before = p.GetRawText();
        Patch(h, _ => { });
        Assert.Equal(before, Pattern(h).GetRawText());
    }

    [Fact]
    public void A_new_attempt_or_another_style_resets_the_beauty()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("gavarets"));
        Assert.True(Paint(h, "rizh", p => Ideal(p), style: "gavarets").Ok);
        Assert.True(Kiln(h).GetProperty("beauty").GetInt32() > 50);
        Assert.True(K(h, new { op = "paint", style = "" }).Ok);
        Assert.Equal(0, Kiln(h).GetProperty("beauty").GetInt32());
        Assert.Equal("", Kiln(h).GetProperty("style").GetString());
    }

    [Fact]
    public void The_painted_style_goes_onto_every_ware_of_the_batch()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("vasylkiv"));
        Assert.True(K(h, new { op = "paint", style = "vasylkiv" }).Ok);
        Rack(h, dry: 4, ware: "bowl");
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(30);
        var items = Craft(h).GetProperty("items");
        Assert.Equal("bowl|vasylkiv|1", items[0].GetProperty("key").GetString());
        Assert.Equal("vasylkiv", Kiln(h).GetProperty("last").GetProperty("style").GetString());
        Assert.Equal("vasylkiv", Kiln(h).GetProperty("style").GetString());   // розпис лишається для наступної партії
    }

    [Fact]
    public void Painting_without_the_minigame_gives_zero_beauty_but_the_style_holds()
    {
        var h = Wheel();
        Patch(h, s => { s["styles"] = new JsonArray("gavarets"); s["upgrades"]!["kiln"] = 180; });
        Assert.True(K(h, new { op = "paint", style = "gavarets", tech = "rizh" }).Ok);   // почав і кинув
        Rack(h, dry: 6);
        Assert.True(Burn(h).Ok);
        var last = Kiln(h).GetProperty("last");
        Assert.Equal(0, last.GetProperty("beauty").GetInt32());
        Assert.Equal("gavarets", last.GetProperty("style").GetString());
    }

    // ---------- збереження, обпал, ачівка ----------

    [Fact]
    public void A_burning_kiln_survives_a_reload()
    {
        var h = Wheel();
        Patch(h, s => { s["styles"] = new JsonArray("kosiv"); s["pots"] = 1000; s["total"] = 1000; });
        Assert.True(K(h, new { op = "straw", n = 3 }).Ok);
        Assert.True(Paint(h, "rizh", p => Ideal(p), style: "kosiv").Ok);
        Rack(h, dry: 5);
        Assert.True(K(h, new { op = "light", straw = true }).Ok);
        var before = Kiln(h).GetRawText();
        Patch(h, _ => { });
        Assert.Equal(before, Kiln(h).GetRawText());
        h.Clock.Advance(31);
        var seed = Kiln(h).GetProperty("seed").GetInt32();
        Assert.True(K(h, Timeline(Stoker(seed))).Ok);
        var last = Kiln(h).GetProperty("last");
        Assert.True(last.GetProperty("beauty").GetInt32() > 50);
        Assert.True(last.GetProperty("straw").GetBoolean());
        // Результат відкриття теж переживає F5 — клієнт покаже його після перезавантаження.
        var opened = Kiln(h).GetRawText();
        Patch(h, _ => { });
        Assert.Equal(opened, Kiln(h).GetRawText());
    }

    [Fact]
    public void An_old_save_without_the_kiln_has_a_cold_empty_kiln()
    {
        var h = Wheel();
        Rack(h, dry: 4);
        Assert.True(K(h, new { op = "light" }).Ok);
        Patch(h, s => s.Remove("kiln"));
        var k = Kiln(h);
        Assert.Equal("cold", k.GetProperty("state").GetString());
        Assert.Equal(0, k.GetProperty("batch").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, k.GetProperty("last").ValueKind);
        Patch(h, s => s["kiln"] = new JsonObject());
        Assert.Equal("cold", State(h));
    }

    [Fact]
    public void A_broken_kiln_save_is_cleaned_up()
    {
        var h = Wheel();
        Patch(h, s => s["kiln"] = new JsonObject
        {
            ["batch"] = new JsonArray("pot", "vase", "bowl"),
            ["style"] = "kosiv",                                    // розпису нема в колекції
            ["tech"] = "dance",
            ["beauty"] = 500,
            ["paintSeed"] = 77,
            ["litAt"] = h.Clock.UtcNow.ToString("O"),               // палає без зерна — так не буває
            ["strawStock"] = 99,
            ["last"] = new JsonObject { ["at"] = h.Clock.UtcNow.ToString("O"), ["heat"] = 300, ["items"] = new JsonArray(new JsonObject { ["ware"] = "vase", ["q"] = 2 }, new JsonObject { ["ware"] = "jug", ["q"] = 3 }) },
        });
        var k = Kiln(h);
        Assert.Equal(new[] { "pot", "bowl" }, k.GetProperty("batch").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("", k.GetProperty("style").GetString());
        Assert.Equal("", k.GetProperty("tech").GetString());
        Assert.Equal(100, k.GetProperty("beauty").GetInt32());
        Assert.Equal(JsonValueKind.Null, k.GetProperty("pattern").ValueKind);
        Assert.Equal("loaded", k.GetProperty("state").GetString());
        Assert.Equal(Clicker.StrawMax, k.GetProperty("straw").GetInt32());
        Assert.Equal(1, k.GetProperty("last").GetProperty("items").GetArrayLength());
        Assert.Equal(100, k.GetProperty("last").GetProperty("heat").GetInt32());
    }

    [Fact]
    public void Firing_the_workshop_burns_the_batch_in_the_kiln_and_the_straw()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 1000; s["total"] = 1000; });
        Assert.True(K(h, new { op = "straw", n = 2 }).Ok);
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light", straw = true }).Ok);
        Patch(h, s => s["total"] = 2_000_000_000);
        Assert.True(Act(h, "fire").Ok);
        var k = Kiln(h);
        Assert.Equal("cold", k.GetProperty("state").GetString());
        Assert.Equal(0, k.GetProperty("batch").GetArrayLength());
        Assert.Equal(0, k.GetProperty("straw").GetInt32());
        Assert.Equal(JsonValueKind.Null, k.GetProperty("litAt").ValueKind);
        Assert.Equal(0, StoreCount(h));
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, StoreCount(h));                                        // згоріле не відкривається потім
    }

    [Fact]
    public void Firing_while_cooling_leaves_a_cold_kiln()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(31);
        Patch(h, s => s["total"] = 2_000_000_000);
        Assert.True(Act(h, "fire").Ok);
        Rack(h, dry: 2);
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
    }

    [Fact]
    public void An_all_ringing_batch_of_four_is_an_achievement_and_three_are_not()
    {
        var awarded = false;
        var threeRinging = false;
        for (var seed = 1; seed < 3000 && !(awarded && threeRinging); seed++)
        {
            var h = Wheel(seed);
            var n = seed % 2 == 0 ? 4 : 3;
            if (n == 4 && awarded) continue;
            if (n == 3 && threeRinging) continue;
            Patch(h, s => s["kiln"] = new JsonObject { ["beauty"] = 100 });
            Rack(h, dry: n);
            Assert.True(Burn(h).Ok);
            var all = Kiln(h).GetProperty("last").GetProperty("items").EnumerateArray().All(i => i[1].GetInt32() == 3);
            var got = h.Awards.Any(a => a.Reason == "ach:potter-kiln-perfect");
            if (n == 4)
            {
                Assert.Equal(all, got);
                awarded |= got;
            }
            else
            {
                Assert.False(got);
                threeRinging |= all;
            }
        }
        Assert.True(awarded);
        Assert.True(threeRinging);
    }

    [Fact]
    public void The_catalog_carries_the_kiln_model_and_techniques()
    {
        var h = Wheel();
        var c = View(h).GetProperty("catalog").GetProperty("kiln");
        Assert.Equal(30_000, c.GetProperty("burnMs").GetDouble());
        Assert.Equal(KilnHeat.HeatOpen, c.GetProperty("model").GetProperty("heat")[1].GetDouble());
        Assert.Equal(Clicker.Techniques.Length, c.GetProperty("techs").GetArrayLength());
    }

    [Fact]
    public void The_view_stays_a_pure_function_of_state()
    {
        var h = Wheel();
        Rack(h, dry: 6);
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(40);
        Assert.Equal(Views.Text(View(h)), Views.Text(View(h)));
    }
}
