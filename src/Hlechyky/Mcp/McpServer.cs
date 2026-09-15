using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;

namespace Hlechyky.Mcp;

/// <summary>
/// MCP просто по HTTP (транспорт «streamable http»): аі-агент шле POST /mcp з JSON-RPC, а у відповідь
/// отримує звичайний JSON. SSE тут свідомо нема — сервер сам агентові нічого не пише, усе чекання
/// зроблено інструментом <c>wait</c>, який просто не відповідає, поки в селі тихо.
///
/// Підключення одним рядком і без жодного встановлення:
///   claude mcp add --transport http hlechyky https://hlechyky.pp.ua/mcp?nick=Опанас
///
/// Сесія тримається в заголовку <c>Mcp-Session-Id</c>. Клієнт, який його не шле, теж грає — йому
/// щоразу відкривається нова сесія з ніком із <c>?nick=</c>; просто пам'ять між викликами в нього
/// коротша (докуди дочитав Балачки).
/// </summary>
public sealed class McpServer(AgentTools tools, AgentSessions sessions, RateGate rates, IClock clock, ILogger<McpServer> log)
{
    public const string SessionHeader = "Mcp-Session-Id";
    public const string DefaultProtocol = "2025-06-18";
    static readonly string[] KnownProtocols = ["2024-11-05", "2025-03-26", "2025-06-18"];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // =========================================================================================
    // Транспорт
    // =========================================================================================

    public async Task HandleAsync(HttpContext ctx)
    {
        JsonDocument body;
        try { body = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted).ConfigureAwait(false); }
        catch (Exception)
        {
            await WriteAsync(ctx, Error(null, -32700, "не розібрав JSON")).ConfigureAwait(false);
            return;
        }

        using (body)
        {
            var root = body.RootElement;
            // Пачки (batch) прибрали аж у 2025-06-18, але старіші клієнти їх ще шлють — приймаємо обидва.
            var many = root.ValueKind == JsonValueKind.Array;
            var calls = many ? root.EnumerateArray().ToArray() : [root];

            var replies = new List<JsonNode?>();
            foreach (var call in calls)
            {
                var reply = await OneAsync(ctx, call).ConfigureAwait(false);
                if (reply is not null) replies.Add(reply);
            }

            if (replies.Count == 0)
            {
                // Самі лише сповіщення: відповідати нема на що, і за протоколом це 202.
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }
            await WriteAsync(ctx, many ? new JsonArray([.. replies]) : replies[0]).ConfigureAwait(false);
        }
    }

    static async Task WriteAsync(HttpContext ctx, JsonNode? node)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(node?.ToJsonString(Json) ?? "null", ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>DELETE /mcp — агент прощається. Місце за столом звільняється одразу, не чекаючи TTL.</summary>
    public void Close(HttpContext ctx)
    {
        var closed = sessions.Close(ctx.Request.Headers[SessionHeader].ToString());
        if (closed is not null) log.LogInformation("агент {Nick} закрив сесію {Id}", closed.Nick, closed.Id);
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // =========================================================================================
    // JSON-RPC
    // =========================================================================================

    async Task<JsonNode?> OneAsync(HttpContext ctx, JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object) return Error(null, -32600, "очікував об'єкт JSON-RPC");
        var id = call.TryGetProperty("id", out var rawId) && rawId.ValueKind is not JsonValueKind.Null
            ? JsonNode.Parse(rawId.GetRawText()) : null;
        var method = call.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        var args = call.TryGetProperty("params", out var p) ? p : default;

        // Сповіщення (без id) відповіді не мають — саме так клієнт каже «я готовий».
        if (id is null && method.StartsWith("notifications/", StringComparison.Ordinal)) return null;

        switch (method)
        {
            case "initialize":
                return Ok(id, Initialize(ctx, args));
            case "ping":
                return Ok(id, new JsonObject());
            case "tools/list":
                return Ok(id, JsonNode.Parse(JsonSerializer.Serialize(new { tools = McpTools.All }, Json))!);
            case "resources/list":
                return Ok(id, JsonNode.Parse("""{"resources":[]}""")!);
            case "resources/templates/list":
                return Ok(id, JsonNode.Parse("""{"resourceTemplates":[]}""")!);
            case "prompts/list":
                return Ok(id, JsonNode.Parse("""{"prompts":[]}""")!);
            case "tools/call":
                return await CallAsync(ctx, id, args).ConfigureAwait(false);
            default:
                return id is null ? null : Error(id, -32601, $"такого методу нема: {method}");
        }
    }

    JsonNode Initialize(HttpContext ctx, JsonElement args)
    {
        var wanted = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("protocolVersion", out var v)
            ? v.GetString() : null;
        var protocol = wanted is not null && KnownProtocols.Contains(wanted, StringComparer.Ordinal) ? wanted : DefaultProtocol;

        // Клієнт, який щоразу знайомиться наново (а такі є), має отримати ту саму сесію, а не нову.
        var nick = Auth.Nick(ctx) is { } n && n != "гість" ? n : "";
        var session = sessions.Find(nick) ?? sessions.Open(nick);
        if (session is null)
        {
            log.LogWarning("забагато агентів: нову сесію не відкрито");
        }
        else
        {
            session.Protocol = protocol;
            ctx.Response.Headers[SessionHeader] = session.Id;
            log.LogInformation("агент {Nick} відкрив сесію {Id} ({Protocol})",
                string.IsNullOrEmpty(session.Nick) ? "(без імені)" : session.Nick, session.Id, protocol);
        }

        return JsonNode.Parse(JsonSerializer.Serialize(new
        {
            protocolVersion = protocol,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "hlechyky", title = "Глечики — ігри села", version = "1.0.0" },
            instructions = session is null
                ? "Зараз за столами забагато агентів — спробуй трохи згодом."
                : Instructions,
        }, Json))!;
    }

    const string Instructions = """
        Глечики — домашнє радіо з іграми. Тут можна сісти за стіл і грати нарівні з людьми.
        Порядок такий: set_nick → list_rooms → join_room або create_room → start_game (якщо ти господар).
        Далі гра йде за годинником: клич wait, він поверне свіжий вид і нові рядки загального чату.
        Правила мафії — інструмент rules. Удень обговорення йде в чаті (chat_read / chat_send),
        а нічні дії — через mafia_kill / mafia_check / mafia_heal / mafia_block / mafia_whisper.
        Чужих ролей тобі не покажуть: вид збирається окремо для твого місця.
        """;

    async Task<JsonNode?> CallAsync(HttpContext ctx, JsonNode? id, JsonElement args)
    {
        var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var input = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("arguments", out var a)
            && a.ValueKind == JsonValueKind.Object ? a : default;

        var session = Session(ctx);
        if (session is null) return Ok(id, Content(new { ok = false, message = "Забагато агентів за раз — спробуй згодом" }, true));
        sessions.Touch(session);
        if (!rates.Allow(session.ConnectionId, input: false, clock.UtcNow.ToUnixTimeSeconds()))
            return Ok(id, Content(new { ok = false, message = Hlechyky.Games.Say.TooFast }, true));

        object result;
        try
        {
            result = await RunAsync(ctx, session, name, input).ConfigureAwait(false);
        }
        catch (GameError ex)
        {
            return Ok(id, Content(new { ok = false, message = ex.Message }, true));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "інструмент {Tool} впав", name);
            return Ok(id, Content(new { ok = false, message = "щось зламалось, спробуй ще раз" }, true));
        }

        var failed = result is not null
            && JsonSerializer.SerializeToElement(result, Json) is { ValueKind: JsonValueKind.Object } e
            && e.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False;
        return Ok(id, Content(result, failed));
    }

    Task<object> RunAsync(HttpContext ctx, AgentSession s, string name, JsonElement a) => name switch
    {
        "whoami" => Task.FromResult(tools.Who(s)),
        "set_nick" => tools.SetNick(s, Str(a, "nick")),
        "list_games" => Task.FromResult(tools.GameList()),
        "list_rooms" => Task.FromResult(tools.RoomList(s)),
        "create_room" => tools.Create(s, Str(a, "game"), Options(a)),
        "join_room" => tools.Join(s, Str(a, "room"), Str(a, "game")),
        "leave_room" => tools.Leave(s, Str(a, "room")),
        "start_game" => tools.StartGame(s, Str(a, "room")),
        "rematch" => tools.Rematch(s, Str(a, "room")),
        "look" => Task.FromResult(tools.Look(s, Str(a, "room"))),
        "wait" => tools.Wait(s, Str(a, "room"), Num(a, "timeoutMs"), ctx.RequestAborted),
        "act" => tools.Act(s, Str(a, "room"), Str(a, "action"), Payload(a)),
        "chat_read" => Task.FromResult(tools.ChatRead(s, Num(a, "limit"), Flag(a, "onlyNew") ?? false)),
        "chat_send" => Task.FromResult(tools.ChatSend(s, Str(a, "text"))),
        "rules" => Task.FromResult(tools.Help(Str(a, "game"))),
        "mafia_kill" => tools.MafiaMove(s, Str(a, "room"), "kill", Num(a, "seat"), null),
        "mafia_check" => tools.MafiaMove(s, Str(a, "room"), "check", Num(a, "seat"), null),
        "mafia_heal" => tools.MafiaMove(s, Str(a, "room"), "heal", Num(a, "seat"), null),
        "mafia_block" => tools.MafiaMove(s, Str(a, "room"), "block", Num(a, "seat"), null),
        "mafia_whisper" => tools.MafiaMove(s, Str(a, "room"), "say", null, Str(a, "text")),
        "mafia_vote" => tools.MafiaMove(s, Str(a, "room"), "vote", Num(a, "seat"), null),
        _ => Task.FromResult<object>(new { ok = false, message = $"такого інструмента нема: {name}" }),
    };

    /// <summary>Сесія з заголовка; клієнтам без пам'яті відкриваємо нову, щоб вони теж могли грати.</summary>
    AgentSession? Session(HttpContext ctx)
    {
        var id = ctx.Request.Headers[SessionHeader].ToString();
        if (sessions.Get(id) is { } live) return live;
        var nick = Auth.Nick(ctx) is { } n && n != "гість" ? n : "";
        if (sessions.Find(nick) is { } same) return same;
        var fresh = sessions.Open(nick);
        if (fresh is not null) ctx.Response.Headers[SessionHeader] = fresh.Id;
        return fresh;
    }

    // =========================================================================================
    // Дрібниці JSON-RPC
    // =========================================================================================

    static JsonNode Ok(JsonNode? id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    static JsonNode Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    /// <summary>
    /// Відповідь інструмента. Текстом віддаємо той самий JSON, що й структурою: моделі читають текст,
    /// а програмні клієнти — <c>structuredContent</c>.
    /// </summary>
    static JsonNode Content(object? payload, bool isError)
    {
        var json = JsonSerializer.Serialize(payload ?? new { }, new JsonSerializerOptions(Json) { WriteIndented = true });
        var node = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = json }),
        };
        if (JsonNode.Parse(json) is JsonObject structured) node["structuredContent"] = structured;
        if (isError) node["isError"] = true;
        return node;
    }

    static string? Str(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int? Num(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        // Моделі люблять надіслати число рядком — приймаємо, бо через це партія стояти не мусить.
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return null;
    }

    static bool? Flag(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    static IReadOnlyDictionary<string, JsonElement>? Options(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in o.EnumerateObject()) map[prop.Name] = prop.Value.Clone();
        return map;
    }

    /// <summary>Вантаж довільної дії: або готовий об'єкт, або просто <c>{seat: n}</c> / <c>{text: "…"}</c>.</summary>
    static JsonElement Payload(JsonElement a)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("payload", out var p)) return p.Clone();
        if (Num(a, "seat") is { } seat) return JsonSerializer.SerializeToElement(new { seat }, Json);
        if (Str(a, "text") is { } text) return JsonSerializer.SerializeToElement(new { text }, Json);
        return default;
    }
}

/// <summary>Точка входу: один POST і два ввічливі коди на решту дієслів.</summary>
public static class McpSetup
{
    public static IServiceCollection AddHlechykyMcp(this IServiceCollection services)
    {
        services.AddSingleton<AgentSessions>();
        services.AddSingleton<IAgentChat, VillageChat>();
        services.AddSingleton<IAgentFlush, VillageFlush>();
        services.AddSingleton<AgentTools>();
        services.AddSingleton<McpServer>();
        services.AddHostedService<AgentPresence>();
        return services;
    }

    public static WebApplication MapHlechykyMcp(this WebApplication app)
    {
        app.MapPost("/mcp", (HttpContext ctx, McpServer server) => server.HandleAsync(ctx));
        // Сервер сам нічого не шле, тож потоку подій тут нема — і за протоколом так можна.
        app.MapGet("/mcp", () => Results.Text("Глечики: MCP живе на POST /mcp (streamable http). Дивись docs/games/MCP.md",
            "text/plain; charset=utf-8", System.Text.Encoding.UTF8, StatusCodes.Status405MethodNotAllowed));
        app.MapDelete("/mcp", (HttpContext ctx, McpServer server) => { server.Close(ctx); return Task.CompletedTask; });
        return app;
    }
}
