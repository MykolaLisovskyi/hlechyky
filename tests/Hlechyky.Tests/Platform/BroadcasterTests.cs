using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Розкладка Outbox по адресатах. Перевіряємо чисту <see cref="Broadcaster.Plan"/> — саме вона вирішує, що
/// склеїти і кому що надіслати; сам SignalR у тестах не піднімається (TESTING.md §1).
/// </summary>
public class BroadcasterTests
{
    static List<Send> Plan(RoomHarness h, IReadOnlyList<Outgoing> messages, Dictionary<string, string>? conns = null)
    {
        var byConn = conns ?? [];
        return Broadcaster.Plan(
            messages,
            h.Rooms.Snapshot,
            h.Rooms.ViewsFor,
            id => byConn.TryGetValue(id, out var nick) ? nick : null,
            nick => [.. byConn.Where(p => string.Equals(p.Value, nick, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key)],
            (text, roomId) => new { text, roomId });
    }

    static RoomHarness Watched(string game, Dictionary<string, string> conns)
    {
        var h = new RoomHarness(game);
        h.Join("Оля");
        h.Join("Петро");
        foreach (var (conn, nick) in conns) h.Rooms.Watch(h.RoomId, conn, nick);
        return h;
    }

    /// <summary>Тіло повідомлення так, як воно піде на дріт.</summary>
    static JsonElement Body(Send send) => Views.Json(send.Payload);

    [Fact]
    public void Repeated_lobby_and_view_messages_collapse_into_one()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        var sends = Plan(h, [
            new LobbyChanged(), new RoomViews(h.RoomId), new LobbyChanged(),
            new RoomViews(h.RoomId), new Journal("рядок"), new LobbyChanged(),
        ]);

        Assert.Single(sends, s => s.Event == "rooms");
        Assert.Single(sends, s => s.Event == "chat");
        Assert.Single(sends, s => s.Event == "room");   // ніхто не дивиться → лише вид групі
    }

    [Fact]
    public void Only_the_last_frame_of_a_room_survives()
    {
        var h = new RoomHarness("snake");
        h.Join("Оля");
        h.Join("Петро");
        var sends = Plan(h, [new RoomFrame(h.RoomId, new { n = 1 }), new RoomFrame(h.RoomId, new { n = 2 })]);

        var frame = Assert.Single(sends, s => s.Event == "frame");
        Assert.Equal(new ToGroup("room:" + h.RoomId), frame.Target);
        Assert.Equal(h.RoomId, Body(frame).GetProperty("id").GetString());
        Assert.Equal(2, Body(frame).GetProperty("f").GetProperty("n").GetInt32());
    }

    [Fact]
    public void Hidden_game_gives_every_seat_its_own_view_and_watchers_the_public_one()
    {
        var conns = new Dictionary<string, string> { ["c-оля"] = "Оля", ["c-петро"] = "Петро", ["c-глядач"] = "Ганна" };
        var h = Watched("t-hidden", conns);
        var sends = Plan(h, [new RoomViews(h.RoomId)], conns);

        var personal = sends.Where(s => s.Target is ToConnections).ToList();
        Assert.Equal(2, personal.Count);
        var secrets = personal.Select(s => Body(s).GetProperty("view").GetProperty("secret").GetString()).Order().ToList();
        Assert.Equal(["карта-0", "карта-1"], secrets);

        var group = Assert.Single(sends, s => s.Target is ToGroupExcept);
        var except = ((ToGroupExcept)group.Target).Except;
        Assert.Equal(2, except.Count);
        Assert.DoesNotContain("c-глядач", except);
        Assert.Equal(JsonValueKind.Null, Body(group).GetProperty("view").GetProperty("secret").ValueKind);
    }

    [Fact]
    public void Seated_player_sees_his_seat_number_and_a_watcher_sees_null()
    {
        var conns = new Dictionary<string, string> { ["c-петро"] = "Петро", ["c-глядач"] = "Ганна" };
        var h = Watched("ttt", conns);
        var sends = Plan(h, [new RoomViews(h.RoomId)], conns);

        var mine = Assert.Single(sends, s => s.Target is ToConnections);
        Assert.Equal(1, Body(mine).GetProperty("seat").GetInt32());
        Assert.Equal("ttt", Body(mine).GetProperty("room").GetProperty("game").GetString());

        var group = Assert.Single(sends, s => s.Target is ToGroupExcept);
        Assert.Equal(JsonValueKind.Null, Body(group).GetProperty("seat").ValueKind);
        Assert.Equal(["c-петро"], ((ToGroupExcept)group.Target).Except);
    }

    [Fact]
    public void Wallet_achievement_and_toast_go_to_every_connection_of_the_nick()
    {
        var conns = new Dictionary<string, string> { ["c1"] = "Оля", ["c2"] = "оля", ["c3"] = "Петро" };
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        var sends = Plan(h, [
            new WalletChanged("Оля", 42, 5, "win:ttt", "за перемогу"),
            new AchievementUnlocked("Оля", "first-win", "Перша перемога", "текст", "🏅", 10),
            new ToastFor("Петро", "ставку повернуто", "ok"),
        ], conns);

        var wallet = Assert.Single(sends, s => s.Event == "wallet");
        Assert.Equal(["c1", "c2"], ((ToConnections)wallet.Target).Ids.Order());
        Assert.Equal(42, Body(wallet).GetProperty("balance").GetInt32());

        var achievement = Assert.Single(sends, s => s.Event == "achievement");
        Assert.Equal("first-win", Body(achievement).GetProperty("key").GetString());

        var toast = Assert.Single(sends, s => s.Event == "toast");
        Assert.Equal(["c3"], ((ToConnections)toast.Target).Ids);
        Assert.Equal("ok", Body(toast).GetProperty("kind").GetString());
    }

    [Fact]
    public void Dj_lines_are_not_planned_here_and_a_gone_room_sends_nothing()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        var sends = Plan(h, [new DjSays("а зараз буде щось таке"), new RoomViews("00000000")]);

        Assert.Empty(sends);
    }

    [Fact]
    public void Posted_messages_from_services_are_not_lost()
    {
        // Перевіряємо саме чергу Broadcaster'а, тому решта залежностей йому тут і не потрібна.
        var b = new Broadcaster(null!, null!, null!, null!, null!, null!, NullLogger<Broadcaster>.Instance);
        b.Post(new WalletChanged("Оля", 1, 1, "listen", "за слухання"));
        b.Post(new LobbyChanged());

        var alone = b.Drain([]);                       // своїх повідомлень нема, а чергу однаково злито
        Assert.Equal(2, alone.Count);
        Assert.Empty(b.Drain([]));                     // двічі та сама пачка не летить

        b.Post(new LobbyChanged());
        var mixed = b.Drain([new Journal("рядок")]);
        Assert.Equal(2, mixed.Count);
        Assert.IsType<Journal>(mixed[0]);              // своє йде першим, черга сервісів — слідом
    }

    [Fact]
    public void A_failing_chat_write_costs_only_its_own_line()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        var sends = Broadcaster.Plan(
            [new Journal("рядок"), new LobbyChanged(), new RoomViews(h.RoomId)],
            h.Rooms.Snapshot, h.Rooms.ViewsFor, _ => null, _ => [],
            (_, _) => throw new InvalidOperationException("база зайнята"));

        Assert.DoesNotContain(sends, s => s.Event == "chat");
        Assert.Single(sends, s => s.Event == "rooms");   // решта пачки летить як летіла
        Assert.Single(sends, s => s.Event == "room");
    }

    [Fact]
    public void A_broken_game_does_not_swallow_the_whole_batch()
    {
        var h = new RoomHarness("t-badseat");
        h.Join("Оля");
        var sends = Plan(h, [new LobbyChanged(), new RoomViews(h.RoomId), new Journal("рядок")]);

        Assert.Single(sends, s => s.Event == "rooms");
        Assert.Single(sends, s => s.Event == "chat");
    }

    [Fact]
    public void The_lobby_snapshot_goes_out_before_the_line_that_talks_about_it()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        // Так це й лежить в Outbox після Join: спершу рядок зі StartRound, потім LobbyChanged.
        var sends = Plan(h, [new Journal("сіли грати", h.RoomId), new LobbyChanged()]);

        Assert.Equal("rooms", sends[0].Event);
        Assert.True(sends.FindIndex(s => s.Event == "rooms") < sends.FindIndex(s => s.Event == "chat"));
    }

    [Fact]
    public void A_journal_line_carries_the_table_it_is_about()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        var sends = Plan(h, [new Journal("Оля поставила стіл", h.RoomId)]);

        var chat = Assert.Single(sends, s => s.Event == "chat");
        Assert.Equal(h.RoomId, Body(chat).GetProperty("roomId").GetString());
    }

    [Fact]
    public void An_invite_goes_to_everyone_with_the_nick_that_calls()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        var sends = Plan(h, [new Invite(h.RoomId, "Оля", "Оля кличе в тестову компанію")]);

        var invite = Assert.Single(sends, s => s.Event == "invite");
        Assert.Equal(new ToAll(), invite.Target);
        Assert.Equal(h.RoomId, Body(invite).GetProperty("roomId").GetString());
        Assert.Equal("Оля", Body(invite).GetProperty("by").GetString());
        Assert.Equal("Оля кличе в тестову компанію", Body(invite).GetProperty("text").GetString());
    }

    [Fact]
    public void The_deferred_outbox_finds_the_broadcaster_only_on_the_first_message()
    {
        // Скринька, яку каркас дає сервісам, не сміє тягнути Broadcaster під час побудови графа:
        // Rooms просить IStakes (економіка), економіка — IOutbox, Broadcaster — знову Rooms.
        // На такому колі контейнер завмирає ще до того, як Kestrel відкриє порт.
        var calls = 0;
        var real = new FakeOutbox();
        var outbox = new DeferredOutbox(() => { calls++; return real; });

        Assert.Equal(0, calls);
        outbox.Post(new Journal("перший"));
        outbox.Post(new Journal("другий"));

        Assert.Equal(1, calls);
        Assert.Equal(2, real.Of<Journal>().Count);
    }
}
