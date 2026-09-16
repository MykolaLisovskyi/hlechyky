using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Цех (пакет B5, ClickerGuild.cs + ClickerGuildService.cs): денний віз, нагорода, дарунки, хата друга, похвала,
/// ранги й майстерштук, перки, без сервісу, збереження, обпал, двоє друзів з одним сервісом і паралельні виклики.
/// </summary>
public class ClickerGuildTests
{
    // Годинник RoomHarness стоїть на четвер 2026-09-10 12:00 UTC — це 15:00 у Києві, київський день 2026-09-10.
    static readonly DateTimeOffset Thursday = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    const string D10 = "2026-09-10", D11 = "2026-09-11";

    sealed class Tsekh
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public ClickerGuildService Svc { get; }
        public Tsekh() => Svc = new ClickerGuildService(Store, Clock);

        /// <summary>Гончар у своїй кімнаті з цим сервісом; збереження кола — одразу і в сховищі цеху (щоб йому можна було дарувати).</summary>
        public RoomHarness Potter(string nick)
        {
            var h = new RoomHarness("clicker", services: RoomHarness.WithService(Svc));
            h.Solo(nick);
            Assert.True(h.Act(0, "look").Ok);
            Publish(h, nick);
            return h;
        }

        public void Publish(RoomHarness h, string nick)
        {
            lock (h.Room.Sync) Store.SaveState("clicker:" + ClickerGuildService.Key(nick), h.Room.Game.Save()!);
        }
    }

    static JsonElement G(RoomHarness h) => h.View(0).GetProperty("guild");
    static long Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetInt64();
    static ActResult Guild(RoomHarness h, object payload) => h.Act(0, "guild", payload);

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

    static JsonObject GuildRow(JsonObject s) => (s["guild"] as JsonObject) ?? (JsonObject)(s["guild"] = new JsonObject());

    static void GiveN(RoomHarness h, string ware, int n)
    {
        while (n > 0)
        {
            var k = Math.Min(n, Clicker.StoreCap);
            Items(h, ($"{ware}||1", k));
            var r = Guild(h, new { op = "give", key = $"{ware}||1", n = k });
            Assert.True(r.Ok, r.Message);
            n -= k;
        }
    }

    /// <summary>Докласти на сьогоднішній віз рівно стільки, щоб він дійшов до рівня tier.</summary>
    static void Fill(RoomHarness h, Tsekh g, string nick, int tier)
    {
        var w = g.Svc.Summary(ClickerGuildService.Key(nick), h.Clock.UtcNow).Today;
        foreach (var sub in w.Subs) if (sub.Need > sub.Have) GiveN(h, sub.Ware, sub.Need - sub.Have);
        w = g.Svc.Summary(ClickerGuildService.Key(nick), h.Clock.UtcNow).Today;
        var target = (int)Math.Ceiling(w.Goal * ClickerGuildService.TierShare[tier]);
        if (target > w.Total) GiveN(h, "pot", target - w.Total);
    }

    static IEnumerable<string> Journal(RoomHarness h) => h.Outbox.OfType<Journal>().Select(j => j.Text);

    // ---------- день і ціль ----------

    [Fact]
    public void The_day_follows_the_Kyiv_calendar()
    {
        Assert.Equal(D10, ClickerGuildService.DayOf(Thursday));
        // 23:59 у Києві (літо, UTC+3) — ще той день; 00:00 — уже наступний.
        Assert.Equal(D10, ClickerGuildService.DayOf(new DateTimeOffset(2026, 9, 10, 20, 59, 0, TimeSpan.Zero)));
        Assert.Equal(D11, ClickerGuildService.DayOf(new DateTimeOffset(2026, 9, 10, 21, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 21, 0, 0, TimeSpan.Zero), ClickerGuildService.DayEnds(Thursday));
        Assert.Equal("2026-09-09", ClickerGuildService.DayBefore(Thursday));
        // Узимку (UTC+2) межа зсувається на годину.
        Assert.Equal("2026-12-13", ClickerGuildService.DayOf(new DateTimeOffset(2026, 12, 13, 21, 59, 0, TimeSpan.Zero)));
        Assert.Equal("2026-12-14", ClickerGuildService.DayOf(new DateTimeOffset(2026, 12, 13, 22, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_goal_comes_from_the_day_seed_and_the_number_of_potters()
    {
        var (a, subsA) = ClickerGuildService.GoalFor(D10, 2);
        var (b, subsB) = ClickerGuildService.GoalFor(D10, 2);
        Assert.Equal(a, b);
        Assert.Equal(subsA, subsB);
        Assert.InRange(a, 20, 30);
        Assert.Equal(0, a % 5);
        Assert.InRange(subsA.Count, 2, 3);
        Assert.Equal(subsA.Count, subsA.Select(s => s.Ware).Distinct().Count());
        Assert.All(subsA, s => Assert.Contains(s.Ware, new[] { "pot", "bowl", "jug", "makitra", "dish" }));
        Assert.All(subsA, s => Assert.InRange(s.Need, 5, a));

        // Один гончар — однаково як двоє; четверо — удвічі більше.
        Assert.Equal(a, ClickerGuildService.GoalFor(D10, 1).Goal);
        Assert.InRange(ClickerGuildService.GoalFor(D10, 4).Goal, 40, 55);
        // Різні дні — різні вози (хоч котрийсь із кількох).
        Assert.True(Enumerable.Range(10, 8).Select(i => ClickerGuildService.GoalFor($"2026-09-{i}", 2)).Distinct().Count() > 1);
    }

    [Fact]
    public void Givers_of_yesterday_decide_how_heavy_today_is()
    {
        var g = new Tsekh();
        foreach (var nick in new[] { "а", "б", "в" }) g.Svc.Give(nick, nick, "pot", 1, Thursday);
        var next = Thursday.AddDays(1);
        var w = g.Svc.Summary("а", next).Today;
        Assert.Equal(D11, w.Day);
        Assert.Equal(3, w.Potters);
        Assert.Equal(ClickerGuildService.GoalFor(D11, 3).Goal, w.Goal);
        // А сьогодні вчорашнього нема — двоє за замовчуванням.
        Assert.Equal(2, g.Svc.Summary("а", Thursday).Today.Potters);
    }

    [Fact]
    public void Tiers_need_every_sub_goal_and_then_100_150_200_percent()
    {
        var subs = new[] { new WagonSub("pot", 20, 20), new WagonSub("jug", 10, 10) };
        Assert.Equal(0, ClickerGuildService.TierOf(100, 99, subs));
        Assert.Equal(1, ClickerGuildService.TierOf(100, 100, subs));
        Assert.Equal(1, ClickerGuildService.TierOf(100, 149, subs));
        Assert.Equal(2, ClickerGuildService.TierOf(100, 150, subs));
        Assert.Equal(3, ClickerGuildService.TierOf(100, 200, subs));
        Assert.Equal(3, ClickerGuildService.TierOf(100, 900, subs));
        Assert.Equal(0, ClickerGuildService.TierOf(100, 900, [new WagonSub("pot", 20, 20), new WagonSub("jug", 10, 9)]));
    }

    // ---------- віз у кімнаті ----------

    [Fact]
    public void Giving_takes_items_from_the_store_and_records_the_share()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Items(h, ("pot||1", 3), ("jug|kosiv|3", 2));
        var r = Guild(h, new { op = "give", key = "pot||1", n = 10 });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("на віз", r.Message);

        var day = G(h).GetProperty("day");
        Assert.Equal(D10, day.GetProperty("id").GetString());
        Assert.Equal(3, day.GetProperty("total").GetInt32());
        Assert.Equal(3, day.GetProperty("mine").GetInt32());
        Assert.Equal("Оля", day.GetProperty("givers")[0].GetProperty("nick").GetString());
        var items = h.View(0).GetProperty("craft").GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());                         // горщики пішли, глечики лишились
        Assert.Equal(3L, G(h).GetProperty("given").GetInt64());

        Assert.Equal("Такого виробу в коморі нема", Guild(h, new { op = "give", key = "pot||1" }).Message);
        Assert.Equal("Такого виробу в коморі нема", Guild(h, new { op = "give", key = "vase||1" }).Message);
        Assert.Equal("Скільки покласти — хоч один", Guild(h, new { op = "give", key = "jug|kosiv|3", n = 0 }).Message);
        Assert.Equal("Такого в цеху не роблять", Guild(h, new { op = "dance" }).Message);
    }

    [Fact]
    public void Reaching_a_tier_says_so_in_the_journal_once()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Fill(h, g, "Оля", 1);
        Assert.Equal(1, G(h).GetProperty("day").GetProperty("tier").GetInt32());
        Assert.Single(Journal(h), t => t.Contains("Віз цеху") && t.Contains("бронза") && t.Contains("Оля"));

        GiveN(h, "pot", 1);
        Assert.Single(Journal(h), t => t.Contains("Віз цеху"));
        Fill(h, g, "Оля", 3);
        Assert.Contains(Journal(h), t => t.Contains("золото"));
    }

    [Fact]
    public void Only_pots_do_not_fill_the_sub_goals()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        var goal = g.Svc.Summary("оля", h.Clock.UtcNow).Today.Goal;
        GiveN(h, "pot", goal * 2);
        Assert.Equal(0, G(h).GetProperty("day").GetProperty("tier").GetInt32());
        Assert.Equal(200.0, G(h).GetProperty("day").GetProperty("pct").GetDouble());
    }

    [Fact]
    public void A_claim_pays_minutes_of_own_passive_once_per_tier_and_the_first_is_an_achievement()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Assert.StartsWith("Нагорода — тим, хто поклав", Guild(h, new { op = "claim" }).Message);
        GiveN(h, "pot", 5);
        Assert.Equal("Віз ще не наповнився навіть до бронзи — докладаймо разом", Guild(h, new { op = "claim" }).Message);

        Fill(h, g, "Оля", 1);
        var claim = G(h).GetProperty("claims")[0];
        Assert.Equal(1, claim.GetProperty("tier").GetInt32());
        // Голе коло: пасиву нема, тож дно — 20 кліків на хвилину: 10 хв бронзи — 200 глеків.
        Assert.Equal(200, claim.GetProperty("pots").GetInt64());
        var before = Pots(h);
        var r = Guild(h, new { op = "claim" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before + 200, Pots(h));
        Assert.Single(h.Awards, a => a.Reason == "ach:potter-wagon");
        Assert.Equal("Нагороду за бронзу ти вже забрав", Guild(h, new { op = "claim" }).Message);
        Assert.Equal(0, G(h).GetProperty("claims").GetArrayLength());

        // Віз доріс до золота — добираємо різницю, ачівка вдруге не приходить.
        Fill(h, g, "Оля", 3);
        before = Pots(h);
        Assert.True(Guild(h, new { op = "claim", day = D10 }).Ok);
        Assert.Equal(before + (35 - 10) * 20, Pots(h));
        Assert.Single(h.Awards, a => a.Reason == "ach:potter-wagon");
    }

    [Fact]
    public void The_reward_follows_the_passive_and_a_guildmaster_gets_half_again()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s => s["upgrades"]!["kiln"] = 100);                    // 300 глеків/с
        Fill(h, g, "Оля", 1);
        var passive = h.View(0).GetProperty("baseSecond").GetDouble();
        Assert.Equal((long)(passive * 10 * 60), G(h).GetProperty("claims")[0].GetProperty("pots").GetInt64());
        Patch(h, s => GuildRow(s)["rank"] = 3);
        Assert.Equal((long)(passive * 15 * 60), G(h).GetProperty("claims")[0].GetProperty("pots").GetInt64());
    }

    [Fact]
    public void Yesterdays_wagon_can_be_claimed_today_but_not_later()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Fill(h, g, "Оля", 2);
        h.Clock.UtcNow = new DateTimeOffset(2026, 9, 10, 21, 5, 0, TimeSpan.Zero);   // 00:05 наступного дня в Києві
        var view = G(h);
        Assert.Equal(D11, view.GetProperty("day").GetProperty("id").GetString());
        Assert.Equal(0, view.GetProperty("day").GetProperty("total").GetInt32());
        Assert.Equal(D10, view.GetProperty("prev").GetProperty("id").GetString());
        Assert.Equal(D10, view.GetProperty("claims")[0].GetProperty("day").GetString());
        var before = Pots(h);
        Assert.True(Guild(h, new { op = "claim" }).Ok);
        Assert.Equal(before + 20 * 20, Pots(h));

        // Ще день — той віз поїхав назавжди.
        var g2 = new Tsekh();
        var h2 = g2.Potter("Петро");
        Fill(h2, g2, "Петро", 1);
        h2.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal("Той віз уже давно поїхав — нагороди за нього не забрати", Guild(h2, new { op = "claim", day = D10 }).Message);
        Assert.StartsWith("Нагорода — тим, хто поклав", Guild(h2, new { op = "claim" }).Message);
    }

    [Fact]
    public void A_skipped_day_takes_nothing_away()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s => { GuildRow(s)["rank"] = 1; GuildRow(s)["given"] = 30; });
        h.Clock.Advance(TimeSpan.FromDays(10));
        var view = G(h);
        Assert.Equal(1, view.GetProperty("rank").GetInt32());
        Assert.Equal(30, view.GetProperty("given").GetInt64());
        Assert.Equal(2, view.GetProperty("day").GetProperty("potters").GetInt32());
    }

    // ---------- двоє друзів ----------

    [Fact]
    public void Two_friends_fill_one_wagon_and_each_claims_their_own_reward()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        GiveN(ola, "pot", 4);

        var seen = G(petro).GetProperty("day");
        Assert.Equal(4, seen.GetProperty("total").GetInt32());
        Assert.Equal(0, seen.GetProperty("mine").GetInt32());
        Assert.Equal("Оля", seen.GetProperty("givers")[0].GetProperty("nick").GetString());

        Fill(petro, g, "Петро", 1);
        Assert.True(Guild(petro, new { op = "claim" }).Ok);
        // Оля поклала лише чотири — ще один, і нагорода її.
        Assert.StartsWith("Нагорода — тим, хто поклав на віз хоч 5", Guild(ola, new { op = "claim" }).Message);
        GiveN(ola, "pot", 1);
        Assert.True(Guild(ola, new { op = "claim" }).Ok);
        var givers = G(ola).GetProperty("day").GetProperty("givers").EnumerateArray().Select(x => x.GetProperty("nick").GetString()!).ToList();
        Assert.Equal(["Оля", "Петро"], givers);                          // за абеткою, не за внеском
    }

    // ---------- дарунки ----------

    [Fact]
    public void A_gift_travels_to_a_friend_and_lands_on_the_gift_shelf()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        Items(ola, ("kumanets|kosiv|3", 2));
        // Петро сам уже може ліпити куманці й має косівський розпис — тоді дарунок відкриє йому клітинку альбому.
        Patch(petro, s => { s["total"] = 5_000_000_000; s["styles"] = new JsonArray("kosiv"); });
        petro.Clock.Advance(TimeSpan.FromMinutes(10));

        var r = Guild(ola, new { op = "gift", nick = "петро ", key = "kumanets|kosiv|3" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("петро", r.Message);
        Assert.Equal(1, ola.View(0).GetProperty("craft").GetProperty("items")[0].GetProperty("n").GetInt32());
        Assert.Equal(2, G(ola).GetProperty("gifts").GetProperty("left").GetInt32());

        // Пошту цеху кімната забирає лише на дії (див. SyncGuild) — вид сам по собі скриньки не чіпає.
        Assert.Equal(0, G(petro).GetProperty("shelf").GetArrayLength());
        Assert.True(petro.Act(0, "look").Ok);
        var view = petro.View(0);
        var shelf = view.GetProperty("guild").GetProperty("shelf");
        Assert.Equal(1, shelf.GetArrayLength());
        Assert.Equal("Оля", shelf[0].GetProperty("from").GetString());
        Assert.Equal("kumanets", shelf[0].GetProperty("ware").GetString());
        Assert.Equal(3, shelf[0].GetProperty("q").GetInt32());
        Assert.Contains(view.GetProperty("away").GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("Оля"));
        // У комору дарунок не йде — його не продаси.
        Assert.Equal(0, view.GetProperty("craft").GetProperty("items").GetArrayLength());
        // Зате відкриває клітинку альбому — без зірки: дзвінким його зробив не Петро.
        var row = Array.FindIndex(Clicker.Wares, w => w.Key == "kumanets");
        var col = 1 + Array.FindIndex(Clicker.Styles, s => s.Key == "kosiv");
        Assert.NotEqual(0, view.GetProperty("album").GetProperty("cells")[row].GetInt32() & (1 << col));
        Assert.Equal(0, view.GetProperty("album").GetProperty("stars")[row].GetInt32() & (1 << col));
        // Скринька порожня — вдруге не приходить.
        Assert.Equal(1, G(petro).GetProperty("shelf").GetArrayLength());
    }

    [Fact]
    public void Gifts_need_a_real_friend_and_are_limited_to_three_a_day()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        g.Potter("Петро");
        Items(ola, ("pot||1", 10));
        Assert.Equal("Собі дарувати — то вже не дарунок 🙂", Guild(ola, new { op = "gift", nick = "ОЛЯ", key = "pot||1" }).Message);
        Assert.StartsWith("Незнайко ще не сідав за гончарне коло", Guild(ola, new { op = "gift", nick = "Незнайко", key = "pot||1" }).Message);
        Assert.Equal("Кому дарувати? Обери гончаря", Guild(ola, new { op = "gift", nick = " ", key = "pot||1" }).Message);
        Assert.Equal("Такого виробу в коморі нема", Guild(ola, new { op = "gift", nick = "Петро", key = "bowl||1" }).Message);

        for (var i = 0; i < 3; i++) Assert.True(Guild(ola, new { op = "gift", nick = "Петро", key = "pot||1" }).Ok);
        Assert.StartsWith("Сьогодні вже 3 дарунки", Guild(ola, new { op = "gift", nick = "Петро", key = "pot||1" }).Message);
        Assert.Equal(7, ola.View(0).GetProperty("craft").GetProperty("items")[0].GetProperty("n").GetInt32());   // відмова виробу не з'їла

        // Київська північ — і знову можна (15:00 → 00:30 наступного дня за Києвом).
        ola.Clock.Advance(TimeSpan.FromHours(9.5));
        Assert.Equal(3, G(ola).GetProperty("gifts").GetProperty("left").GetInt32());
        Assert.True(Guild(ola, new { op = "gift", nick = "Петро", key = "pot||1" }).Ok);
    }

    [Fact]
    public void The_gift_shelf_keeps_the_last_twelve_and_ten_gifts_are_an_achievement()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        Items(ola, ("pot||1", 20), ("bowl||2", 1));
        for (var day = 0; day < 5; day++)
        {
            for (var i = 0; i < 3; i++)
                Assert.True(Guild(ola, new { op = "gift", nick = "Петро", key = day == 4 && i == 2 ? "bowl||2" : "pot||1" }).Ok);
            ola.Clock.Advance(TimeSpan.FromDays(1));
        }
        Assert.Single(ola.Awards, a => a.Reason == "ach:potter-gift");
        Assert.Equal(15, G(ola).GetProperty("gifts").GetProperty("sent").GetInt32());

        Assert.True(petro.Act(0, "look").Ok);
        var view = G(petro);
        Assert.Equal(ClickerGuildService.ShelfSize, view.GetProperty("shelf").GetArrayLength());
        Assert.Equal(15, view.GetProperty("gifts").GetProperty("got").GetInt32());
    }

    // ---------- хата друга ----------

    [Fact]
    public void The_house_snapshot_shows_only_public_things()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s =>
        {
            s["upgrades"]!["artel"] = 7;
            s["house"]!["decor"] = new JsonArray("towel", "dog", "unicorn");
            s["house"]!["tools"] = new JsonArray("paddle");
            s["styles"] = new JsonArray("kosiv", "gavarets");
            s["craft"]!["items"] = new JsonObject { ["pot||1"] = 5, ["kumanets|kosiv|3"] = 1 };
            s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 40, ["jug"] = 2 };
            var guild = GuildRow(s);
            guild["rank"] = 2;
            guild["shelf"] = new JsonArray(new JsonObject { ["from"] = "Петро", ["ware"] = "bowl", ["style"] = "", ["quality"] = 2, ["at"] = "2026-09-10T12:00:00+00:00" });
        });
        // Майстер питає — у збереженні з'являється ключ полиці Ока, і його в знімку бути не повинно.
        Patch(h, s => s["guard"]!["left"] = 0);
        for (var i = 0; i < 2; i++) { h.Act(0, "spin", PotterHands.Human(12)); h.Clock.Advance(1); }
        var shelfKey = PotterHands.Shelf(h);
        Assert.NotNull(shelfKey);

        string save;
        lock (h.Room.Sync) save = h.Room.Game.Save()!;
        // Альбом у збереженні — «виріб → список розписів» (ClickerAlbum.SaveAlbum): три клітинки на два вироби.
        var withAlbum = JsonNode.Parse(save)!.AsObject();
        withAlbum["album"] = new JsonObject { ["cells"] = new JsonObject { ["pot"] = new JsonArray("", "kosiv"), ["jug"] = new JsonArray("kosiv") } };
        save = withAlbum.ToJsonString();
        var snap = Views.Json(ClickerGuildService.HouseSnapshot("Оля", save));
        var text = snap.GetRawText();
        Assert.False(Views.Has(snap, "guard"));
        Assert.False(Views.Has(snap, "pots"));
        Assert.DoesNotContain(Convert.ToBase64String(shelfKey!), text);
        Assert.DoesNotContain("\"shelf\"", text);
        Assert.DoesNotContain("board", text);

        Assert.Equal("Оля", snap.GetProperty("nick").GetString());
        Assert.Equal(["dog", "towel"], snap.GetProperty("decor").EnumerateArray().Select(x => x.GetString()!).ToArray());   // «єдиноріг» відкинуто
        Assert.Equal(7, snap.GetProperty("ladder").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "artel").GetProperty("level").GetInt32());
        Assert.Equal(2, snap.GetProperty("styles").GetArrayLength());
        Assert.Equal(2, snap.GetProperty("rank").GetInt32());
        Assert.Equal(42, snap.GetProperty("fired").GetInt64());
        Assert.Equal(3, snap.GetProperty("album").GetInt32());
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("tiles").ValueKind);
        Assert.Equal("Петро", snap.GetProperty("gifts")[0].GetProperty("from").GetString());
        Assert.Equal("kumanets", snap.GetProperty("best")[0].GetProperty("ware").GetString());
    }

    [Fact]
    public void The_house_endpoint_reads_a_friends_save_or_says_there_is_none()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s => s["house"]!["decor"] = new JsonArray("icon"));
        g.Publish(h, "Оля");
        var snap = Views.Json(g.Svc.House(" ОЛЯ"));
        Assert.Equal("Оля", snap.GetProperty("nick").GetString());           // ім'я — як гончар сам себе звав
        Assert.Equal("icon", snap.GetProperty("decor")[0].GetString());

        Assert.Null(g.Svc.House("нема"));
        Assert.Null(g.Svc.House(""));
        Assert.Null(ClickerGuildService.HouseSnapshot("x", "{not json"));
        Assert.Null(ClickerGuildService.HouseSnapshot("x", "[1,2]"));
        Assert.Null(ClickerGuildService.HouseSnapshot("x", null));
        // Старе збереження без хати, ремесла й цеху — порожня, але ціла хата.
        var old = Views.Json(ClickerGuildService.HouseSnapshot("x", """{"pots":5,"total":77}"""));
        Assert.Equal(77, old.GetProperty("total").GetInt64());
        Assert.Equal(0, old.GetProperty("rank").GetInt32());
        Assert.Equal(0, old.GetProperty("decor").GetArrayLength());
    }

    [Fact]
    public void The_roster_lists_potters_alphabetically_with_todays_share()
    {
        var g = new Tsekh();
        g.Clock.UtcNow = Thursday;
        var petro = g.Potter("Петро");
        g.Potter("Оля");
        GiveN(petro, "pot", 3);
        var roster = Views.Json(g.Svc.Roster("петро"));
        var potters = roster.GetProperty("potters").EnumerateArray().ToList();
        Assert.Equal(["Оля", "Петро"], potters.Select(p => p.GetProperty("nick").GetString()!).ToArray());
        Assert.Equal(3, potters[1].GetProperty("gave").GetInt32());
        Assert.True(potters[1].GetProperty("me").GetBoolean());
        Assert.Equal(3, roster.GetProperty("day").GetProperty("total").GetInt32());
    }

    // ---------- похвала ----------

    [Fact]
    public void Bragging_goes_to_the_journal_at_most_every_fifteen_minutes_and_only_with_a_real_item()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Assert.Equal("Хвалитись можна лише тим, що лежить у коморі", Guild(h, new { op = "brag", key = "kumanets|kosiv|3" }).Message);
        Items(h, ("kumanets|kosiv|3", 1), ("makitra|bubnivka|2", 1), ("barrel||1", 1));
        Assert.True(Guild(h, new { op = "brag", key = "kumanets|kosiv|3" }).Ok);
        Assert.Contains("🏺 Оля хвалиться: дзвінкий куманець — косівський розпис", Journal(h));
        Assert.StartsWith("Дай селу надивуватись", Guild(h, new { op = "brag", key = "makitra|bubnivka|2" }).Message);

        h.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(Guild(h, new { op = "brag", key = "makitra|bubnivka|2" }).Ok);
        Assert.Contains("🏺 Оля хвалиться: добра макітра — бубнівський розпис", Journal(h));
        // Похвала виробу не забирає.
        Assert.Equal(3, h.View(0).GetProperty("craft").GetProperty("items").GetArrayLength());

        Patch(h, s => GuildRow(s)["rank"] = 3);
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(Guild(h, new { op = "brag", key = "barrel||1" }).Ok);
        Assert.Contains("🏺 Цехмістр Оля хвалиться: звичайне барило — без розпису, чиста глина", Journal(h));
    }

    // ---------- ранги ----------

    static JsonElement PieceOf(RoomHarness h) => G(h).GetProperty("next").GetProperty("piece");

    [Fact]
    public void A_masterpiece_is_refused_until_the_conditions_are_met()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        var r = Guild(h, new { op = "masterpiece" });
        Assert.StartsWith("До рангу «Челядник» ще: обпалити ще 50", r.Message);
        Assert.Contains("покласти на вози ще 10", r.Message);

        Patch(h, s => { s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 50 }; GuildRow(s)["given"] = 10; });
        Assert.True(G(h).GetProperty("next").GetProperty("ready").GetBoolean());
        var piece = PieceOf(h);
        Assert.Contains(piece.GetProperty("ware").GetString(), new[] { "jug", "makitra" });
        Assert.StartsWith("Цех чекає майстерштук: дзвінк", Guild(h, new { op = "masterpiece" }).Message);

        // Добрий не годиться — лише дзвінкий, розпис для челядника будь-який.
        var ware = piece.GetProperty("ware").GetString();
        Items(h, ($"{ware}|gavarets|2", 1));
        Assert.False(Guild(h, new { op = "masterpiece" }).Ok);
        Items(h, ($"{ware}|gavarets|3", 1));
        Assert.True(PieceOf(h).GetProperty("have").GetBoolean());
        r = Guild(h, new { op = "masterpiece" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, G(h).GetProperty("rank").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("craft").GetProperty("items").GetArrayLength());
        Assert.Contains(Journal(h), t => t.Contains("Оля") && t.Contains("челядник"));
        Assert.Equal(1, Views.Json(g.Svc.Roster(null)).GetProperty("potters")[0].GetProperty("rank").GetInt32());
    }

    [Fact]
    public void Climbing_to_guildmaster_is_an_achievement_and_the_top_is_the_top()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s =>
        {
            s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 3000 };
            s["styles"] = new JsonArray("gavarets", "vasylkiv", "bubnivka", "kosiv", "opishnia", "mezhyhirya");
            GuildRow(s)["given"] = 400;
        });
        for (var rank = 1; rank <= 3; rank++)
        {
            var piece = PieceOf(h);
            var style = piece.GetProperty("style").GetString();
            if (rank == 1) Assert.Equal("", style);
            else Assert.Contains(style, Clicker.GuildRanks[rank].PieceStyles);
            Assert.Contains(piece.GetProperty("ware").GetString(), Clicker.GuildRanks[rank].PieceWares);
            Items(h, ($"{piece.GetProperty("ware").GetString()}|{(style == "" ? "trypillia" : style)}|3", 1));
            var r = Guild(h, new { op = "masterpiece" });
            Assert.True(r.Ok, r.Message);
            Assert.Equal(rank, G(h).GetProperty("rank").GetInt32());
        }
        Assert.Single(h.Awards, a => a.Reason == "ach:potter-rank");
        Assert.Equal(JsonValueKind.Null, G(h).GetProperty("next").ValueKind);
        Assert.Equal("Ти вже цехмістр — вище в цеху лише небо", Guild(h, new { op = "masterpiece" }).Message);
    }

    [Fact]
    public void Masterpieces_differ_between_friends_but_stay_put_for_one_potter()
    {
        var g = new Tsekh();
        var pieces = new[] { "Оля", "Петро", "Микола", "Ганна", "Тарас", "Леся" }
            .Select(n => { var h = g.Potter(n); Patch(h, s => GuildRow(s)["rank"] = 2); return PieceOf(h).GetRawText(); })
            .ToList();
        Assert.True(pieces.Distinct().Count() > 1);
        var again = g.Potter("Оля");
        Patch(again, s => GuildRow(s)["rank"] = 2);
        Assert.Equal(pieces[0], PieceOf(again).GetRawText());
    }

    // ---------- перки ----------

    [Fact]
    public void A_master_opens_the_next_ware_a_step_early_and_gets_kiln_slots()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s => { s["total"] = 1_000; s["pots"] = 0; });
        Assert.StartsWith("Макітра відкриється на", h.Act(0, "form", new { ware = "makitra" }).Message);
        Assert.Equal(0, G(h).GetProperty("kilnSlots").GetInt32());

        Patch(h, s => GuildRow(s)["rank"] = 2);
        Assert.True(h.Act(0, "form", new { ware = "makitra" }).Ok);    // 10 тис → уже на 1 тис
        Assert.StartsWith("Полумисок відкриється на", h.Act(0, "form", new { ware = "dish" }).Message);   // лише на щабель
        var wares = h.View(0).GetProperty("craft").GetProperty("wares").EnumerateArray().ToDictionary(w => w.GetProperty("key").GetString()!, w => w.GetProperty("open").GetBoolean());
        Assert.True(wares["makitra"]);
        Assert.False(wares["dish"]);
        Assert.Equal(Clicker.MasterKilnSlots, G(h).GetProperty("kilnSlots").GetInt32());
    }

    [Fact]
    public void A_journeyman_has_an_auto_kiln_that_can_be_switched_off()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Assert.False(G(h).GetProperty("autoKiln").GetBoolean());
        Assert.Equal("Автогорно — перк челядника", Guild(h, new { op = "auto", on = true }).Message);
        Patch(h, s => GuildRow(s)["rank"] = 1);
        Assert.True(G(h).GetProperty("autoKiln").GetBoolean());
        Assert.True(Guild(h, new { op = "auto", on = false }).Ok);
        Assert.False(G(h).GetProperty("autoKiln").GetBoolean());
        Patch(h, _ => { });
        Assert.False(G(h).GetProperty("autoKiln").GetBoolean());          // вимикач переживає F5
        Assert.True(Guild(h, new { op = "auto", on = true }).Ok);
        Assert.True(G(h).GetProperty("autoKiln").GetBoolean());
    }

    // ---------- без сервісу, збереження, обпал ----------

    [Fact]
    public void Without_the_service_the_guild_is_closed_and_nothing_breaks()
    {
        var h = new RoomHarness("clicker");
        h.Solo("Оля");
        var view = G(h);
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Items(h, ("pot||1", 5));
        foreach (var op in new[] { "give", "claim", "gift", "brag", "masterpiece", "auto" })
            Assert.Equal("Цех зараз зачинений", h.Act(0, "guild", new { op, key = "pot||1", nick = "Петро" }).Message);
        Assert.True(h.Act(0, "spin", PotterHands.Human(5)).Ok);
        Assert.True(h.Act(0, "look", new { catalog = true }).Ok);
        Assert.Equal(JsonValueKind.Object, h.View(0).GetProperty("catalog").GetProperty("guild").ValueKind);
        // Ранг зі збереження однаково діє: майстер відкриває виріб раніше й без цеху.
        Patch(h, s => { s["total"] = 1_000; GuildRow(s)["rank"] = 2; });
        Assert.True(h.Act(0, "form", new { ware = "makitra" }).Ok);
        Assert.Equal(2, G(h).GetProperty("rank").GetInt32());
    }

    [Fact]
    public void The_guild_row_survives_a_reload_and_an_old_save_starts_as_an_apprentice()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s =>
        {
            var row = GuildRow(s);
            row["rank"] = 2; row["given"] = 123; row["claims"] = 4; row["giftsSent"] = 7; row["giftsGot"] = 2;
            row["bragAt"] = "2026-09-10T12:10:00+00:00";
            row["shelf"] = new JsonArray(
                new JsonObject { ["from"] = "Петро", ["ware"] = "bowl", ["style"] = "kosiv", ["quality"] = 3, ["at"] = "2026-09-10T11:00:00+00:00" },
                new JsonObject { ["from"] = "Хтось", ["ware"] = "vase", ["style"] = "", ["quality"] = 1, ["at"] = "2026-09-10T11:00:00+00:00" },
                new JsonObject { ["from"] = "Хтось", ["ware"] = "pot", ["style"] = "", ["quality"] = 7, ["at"] = "2026-09-10T11:00:00+00:00" });
        });
        var before = G(h).GetRawText();
        Assert.Equal(1, G(h).GetProperty("shelf").GetArrayLength());       // зіпсовані дарунки відкинуто
        Patch(h, _ => { });
        Assert.Equal(before, G(h).GetRawText());

        Patch(h, s => s.Remove("guild"));
        var old = G(h);
        Assert.Equal(0, old.GetProperty("rank").GetInt32());
        Assert.Equal(0, old.GetProperty("given").GetInt64());
        Assert.Equal(0, old.GetProperty("shelf").GetArrayLength());
        Patch(h, s => GuildRow(s)["rank"] = 99);
        Assert.Equal(3, G(h).GetProperty("rank").GetInt32());
    }

    [Fact]
    public void Firing_the_workshop_keeps_the_rank_the_shares_and_the_gift_shelf()
    {
        var g = new Tsekh();
        var h = g.Potter("Оля");
        Patch(h, s =>
        {
            s["total"] = 2_000_000_000;
            var row = GuildRow(s);
            row["rank"] = 2; row["given"] = 50; row["giftsSent"] = 3;
            row["shelf"] = new JsonArray(new JsonObject { ["from"] = "Петро", ["ware"] = "jug", ["style"] = "", ["quality"] = 2, ["at"] = "2026-09-10T11:00:00+00:00" });
        });
        var before = G(h);
        Assert.True(h.Act(0, "fire").Ok);
        var after = G(h);
        Assert.Equal(2, after.GetProperty("rank").GetInt32());
        Assert.Equal(50, after.GetProperty("given").GetInt64());
        Assert.Equal(3, after.GetProperty("gifts").GetProperty("sent").GetInt32());
        Assert.Equal(before.GetProperty("shelf").GetRawText(), after.GetProperty("shelf").GetRawText());
    }

    // ---------- сервіс: збереження й замок ----------

    [Fact]
    public void The_service_state_lives_in_the_store_under_its_key()
    {
        var g = new Tsekh();
        g.Svc.Give("оля", "Оля", "pot", 7, Thursday);
        g.Svc.Hello("оля", "Оля", 1, Thursday);
        Assert.True(g.Store.States.ContainsKey(ClickerGuildService.StoreKey));

        var again = new ClickerGuildService(g.Store, g.Clock);
        Assert.Equal(7, again.Summary("оля", Thursday).Today.Mine);
        Assert.Equal(1, Views.Json(again.Roster(null)).GetProperty("potters")[0].GetProperty("rank").GetInt32());

        // Зіпсований стан — цех з чистого, без падіння.
        g.Store.SaveState(ClickerGuildService.StoreKey, "{oops");
        Assert.Equal(0, new ClickerGuildService(g.Store, g.Clock).Summary("оля", Thursday).Today.Total);
    }

    [Fact]
    public void Hello_writes_only_when_something_changed()
    {
        var g = new Tsekh();
        g.Svc.Hello("оля", "Оля", 0, Thursday);
        var saves = g.Store.Saves;
        g.Svc.Hello("оля", "Оля", 0, Thursday.AddMinutes(5));
        Assert.Equal(saves, g.Store.Saves);
        g.Svc.Hello("оля", "Оля", 1, Thursday.AddMinutes(6));
        Assert.Equal(saves + 1, g.Store.Saves);
        Assert.Null(g.Svc.TakeMail("оля"));
        Assert.Equal(saves + 1, g.Store.Saves);                            // порожня скринька нічого не пише
    }

    [Fact]
    public void Old_days_are_pruned()
    {
        var g = new Tsekh();
        g.Svc.Give("оля", "Оля", "pot", 1, Thursday);
        g.Svc.Give("оля", "Оля", "pot", 1, Thursday.AddDays(5));
        var json = JsonNode.Parse(g.Store.States[ClickerGuildService.StoreKey])!;
        Assert.Single(json["days"]!.AsObject());
    }

    [Fact]
    public void Parallel_gives_and_gifts_do_not_lose_a_single_item()
    {
        var store = new SyncStore();
        store.SaveState("clicker:петро", "{}");
        var svc = new ClickerGuildService(store, new FakeClock());
        Parallel.For(0, 800, i =>
        {
            var nick = "гончар" + (i % 8);
            svc.Give(nick, nick, i % 2 == 0 ? "pot" : "bowl", 1, Thursday);
            if (i % 100 == 0) svc.Gift("дарувальник" + i / 100, "Дарувальник", "Петро", new ItemInfo("pot", "", 1), Thursday);
            svc.Summary(nick, Thursday);
        });
        var w = svc.Summary("гончар0", Thursday).Today;
        Assert.Equal(800, w.Total);
        Assert.Equal(8, w.Givers.Count);
        Assert.All(w.Givers, x => Assert.Equal(100, x.N));
        Assert.Equal(8, svc.TakeMail("петро")!.Count);
        Assert.Equal(800, new ClickerGuildService(store, new FakeClock()).Summary("x", Thursday).Today.Total);
    }

    /// <summary>Сховище, яке витримує паралельні записи (FakeStore — звичайний словник).</summary>
    sealed class SyncStore : IGameStore
    {
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _d = new();
        public void SaveState(string key, string json) => _d[key] = json;
        public string? LoadState(string key) => _d.TryGetValue(key, out var j) ? j : null;
        public void DeleteState(string key) => _d.TryRemove(key, out _);
    }

    [Fact]
    public void The_catalog_carries_ranks_tiers_and_words()
    {
        var g = new Tsekh();
        var h = new RoomHarness("clicker", services: RoomHarness.WithService(g.Svc));
        h.Solo("Оля");
        var cat = h.View(0).GetProperty("catalog").GetProperty("guild");
        Assert.Equal(4, cat.GetProperty("ranks").GetArrayLength());
        Assert.Equal("Цехмістр", cat.GetProperty("ranks")[3].GetProperty("name").GetString());
        Assert.Equal(35, cat.GetProperty("tiers")[3].GetProperty("minutes").GetInt32());
        Assert.Equal(ClickerGuildService.MinGive, cat.GetProperty("minGive").GetInt32());
        Assert.Equal("косівський розпис", cat.GetProperty("styleWords").GetProperty("kosiv").GetString());
        // Каталог — не у виді кожної пачки.
        Assert.True(h.Act(0, "spin", PotterHands.Human(3)).Ok);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("catalog").ValueKind);
    }
}
