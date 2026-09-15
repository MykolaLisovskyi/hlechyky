using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Альбом майстра (пакет B3, ClickerAlbum.cs): сітка «виріб × розпис», майстерність, кахляна піч, трипільський музей,
/// збереження й обпал. Горна ще нема, тож гачок обпалу (AlbumOnFired) тут не викликається напряму: правила сітки й
/// рівнів — публічні статичні функції, а стан альбому підкладаємо патчем збереження.
/// </summary>
public class ClickerAlbumTests
{
    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Album(RoomHarness h) => View(h).GetProperty("album");
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult AlbumAct(RoomHarness h, object payload) => Act(h, "album", payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonObject AlbumNode(JsonObject s)
    {
        if (s["album"] is JsonObject a) return a;
        var fresh = new JsonObject();
        s["album"] = fresh;
        return fresh;
    }

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static void Click(RoomHarness h, int n)
    {
        Assert.True(Act(h, "spin", PotterHands.Human(n)).Ok);
        h.Clock.Advance(1);
    }

    /// <summary>Виліпити n виробів: на колі вже майже готова робота, сушарня порожня — один клік її довершує.</summary>
    static void FormMany(RoomHarness h, int wares)
    {
        Patch(h, s => s["upgrades"]!["workshop"] = 500);         // сушарня на 20
        while (wares > 0)
        {
            var n = Math.Min(Clicker.RackMax, wares);
            Patch(h, s => { s["craft"]!["rack"] = new JsonArray(); s["craft"]!["work"] = 40.0 * n - 1; });
            Click(h, 1);
            wares -= n;
        }
    }

    static int PopCount(int x) => System.Numerics.BitOperations.PopCount((uint)x);

    static int[] Full(int rows) => Enumerable.Range(0, Clicker.Wares.Length).Select(i => i < rows ? (1 << Clicker.AlbumColumns) - 1 : 0).ToArray();

    // ---------- сітка: чисті правила ----------

    [Fact]
    public void The_grid_is_twelve_wares_by_nine_styles()
    {
        Assert.Equal(9, Clicker.AlbumColumns);
        Assert.Equal(108, Clicker.AlbumSize);
        Assert.Equal(0, Clicker.AlbumStyleIndex(""));
        Assert.Equal(0, Clicker.AlbumStyleIndex(null));
        Assert.Equal(1, Clicker.AlbumStyleIndex("gavarets"));
        Assert.Equal(8, Clicker.AlbumStyleIndex("trypillia"));
        Assert.Equal(-1, Clicker.AlbumStyleIndex("gzhel"));
        Assert.Equal(0, Clicker.AlbumWareIndex("pot"));
        Assert.Equal(11, Clicker.AlbumWareIndex("lion"));
        Assert.Equal(-1, Clicker.AlbumWareIndex("vase"));
    }

    [Fact]
    public void Each_cell_is_a_fifth_of_a_percent()
    {
        var cells = new int[12];
        cells[0] = 0b101;
        cells[5] = 0b1;
        Assert.Equal(3, Clicker.AlbumOpenCount(cells));
        Assert.Equal(0.006, Clicker.AlbumBonusFor(cells, 0, 0), 9);
    }

    [Fact]
    public void A_full_row_adds_two_percent_on_top_of_its_cells()
    {
        var cells = Full(1);
        Assert.Equal(1, Clicker.AlbumFullRows(cells));
        Assert.Equal(0, Clicker.AlbumFullColumns(cells));
        Assert.Equal(9 * 0.002 + 0.02, Clicker.AlbumBonusFor(cells, 0, 0), 9);
    }

    [Fact]
    public void A_full_column_adds_three_percent_on_top_of_its_cells()
    {
        var cells = Enumerable.Repeat(1 << 4, 12).ToArray();       // косівський на всіх виробах
        cells[3] |= 1;                                              // і одна проста макітра
        Assert.Equal(1, Clicker.AlbumFullColumns(cells));
        Assert.Equal(0, Clicker.AlbumFullRows(cells));
        Assert.Equal(13 * 0.002 + 0.03, Clicker.AlbumBonusFor(cells, 0, 0), 9);
    }

    [Fact]
    public void The_whole_grid_is_worth_about_seventy_three_percent()
    {
        var cells = Full(12);
        Assert.Equal(108, Clicker.AlbumOpenCount(cells));
        Assert.Equal(12, Clicker.AlbumFullRows(cells));
        Assert.Equal(9, Clicker.AlbumFullColumns(cells));
        Assert.Equal(108 * 0.002 + 12 * 0.02 + 9 * 0.03, Clicker.AlbumBonusFor(cells, 0, 0), 9);
    }

    [Fact]
    public void Stray_bits_beyond_the_ninth_style_count_for_nothing()
    {
        var cells = new int[12];
        cells[0] = 1 << 12;
        Assert.Equal(0, Clicker.AlbumOpenCount(cells));
        Assert.Equal(0, Clicker.AlbumBonusFor(cells, 0, 1 << 9));
    }

    [Fact]
    public void The_stove_is_a_percent_a_tile_and_five_more_when_full()
    {
        var empty = new int[12];
        Assert.Equal(0.05, Clicker.AlbumBonusFor(empty, 5, 0), 9);
        Assert.Equal(0.11, Clicker.AlbumBonusFor(empty, 11, 0), 9);
        Assert.Equal(0.17, Clicker.AlbumBonusFor(empty, 12, 0), 9);
        Assert.Equal(0.17, Clicker.AlbumBonusFor(empty, 40, 0), 9);
    }

    [Fact]
    public void The_museum_is_a_percent_a_find_and_five_more_when_full()
    {
        var empty = new int[12];
        Assert.Equal(0.03, Clicker.AlbumBonusFor(empty, 0, 0b1011), 9);
        Assert.Equal(0.13, Clicker.AlbumBonusFor(empty, 0, 0xFF), 9);
    }

    // ---------- майстерність: чисті правила ----------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(40, 3)]
    [InlineData(100, 4)]
    [InlineData(250, 5)]
    [InlineData(600, 6)]
    [InlineData(1_499, 6)]
    [InlineData(1_500, 7)]
    [InlineData(4_000, 8)]
    [InlineData(10_000, 9)]
    [InlineData(24_999, 9)]
    [InlineData(25_000, 10)]
    [InlineData(10_000_000, 10)]
    public void Mastery_levels_follow_the_thresholds(long fired, int level) => Assert.Equal(level, Clicker.MasteryLevel(fired));

    [Fact]
    public void Each_mastery_level_is_four_percent_less_work_and_five_percent_more_value()
    {
        Assert.Equal(1, Clicker.MasteryWorkMult(0));
        Assert.Equal(0.96, Clicker.MasteryWorkMult(1), 9);
        Assert.Equal(0.6, Clicker.MasteryWorkMult(10), 9);
        Assert.Equal(0.6, Clicker.MasteryWorkMult(99), 9);
        Assert.Equal(1.05, Clicker.MasteryValueMult(1), 9);
        Assert.Equal(1.5, Clicker.MasteryValueMult(10), 9);
    }

    // ---------- у грі ----------

    [Fact]
    public void A_new_game_has_an_empty_album()
    {
        var a = Album(Wheel());
        Assert.Equal(12, a.GetProperty("cells").GetArrayLength());
        Assert.All(a.GetProperty("cells").EnumerateArray(), c => Assert.Equal(0, c.GetInt32()));
        Assert.Equal(0, a.GetProperty("open").GetInt32());
        Assert.Equal(108, a.GetProperty("size").GetInt32());
        Assert.Equal(0, a.GetProperty("stove").GetArrayLength());
        Assert.Equal(0, a.GetProperty("finds").GetInt32());
        Assert.Equal(0, a.GetProperty("shards").GetInt32());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("find").ValueKind);
        Assert.Equal(0, a.GetProperty("bonus").GetDouble());
        Assert.All(a.GetProperty("mastery").EnumerateArray(), m => Assert.Equal(0, m.GetInt32()));
    }

    [Fact]
    public void Saved_cells_and_stars_show_in_the_view_and_boost_everything()
    {
        var h = Wheel();
        var before = View(h).GetProperty("allMult").GetDouble();
        Patch(h, s =>
        {
            var a = AlbumNode(s);
            a["cells"] = new JsonObject
            {
                ["pot"] = new JsonArray("", "gavarets", "vasylkiv", "bubnivka", "kosiv", "opishnia", "mezhyhirya", "petrykivka", "trypillia"),
                ["jug"] = new JsonArray("kosiv"),
            };
            a["stars"] = new JsonObject { ["jug"] = new JsonArray("kosiv") };
        });
        var al = Album(h);
        Assert.Equal(511, al.GetProperty("cells")[0].GetInt32());
        Assert.Equal(1 << 4, al.GetProperty("cells")[2].GetInt32());
        Assert.Equal(1 << 4, al.GetProperty("stars")[2].GetInt32());
        Assert.Equal(10, al.GetProperty("open").GetInt32());
        Assert.Equal(1, al.GetProperty("rows").GetInt32());
        Assert.Equal(0, al.GetProperty("cols").GetInt32());
        var bonus = 10 * 0.002 + 0.02;
        Assert.Equal(bonus, al.GetProperty("bonus").GetDouble(), 9);
        Assert.Equal(before * (1 + bonus), View(h).GetProperty("allMult").GetDouble(), 9);
    }

    [Fact]
    public void A_star_in_a_save_opens_its_cell_too_and_junk_is_dropped()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var a = AlbumNode(s);
            a["cells"] = new JsonObject { ["vase"] = new JsonArray(""), ["pot"] = new JsonArray("gzhel", "") };
            a["stars"] = new JsonObject { ["bowl"] = new JsonArray("trypillia") };
        });
        var al = Album(h);
        Assert.Equal(1, al.GetProperty("cells")[0].GetInt32());
        Assert.Equal(1 << 8, al.GetProperty("cells")[1].GetInt32());
        Assert.Equal(2, al.GetProperty("open").GetInt32());
    }

    [Fact]
    public void Mastery_makes_the_ware_quicker_to_form()
    {
        var h = Wheel();
        int Need(string ware) => Craft(h).GetProperty("wares").EnumerateArray().First(w => w.GetProperty("key").GetString() == ware).GetProperty("need").GetInt32();
        Assert.Equal(40, Need("pot"));
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 5, ["bowl"] = 25_000, ["lion"] = 600 });
        Assert.Equal(39, Need("pot"));                              // ceil(40 × 0,96)
        Assert.Equal(30, Need("bowl"));                             // 50 × 0,6
        Assert.Equal(380, Need("lion"));                            // 500 × 0,76
        Assert.Equal(new[] { 1, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6 }, Album(h).GetProperty("mastery").EnumerateArray().Select(x => x.GetInt32()));

        // Виріб на колі теж просить менше: 39 кліків — і горщик на сушарні.
        for (var i = 0; i < 39; i += 12) Click(h, Math.Min(12, 39 - i));
        Assert.Equal(1, Craft(h).GetProperty("rack").GetArrayLength());
    }

    [Fact]
    public void Mastery_raises_the_price_of_the_ware()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["kiln"] = 100);
        long Value(string ware) => Craft(h).GetProperty("wares").EnumerateArray().First(w => w.GetProperty("key").GetString() == ware).GetProperty("value").GetInt64();
        var jug = Value("jug");
        var pot = Value("pot");
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["jug"] = 250 });
        Assert.InRange(Value("jug"), jug * 1.25 - 1, jug * 1.25 + 1);
        Assert.Equal(pot, Value("pot"));
        Items(h, ("jug|kosiv|3", 1));
        var item = Craft(h).GetProperty("items")[0].GetProperty("value").GetInt64();
        var passive = View(h).GetProperty("baseSecond").GetDouble();
        var expect = passive * 6 * 2.6 * 1.5 * 1.25;
        Assert.InRange(item, expect - 1, expect + 1);
    }

    [Fact]
    public void Mastery_is_not_a_prestige_casualty()
    {
        var h = Wheel();
        Patch(h, s => { s["total"] = 2_000_000_000; s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 100 }; });
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(4, Album(h).GetProperty("mastery")[0].GetInt32());
        Assert.Equal(34, Craft(h).GetProperty("wares")[0].GetProperty("need").GetInt32());   // ceil(40 × 0,84)
    }

    // ---------- кахляна піч ----------

    [Fact]
    public void A_good_tile_goes_from_the_store_into_the_stove()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 2), ("pot||1", 1));
        Assert.Equal(2, Album(h).GetProperty("tiles").GetInt32());
        var r = AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" });
        Assert.True(r.Ok, r.Message);
        var al = Album(h);
        Assert.Equal(1, al.GetProperty("stove").GetArrayLength());
        Assert.Equal("kosiv", al.GetProperty("stove")[0].GetProperty("style").GetString());
        Assert.Equal(2, al.GetProperty("stove")[0].GetProperty("q").GetInt32());
        Assert.Equal(0.01, al.GetProperty("bonus").GetDouble(), 9);
        Assert.Equal(1, al.GetProperty("tiles").GetInt32());
        var items = Craft(h).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("n").GetInt32());
        Assert.Equal(1, items["tile|kosiv|2"]);
        Assert.Equal(1, items["pot||1"]);
    }

    [Fact]
    public void The_stove_refuses_plain_quality_other_wares_and_empty_hands()
    {
        var h = Wheel();
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile" }).Message);
        Items(h, ("tile||1", 3), ("pot||3", 1));
        Assert.StartsWith("Звичайна кахля на піч не йде", AlbumAct(h, new { op = "tile", key = "tile||1" }).Message);
        Assert.Equal("У піч кладуть лише кахлі", AlbumAct(h, new { op = "tile", key = "pot||3" }).Message);
        Assert.Equal("У піч кладуть лише кахлі", AlbumAct(h, new { op = "tile", key = "nonsense" }).Message);
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile" }).Message);
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile", key = "tile|kosiv|3" }).Message);
        Assert.Equal("Такого в альбомі не роблять", AlbumAct(h, new { op = "burn" }).Message);
        Assert.Equal(0, Album(h).GetProperty("stove").GetArrayLength());
        Assert.Equal(4, Craft(h).GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("n").GetInt32()));
    }

    [Fact]
    public void Without_a_key_the_stove_takes_the_plainest_good_tile()
    {
        var h = Wheel();
        Items(h, ("tile|opishnia|3", 1), ("tile|bubnivka|2", 1), ("tile||1", 5));
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Assert.Equal("bubnivka", Album(h).GetProperty("stove")[0].GetProperty("style").GetString());
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Assert.Equal(3, Album(h).GetProperty("stove")[1].GetProperty("q").GetInt32());
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile" }).Message);
    }

    [Fact]
    public void Twelve_tiles_make_a_stove_and_an_achievement_and_then_it_is_full()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 13));
        for (var i = 0; i < 11; i++) Assert.True(AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Ok);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-stove");
        var last = AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" });
        Assert.True(last.Ok);
        Assert.Contains("+17 %", last.Message);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-stove");
        Assert.Equal(0.17, Album(h).GetProperty("bonus").GetDouble(), 9);
        Assert.StartsWith("Піч уже вся в кахлях", AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Message);
        Assert.Equal(1, Craft(h).GetProperty("items")[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public void A_broken_stove_in_a_save_keeps_only_real_good_tiles_and_no_more_than_twelve()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var stove = new JsonArray();
            stove.Add(new JsonObject { ["style"] = "kosiv", ["quality"] = 1 });
            stove.Add(new JsonObject { ["style"] = "gzhel", ["quality"] = 3 });
            stove.Add(new JsonObject { ["style"] = null, ["quality"] = 2 });
            for (var i = 0; i < 20; i++) stove.Add(new JsonObject { ["style"] = "opishnia", ["quality"] = 3 });
            AlbumNode(s)["stove"] = stove;
        });
        var st = Album(h).GetProperty("stove");
        Assert.Equal(12, st.GetArrayLength());
        Assert.Equal("", st[0].GetProperty("style").GetString());
    }

    // ---------- трипільський музей ----------

    [Fact]
    public void Digging_clay_finds_trypillian_things_about_one_and_a_half_percent_of_the_time()
    {
        var h = Wheel(seed: 7);
        FormMany(h, 600);
        Assert.Equal(600, Craft(h).GetProperty("formed").GetInt64());
        var a = Album(h);
        var found = PopCount(a.GetProperty("finds").GetInt32()) + a.GetProperty("shards").GetInt32();
        // У середньому дев'ять; зерно стале, тож межі — лише запобіжник від зламаного шансу (нуль чи кожен другий).
        Assert.InRange(found, 2, 25);
        Assert.Equal(JsonValueKind.Object, a.GetProperty("find").ValueKind);
        Assert.True(a.GetProperty("bonus").GetDouble() >= 0.01);
    }

    [Fact]
    public void A_duplicate_find_becomes_a_shard()
    {
        var h = Wheel(seed: 3);
        Patch(h, s => AlbumNode(s)["finds"] = new JsonArray("spiral", "binocular", "figurine", "house", "grain", "krater", "ladle"));
        for (var i = 0; i < 60 && Album(h).GetProperty("shards").GetInt32() == 0; i++) FormMany(h, 20);
        var a = Album(h);
        Assert.True(a.GetProperty("shards").GetInt32() > 0 || PopCount(a.GetProperty("finds").GetInt32()) == 8);
        if (a.GetProperty("shards").GetInt32() > 0 && PopCount(a.GetProperty("finds").GetInt32()) == 7)
            Assert.True(a.GetProperty("find").GetProperty("dup").GetBoolean());
    }

    [Fact]
    public void The_full_museum_gives_thirteen_percent_an_achievement_and_stops_digging()
    {
        var h = Wheel(seed: 11);
        for (var i = 0; i < 200 && PopCount(Album(h).GetProperty("finds").GetInt32()) < 8; i++) FormMany(h, 20);
        var a = Album(h);
        Assert.Equal(255, a.GetProperty("finds").GetInt32());
        Assert.Contains(h.Awards, x => x.Reason == "ach:potter-museum-shards");
        Assert.Equal(0.13, a.GetProperty("bonus").GetDouble(), 9);
        var shards = a.GetProperty("shards").GetInt32();
        var last = Views.Text(a.GetProperty("find"));
        FormMany(h, 400);
        Assert.Equal(shards, Album(h).GetProperty("shards").GetInt32());
        Assert.Equal(last, Views.Text(Album(h).GetProperty("find")));
    }

    [Fact]
    public void Five_shards_glue_into_a_missing_find()
    {
        var h = Wheel();
        Patch(h, s => { var a = AlbumNode(s); a["finds"] = new JsonArray("spiral", "binocular", "figurine", "house", "grain", "krater", "ladle"); a["shards"] = 4; });
        Assert.Equal("Бракує уламків: треба 5, є 4", AlbumAct(h, new { op = "glue" }).Message);
        Patch(h, s => AlbumNode(s)["shards"] = 6);
        var r = AlbumAct(h, new { op = "glue" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("пряслице", r.Message);
        var al = Album(h);
        Assert.Equal(255, al.GetProperty("finds").GetInt32());
        Assert.Equal(1, al.GetProperty("shards").GetInt32());
        Assert.Equal("whorl", al.GetProperty("find").GetProperty("key").GetString());
        Assert.False(al.GetProperty("find").GetProperty("dup").GetBoolean());
        Assert.Contains(h.Awards, x => x.Reason == "ach:potter-museum-shards");
        Patch(h, s => AlbumNode(s)["shards"] = 9);
        Assert.StartsWith("Музей уже повний", AlbumAct(h, new { op = "glue" }).Message);
    }

    [Fact]
    public void Glue_picks_among_missing_finds_only()
    {
        var h = Wheel(seed: 5);
        Patch(h, s => { var a = AlbumNode(s); a["finds"] = new JsonArray("spiral", "figurine"); a["shards"] = 30; });
        for (var i = 0; i < 6; i++) Assert.True(AlbumAct(h, new { op = "glue" }).Ok);
        Assert.Equal(255, Album(h).GetProperty("finds").GetInt32());
        Assert.Equal(0, Album(h).GetProperty("shards").GetInt32());
    }

    [Fact]
    public void A_find_while_away_is_noted()
    {
        // Підмайстри ліплять без гравця; зерна перебираємо, доки хоч одне не принесе знахідку за простій.
        for (var seed = 1; seed < 400; seed++)
        {
            var h = Wheel(seed);
            Patch(h, s => { s["upgrades"]!["apprentice"] = 25; s["upgrades"]!["workshop"] = 500; });
            h.Clock.Advance(TimeSpan.FromMinutes(30));
            var away = View(h).GetProperty("away");
            if (away.ValueKind != JsonValueKind.Object) continue;
            if (!away.GetProperty("notes").EnumerateArray().Any(n => n.GetString()!.StartsWith("🏺"))) continue;
            Assert.True(PopCount(Album(h).GetProperty("finds").GetInt32()) > 0);
            return;
        }
        Assert.Fail("за 400 зерен жодної знахідки за простій — шанс зламано");
    }

    // ---------- збереження, обпал, каталог ----------

    [Fact]
    public void The_album_survives_a_reload()
    {
        var h = Wheel();
        Items(h, ("tile|gavarets|3", 2));
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Patch(h, s =>
        {
            var a = AlbumNode(s);
            a["cells"] = new JsonObject { ["dish"] = new JsonArray("", "petrykivka") };
            a["stars"] = new JsonObject { ["dish"] = new JsonArray("petrykivka") };
            a["finds"] = new JsonArray("house");
            a["shards"] = 3;
            a["last"] = new JsonObject { ["key"] = "house", ["at"] = "2026-09-15T10:00:00+00:00", ["dup"] = false };
        });
        var before = Views.Text(Album(h));
        Patch(h, _ => { });
        Assert.Equal(before, Views.Text(Album(h)));
        Assert.Equal(1, Album(h).GetProperty("stove").GetArrayLength());
        Assert.Equal("house", Album(h).GetProperty("find").GetProperty("key").GetString());
    }

    [Fact]
    public void An_old_save_without_the_album_starts_it_empty()
    {
        var h = Wheel();
        Patch(h, s => AlbumNode(s)["cells"] = new JsonObject { ["pot"] = new JsonArray("") });
        Patch(h, s => s.Remove("album"));
        Assert.Equal(0, Album(h).GetProperty("open").GetInt32());
        Patch(h, s => s["album"] = null);
        Assert.Equal(0, Album(h).GetProperty("open").GetInt32());
        Patch(h, s => s["album"] = new JsonObject());
        Assert.Equal(0, Album(h).GetProperty("bonus").GetDouble());
    }

    [Fact]
    public void Firing_the_workshop_keeps_the_album_the_stove_and_the_museum()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 1));
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Patch(h, s =>
        {
            s["total"] = 2_000_000_000;
            var a = AlbumNode(s);
            a["cells"] = new JsonObject { ["pot"] = new JsonArray("", "kosiv") };
            a["finds"] = new JsonArray("spiral", "krater");
            a["shards"] = 2;
        });
        var before = Views.Text(Album(h));
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(before, Views.Text(Album(h)));
        Assert.Equal(0.002 * 2 + 0.01 + 0.02, Album(h).GetProperty("bonus").GetDouble(), 9);
    }

    [Fact]
    public void The_catalog_carries_facts_for_every_ware_style_and_find()
    {
        var c = View(Wheel()).GetProperty("catalog").GetProperty("album");
        Assert.Equal(10, c.GetProperty("masteryAt").GetArrayLength());
        Assert.Equal(8, c.GetProperty("finds").GetArrayLength());
        Assert.All(c.GetProperty("finds").EnumerateArray(), f => Assert.False(string.IsNullOrWhiteSpace(f.GetProperty("desc").GetString())));
        foreach (var w in Clicker.Wares) Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("wares").GetProperty(w.Key).GetString()));
        Assert.True(c.GetProperty("styles").TryGetProperty("", out _));
        foreach (var s in Clicker.Styles) Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("styles").GetProperty(s.Key).GetProperty("text").GetString()));
        Assert.Contains("не на кераміці", c.GetProperty("styles").GetProperty("petrykivka").GetProperty("text").GetString());
        Assert.Contains("ЮНЕСКО з 2019", c.GetProperty("styles").GetProperty("kosiv").GetProperty("text").GetString());
    }

    [Fact]
    public void The_album_view_is_a_pure_function_of_state()
    {
        var h = Wheel(seed: 9);
        FormMany(h, 200);
        Assert.Equal(Views.Text(Album(h)), Views.Text(Album(h)));
    }

    [Fact]
    public void Styles_still_buy_and_wear_through_the_old_actions()
    {
        // Вкладка «Розписи» переїхала в альбом лише на клієнті: серверні paint і wear ті самі.
        var h = Wheel();
        Patch(h, s => { s["pots"] = 2_000_000; s["total"] = 2_000_000; });
        var r = Act(h, "paint", new { key = "gavarets" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("gavarets", View(h).GetProperty("wear").GetString());
        Assert.True(Act(h, "wear", new { key = "" }).Ok);
        Assert.Equal("", View(h).GetProperty("wear").GetString());
        Assert.True(Act(h, "wear", new { key = "gavarets" }).Ok);
        Assert.Equal("Цього розпису ще нема в колекції", Act(h, "wear", new { key = "kosiv" }).Message);
    }
}
