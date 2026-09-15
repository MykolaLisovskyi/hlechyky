using System.Text.Json;
using System.Text.Json.Nodes;
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
}
