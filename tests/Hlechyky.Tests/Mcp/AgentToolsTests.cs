using System.Text.Json;
using Hlechyky.Mcp;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Mcp;

/// <summary>
/// Інструменти для аі-агентів. Перевіряємо їх так само, як гру: очима того, хто ними користується, —
/// по тому JSON, який справді поїде в модель. Ніяких «внутрішніх» полів у цих відповідях бути не може:
/// що агент побачив, те він і знає.
/// </summary>
public class AgentToolsTests
{
    static JsonElement J(object result) => Views.Json(result);

    static bool Ok(object result) =>
        !J(result).TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;

    static string Message(object result) => J(result).GetProperty("message").GetString()!;

    static IReadOnlyDictionary<string, JsonElement> Opts(object options) =>
        JsonSerializer.SerializeToElement(options, AgentTools.Json).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    static string Phase(VillageHarness v, AgentSession s) =>
        J(v.Tools.Look(s, null)).GetProperty("view").GetProperty("phase").GetString()!;

    static string? Role(VillageHarness v, AgentSession s)
    {
        var me = J(v.Tools.Look(s, null)).GetProperty("view").GetProperty("me");
        return me.ValueKind == JsonValueKind.Object ? me.GetProperty("role").GetString() : null;
    }

    static int[] Alive(VillageHarness v, AgentSession s) =>
        [.. J(v.Tools.Look(s, null)).GetProperty("view").GetProperty("players").EnumerateArray()
            .Where(p => p.GetProperty("alive").GetBoolean()).Select(p => p.GetProperty("seat").GetInt32())];

    static int SeatOf(VillageHarness v, AgentSession s) => J(v.Tools.Look(s, null)).GetProperty("seat").GetInt32();

    /// <summary>Стіл на чотирьох агентів: господар створює, решта сідають.</summary>
    static async Task<(VillageHarness Village, string Room, List<AgentSession> Agents)> Village(object? options = null, int seed = 1)
    {
        var v = new VillageHarness(seed);
        var host = await v.Agent("Оля");
        var created = J(await v.Tools.Create(host, "mafia", options is null ? null : Opts(options)));
        Assert.True(created.GetProperty("ok").GetBoolean(), created.ToString());
        var room = created.GetProperty("room").GetString()!;

        var agents = new List<AgentSession> { host };
        foreach (var nick in new[] { "Петро", "Ганна", "Микола" })
        {
            var a = await v.Agent(nick);
            Assert.True(Ok(await v.Tools.Join(a, room, null)));
            agents.Add(a);
        }
        return (v, room, agents);
    }

    // =========================================================================================
    // Знайомство
    // =========================================================================================

    [Fact]
    public async Task An_agent_without_a_name_is_not_let_to_the_table()
    {
        var v = new VillageHarness();
        var s = v.Sessions.Open("")!;

        var r = await v.Tools.Create(s, "mafia", null);

        Assert.False(Ok(r));
        Assert.Contains("set_nick", Message(r));
    }

    [Fact]
    public async Task A_named_agent_sees_himself_in_whoami()
    {
        var v = new VillageHarness();
        var s = await v.Agent("Опанас");

        var who = J(v.Tools.Who(s));

        Assert.Equal("Опанас", who.GetProperty("nick").GetString());
        Assert.Equal(JsonValueKind.Null, who.GetProperty("myRoom").ValueKind);
    }

    [Fact]
    public async Task A_nameless_name_is_refused()
    {
        var v = new VillageHarness();
        var s = v.Sessions.Open("")!;

        Assert.False(Ok(await v.Tools.SetNick(s, "   ")));
    }

    [Fact]
    public void The_catalog_tells_the_agent_about_every_mafia_setting()
    {
        var v = new VillageHarness();

        var mafia = J(v.Tools.GameList()).GetProperty("games").EnumerateArray()
            .First(g => g.GetProperty("id").GetString() == "mafia");
        var keys = mafia.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("key").GetString()).ToArray();

        Assert.Contains("pace", keys);
        Assert.Contains("extra", keys);
        // Без підписів значень агент не вгадає, що «kuma» взагалі існує.
        var extra = mafia.GetProperty("options").EnumerateArray().First(o => o.GetProperty("key").GetString() == "extra");
        Assert.True(extra.GetProperty("multi").GetBoolean());
        Assert.Contains("maniac", extra.GetProperty("values").EnumerateArray().Select(x => x.GetProperty("value").GetString()));
    }

    // =========================================================================================
    // Столи
    // =========================================================================================

    [Fact]
    public async Task A_created_table_keeps_the_options_the_agent_asked_for()
    {
        var (v, room, agents) = await Village(new { pace = "fast", extra = "don" });

        var list = J(v.Tools.RoomList(agents[0])).GetProperty("rooms").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == room);

        Assert.Equal("fast", list.GetProperty("options").GetProperty("pace").GetString());
        Assert.Equal("don", list.GetProperty("options").GetProperty("extra").GetString());
        Assert.True(list.GetProperty("mine").GetBoolean());
        // Стіл мафії — на дванадцять душ, четверо вже сидять.
        Assert.Equal(8, list.GetProperty("free").GetInt32());
    }

    [Fact]
    public async Task Joining_by_game_finds_an_open_table()
    {
        var v = new VillageHarness();
        var host = await v.Agent("Оля");
        await v.Tools.Create(host, "mafia", null);
        var guest = await v.Agent("Петро");

        var r = J(await v.Tools.Join(guest, null, "mafia"));

        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(1, r.GetProperty("state").GetProperty("seat").GetInt32());
    }

    [Fact]
    public async Task Joining_without_a_table_or_a_game_is_an_honest_refusal()
    {
        var v = new VillageHarness();
        var s = await v.Agent("Оля");

        Assert.False(Ok(await v.Tools.Join(s, null, null)));
        Assert.False(Ok(await v.Tools.Join(s, null, "mafia")));   // столів ще нема
    }

    [Fact]
    public async Task Only_the_host_starts_the_game()
    {
        var (v, room, agents) = await Village();

        Assert.False(Ok(await v.Tools.StartGame(agents[1], room)));
        Assert.True(Ok(await v.Tools.StartGame(agents[0], room)));
        Assert.Equal("intro", Phase(v, agents[0]));
    }

    [Fact]
    public async Task Leaving_frees_the_seat()
    {
        var (v, room, agents) = await Village();

        Assert.True(Ok(await v.Tools.Leave(agents[3], room)));

        var list = J(v.Tools.RoomList(agents[0])).GetProperty("rooms").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == room);
        Assert.Equal(9, list.GetProperty("free").GetInt32());
    }

    // =========================================================================================
    // Партія
    // =========================================================================================

    [Fact]
    public async Task Four_agents_play_a_night_and_a_vote_through_the_short_tools()
    {
        var (v, room, agents) = await Village(new { pace = "fast" });
        await v.Tools.StartGame(agents[0], room);
        v.Until(() => Phase(v, agents[0]) == "night");

        var mafia = agents.First(a => Role(v, a) == "mafia");
        var sheriff = agents.First(a => Role(v, a) == "sheriff");
        var victim = agents.First(a => Role(v, a) == "civil");
        var victimSeat = SeatOf(v, victim);

        Assert.True(Ok(await v.Tools.MafiaMove(mafia, null, "say", null, "беремо мирного")));
        Assert.True(Ok(await v.Tools.MafiaMove(mafia, null, "kill", victimSeat, null)));
        var check = J(await v.Tools.MafiaMove(sheriff, null, "check", SeatOf(v, mafia), null));
        Assert.True(check.GetProperty("ok").GetBoolean());

        v.Until(() => Phase(v, agents[0]) == "day");
        Assert.DoesNotContain(victimSeat, Alive(v, agents[0]));
        // Удень агент сперечається там само, де й люди, — у загальних Балачках.
        Assert.True(Ok(v.Tools.ChatSend(agents[0], "мені здається, це Петро")));

        v.Until(() => Phase(v, agents[0]) == "vote");
        var mafiaSeat = SeatOf(v, mafia);
        foreach (var a in agents.Where(a => a != mafia && a != victim))
            Assert.True(Ok(await v.Tools.MafiaMove(a, null, "vote", mafiaSeat, null)));

        v.Until(() => Phase(v, agents[0]) == "done");
        var result = J(v.Tools.Look(agents[0], null)).GetProperty("view").GetProperty("result");
        Assert.Equal("civil", result.GetProperty("team").GetString());
    }

    [Fact]
    public async Task An_agent_never_sees_a_stranger_role()
    {
        var (v, room, agents) = await Village();
        await v.Tools.StartGame(agents[0], room);
        var civil = agents.First(a => Role(v, a) == "civil");

        var players = J(v.Tools.Look(civil, null)).GetProperty("view").GetProperty("players").EnumerateArray();
        var mine = SeatOf(v, civil);

        Assert.All(players.Where(p => p.GetProperty("seat").GetInt32() != mine),
            p => Assert.Equal(JsonValueKind.Null, p.GetProperty("role").ValueKind));
    }

    [Fact]
    public async Task A_spectator_gets_the_spectator_view_and_no_hints()
    {
        var (v, room, agents) = await Village();
        await v.Tools.StartGame(agents[0], room);
        var watcher = await v.Agent("Стороння");

        var look = J(v.Tools.Look(watcher, room));

        Assert.True(look.GetProperty("spectator").GetBoolean());
        Assert.Equal(JsonValueKind.Null, look.GetProperty("view").GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, look.GetProperty("can").ValueKind);
    }

    [Fact]
    public async Task The_hint_follows_the_phase_and_the_role()
    {
        var (v, room, agents) = await Village(new { pace = "fast" });
        await v.Tools.StartGame(agents[0], room);
        v.Until(() => Phase(v, agents[0]) == "night");
        var sheriff = agents.First(a => Role(v, a) == "sheriff");

        var can = J(v.Tools.Look(sheriff, null)).GetProperty("can").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Contains(can, x => x.StartsWith("check(", StringComparison.Ordinal));

        v.Until(() => Phase(v, agents[0]) == "vote", 400);
        var voting = J(v.Tools.Look(sheriff, null)).GetProperty("can").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Contains(voting, x => x.StartsWith("vote(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_action_comes_back_as_a_refusal_not_as_a_crash()
    {
        var (v, room, agents) = await Village();
        await v.Tools.StartGame(agents[0], room);

        var r = await v.Tools.Act(agents[0], null, "танцювати", default);

        Assert.False(Ok(r));
    }

    [Fact]
    public async Task A_move_without_a_seat_is_refused_before_it_reaches_the_game()
    {
        var (v, room, agents) = await Village();
        await v.Tools.StartGame(agents[0], room);

        var r = await v.Tools.MafiaMove(agents[0], null, "kill", null, null);

        Assert.False(Ok(r));
        Assert.Contains("seat", Message(r));
    }

    // =========================================================================================
    // Балачки й очікування
    // =========================================================================================

    [Fact]
    public async Task Only_new_chat_lines_come_twice_never()
    {
        var v = new VillageHarness();
        var s = await v.Agent("Оля");
        v.Chat.Send("Петро", "добрий вечір");

        var first = J(v.Tools.ChatRead(s, 40, onlyNew: true)).GetProperty("chat").EnumerateArray().Count();
        var second = J(v.Tools.ChatRead(s, 40, onlyNew: true)).GetProperty("chat").EnumerateArray().Count();
        v.Chat.Send("Ганна", "і вам");
        var third = J(v.Tools.ChatRead(s, 40, onlyNew: true)).GetProperty("chat").EnumerateArray().Count();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, third);
    }

    [Fact]
    public async Task What_the_agent_says_lands_in_the_same_chat_as_everyone_else()
    {
        var v = new VillageHarness();
        var s = await v.Agent("Оля");

        Assert.True(Ok(v.Tools.ChatSend(s, "а хто мовчить, той і мафія")));

        Assert.Contains(v.Chat.Recent(10), l => l.Nick == "Оля" && l.Text.Contains("мовчить"));
    }

    [Fact]
    public async Task Wait_comes_back_the_moment_somebody_speaks()
    {
        var (v, room, agents) = await Village();
        var waiting = v.Tools.Wait(agents[0], room, 5_000, default);
        await Task.Delay(50);
        v.Chat.Send("Петро", "агов, ви тут?");

        var r = J(await waiting);

        Assert.True(r.GetProperty("changed").GetBoolean());
        Assert.True(r.GetProperty("waited").GetInt32() < 5_000);
    }

    [Fact]
    public async Task Wait_gives_up_politely_when_the_village_sleeps()
    {
        var (v, room, agents) = await Village();

        var r = J(await v.Tools.Wait(agents[0], room, 1_000, default));

        Assert.False(r.GetProperty("changed").GetBoolean());
        // Навіть коли нічого не сталось, свіжий стан агент однаково отримує.
        Assert.Equal(room, r.GetProperty("state").GetProperty("room").GetProperty("id").GetString());
    }

    // =========================================================================================
    // Довідка й сесії
    // =========================================================================================

    [Fact]
    public void The_rules_tool_names_every_role_and_every_action()
    {
        var v = new VillageHarness();
        var text = J(v.Tools.Help("mafia")).GetProperty("text").GetString()!;

        foreach (var word in new[] { "mafia", "don", "sheriff", "doctor", "kuma", "maniac", "civil" })
            Assert.Contains(word, text);
        foreach (var word in new[] { "kill(seat)", "check(seat)", "heal(seat)", "block(seat)", "say(text)", "vote(seat)" })
            Assert.Contains(word, text);
    }

    [Fact]
    public void The_rules_tool_does_not_pretend_to_know_a_game_that_is_not_here()
    {
        var v = new VillageHarness();

        Assert.Contains("list_games", J(v.Tools.Help("канасту")).GetProperty("text").GetString()!);
    }

    [Fact]
    public void A_session_that_fell_silent_is_swept_away()
    {
        var v = new VillageHarness();
        var s = v.Sessions.Open("Опанас")!;
        v.Clock.Advance(AgentSessions.Ttl + TimeSpan.FromMinutes(1));

        var gone = v.Sessions.Sweep();

        Assert.Contains(gone, x => x.Id == s.Id);
        Assert.Null(v.Sessions.Get(s.Id));
    }

    [Fact]
    public void Too_many_agents_are_turned_away_at_the_gate()
    {
        var v = new VillageHarness();
        for (var i = 0; i < AgentSessions.MaxSessions; i++) Assert.NotNull(v.Sessions.Open($"бот{i}"));

        Assert.Null(v.Sessions.Open("зайвий"));
    }
}
