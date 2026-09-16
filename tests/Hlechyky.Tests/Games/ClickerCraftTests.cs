using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Ремесло (сьоме оновлення, ClickerCraft.cs): кліки й підмайстри ліплять виріб, він сохне на сушарні, комора й
/// базар, «поки тебе не було», каталоги у виді, збереження й обпал. Горно, альбом, ярмарок і цех — у своїх тестах.
/// </summary>
public class ClickerCraftTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static int RackCount(RoomHarness h) => Craft(h).GetProperty("rack").GetArrayLength();

    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);

    static void Click(RoomHarness h, int times)
    {
        while (times > 0)
        {
            var n = Math.Min(Clicker.MaxClicksPerSecond, times);
            Assert.True(Act(h, "spin", PotterHands.Human(n)).Ok);
            times -= n;
            h.Clock.Advance(1);
        }
    }

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
        var craft = s["craft"]!.AsObject();
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        craft["items"] = bag;
    });

    [Fact]
    public void Forty_clicks_make_a_pot_on_the_rack_and_not_a_single_extra_pot()
    {
        var h = Wheel();
        Click(h, 39);
        Assert.Equal(0, RackCount(h));
        Assert.Equal(39, Craft(h).GetProperty("work").GetDouble());
        Click(h, 1);

        var c = Craft(h);
        Assert.Equal(1, c.GetProperty("rack").GetArrayLength());
        Assert.Equal("pot", c.GetProperty("rack")[0].GetProperty("ware").GetString());
        Assert.Equal(0, c.GetProperty("work").GetDouble());
        Assert.Equal(1, c.GetProperty("formed").GetInt64());
        // Ремесло глеків не додає: сорок кліків — сорок глеків, як і було.
        Assert.Equal(40, Pots(h));
    }

    [Fact]
    public void The_first_ware_is_an_achievement()
    {
        var h = Wheel();
        Click(h, 40);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-ware-1");
    }

    [Fact]
    public void A_full_rack_stops_the_wheel_at_the_last_bit_of_work()
    {
        var h = Wheel();
        Click(h, 40 * Clicker.RackBase + 45);
        var c = Craft(h);
        Assert.Equal(Clicker.RackBase, c.GetProperty("rack").GetArrayLength());
        Assert.True(c.GetProperty("rackFull").GetBoolean());
        Assert.Equal(40, c.GetProperty("work").GetDouble());
    }

    [Fact]
    public void Wares_dry_on_the_rack()
    {
        var h = Wheel();
        Click(h, 40);
        var dryAt = Craft(h).GetProperty("rack")[0].GetProperty("dryAt").GetDateTimeOffset();
        Assert.True(dryAt > h.Clock.UtcNow);
        Assert.True(dryAt <= h.Clock.UtcNow + Clicker.DryTime);
    }

    [Fact]
    public void Apprentices_form_on_their_own_but_slowly_and_only_into_free_rack_space()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 25);           // 0,5 роботи за секунду — стеля
        Assert.Equal(Clicker.ApprenticeWorkMax, Craft(h).GetProperty("apprentice").GetDouble());
        h.Clock.Advance(79);
        Assert.Equal(0, RackCount(h));
        h.Clock.Advance(2);
        Assert.Equal(1, RackCount(h));

        h.Clock.Advance(TimeSpan.FromHours(8));
        var c = Craft(h);
        Assert.Equal(Clicker.RackBase, c.GetProperty("rack").GetArrayLength());
        // За довгий простій виліплене встигло висохнути.
        Assert.All(c.GetProperty("rack").EnumerateArray().Skip(1), r => Assert.True(r.GetProperty("dryAt").GetDateTimeOffset() <= h.Clock.UtcNow));
    }

    [Fact]
    public void A_bigger_workshop_means_a_bigger_rack()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["workshop"] = 25);
        Assert.Equal(Clicker.RackBase + 5, Craft(h).GetProperty("rackSize").GetInt32());
        Patch(h, s => s["upgrades"]!["workshop"] = 500);
        Assert.Equal(Clicker.RackMax, Craft(h).GetProperty("rackSize").GetInt32());
    }

    [Fact]
    public void A_locked_ware_is_refused_and_an_open_one_goes_on_the_wheel()
    {
        var h = Wheel();
        Assert.StartsWith("Глечик відкриється на", Act(h, "form", new { ware = "jug" }).Message);
        Assert.Equal("Такого виробу гончарі не ліплять", Act(h, "form", new { ware = "vase" }).Message);

        Patch(h, s => { s["total"] = 1_000; s["pots"] = 0; });
        Click(h, 30);
        Assert.True(Act(h, "form", new { ware = "jug" }).Ok);
        var c = Craft(h);
        Assert.Equal("jug", c.GetProperty("ware").GetString());
        Assert.Equal(80, c.GetProperty("need").GetInt32());
        Assert.Equal(30, c.GetProperty("work").GetDouble());      // робота не губиться при зміні виробу
        Click(h, 50);
        Assert.Equal("jug", Craft(h).GetProperty("rack")[0].GetProperty("ware").GetString());
    }

    [Fact]
    public void Switching_to_a_smaller_ware_trims_the_work()
    {
        var h = Wheel();
        Patch(h, s => { s["total"] = 1_000; s["craft"]!["ware"] = "jug"; s["craft"]!["work"] = 70; });
        Assert.True(Act(h, "form", new { ware = "bowl" }).Ok);
        Assert.Equal(50, Craft(h).GetProperty("work").GetDouble());
    }

    [Fact]
    public void The_bazaar_pays_the_click_floor_on_a_bare_wheel()
    {
        var h = Wheel();
        Items(h, ("pot||1", 3), ("pot||3", 1));
        var before = Pots(h);
        // Голе коло: пасиву нема, тож ціна — половина кліків на виріб (40 × 0,5), дзвінкий — ×2,6.
        var r = Act(h, "bazaar", new { key = "pot||1", n = 2 });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before + 40, Pots(h));
        Assert.True(Act(h, "bazaar", new { key = "pot||3" }).Ok);
        Assert.Equal(before + 40 + 52, Pots(h));
        Assert.Equal(1, Craft(h).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void The_bazaar_price_follows_the_passive_quality_and_style()
    {
        var h = Wheel();
        Patch(h, s => { s["upgrades"]!["kiln"] = 1000; s["styles"] = new JsonArray("gavarets"); });   // 3000 глеків/с × 1,05 за розпис — вище дна кліків
        Items(h, ("jug||1", 1), ("jug|gavarets|2", 1));
        var items = Craft(h).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").GetInt64());
        var passive = View(h).GetProperty("baseSecond").GetDouble();
        Assert.Equal((long)(passive * 6), items["jug||1"]);
        Assert.Equal((long)(passive * 6 * 1.6 * 1.2), items["jug|gavarets|2"]);
    }

    [Fact]
    public void Selling_everything_asks_for_something_to_sell()
    {
        var h = Wheel();
        Assert.Equal("У коморі порожньо — нічого везти на базар", Act(h, "bazaar", new { all = true }).Message);
        Items(h, ("pot||1", 2), ("bowl||2", 1));
        var before = Pots(h);
        Assert.True(Act(h, "bazaar", new { all = true }).Ok);
        Assert.Equal(before + 2 * 20 + (long)(50 * 0.5 * 1.6), Pots(h));
        Assert.Equal(0, Craft(h).GetProperty("items").GetArrayLength());
        Assert.Equal("Такого виробу в коморі нема", Act(h, "bazaar", new { key = "pot||1" }).Message);
    }

    [Fact]
    public void Selling_up_to_a_quality_leaves_the_dearer_wares_in_the_store()
    {
        var h = Wheel();
        Items(h, ("pot||1", 2), ("pot||2", 1), ("pot||3", 1));
        var before = Pots(h);
        // «Лише звичайні» бере два горщики по 20 (дно кліків: 40 × 0,5) — добрий і дзвінкий лишаються.
        var r = Act(h, "bazaar", new { all = true, q = 1 });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("(лише звичайні)", r.Message);
        Assert.Equal(before + 2 * 20, Pots(h));
        // Звичайних більше нема — кнопка про це й каже, а не про порожню комору.
        Assert.Equal("Звичайних у коморі нема", Act(h, "bazaar", new { all = true, q = 1 }).Message);
        // «Усе, крім дзвінких» забирає доброго (20 × 1,6) і лишає дзвінкого.
        var two = Act(h, "bazaar", new { all = true, q = 2 });
        Assert.True(two.Ok, two.Message);
        Assert.Contains("(крім дзвінких)", two.Message);
        Assert.Equal(before + 2 * 20 + 32, Pots(h));
        Assert.Equal("pot||3", Craft(h).GetProperty("items").EnumerateArray().Single().GetProperty("key").GetString());
        Assert.Equal("У коморі самі дзвінкі — їх базар не бере", Act(h, "bazaar", new { all = true, q = 2 }).Message);
    }

    [Fact]
    public void Broken_items_in_a_save_are_dropped()
    {
        var h = Wheel();
        Items(h, ("pot||1", 2), ("vase||1", 5), ("pot|nope|1", 5), ("pot||9", 5), ("pot||2", -3));
        var items = Craft(h).GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("pot||1", items[0].GetProperty("key").GetString());
    }

    [Fact]
    public void The_craft_survives_a_reload()
    {
        var h = Wheel();
        Click(h, 95);
        Items(h, ("bowl||2", 4));
        var before = Views.Text(Craft(h));
        Patch(h, _ => { });
        Assert.Equal(before, Views.Text(Craft(h)));
        Assert.Equal(2, RackCount(h));
    }

    [Fact]
    public void An_old_save_without_the_craft_starts_with_an_empty_rack()
    {
        var h = Wheel();
        Click(h, 50);
        Patch(h, s => s.Remove("craft"));
        var c = Craft(h);
        Assert.Equal("pot", c.GetProperty("ware").GetString());
        Assert.Equal(0, c.GetProperty("work").GetDouble());
        Assert.Equal(0, c.GetProperty("rack").GetArrayLength());
    }

    [Fact]
    public void Firing_the_workshop_burns_the_rack_and_the_store()
    {
        var h = Wheel();
        Click(h, 45);
        Patch(h, s =>
        {
            s["total"] = 2_000_000_000;
            s["craft"]!["items"] = new JsonObject { ["pot||1"] = 3 };
            s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 17 };
        });
        Assert.True(Act(h, "fire").Ok);
        var c = Craft(h);
        Assert.Equal(0, c.GetProperty("rack").GetArrayLength());
        Assert.Equal(0, c.GetProperty("items").GetArrayLength());
        Assert.Equal(0, c.GetProperty("work").GetDouble());
        Assert.Equal(17, c.GetProperty("fired").GetInt64());         // майстерність рук не згорає
    }

    [Fact]
    public void Coming_back_after_a_while_shows_what_happened()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["apprentice"] = 25);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("away").ValueKind);
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        var away = View(h).GetProperty("away");
        Assert.Equal(1800, away.GetProperty("seconds").GetInt64());
        Assert.True(away.GetProperty("pots").GetInt64() > 0);
        Assert.True(away.GetProperty("formed").GetInt32() > 0);

        // Коротка перерва не переписує запис про довгу.
        h.Clock.Advance(20);
        Assert.Equal(1800, View(h).GetProperty("away").GetProperty("seconds").GetInt64());
    }

    [Fact]
    public void Catalogs_ride_the_view_until_the_first_action_and_come_back_on_request()
    {
        var h = Wheel();
        var catalog = View(h).GetProperty("catalog");
        Assert.Equal(Clicker.Wares.Length, catalog.GetProperty("wares").GetArrayLength());
        Assert.Equal(Views.Text(View(h)), Views.Text(View(h)));    // вид — чиста функція стану

        Click(h, 1);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("catalog").ValueKind);
        Assert.True(Act(h, "look", new { catalog = true }).Ok);
        Assert.Equal(JsonValueKind.Object, View(h).GetProperty("catalog").ValueKind);
        Click(h, 1);
        Assert.Equal(JsonValueKind.Null, View(h).GetProperty("catalog").ValueKind);
    }

    [Fact]
    public void While_the_master_waits_the_wheel_forms_nothing()
    {
        var h = Wheel();
        Click(h, 10);
        Patch(h, s => s["guard"]!["left"] = 0);
        Click(h, 12);                                              // ця пачка кличе майстра
        var work = Craft(h).GetProperty("work").GetDouble();
        Click(h, 24);
        Assert.Equal(work, Craft(h).GetProperty("work").GetDouble());
    }
}
