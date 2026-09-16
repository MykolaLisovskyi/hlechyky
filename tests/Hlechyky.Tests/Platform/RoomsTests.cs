using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Життєвий цикл кімнати: місця, старт, ходи, «ще раз», техпоразка, ставки, прибирання (TESTING.md §3).</summary>
public class RoomsTests
{
    static Rooms New(FakeClock clock, FakeStakes? stakes = null, FakeStore? store = null, GameEvents? events = null) =>
        new(RoomHarness.NewRegistry(), clock, events ?? new GameEvents(), stakes ?? new FakeStakes(),
            store ?? new FakeStore(), RoomHarness.Empty()) { SeedOverride = 7 };

    // ---------- створення і місця ----------

    [Fact]
    public void Create_seats_the_host_and_shows_the_room_in_the_lobby()
    {
        var h = new RoomHarness("ttt");
        Assert.True(h.Join("Оля").Ok);

        Assert.Equal("Оля", h.Room.Seats[0]);
        Assert.Equal("Оля", h.Room.Host);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Single(h.Rooms.Snapshot());
        Assert.Contains(h.Outbox, m => m is LobbyChanged);
    }

    [Fact]
    public void Private_solo_room_stays_out_of_the_lobby()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");

        Assert.Empty(h.Rooms.Snapshot());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // Immediate стартує одразу
    }

    [Fact]
    public void Second_player_starts_a_when_full_game_once_and_writes_the_journal()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);     // ByHost сам не стартує

        var ttt = new RoomHarness("ttt");
        ttt.Join("Оля");
        ttt.Join("Петро");
        Assert.Equal(RoomStatus.Playing, ttt.Room.Status);
        // Два рядки: «Новий стіл» від Create і «сіли грати» від старту — і обидва з id столу, щоб у
        // Журналі біля них була кнопка (docs/redesign/PLAN.md §7.4).
        var journal = ttt.Outbox.OfType<Journal>().ToList();
        Assert.Equal(2, journal.Count);
        Assert.Equal("Новий стіл: Хрестики-нолики (2) · господар Оля", journal[0].Text);
        Assert.Equal("Оля і Петро сіли грати в хрестики-нолики", journal[1].Text);
        Assert.All(journal, line => Assert.Equal(ttt.RoomId, line.RoomId));
    }

    [Fact]
    public void One_nick_holds_one_multiplayer_seat_but_solo_does_not_count()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var first = rooms.Create("Оля", "ttt", null);
        Assert.True(first.Reply.Ok);

        var second = rooms.Create("Оля", "c4", null);
        Assert.False(second.Reply.Ok);
        Assert.Equal("Ти вже за столом. Встань, якщо хочеш новий", second.Reply.Message);

        Assert.True(rooms.OpenSolo("Оля", "t-solo", null).Reply.Ok);   // соло не заважає
    }

    [Fact]
    public void Room_limit_holds_at_twelve()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        for (var i = 0; i < Rooms.MaxRooms; i++) Assert.True(rooms.Create($"гравець{i}", "ttt", null).Reply.Ok);
        var over = rooms.Create("зайвий", "ttt", null);
        Assert.False(over.Reply.Ok);
        Assert.Equal("Столів уже задосить, дограйте ті, що є", over.Reply.Message);
    }

    [Fact]
    public void Joining_a_full_room_and_an_unknown_game_are_refused()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal("Місць уже нема", h.Rooms.Join(h.RoomId, "Третій").Reply.Message);
        Assert.Equal("Ти вже в цій кімнаті", h.Rooms.Join(h.RoomId, "Оля").Reply.Message);
        Assert.Equal("Такої гри тут нема", h.Rooms.Create("Хтось", "не-гра", null).Reply.Message);
        Assert.Equal("Такої кімнати вже нема", h.Rooms.Join("00000000", "Хтось").Reply.Message);
        Assert.Equal("Спершу скажи, як тебе кликати", h.Rooms.Create("гість", "ttt", null).Reply.Message);
    }

    [Fact]
    public void Someone_elses_private_room_is_invisible_and_unwatchable()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");

        Assert.Equal("Такої кімнати вже нема", h.Rooms.Join(h.RoomId, "Петро").Reply.Message);
        Assert.Empty(h.Rooms.Watch(h.RoomId, "conn-петро", "Петро"));
        Assert.Single(h.Rooms.Watch(h.RoomId, "conn-оля", "Оля"));
    }

    // ---------- старт господарем ----------

    [Fact]
    public void By_host_start_needs_the_host_and_enough_players()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        Assert.Equal("Замало гравців, треба щонайменше 2", h.Rooms.StartByHost(h.RoomId, "Оля").Reply.Message);

        h.Join("Петро");
        Assert.Equal("Почати може лише господар", h.Rooms.StartByHost(h.RoomId, "Петро").Reply.Message);

        Assert.True(h.Start().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(1, ((TestParty)h.Room.Game).Starts);
    }

    // ---------- ходи ----------

    [Fact]
    public void Act_is_refused_out_of_turn_before_start_and_after_finish()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        Assert.Equal("Чекаємо на гравців", h.Act(0, "move", new { cell = 0 }).Message);

        h.Join("Петро");
        Assert.Equal("Зараз не твій хід", h.Act(1, "move", new { cell = 0 }).Message);
        Assert.Equal("Ти тут не граєш", h.Rooms.Act(h.RoomId, "Чужий", "move", Views.Payload(new { cell = 0 })).Reply.Message);

        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) }) h.Act(seat, "move", new { cell });
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Партію зіграно, тисни «Ще раз»", h.Act(1, "move", new { cell = 8 }).Message);
    }

    [Fact]
    public void Failed_act_leaves_the_view_untouched()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Act(0, "move", new { cell = 4 });
        var before = Views.Text(h.Room.Game.View(0));

        Assert.False(h.Act(1, "move", new { cell = 4 }).Ok);
        Assert.Equal(before, Views.Text(h.Room.Game.View(0)));
        Assert.Equal(1, h.Room.Moves);
    }

    [Fact]
    public void Oversized_payload_never_reaches_the_game()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        var fat = new { text = new string('щ', Rooms.MaxPayloadBytes) };

        var r = h.Rooms.Act(h.RoomId, "Оля", "say", Views.Payload(fat));
        Assert.False(r.Reply.Ok);
        Assert.Equal(0, ((TestParty)h.Room.Game).Acts);
    }

    // ---------- завершення ----------

    [Fact]
    public void Finish_sets_the_result_writes_the_journal_and_raises_one_event()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        h.Act(0, "win");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.False(h.Room.Result.Draw);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "компанія: виграв Оля");
        var e = Assert.Single(h.Finished);
        Assert.Equal("t-party", e.GameId);
        Assert.Equal(1, e.Moves);
        Assert.Equal(["Оля", "Петро", null, null], e.Seats);
    }

    [Fact]
    public void Second_finish_in_the_same_round_is_ignored()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        var game = (TestParty)h.Room.Game;
        h.Act(0, "win");
        var ctx = game.Ctx;
        ctx.Finish([1], "друга спроба");

        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Game_that_throws_in_act_finishes_the_room_in_a_draw()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Start();

        Assert.False(h.Act(0, "boom").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Contains("вибачте", h.Room.Result.Text);
    }

    // ---------- ще раз ----------

    [Fact]
    public void Rematch_rotates_seats_bumps_the_round_and_starts_again()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        h.Act(0, "win");

        Assert.True(h.Rematch("Петро").Ok);
        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.Equal("Оля", h.Room.Seats[1]);
        Assert.Equal(2, h.Room.Round);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, ((TestParty)h.Room.Game).Starts);
    }

    [Fact]
    public void Rematch_before_the_end_is_refused()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal("Партія ще не скінчилась", h.Rooms.Rematch(h.RoomId, "Оля").Reply.Message);
        Assert.Equal("Ти тут не граєш", h.Rooms.Rematch(h.RoomId, "Чужий").Reply.Message);
    }

    // ---------- вихід ----------

    [Fact]
    public void Leaving_mid_game_is_a_technical_loss_and_leaving_the_lobby_just_frees_the_seat()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Leave("Оля");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("Хрестики-нолики: Оля встав з-за столу, партію не дограли", h.Room.Result.Text);
        Assert.Null(h.Room.Seats[0]);
        Assert.Equal("Петро", h.Room.Host);
        Assert.Equal(["Оля", "Петро"], h.Finished.Single().Seats);   // рейтинг має бачити, хто саме програв

        var lobby = new RoomHarness("t-party");
        lobby.Join("Оля");
        lobby.Join("Петро");
        lobby.Leave("Петро");
        Assert.Equal(RoomStatus.Lobby, lobby.Room.Status);
        Assert.Null(lobby.Room.Seats[1]);
    }

    [Fact]
    public void Empty_room_disappears_at_once()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        var id = h.RoomId;
        h.Leave("Оля");

        Assert.Null(h.Rooms.Find(id));
        Assert.Empty(h.Rooms.Snapshot());
    }

    // ---------- присутність ----------

    [Fact]
    public void Seat_survives_the_grace_period_and_is_freed_after_it()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromSeconds(19));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        Assert.Equal("Петро", h.Room.Seats[1]);

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        Assert.Null(h.Room.Seats[1]);
    }

    [Fact]
    public void Coming_back_before_the_grace_ends_keeps_the_seat()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Rooms.NoteOnline("Петро");

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        Assert.Equal("Петро", h.Room.Seats[1]);
    }

    [Fact]
    public void Renaming_frees_the_seat_immediately()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");
        h.Join("Петро");
        h.Rooms.DropNick("Петро");
        Assert.Null(h.Room.Seats[1]);
    }

    // ---------- прибирання ----------

    [Fact]
    public void Housekeeping_clears_stale_lobbies_and_old_finished_rooms()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var lonely = rooms.Create("Оля", "ttt", null).Reply.RoomId!;

        clock.Advance(TimeSpan.FromMinutes(31));
        rooms.Housekeeping(clock.UtcNow);
        Assert.Null(rooms.Find(lonely));

        var played = rooms.Create("Оля", "ttt", null).Reply.RoomId!;
        rooms.Join(played, "Петро");
        rooms.Leave(played, "Оля");                       // техпоразка → Finished, Петро ще сидить
        Assert.Equal(RoomStatus.Finished, rooms.Find(played)!.Status);
        clock.Advance(TimeSpan.FromMinutes(16));
        rooms.Housekeeping(clock.UtcNow);
        Assert.Null(rooms.Find(played));
    }

    // ---------- ставки ----------

    [Fact]
    public void Stake_is_refused_to_the_poor_charged_at_start_and_paid_to_the_winner()
    {
        var clock = new FakeClock();
        var stakes = new FakeStakes().Set("Оля", 20).Set("Петро", 3);
        var rooms = New(clock, stakes);

        var id = rooms.Create("Оля", "ttt", new Dictionary<string, string> { ["stake"] = "5" }).Reply.RoomId!;
        Assert.Equal(5, rooms.Find(id)!.Stake);
        Assert.Equal("Бракує черепків на ставку", rooms.Join(id, "Петро").Reply.Message);

        stakes.Set("Петро", 30);
        Assert.True(rooms.Join(id, "Петро").Reply.Ok);
        Assert.Equal(15, stakes.Balance("Оля"));
        Assert.Equal(25, stakes.Balance("Петро"));

        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) })
            rooms.Act(id, seat == 0 ? "Оля" : "Петро", "move", Views.Payload(new { cell }));

        Assert.Equal(RoomStatus.Finished, rooms.Find(id)!.Status);
        Assert.Equal(25, stakes.Balance("Оля"));          // 15 + банк 10
        Assert.Equal(25, stakes.Balance("Петро"));
    }

    [Fact]
    public void Draw_returns_the_stake_to_everyone()
    {
        var h = new RoomHarness("t-duel", new { stake = 5 });
        h.Stakes.Set("Оля", 10).Set("Петро", 10);
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(5, h.Stakes.Balance("Оля"));

        h.Act(0, "draw");
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal(10, h.Stakes.Balance("Оля"));
        Assert.Equal(10, h.Stakes.Balance("Петро"));
    }

    [Fact]
    public void Game_that_finishes_twice_pays_the_bank_once()
    {
        var h = new RoomHarness("t-duel", new { stake = 25 });
        h.Stakes.Set("Оля", 25).Set("Петро", 25);
        h.Join("Оля");
        h.Join("Петро");

        h.Act(0, "double");
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("дуель: перший раз", h.Room.Result.Text);
        Assert.Equal(50, h.Stakes.Balance("Оля"));
        Assert.Equal(0, h.Stakes.Balance("Петро"));
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Stakes_are_only_for_rated_pairs()
    {
        var h = new RoomHarness("t-party", new { stake = 25 });
        h.Stakes.Set("Оля", 100);
        h.Join("Оля");
        Assert.Equal(0, h.Room.Stake);
    }

    [Fact]
    public void Rematch_charges_the_stake_again()
    {
        var clock = new FakeClock();
        var stakes = new FakeStakes().Set("Оля", 20).Set("Петро", 20);
        var rooms = New(clock, stakes);
        var id = rooms.Create("Оля", "ttt", new Dictionary<string, string> { ["stake"] = "5" }).Reply.RoomId!;
        rooms.Join(id, "Петро");
        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) })
            rooms.Act(id, seat == 0 ? "Оля" : "Петро", "move", Views.Payload(new { cell }));
        var afterFirst = stakes.Balance("Оля");

        rooms.Rematch(id, "Оля");
        Assert.Equal(afterFirst - 5, stakes.Balance("Оля"));
        Assert.Equal(2, rooms.Find(id)!.Round);
    }

    // ---------- соло і збереження ----------

    [Fact]
    public void Open_solo_returns_the_same_room_and_restores_the_saved_state()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        var first = h.RoomId;
        h.Act(0, "add", new { v = 7 });
        Assert.True(h.Store.Saves > 0);

        Assert.Equal(first, h.Rooms.OpenSolo("Оля", "t-solo", null).Reply.RoomId);

        h.Leave("Оля");                                   // кімната зникла, стан лишився в сховищі
        h.Solo("Оля");
        Assert.Equal(7, h.View(0).GetProperty("value").GetInt64());
    }

    [Fact]
    public void Solo_score_and_award_reach_the_services()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        h.Act(0, "add", new { v = 42 });
        h.Act(0, "award");
        h.Act(0, "done");

        var score = Assert.Single(h.Scores);
        Assert.Equal(42, score.Score);
        Assert.Equal(ScoreOrder.HigherIsBetter, score.Order);
        Assert.Equal(h.Room.Key, score.Key);
        var award = Assert.Single(h.Awards);
        Assert.Equal(3, award.Shards);
    }

    [Fact]
    public void An_award_of_zero_shards_still_reaches_the_services()
    {
        // Домовленість ARCHITECTURE §4.3/§8: Award(seat, 0, "ach:<key>") — це прохання видати ачівку,
        // якої платформа сама не побачить. Відсікати нуль каркасові не можна, інакше мафія й Ерудит
        // мовчки лишаються без своїх ачівок.
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        h.Act(0, "ach");

        var award = Assert.Single(h.Awards);
        Assert.Equal(0, award.Shards);
        Assert.Equal("ach:test-key", award.Reason);
        Assert.Equal("Оля", award.Nick);
    }

    // ---------- види ----------

    [Fact]
    public void Views_for_broadcast_differ_per_seat_in_hidden_games()
    {
        var h = new RoomHarness("t-hidden");
        h.Join("Оля");
        h.Join("Петро");
        var b = h.Rooms.ViewsFor(h.RoomId)!;

        Assert.True(b.Hidden);
        Assert.Equal(0, b.SeatOf("оля"));
        Assert.Equal(1, b.SeatOf("Петро"));
        Assert.Null(b.SeatOf("Глядач"));
        Assert.NotEqual(Views.Text(b.SeatViews[0]), Views.Text(b.SeatViews[1]));
        Assert.DoesNotContain("карта", Views.Text(b.WatcherView));
    }

    [Fact]
    public void Room_summary_matches_the_protocol()
    {
        var h = new RoomHarness("c4");
        h.Join("Оля");
        h.Join("Петро");
        h.Rooms.Watch(h.RoomId, "conn-глядач", "Ганна");
        var json = Views.Json(h.Rooms.Snapshot()[0]);

        Assert.Equal("c4", json.GetProperty("game").GetString());
        Assert.Equal(1, json.GetProperty("watchers").GetInt32());
        Assert.Equal("playing", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("seats").GetArrayLength());
        Assert.Equal("Оля", json.GetProperty("seats")[0].GetProperty("nick").GetString());
        Assert.Equal("жовті", json.GetProperty("seatNames")[0].GetString());
        Assert.Equal("Оля", json.GetProperty("host").GetString());
        Assert.Equal(0, json.GetProperty("stake").GetInt32());
        Assert.Equal(1, json.GetProperty("round").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("result").ValueKind);
        Assert.False(Views.Has(json, "charged"));
    }

    /// <summary>
    /// Стіл після техпоразки лишається дограним із вільним місцем — і його має підхопити будь-хто ззовні,
    /// інакше він висить у лобі до прибиральника. Кнопку «Сісти» клієнт малює саме за цим станом
    /// (<c>core.js</c>, <c>btnsHtml</c>): статус там не питають, бо сервер його сам скидає в лобі.
    /// </summary>
    [Fact]
    public void A_table_left_after_a_technical_loss_seats_a_newcomer()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Leave("Петро");                                  // техпоразка, місце ◯ звільнилось

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, h.Room.FreeSeat);                  // сервер тримає стіл відкритим
        // і в лобі цей стіл видно з вільним місцем — саме з цього знімка клієнт вирішує, чи малювати «Сісти»
        var json = Views.Json(h.Rooms.Snapshot().Single());
        Assert.Equal("finished", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("seats")[1].GetProperty("nick").ValueKind);

        var join = h.Rooms.Join(h.RoomId, "Ганна");         // глядач, який щойно бачив кінець партії
        Assert.True(join.Reply.Ok);
        Assert.Equal("Сів за ◯", join.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);    // WhenFull → партія почалась одразу
        Assert.Equal(2, h.Room.Round);
        Assert.Null(h.Room.Result);
    }

    // ---------- відмова нічого не псує ----------

    [Fact]
    public void A_refused_join_leaves_a_finished_room_exactly_as_it_was()
    {
        var h = new RoomHarness("t-duel", new { stake = 5 });
        h.Stakes.Set("Оля", 10).Set("Петро", 10);
        h.Join("Оля");
        h.Join("Петро");
        h.Act(0, "win");
        h.Leave("Петро");                                  // стіл лишився дограним, місце вільне

        var refused = h.Rooms.Join(h.RoomId, "Голодранець");
        Assert.Equal("Бракує черепків на ставку", refused.Reply.Message);
        Assert.Empty(refused.Out);                         // ніхто нічого не перемальовує
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, h.Room.Round);
        Assert.Equal("дуель: виграв Оля", h.Room.Result!.Text);

        // а той, у кого черепки є, стіл таки відкриває наново
        h.Stakes.Set("Багатій", 50);
        Assert.True(h.Rooms.Join(h.RoomId, "Багатій").Reply.Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, h.Room.Round);
    }

    [Fact]
    public void Start_that_did_not_happen_rolls_the_room_back()
    {
        var h = new RoomHarness("t-duel", new { stake = 5 });
        h.Stakes.Set("Оля", 5).Set("Петро", 100);
        h.Join("Оля");
        h.Join("Петро");
        h.Act(1, "win");                                   // Оля лишилась без черепків, Петро забрав банк
        h.Leave("Петро");
        Assert.Equal(0, h.Stakes.Balance("Оля"));

        h.Stakes.Set("Ганна", 100);
        var refused = h.Rooms.Join(h.RoomId, "Ганна");      // сама Ганна багата, а от Оля вже ні
        Assert.Equal("Бракує черепків на ставку", refused.Reply.Message);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, h.Room.Round);
        Assert.Equal("дуель: виграв Петро", h.Room.Result!.Text);
        Assert.Null(h.Room.Seats[1]);
    }

    [Fact]
    public void A_game_that_throws_in_configure_is_refused_politely()
    {
        var h = new RoomHarness("ttt");
        var reply = h.Rooms.Create("Оля", "t-badconfig", null).Reply;

        Assert.False(reply.Ok);
        Assert.Equal("Такої гри тут нема", reply.Message);
        Assert.Empty(h.Rooms.Snapshot());
    }

    [Fact]
    public void A_broken_seat_name_does_not_take_the_lobby_down()
    {
        var h = new RoomHarness("t-badseat");
        h.Join("Оля");
        var good = h.Rooms.Create("Петро", "ttt", null).Reply.RoomId!;

        var lobby = h.Rooms.Snapshot();
        Assert.Equal(2, lobby.Count);
        Assert.Contains(lobby, r => r.Id == good);
        Assert.Equal(["єдина", "другий"], h.Rooms.Snapshot().First(r => r.Id == h.RoomId).SeatNames);
        Assert.NotNull(h.Rooms.ViewsFor(h.RoomId));
    }

    // ---------- соло ----------

    [Fact]
    public void Leaving_a_solo_room_says_nothing_to_the_chat_and_ends_no_game()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        var id = h.RoomId;
        h.Rooms.Leave(id, "Оля");

        Assert.Null(h.Rooms.Find(id));
        Assert.Empty(h.Finished);
        Assert.DoesNotContain(h.Outbox, m => m is Journal);
    }

    [Fact]
    public void Solo_room_survives_a_disconnect_and_dies_only_of_old_age()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");
        var id = h.RoomId;

        h.Rooms.NoteOffline("Оля", h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(25));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        Assert.NotNull(h.Rooms.Find(id));                  // приватна головоломка живе далі
        Assert.Empty(h.Finished);

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.Null(h.Rooms.Find(id));
    }

    // ---------- Журнал ----------

    [Fact]
    public void Every_new_crew_is_announced_and_a_rematch_stays_quiet()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");
        h.Leave("Оля");                                    // техпоразка, місце 0 вільне
        h.Join("Ганна");                                   // нова пара за тим самим столом

        Assert.Equal(2, h.Outbox.OfType<Journal>().Count(j => j.Text.Contains("сіли грати")));

        Assert.Equal("Партія ще не скінчилась", h.Rooms.Rematch(h.RoomId, "Петро").Reply.Message);

        foreach (var (seat, cell) in new[] { (0, 0), (1, 3), (0, 1), (1, 4), (0, 2) }) h.Act(seat, "move", new { cell });
        var outcome = h.Rooms.Rematch(h.RoomId, "Петро");
        Assert.True(outcome.Reply.Ok);
        Assert.DoesNotContain(outcome.Out.OfType<Journal>(), j => j.Text.Contains("сіли грати"));
    }

    // ---------- глядачі ----------

    [Fact]
    public void Watch_counts_once_and_unwatch_takes_it_back()
    {
        var h = new RoomHarness("ttt");
        h.Join("Оля");
        h.Join("Петро");

        Assert.Single(h.Rooms.Watch(h.RoomId, "c1", "Оля"));
        Assert.Empty(h.Rooms.Watch(h.RoomId, "c1", "Оля"));   // повтор нічого не розсилає
        Assert.Equal(1, h.Rooms.Snapshot()[0].Watchers);

        h.Rooms.Unwatch(h.RoomId, "c1");
        Assert.Equal(0, h.Rooms.Snapshot()[0].Watchers);
    }

    [Fact]
    public void Dropped_connection_leaves_no_watcher_behind_in_any_room()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var a = rooms.Create("Оля", "ttt", null).Reply.RoomId!;
        var b = rooms.Create("Петро", "c4", null).Reply.RoomId!;
        rooms.Watch(a, "c1", "Ганна");
        rooms.Watch(b, "c1", "Ганна");
        Assert.All(rooms.Snapshot(), r => Assert.Equal(1, r.Watchers));

        rooms.DropWatcher("c1");
        Assert.All(rooms.Snapshot(), r => Assert.Equal(0, r.Watchers));
    }

    // ---------- ходи й ставки ----------

    [Fact]
    public void Realtime_input_through_act_is_not_a_move()
    {
        var h = new RoomHarness("snake");
        h.Join("Оля");
        h.Join("Петро");

        var before = h.Outbox.Count;
        for (var i = 0; i < 10; i++) Assert.True(h.Act(i % 2, "turn", new { dir = i % 2 == 0 ? 1 : 3 }).Ok);
        Assert.Equal(before, h.Outbox.Count);              // види з тика, а не з кожного повороту

        h.Tick(400);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(0, h.Finished.Single().Moves);        // Contracts.cs: 0 для реалтайму
    }

    [Fact]
    public void A_rated_live_game_may_carry_a_stake()
    {
        var h = new RoomHarness("snake", new { stake = 5 });
        h.Stakes.Set("Оля", 10);
        h.Join("Оля");

        Assert.Equal(5, h.Room.Stake);
    }

    [Fact]
    public void A_numeric_stake_from_the_wire_reaches_the_room()
    {
        // Саме так аргумент прилітає з браузера: SignalR розбирає його звичайним System.Text.Json.
        const string wire = "{\"stake\":5,\"variant\":\"classic\"}";
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Dictionary<string, string>>(wire, web));

        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(wire, web)!;
        var options = RoomOptions.From(raw)!;
        Assert.Equal("5", options["stake"]);
        Assert.Equal("classic", options["variant"]);

        var h = new RoomHarness("t-duel");
        h.Stakes.Set("Оля", 10);
        var id = h.Rooms.Create("Оля", "t-duel", options).Reply.RoomId!;
        Assert.Equal(5, h.Rooms.Find(id)!.Stake);
    }

    // ---------- прибирання ----------

    [Fact]
    public void A_lobby_that_still_lives_is_not_swept_by_its_age()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var id = rooms.Create("Оля", "t-party", null).Reply.RoomId!;

        clock.Advance(TimeSpan.FromMinutes(31));
        rooms.Join(id, "Петро");
        rooms.Leave(id, "Петро");                          // знову неповна, але щойно жила
        rooms.Housekeeping(clock.UtcNow);
        Assert.NotNull(rooms.Find(id));

        clock.Advance(TimeSpan.FromMinutes(31));
        rooms.Housekeeping(clock.UtcNow);
        Assert.Null(rooms.Find(id));
    }

    // ---------- щоб столи знаходились (PLAN.md §7.4) ----------

    [Fact]
    public void A_new_table_calls_everyone_with_a_journal_line_and_an_invite()
    {
        var h = new RoomHarness("t-party");
        h.Join("Оля");

        var line = Assert.Single(h.Outbox.OfType<Journal>());
        Assert.Equal("Новий стіл: Тестова компанія (2–4) · господар Оля", line.Text);
        Assert.Equal(h.RoomId, line.RoomId);

        var invite = Assert.Single(h.Outbox.OfType<Invite>());
        Assert.Equal(h.RoomId, invite.RoomId);
        Assert.Equal("Оля", invite.By);
        Assert.Equal("Оля кличе в тестову компанію", invite.Text);
    }

    [Fact]
    public void A_private_table_calls_nobody()
    {
        var h = new RoomHarness("t-solo");
        h.Solo("Оля");

        Assert.Empty(h.Outbox.OfType<Invite>());
        Assert.Empty(h.Outbox.OfType<Journal>());
    }

    [Fact]
    public void A_table_that_started_at_once_invites_nobody_to_sit()
    {
        // Соло-подібний стіл на одного (Immediate) сідати вже нікуди: кликати нема на що, і «Новий стіл»
        // теж зайвий — про партію напише сам старт.
        var clock = new FakeClock();
        var rooms = New(clock);
        var outbox = rooms.Create("Оля", "t-now", null).Out;

        Assert.Empty(outbox.OfType<Invite>());
        Assert.DoesNotContain(outbox.OfType<Journal>(), l => l.Text.StartsWith("Новий стіл"));
    }

    [Fact]
    public void Live_ids_put_the_tables_you_can_still_join_first()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var playing = rooms.Create("Оля", "ttt", null).Reply.RoomId!;
        rooms.Join(playing, "Петро");                       // WhenFull → уже грають
        clock.Advance(TimeSpan.FromMinutes(1));
        var waiting = rooms.Create("Ганна", "c4", null).Reply.RoomId!;

        Assert.Equal([waiting, playing], rooms.LiveIds());
    }

    [Fact]
    public void Live_ids_forget_a_finished_table()
    {
        var h = new RoomHarness("t-duel");
        h.Join("Оля");
        h.Join("Петро");
        h.Act(0, "win");

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Rooms.LiveIds());
    }

    // ---------- продуктивність ----------

    [Fact]
    [Trait("Category", "Perf")]
    public void Twelve_snake_rooms_survive_a_thousand_ticks()
    {
        var clock = new FakeClock();
        var rooms = New(clock);
        var ids = new List<string>();
        for (var i = 0; i < Rooms.MaxRooms; i++)
        {
            var id = rooms.Create($"жовтий{i}", "snake", null).Reply.RoomId!;
            rooms.Join(id, $"зелений{i}");
            ids.Add(id);
        }

        var sw = Stopwatch.StartNew();
        for (var t = 0; t < 1000; t++)
        {
            clock.AdvanceMs(120);
            foreach (var room in rooms.TickDue(clock.UtcNow)) rooms.Tick(room);
            // Змійки врізаються в стіни секунд за п'ять; без «Ще раз» решта тиків міряла б порожнечу.
            for (var i = 0; i < ids.Count; i++)
                if (rooms.Find(ids[i]) is { Status: RoomStatus.Finished }) rooms.Rematch(ids[i], $"жовтий{i}");
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"12 кімнат × 1000 тиків зайняли {sw.Elapsed}");
    }
}
