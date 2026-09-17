using System.Text.Json;
using Hlechyky.Games;

namespace Hlechyky.Mcp;

/// <summary>Рядок Балачок так, як його бачить агент.</summary>
public sealed record AgentChatLine(long Id, string Nick, string Text, string Kind, DateTimeOffset At);

/// <summary>Що вийшло з «сказати в Балачки».</summary>
public sealed record ChatSendResult(bool Ok, string Message, AgentChatLine? Line);

/// <summary>
/// Балачки з погляду агента. Окремим швом, бо за ними стоїть уся радіо-частина (база, хаб, команди),
/// а перевіряти інструменти треба без жодного з них.
/// </summary>
public interface IAgentChat
{
    IReadOnlyList<AgentChatLine> Recent(int limit);
    ChatSendResult Send(string nick, string text);
    /// <summary>Номер останнього рядка Балачок. Окремо від <see cref="Recent"/>: на це дивиться цикл очікування.</summary>
    long LastId();
}

/// <summary>Куди подіти розсилку, яку повернув каркас. У проді — Broadcaster, у тестах — нікуди.</summary>
public interface IAgentFlush
{
    Task FlushAsync(Outbox outbox);
}

/// <summary>Розсилка в нікуди: тести піднімають кімнати без SignalR.</summary>
public sealed class NoFlush : IAgentFlush
{
    public Task FlushAsync(Outbox outbox) => Task.CompletedTask;
}

/// <summary>
/// Село очима аі-агента. Один метод — один інструмент MCP; жодного HTTP і жодного JSON-RPC тут нема,
/// саме тому все це перевіряється звичайними тестами кімнат.
///
/// Головне правило те саме, що й для браузера: агент не бачить нічого, чого йому не дав <c>Game.View</c>.
/// Ролі, нічний чат і таємні голоси приходять рівно так, як людині за тим самим місцем.
/// </summary>
public sealed class AgentTools(Rooms rooms, Registry registry, IAgentChat chat, IAgentFlush flush, Db? db = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Скільки чекає <c>wait</c>, якщо агент не сказав інакше, і скільки це може бути щонайбільше.</summary>
    public const int WaitDefaultMs = 15_000;
    public const int WaitMaxMs = 30_000;
    const int WaitPollMs = 250;

    // =========================================================================================
    // Хто я і що навколо
    // =========================================================================================

    public object Who(AgentSession s)
    {
        var room = MyRoom(s.Nick);
        return new
        {
            nick = s.Nick,
            session = s.Id,
            calls = s.Calls,
            myRoom = room?.Id,
            myGame = room?.Info.Id,
            mySeat = room?.SeatOf(s.Nick),
            note = string.IsNullOrEmpty(s.Nick)
                ? "Назвись через set_nick — без імені за стіл не пускають."
                : "Готовий грати. Столи — у list_rooms, правила гри — у rules.",
        };
    }

    public async Task<object> SetNick(AgentSession s, string? nick)
    {
        var clean = Auth.SanitizeNick(nick);
        if (clean == Auth.Guest) return Fail("Таке ім'я нічого не означає — вигадай своє");
        // Зареєстроване людиною ім'я — її, з паролем; агентові під ним не сісти.
        if (db?.FindAccount(clean) is not null) return Fail("Це ім'я вже зайняла людина — вигадай інше");
        // Нік — це те саме, що встати з-за столу: старе місце за старим ім'ям лишати не можна.
        if (!string.IsNullOrEmpty(s.Nick) && !string.Equals(s.Nick, clean, StringComparison.OrdinalIgnoreCase))
            await flush.FlushAsync(rooms.DropNick(s.Nick)).ConfigureAwait(false);
        s.Nick = clean;
        rooms.NoteOnline(clean);
        return new { ok = true, nick = clean };
    }

    public object GameList() => new
    {
        games = registry.Catalog.Select(g => new
        {
            id = g.Id,
            title = g.Title,
            group = g.Group,
            players = new { min = g.MinPlayers, max = g.MaxPlayers },
            start = g.Start,
            hint = g.Hint,
            options = g.Options.Select(o => new
            {
                key = o.Key,
                label = o.Label,
                @default = o.Default,
                multi = o.Multi,
                values = o.Values.Select(v => new { value = v[0], label = v[1] }),
            }),
        }),
    };

    public object RoomList(AgentSession s) => new
    {
        rooms = rooms.Snapshot().Select(r => new
        {
            id = r.Id,
            game = r.Game,
            status = r.Status,
            host = r.Host,
            round = r.Round,
            free = r.Seats.Count(x => x.Nick is null),
            players = r.Seats.Where(x => x.Nick is not null).Select(x => x.Nick),
            needs = r.MinPlayers,
            max = r.MaxPlayers,
            options = r.Options,
            mine = r.Seats.Any(x => string.Equals(x.Nick, s.Nick, StringComparison.OrdinalIgnoreCase)),
        }),
    };

    // =========================================================================================
    // Столи
    // =========================================================================================

    public Task<object> Create(AgentSession s, string? game, IReadOnlyDictionary<string, JsonElement>? options)
    {
        if (string.IsNullOrEmpty(s.Nick)) return Task.FromResult(NeedNick());
        if (string.IsNullOrEmpty(game)) return Task.FromResult(Fail("Скажи, у що граємо: створювати стіл наосліп нема сенсу"));
        return Run(s, () => rooms.Create(s.Nick, game, RoomOptions.From(options)));
    }

    /// <summary>Сісти за конкретний стіл або за перший вільний стіл названої гри.</summary>
    public Task<object> Join(AgentSession s, string? roomId, string? game)
    {
        if (string.IsNullOrEmpty(s.Nick)) return Task.FromResult(NeedNick());
        if (string.IsNullOrEmpty(roomId))
        {
            if (string.IsNullOrEmpty(game)) return Task.FromResult(Fail("Скажи, за який стіл сідати (room) або в яку гру (game)"));
            var open = rooms.Snapshot()
                .FirstOrDefault(r => string.Equals(r.Game, game, StringComparison.Ordinal)
                    && r.Status == "lobby" && r.Seats.Any(x => x.Nick is null));
            if (open is null) return Task.FromResult(Fail($"Вільних столів у грі «{game}» нема — постав свій через create_room"));
            roomId = open.Id;
        }
        var id = roomId;
        return Run(s, () => rooms.Join(id, s.Nick));
    }

    public Task<object> Leave(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        return Run(s, () => rooms.Leave(id, s.Nick));
    }

    public Task<object> StartGame(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        return Run(s, () => rooms.StartByHost(id, s.Nick));
    }

    public Task<object> Rematch(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        return Run(s, () => rooms.Rematch(id, s.Nick));
    }

    // =========================================================================================
    // Партія
    // =========================================================================================

    /// <summary>Що я бачу зі свого місця. Глядачеві дістається вид глядача — і нічого понад те.</summary>
    public object Look(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return NoRoom();
        if (Snap(s, id) is not { } snap) return Fail(Hlechyky.Games.Say.NoRoom);
        return snap;
    }

    public Task<object> Act(AgentSession s, string? roomId, string? action, JsonElement payload)
    {
        if (string.IsNullOrEmpty(s.Nick)) return Task.FromResult(NeedNick());
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        if (string.IsNullOrEmpty(action)) return Task.FromResult(Fail("Не сказано, що робити"));
        return Run(s, () => rooms.Act(id, s.Nick, action, payload), id);
    }

    /// <summary>Хід мафії коротко: місце числом, решту допише сам інструмент.</summary>
    public Task<object> MafiaMove(AgentSession s, string? roomId, string action, int? seat, string? text)
    {
        var payload = action switch
        {
            "say" => JsonSerializer.SerializeToElement(new { text = text ?? "" }, Json),
            "vote" when seat is null => JsonSerializer.SerializeToElement(new { seat = (int?)null }, Json),
            _ => JsonSerializer.SerializeToElement(new { seat }, Json),
        };
        if (action != "say" && action != "vote" && seat is null)
            return Task.FromResult(Fail("Не сказано, на кого: передай seat (номер місця зі списку гравців)"));
        return Act(s, roomId, action, payload);
    }

    /// <summary>
    /// Дочекатись, поки в селі щось зміниться: новий вид, нова фаза або свіжий рядок у Балачках.
    /// Це головний інструмент агента: без нього довелось би крутити <c>look</c> у циклі й проґавити день.
    /// </summary>
    public async Task<object> Wait(AgentSession s, string? roomId, int? timeoutMs, CancellationToken ct)
    {
        if (Which(s, roomId) is not { } id) return NoRoom();
        var budget = Math.Clamp(timeoutMs ?? WaitDefaultMs, 1_000, WaitMaxMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(budget);
        var started = DateTime.UtcNow;

        var before = Fingerprint(s, id);
        while (true)
        {
            if (ct.IsCancellationRequested) break;
            var now = Fingerprint(s, id);
            if (now is null) break;                       // стіл зник — хай агент подивиться сам
            if (!string.Equals(now, before, StringComparison.Ordinal)) break;
            if (DateTime.UtcNow >= deadline) break;
            try { await Task.Delay(WaitPollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        var snap = Snap(s, id);
        var waited = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        if (snap is null) return Fail(Hlechyky.Games.Say.NoRoom);
        return new { waited, changed = !string.Equals(Fingerprint(s, id), before, StringComparison.Ordinal), state = snap };
    }

    // =========================================================================================
    // Балачки
    // =========================================================================================

    /// <summary>Загальний чат села. Удень уся гра в мафію відбувається саме тут.</summary>
    public object ChatRead(AgentSession s, int? limit, bool onlyNew)
    {
        var lines = chat.Recent(Math.Clamp(limit ?? 40, 1, 200));
        var fresh = onlyNew ? lines.Where(l => l.Id > s.SeenChatId).ToList() : [.. lines];
        if (lines.Count > 0) s.SeenChatId = Math.Max(s.SeenChatId, lines[^1].Id);
        return new { chat = fresh };
    }

    public object ChatSend(AgentSession s, string? text)
    {
        if (string.IsNullOrEmpty(s.Nick)) return NeedNick();
        var said = (text ?? "").Trim();
        if (said.Length == 0) return Fail("Порожнє нікому не цікаво");
        var r = chat.Send(s.Nick, said);
        if (r.Line is { } line) s.SeenChatId = Math.Max(s.SeenChatId, line.Id);
        if (!r.Ok) return Fail(r.Message);
        return new { ok = true, line = r.Line };
    }

    // =========================================================================================
    // Довідка
    // =========================================================================================

    public object Help(string? gameId) => new
    {
        game = gameId ?? "mafia",
        text = string.Equals(gameId, "mafia", StringComparison.Ordinal) || string.IsNullOrEmpty(gameId)
            ? MafiaGuide
            : registry.Info(gameId ?? "") is { } info
                ? $"{info.Title}: {info.Hint}. Дії гри дивись у виді (look) — вона сама підказує, що зараз можна."
                : "Такої гри тут нема. Подивись list_games.",
    };

    /// <summary>
    /// Правила мафії для того, хто ніколи не бачив картки. Це не прикраса: агент не читає підказок
    /// клієнта, тому все, що людині малює екран, тут має бути словами.
    /// </summary>
    public const string MafiaGuide = """
        МАФІЯ на Глечиках — 4–12 гравців, ведучий Дядько Глек, обговорення йде в загальному чаті (Балачки).

        Як сісти грати:
          set_nick → create_room("mafia", {опції}) або join_room(game:"mafia") → коли всіх зібрано,
          господар тисне start_game. Далі гра йде сама, за годинником: чекай зміни через wait.

        Фази (тривалості залежать від опції pace, точні числа — у view.rules і view.phaseMs):
          intro  — усі дивляться свою роль;
          night  — нічні дії (див. нижче);
          day    — оголошення ранку й СУПЕРЕЧКА В БАЛАЧКАХ (chat_send / chat_read). Кімната лише рахує час;
          vote   — голосування; більшість від живих виганяє, рівність і утримання лишають усіх;
          done   — кінець.

        Ролі (своя роль — у view.me.role):
          mafia   — уночі kill(seat) по мирному; своїх не чіпають. Уночі є шепіт say(text) — його бачить лише мафія;
          don     — той самий мафіозі, але його kill вирішальний, а комісару він показується мирним;
          sheriff — уночі check(seat), одна перевірка на ніч; результат лягає у view.night.myCheck;
          doctor  — уночі heal(seat); не двічі поспіль ту саму людину; себе — якщо view.rules.selfHeal;
          kuma    — уночі block(seat): до кого пішла в гості, той цієї ночі нічого не встигне (комісара не спиняє);
          maniac  — уночі kill(seat) сам по собі, проти всіх; виграє, коли лишається сам;
          civil   — уночі спить, удень говорить і голосує.

        Дії (інструмент mafia_move або act):
          kill(seat), check(seat), heal(seat), block(seat), say(text) — тільки вночі й тільки своєю роллю;
          vote(seat) або vote() без місця — утриматись; міняти голос можна до кінця фази.
          Місця беруться з view.players[].seat. Чужих ролей у виді нема — і не буде.

        Перемога: мирні — коли не лишилось ні мафії, ні маньяка; мафія — коли її не менше, ніж мирних
        (і маньяка в селі вже нема); маньяк — коли лишився сам. Мертві бачать усе й мовчать — це правило честі.

        Порада: після кожного ходу клич wait — він повертає свіжий вид і нові рядки Балачок разом.
        """;

    // =========================================================================================
    // Дрібниці
    // =========================================================================================

    /// <summary>Мій стіл: той, за яким я сиджу. Агент майже ніколи не має двох.</summary>
    Room? MyRoom(string nick) =>
        string.IsNullOrEmpty(nick) ? null : rooms.Snapshot()
            .Where(r => r.Seats.Any(x => string.Equals(x.Nick, nick, StringComparison.OrdinalIgnoreCase)))
            .Select(r => rooms.Find(r.Id))
            .FirstOrDefault(r => r is not null);

    string? Which(AgentSession s, string? roomId) =>
        !string.IsNullOrEmpty(roomId) ? roomId : MyRoom(s.Nick)?.Id;

    async Task<object> Run(AgentSession s, Func<RoomOutcome> work, string? known = null)
    {
        var outcome = work();
        await flush.FlushAsync(outcome.Out).ConfigureAwait(false);
        var id = outcome.Reply.RoomId ?? known ?? Which(s, null);
        var state = id is null ? null : Snap(s, id);
        return new
        {
            ok = outcome.Reply.Ok,
            message = outcome.Reply.Message,
            room = outcome.Reply.RoomId ?? id,
            state,
        };
    }

    /// <summary>Знімок столу для агента: шапка, моє місце, мій вид і підказка, що зараз можна зробити.</summary>
    object? Snap(AgentSession s, string roomId)
    {
        if (rooms.ViewsFor(roomId) is not { } b) return null;
        var seat = b.SeatOf(s.Nick);
        var raw = seat is { } i && b.SeatViews.TryGetValue(i, out var mine) ? mine : b.WatcherView;
        var view = JsonSerializer.SerializeToElement(raw, Json);
        return new
        {
            room = new
            {
                id = b.Summary.Id,
                game = b.Summary.Game,
                status = b.Summary.Status,
                host = b.Summary.Host,
                round = b.Summary.Round,
                options = b.Summary.Options,
                seats = b.Summary.Seats.Select(x => new { seat = x.I, nick = x.Nick }),
                result = b.Summary.Result,
            },
            seat,
            spectator = seat is null,
            view,
            can = b.Summary.Game == "mafia" ? MafiaCan(view, seat) : null,
        };
    }

    /// <summary>
    /// Рядок «що зараз можна зробити». Це підказка, а не правило: справжню перевірку однаково робить гра,
    /// і зібрано її з того самого виду, який агент уже бачить.
    /// </summary>
    static string[]? MafiaCan(JsonElement view, int? seat)
    {
        if (seat is null || view.ValueKind != JsonValueKind.Object) return null;
        if (!view.TryGetProperty("phase", out var p) || p.GetString() is not { } phase) return null;
        if (!view.TryGetProperty("me", out var me) || me.ValueKind != JsonValueKind.Object) return null;
        if (!me.TryGetProperty("alive", out var alive) || !alive.GetBoolean()) return ["Тебе вже нема серед живих: дивись і мовчи"];
        var role = me.TryGetProperty("role", out var r) ? r.GetString() : null;

        var quiet = view.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Object
            && rules.TryGetProperty("firstNightKill", out var fk) && !fk.GetBoolean()
            && view.TryGetProperty("day", out var d) && d.GetInt32() == 1;

        return (phase, role) switch
        {
            ("night", "mafia" or "don") => quiet
                ? ["say(text) — шепіт своїм; ножів цієї ночі нема"]
                : ["kill(seat) — показати на жертву", "say(text) — шепіт своїм"],
            ("night", "maniac") => quiet ? ["цієї ночі нічого; придивляйся"] : ["kill(seat) — зарізати будь-кого, крім себе"],
            ("night", "sheriff") => ["check(seat) — одна перевірка за ніч"],
            ("night", "doctor") => ["heal(seat) — врятувати від нічного ножа"],
            ("night", "kuma") => ["block(seat) — піти в гості й зірвати чужу нічну справу"],
            ("night", _) => ["спи; уночі за тебе працюють інші"],
            ("day", _) => ["chat_send(text) — сперечайся в Балачках", "chat_read() — читай, що кажуть інші"],
            ("vote", _) => ["vote(seat) — вигнати", "vote() без місця — утриматись"],
            ("intro", _) => ["запам'ятай свою роль і чекай ночі (wait)"],
            ("done", _) => ["rematch — зіграти ще раз", "leave_room — встати з-за столу"],
            _ => ["чекай, поки господар почне (start_game)"],
        };
    }

    static object NeedNick() => Fail("Спершу скажи, як тебе кликати: set_nick");
    static object NoRoom() => Fail("Не видно, за яким ти столом. Передай room або спершу сядь (join_room)");
    static object Fail(string message) => new { ok = false, message };

    /// <summary>
    /// Відбиток стану: усе, на що агенту варто прокинутись. Порівнюємо рядками — це дешевше й чесніше,
    /// ніж вигадувати грі номер версії, якого в неї нема.
    /// </summary>
    string? Fingerprint(AgentSession s, string roomId)
    {
        if (rooms.ViewsFor(roomId) is not { } b) return null;
        var seat = b.SeatOf(s.Nick);
        var raw = seat is { } i && b.SeatViews.TryGetValue(i, out var mine) ? mine : b.WatcherView;
        return $"{b.Summary.Status}|{b.Summary.Round}|{seat}|{chat.LastId()}|{JsonSerializer.Serialize(raw, Json)}";
    }
}
