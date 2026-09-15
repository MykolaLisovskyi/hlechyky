using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Mcp;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Mcp;

/// <summary>
/// Дріт MCP: JSON-RPC поверх звичайного POST. Перевіряємо саме те, що побачить чужий клієнт, —
/// байти відповіді, заголовок сесії й коди помилок. Жодного справжнього HTTP-сервера для цього не треба.
/// </summary>
public class McpServerTests
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    sealed class Village
    {
        public Village()
        {
            var registry = RoomHarness.NewRegistry();
            var rooms = new Rooms(registry, Clock, new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty());
            Sessions = new AgentSessions(Clock);
            Server = new McpServer(new AgentTools(rooms, registry, new FakeChat(), new NoFlush()),
                Sessions, new RateGate(), Clock, NullLogger<McpServer>.Instance);
        }

        public FakeClock Clock { get; } = new();
        public AgentSessions Sessions { get; }
        public McpServer Server { get; }
    }

    sealed record Answer(HttpContext Context, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public string? Session => Context.Response.Headers[McpServer.SessionHeader].ToString() is { Length: > 0 } s ? s : null;
        public int Status => Context.Response.StatusCode;
    }

    static async Task<Answer> Post(Village v, object body, string? session = null, string? nick = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body as string ?? JsonSerializer.Serialize(body, Wire)));
        if (session is not null) ctx.Request.Headers[McpServer.SessionHeader] = session;
        if (nick is not null) ctx.Items["nick"] = nick;
        var output = new MemoryStream();
        ctx.Response.Body = output;
        await v.Server.HandleAsync(ctx);
        return new Answer(ctx, Encoding.UTF8.GetString(output.ToArray()));
    }

    static object Call(int id, string tool, object? args = null) => new
    {
        jsonrpc = "2.0",
        id,
        method = "tools/call",
        @params = new { name = tool, arguments = args ?? new { } },
    };

    static async Task<(Village Village, string Session)> Ready(string nick = "Опанас")
    {
        var v = new Village();
        var hello = await Post(v, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } }, nick: nick);
        return (v, hello.Session!);
    }

    static JsonElement Result(Answer a) => a.Json.GetProperty("result");

    /// <summary>Те, що модель справді читає: єдиний текстовий блок відповіді інструмента.</summary>
    static JsonElement Payload(Answer a) =>
        JsonDocument.Parse(Result(a).GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;

    // =========================================================================================
    // Рукостискання
    // =========================================================================================

    [Fact]
    public async Task Initialize_answers_with_a_session_and_an_instruction()
    {
        var v = new Village();

        var a = await Post(v, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } }, nick: "Опанас");

        var r = Result(a);
        Assert.Equal("2025-06-18", r.GetProperty("protocolVersion").GetString());
        Assert.Equal("hlechyky", r.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(r.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Contains("set_nick", r.GetProperty("instructions").GetString());
        Assert.NotNull(a.Session);
        // Нік із ?nick= підхоплюється одразу: агента налаштували один раз — і він уже в селі.
        Assert.Equal("Опанас", v.Sessions.Get(a.Session)!.Nick);
    }

    [Fact]
    public async Task An_unknown_protocol_version_is_answered_with_one_we_know()
    {
        var v = new Village();

        var a = await Post(v, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "1999-01-01" } });

        Assert.Equal(McpServer.DefaultProtocol, Result(a).GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task A_notification_gets_no_answer_at_all()
    {
        var (v, session) = await Ready();

        var a = await Post(v, new { jsonrpc = "2.0", method = "notifications/initialized" }, session);

        Assert.Equal(StatusCodes.Status202Accepted, a.Status);
        Assert.Equal("", a.Body);
    }

    [Fact]
    public async Task Ping_is_answered_with_an_empty_result()
    {
        var (v, session) = await Ready();

        var a = await Post(v, new { jsonrpc = "2.0", id = 7, method = "ping" }, session);

        Assert.Equal(7, a.Json.GetProperty("id").GetInt32());
        Assert.Empty(Result(a).EnumerateObject());
    }

    // =========================================================================================
    // Список інструментів
    // =========================================================================================

    [Fact]
    public async Task Tools_list_describes_every_tool_the_agent_may_need()
    {
        var (v, session) = await Ready();

        var tools = Result(await Post(v, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, session))
            .GetProperty("tools").EnumerateArray().ToArray();
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToArray();

        foreach (var need in new[] { "set_nick", "list_rooms", "create_room", "join_room", "start_game", "look", "wait",
            "act", "chat_read", "chat_send", "rules", "mafia_kill", "mafia_check", "mafia_heal", "mafia_block",
            "mafia_whisper", "mafia_vote" })
            Assert.Contains(need, names);
        // Схема потрібна кожному: без неї модель не знає, що взагалі можна передати.
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()));
            Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString());
        });
        // Обов'язкові поля мусять бути серед описаних, інакше клієнт не збере виклику.
        var kill = tools.First(t => t.GetProperty("name").GetString() == "mafia_kill");
        Assert.Equal(["seat"], kill.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.True(kill.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("room", out _));
    }

    [Fact]
    public async Task Lists_we_do_not_have_come_back_empty_instead_of_broken()
    {
        var (v, session) = await Ready();

        Assert.Empty(Result(await Post(v, new { jsonrpc = "2.0", id = 3, method = "resources/list" }, session)).GetProperty("resources").EnumerateArray());
        Assert.Empty(Result(await Post(v, new { jsonrpc = "2.0", id = 4, method = "prompts/list" }, session)).GetProperty("prompts").EnumerateArray());
    }

    // =========================================================================================
    // Виклики інструментів
    // =========================================================================================

    [Fact]
    public async Task Whoami_comes_back_both_as_text_and_as_structure()
    {
        var (v, session) = await Ready("Опанас");

        var a = await Post(v, Call(5, "whoami"), session);

        Assert.Equal("Опанас", Payload(a).GetProperty("nick").GetString());
        Assert.Equal("Опанас", Result(a).GetProperty("structuredContent").GetProperty("nick").GetString());
        Assert.False(Result(a).TryGetProperty("isError", out _));
    }

    [Fact]
    public async Task A_table_can_be_set_with_settings_over_the_wire()
    {
        var (v, session) = await Ready("Оля");

        var a = await Post(v, Call(6, "create_room", new { game = "mafia", options = new { pace = "fast", extra = "don,maniac" } }), session);

        var payload = Payload(a);
        Assert.True(payload.GetProperty("ok").GetBoolean());
        var rules = payload.GetProperty("state").GetProperty("view").GetProperty("rules");
        Assert.Equal("fast", rules.GetProperty("pace").GetString());
        Assert.True(rules.GetProperty("maniac").GetBoolean());
    }

    [Fact]
    public async Task A_refusal_of_the_game_is_marked_as_an_error_for_the_model()
    {
        var (v, session) = await Ready("Оля");

        var a = await Post(v, Call(7, "start_game", new { room = "немає" }), session);

        Assert.True(Result(a).GetProperty("isError").GetBoolean());
        Assert.False(Payload(a).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_tool_is_a_polite_refusal_not_a_protocol_error()
    {
        var (v, session) = await Ready();

        var a = await Post(v, Call(8, "зварити_борщ"), session);

        Assert.False(a.Json.TryGetProperty("error", out _));
        Assert.True(Result(a).GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task A_number_sent_as_a_string_still_works()
    {
        var (v, session) = await Ready("Оля");
        await Post(v, Call(9, "create_room", new { game = "mafia" }), session);

        // Моделі люблять надіслати «2» рядком. Якщо число не розібрати, інструмент відмовив би сам
        // («не сказано, на кого») і до кімнати не дійшов би — а так відмова приходить уже від столу.
        var a = await Post(v, Call(10, "mafia_kill", new { seat = "2" }), session);

        Assert.Equal("Чекаємо на гравців", Payload(a).GetProperty("message").GetString());
    }

    // =========================================================================================
    // Сесії, пачки й помилки
    // =========================================================================================

    [Fact]
    public async Task A_client_without_a_session_header_still_gets_to_play()
    {
        var v = new Village();

        var a = await Post(v, Call(11, "whoami"), nick: "Гнат");

        Assert.NotNull(a.Session);
        Assert.Equal("Гнат", Payload(a).GetProperty("nick").GetString());
    }

    [Fact]
    public async Task A_batch_comes_back_as_a_batch()
    {
        var (v, session) = await Ready();

        var a = await Post(v, new object[] { Call(12, "whoami"), Call(13, "list_rooms") }, session);

        Assert.Equal(JsonValueKind.Array, a.Json.ValueKind);
        Assert.Equal([12, 13], a.Json.EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task An_unknown_method_is_a_proper_json_rpc_error()
    {
        var (v, session) = await Ready();

        var a = await Post(v, new { jsonrpc = "2.0", id = 14, method = "села/немає" }, session);

        Assert.Equal(-32601, a.Json.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Broken_json_is_answered_not_swallowed()
    {
        var v = new Village();

        var a = await Post(v, "{це не json");

        Assert.Equal(-32700, a.Json.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task An_agent_in_a_hurry_is_told_to_slow_down()
    {
        var (v, session) = await Ready();

        var answers = new List<Answer>();
        for (var i = 0; i < RateGate.ActsPerSecond + 2; i++) answers.Add(await Post(v, Call(100 + i, "whoami"), session));

        Assert.Contains(answers, a => Payload(a).TryGetProperty("message", out var m) && m.GetString() == "Не так швидко");
    }

    [Fact]
    public async Task A_forgetful_client_does_not_eat_a_session_per_call()
    {
        var v = new Village();

        for (var i = 0; i < 40; i++) await Post(v, Call(200 + i, "whoami"), nick: "Гнат");

        // Нік той самий — отже, це той самий гравець у селі, а не сорок нових ботів.
        Assert.Equal(1, v.Sessions.Count);
    }

    [Fact]
    public async Task Goodbye_closes_the_session_at_once()
    {
        var (v, session) = await Ready();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[McpServer.SessionHeader] = session;

        v.Server.Close(ctx);

        Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
        Assert.Null(v.Sessions.Get(session));
    }
}
