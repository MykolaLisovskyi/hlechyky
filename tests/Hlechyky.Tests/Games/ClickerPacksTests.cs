using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Стики пакетів сьомого оновлення, які не належать жодному з них окремо: перки цеху в горні (автогорно челядника,
/// місця майстра). Дарунок → клітинка альбому — у <see cref="ClickerGuildTests"/>.
/// </summary>
public class ClickerPacksTests
{
    static RoomHarness Wheel()
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    /// <summary>Повна суха сушарня з горщиків і ранг цеху.</summary>
    static void FullDryRack(RoomHarness h, int rank, int count = Clicker.RackBase) => Patch(h, s =>
    {
        var rack = new JsonArray();
        for (var i = 0; i < count; i++)
            rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
        s["craft"]!["rack"] = rack;
        s["guild"] = new JsonObject { ["rank"] = rank };
    });

    [Fact]
    public void A_journeyman_s_apprentices_fire_a_full_dry_rack_on_their_own()
    {
        var h = Wheel();
        FullDryRack(h, rank: 1);
        var kiln = View(h).GetProperty("kiln");
        Assert.Equal("burning", kiln.GetProperty("state").GetString());
        Assert.Equal(Clicker.RackBase - 6, View(h).GetProperty("craft").GetProperty("rack").GetArrayLength());

        h.Clock.Advance(Clicker.KilnBurn + TimeSpan.FromSeconds(1));
        var fired = View(h).GetProperty("craft").GetProperty("fired").GetInt64();
        Assert.Equal(6, fired);
    }

    [Fact]
    public void An_apprentice_rank_or_a_half_rack_does_not_light_the_kiln()
    {
        var h = Wheel();
        FullDryRack(h, rank: 0);
        Assert.Equal("cold", View(h).GetProperty("kiln").GetProperty("state").GetString());

        var j = Wheel();
        FullDryRack(j, rank: 1, count: 4);
        Assert.Equal("cold", View(j).GetProperty("kiln").GetProperty("state").GetString());
    }

    [Fact]
    public void A_master_gets_two_more_kiln_slots()
    {
        var h = Wheel();
        var before = View(h).GetProperty("kiln").GetProperty("slots").GetInt32();
        Patch(h, s => s["guild"] = new JsonObject { ["rank"] = 2 });
        Assert.Equal(before + 2, View(h).GetProperty("kiln").GetProperty("slots").GetInt32());
    }

    // ---------- після рецензії ----------

    [Fact]
    public void An_achievement_earned_while_away_is_not_lost_when_it_arrives_in_a_view()
    {
        var h = Wheel();
        Patch(h, s => { s["upgrades"]!["apprentice"] = 25; s["craft"]!["formed"] = 999; });
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(View(h).GetProperty("craft").GetProperty("formed").GetInt64() > 1000);
        // У виді каркас ачівок не приймає — вони чекають першої дії (і лежать у збереженні, якщо дії не буде).
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-ware-1k");
        Assert.True(h.Act(0, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-ware-1k");
    }

    [Fact]
    public void Queued_achievements_survive_a_reload()
    {
        var h = Wheel();
        Patch(h, s => { s["upgrades"]!["apprentice"] = 25; s["craft"]!["formed"] = 999; });
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        View(h);
        Patch(h, _ => { });
        Assert.True(h.Act(0, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-ware-1k");
    }

    [Fact]
    public void The_auto_kiln_leaves_a_batch_the_potter_started_painting_alone()
    {
        var h = Wheel();
        Patch(h, s => s["styles"] = new JsonArray("gavarets"));
        Assert.True(h.Act(0, "kiln", new { op = "paint", style = "gavarets" }).Ok);
        FullDryRack(h, rank: 1);
        Assert.Equal("cold", View(h).GetProperty("kiln").GetProperty("state").GetString());
    }

    [Fact]
    public void A_view_is_a_snapshot_that_later_actions_do_not_change()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["items"] = new JsonObject { ["bowl||1"] = 3 });
        object before;
        lock (h.Room.Sync) before = h.Room.Game.View(0);
        Assert.True(h.Act(0, "bazaar", new { all = true }).Ok);
        // Розсилка серіалізує вид уже поза замком: те, що склали до продажу, мусить лишитись тим, що склали.
        Assert.Equal(1, Views.Json(before).GetProperty("craft").GetProperty("items").GetArrayLength());
        Assert.Equal(0, View(h).GetProperty("craft").GetProperty("items").GetArrayLength());
    }

    sealed class FlakyStore : IGameStore
    {
        public readonly FakeStore Inner = new();
        public int FailReads;
        public int Saves;
        public void SaveState(string key, string json) { Saves++; Inner.SaveState(key, json); }
        public string? LoadState(string key)
        {
            if (FailReads > 0) { FailReads--; throw new InvalidOperationException("database is locked"); }
            return Inner.LoadState(key);
        }
        public void DeleteState(string key) => Inner.DeleteState(key);
    }

    [Fact]
    public void A_database_hiccup_on_first_read_does_not_wipe_the_guild()
    {
        var store = new FlakyStore();
        var clock = new FakeClock();
        new ClickerGuildService(store.Inner, clock).Hello("оля", "Оля", 0, clock.UtcNow);
        var saved = store.Inner.LoadState("clicker-guild");
        Assert.NotNull(saved);

        store.FailReads = 1;
        var svc = new ClickerGuildService(store, clock);
        svc.Hello("петро", "Петро", 0, clock.UtcNow);             // читання впало — цей виклик нічого не пише
        Assert.Equal(0, store.Saves);
        Assert.Equal(saved, store.Inner.LoadState("clicker-guild"));

        svc.Hello("петро", "Петро", 0, clock.UtcNow);             // база ожила — Олю не загубили
        var json = System.Text.RegularExpressions.Regex.Unescape(store.Inner.LoadState("clicker-guild")!);
        Assert.Contains("Оля", json);
        Assert.Contains("Петро", json);
    }
}
