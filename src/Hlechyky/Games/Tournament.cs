using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky.Games;

/// <summary>Місце одного гравця в одній грі турніру.</summary>
public sealed record TournamentPlace(string Nick, int Place, int Points, long? Score);

/// <summary>Одна зіграна (або пропущена) гра турніру.</summary>
public sealed record TournamentGameResult(string GameId, string Title, bool Skipped, IReadOnlyList<TournamentPlace> Places);

/// <summary>
/// Турнір на вечір: кілька ігор поспіль, турнірні очки за місця в кожній, чемпіон отримує 👑 до наступного турніру.
/// Турнір один на сайт і живе в пам'яті (як і столи); корона — у сховищі стану, щоб пережити рестарт.
/// <para>
/// Сервіс нічого не знає про SignalR, крім однієї речі: після кожної зміни розсилає всім подію <c>tournament</c>
/// зі знімком (<see cref="Snapshot"/>). Столи ставить сам через <see cref="Rooms"/> — так само, як це зробили б
/// гравці: господар турніру створює, решта сідає, господар тисне «Почати».
/// </para>
/// </summary>
public sealed class Tournament(Rooms rooms, Registry registry, GameEvents events, IOutbox outbox, IGameStore store,
    Presence presence, IHubContext<RadioHub> hub, IClock clock, ILogger<Tournament> log) : IHostedService
{
    public const int MinGames = 2, MaxGames = 6;
    const string CrownKey = "tournament:crown";

    public const string Gathering = "gathering", Playing = "playing", Between = "between", Done = "done";

    readonly object _lock = new();
    State? _t;
    string[]? _crown;

    sealed class State
    {
        public required string Id { get; init; }
        public required string Host { get; set; }
        public required List<string> Games { get; init; }
        public List<string> Players { get; } = [];
        public string Stage { get; set; } = Gathering;
        /// <summary>Скільки ігор уже зіграно (і номер наступної, рахуючи з нуля).</summary>
        public int Index { get; set; }
        public string? Room { get; set; }
        public Dictionary<string, int> Points { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TournamentGameResult> Results { get; } = [];
        public string[] Champions { get; set; } = [];
        public DateTimeOffset CreatedAt { get; init; }
    }

    public Task StartAsync(CancellationToken ct) { events.RoomFinished += OnRoomFinished; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { events.RoomFinished -= OnRoomFinished; return Task.CompletedTask; }

    // =========================================================================================
    // Дії
    // =========================================================================================

    /// <summary>Зібрати турнір. null — вийшло, інакше текст, чому ні.</summary>
    public string? Create(string nick, IReadOnlyList<string>? games)
    {
        var list = (games ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (list.Count is < MinGames or > MaxGames) return $"Обери від {MinGames} до {MaxGames} ігор";
        foreach (var g in list)
        {
            if (registry.Info(g) is not { } info) return "Нема такої гри";
            if (!Fits(info)) return $"«{info.Title}» — не для турніру: потрібна гра, у яку грають щонайменше вдвох";
        }
        lock (_lock)
        {
            if (_t is { Stage: not Done }) return "Турнір уже йде — приєднуйся до нього";
            _t = new State { Id = Guid.NewGuid().ToString("N")[..8], Host = nick, Games = list, CreatedAt = clock.UtcNow };
            _t.Players.Add(nick);
            _t.Points[nick] = 0;
        }
        outbox.Post(new Journal($"🏆 {nick} збирає турнір: {string.Join(" → ", list.Select(Title))}. Приєднуйтесь у «Іграх» → «Турнір»"));
        Changed();
        return null;
    }

    public string? Join(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема — збери свій";
            if (t.Players.Contains(nick, StringComparer.OrdinalIgnoreCase)) return "Ти вже в турнірі";
            t.Players.Add(nick);
            t.Points.TryAdd(nick, 0);
        }
        Changed();
        return null;
    }

    public string? Leave(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            var i = t.Players.FindIndex(p => string.Equals(p, nick, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return "Тебе нема в турнірі";
            t.Players.RemoveAt(i);
            // очки не забираємо: хто вже заробив — той у таблиці; а в збиранні людини просто нема
            if (t.Stage == Gathering) t.Points.Remove(nick);
            if (t.Players.Count == 0) { _t = null; }
            else if (string.Equals(t.Host, nick, StringComparison.OrdinalIgnoreCase)) t.Host = t.Players[0];
        }
        Changed();
        return null;
    }

    public string? Cancel(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            if (!CanLead(t, nick)) return "Скасувати може лише той, хто збирав турнір";
            if (t.Results.Count > 0) Finish(t, early: true);
            else _t = null;
        }
        if (_t is null) outbox.Post(new Journal($"🏆 Турнір скасовано"));
        Changed();
        return null;
    }

    /// <summary>
    /// Наступна гра: стіл ставить сервер — господар стола перший онлайн-учасник, решта сідає, і партія стартує.
    /// Учасники, яких зараз нема на сайті, цю гру пропускають (0 очок).
    /// </summary>
    public string? Next(string nick)
    {
        Outbox outs = new();
        string? roomId = null;
        string gameTitle;
        int no, of;
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            Refresh(t);
            if (!CanLead(t, nick)) return "Наступну гру запускає той, хто збирав турнір";
            if (t.Stage == Playing) return "Гра ще йде — спершу дограйте";
            if (t.Index >= t.Games.Count) return "Усі ігри вже зіграно";

            var gameId = t.Games[t.Index];
            var info = registry.Info(gameId)!;
            var here = t.Players.Where(presence.IsOnline).ToList();
            if (here.Count < Math.Max(2, info.MinPlayers)) return $"Для «{info.Title}» треба щонайменше {Math.Max(2, info.MinPlayers)} учасників на сайті";
            if (here.Count > info.MaxPlayers) return $"У «{info.Title}» грає щонайбільше {info.MaxPlayers}, а вас {here.Count}";
            if (info.Start == StartMode.WhenFull && here.Count != info.MaxPlayers) return $"«{info.Title}» стартує лише повним складом ({info.MaxPlayers})";
            var lobby = rooms.Snapshot();
            bool At(RoomSummary r, string p) => r.Seats.Any(s => string.Equals(s.Nick, p, StringComparison.OrdinalIgnoreCase));
            var busy = here.FirstOrDefault(p => lobby.Any(r => r.Status != "finished" && At(r, p)));
            if (busy is not null) return $"{busy} ще сидить за іншим столом — хай встане";
            // За дограним столом (зокрема минулою грою турніру) каркас теж тримає місце — звільняємо його самі.
            foreach (var r in lobby.Where(r => r.Status == "finished"))
                foreach (var p in here.Where(p => At(r, p))) outs.Adopt(rooms.Leave(r.Id, p).Out);

            var created = rooms.Create(here[0], gameId, null);
            outs.Adopt(created.Out);
            if (!created.Reply.Ok || created.Reply.RoomId is not { } id) return created.Reply.Message;
            foreach (var p in here.Skip(1))
            {
                var joined = rooms.Join(id, p);
                outs.Adopt(joined.Out);
                if (!joined.Reply.Ok)
                {
                    foreach (var q in here) outs.Adopt(rooms.Leave(id, q).Out);
                    Flush(outs);
                    return $"{p} не зміг сісти: {joined.Reply.Message}";
                }
            }
            if (info.Start == StartMode.ByHost)
            {
                var started = rooms.StartByHost(id, here[0]);
                outs.Adopt(started.Out);
                if (!started.Reply.Ok)
                {
                    foreach (var q in here) outs.Adopt(rooms.Leave(id, q).Out);
                    Flush(outs);
                    return started.Reply.Message;
                }
            }
            t.Room = roomId = id;
            t.Stage = Playing;
            gameTitle = info.Title;
            no = t.Index + 1;
            of = t.Games.Count;
        }
        outs.Add(new Journal($"🏆 Турнір, гра {no} з {of}: {gameTitle}. За стіл!", roomId));
        Flush(outs);
        Changed();
        return null;
    }

    /// <summary>Гра зависла (усі повставали, стіл прибрали) — рахуємо пропущеною й ідемо далі.</summary>
    public string? Skip(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: Playing } t) return "Зараз нема чого пропускати";
            if (!CanLead(t, nick)) return "Пропустити гру може той, хто збирав турнір";
            var room = t.Room is null ? null : rooms.Find(t.Room);
            if (room is not null && room.Status == RoomStatus.Playing) foreach (var p in room.Seats.OfType<string>().ToList()) Flush(rooms.Leave(room.Id, p).Out);
            // Leave посеред партії міг її закінчити — тоді результат уже порахував OnRoomFinished
            if (t.Stage == Playing) Skipped(t);
        }
        Changed();
        return null;
    }

    bool CanLead(State t, string nick) =>
        string.Equals(t.Host, nick, StringComparison.OrdinalIgnoreCase)
        // господаря нема на сайті — турнір не має стояти: веде будь-хто з учасників
        || !presence.IsOnline(t.Host) && t.Players.Contains(nick, StringComparer.OrdinalIgnoreCase);

    static bool Fits(GameInfo info) => !info.Solo && !info.Private && info.MaxPlayers >= 2;

    string Title(string gameId) => registry.Info(gameId)?.Title ?? gameId;

    void Flush(Outbox outs)
    {
        foreach (var m in outs) outbox.Post(m);
        outs.Clear();
    }

    // =========================================================================================
    // Результати
    // =========================================================================================

    void OnRoomFinished(RoomFinishedEvent e)
    {
        try
        {
            lock (_lock)
            {
                if (_t is not { Stage: Playing } t || t.Room != e.RoomId) return;
                var places = Places(e.Seats, e.Result.Scores, e.Result.Winners);
                foreach (var p in places) t.Points[p.Nick] = t.Points.GetValueOrDefault(p.Nick) + p.Points;
                t.Results.Add(new TournamentGameResult(e.GameId, e.Info.Title, false, places));
                // спершу результат гри, потім (якщо це була остання) — чемпіон: так і в Журналі читається
                var line = string.Join(", ", places.Select(p => $"{p.Place}. {p.Nick} +{p.Points}"));
                outbox.Post(new Journal($"🏆 Турнір, {e.Info.Title}: {line}"));
                Advance(t);
            }
            Changed();
        }
        catch (Exception ex) { log.LogWarning(ex, "турнір не порахував стіл {Room}", e.RoomId); }
    }

    /// <summary>
    /// Місця й турнірні очки за одну гру. Є рахунок — місце за рахунком (рівний рахунок — рівне місце); нема — переможці
    /// перші, решта другі, нічия — усі перші. Очки: гравців у грі − місце + 1 (утрьох: 3, 2, 1).
    /// </summary>
    public static List<TournamentPlace> Places(IReadOnlyList<string?> seats, IReadOnlyDictionary<int, long>? scores, int[] winners)
    {
        var who = seats.Select((n, i) => (Nick: n, Seat: i)).Where(x => !string.IsNullOrWhiteSpace(x.Nick)).ToList();
        var n = who.Count;
        List<(string Nick, int Place, long? Score)> ranked;
        if (scores is { Count: > 0 })
        {
            long Of(int seat) => scores.TryGetValue(seat, out var v) ? v : long.MinValue;
            ranked = [.. who.Select(x => (x.Nick!, 1 + who.Count(y => Of(y.Seat) > Of(x.Seat)), scores.TryGetValue(x.Seat, out var s) ? (long?)s : null))];
        }
        else
        {
            ranked = [.. who.Select(x => (x.Nick!, winners.Length == 0 || winners.Contains(x.Seat) ? 1 : 2, (long?)null))];
        }
        return [.. ranked.OrderBy(r => r.Place).ThenBy(r => r.Nick, StringComparer.OrdinalIgnoreCase)
            .Select(r => new TournamentPlace(r.Nick, r.Place, n - r.Place + 1, r.Score))];
    }

    void Skipped(State t)
    {
        t.Results.Add(new TournamentGameResult(t.Games[t.Index], Title(t.Games[t.Index]), true, []));
        outbox.Post(new Journal($"🏆 Турнір: «{Title(t.Games[t.Index])}» не дограли — гру пропущено"));
        Advance(t);
    }

    void Advance(State t)
    {
        t.Index++;
        t.Room = null;
        if (t.Index >= t.Games.Count) Finish(t, early: false);
        else t.Stage = Between;
    }

    void Finish(State t, bool early)
    {
        t.Stage = Done;
        t.Room = null;
        var best = t.Points.Count == 0 ? 0 : t.Points.Values.Max();
        t.Champions = best > 0 ? [.. t.Points.Where(kv => kv.Value == best).Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase)] : [];
        if (t.Champions.Length == 0)
        {
            outbox.Post(new Journal("🏆 Турнір закінчено — без чемпіона"));
            return;
        }
        _crown = t.Champions;
        var json = JsonSerializer.Serialize(new { nicks = t.Champions, at = clock.UtcNow });
        // Один короткий запис раз на турнір: під замком турніру (не кімнати) це нікому не заважає.
        try { store.SaveState(CrownKey, json); } catch (Exception ex) { log.LogWarning(ex, "корону не збережено"); }
        var who = string.Join(" і ", t.Champions);
        var table = string.Join(", ", t.Points.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
        outbox.Post(new Journal($"👑 {(early ? "Турнір закінчили достроково. " : "")}Чемпіон турніру — {who}! Таблиця: {table}"));
        outbox.Post(new DjSays($"Вітаю, {who}! Корона ваша до наступного турніру 👑"));
    }

    /// <summary>Стіл поточної гри зник, не дограний (усі встали ще в лобі, прибиральник) — не чекати ж вічно.</summary>
    void Refresh(State t)
    {
        if (t.Stage != Playing || t.Room is null) return;
        if (rooms.Find(t.Room) is null) Skipped(t);
    }

    // =========================================================================================
    // Знімок
    // =========================================================================================

    public string[] Crown()
    {
        lock (_lock)
        {
            if (_crown is not null) return _crown;
        }
        string[] loaded = [];
        try
        {
            if (store.LoadState(CrownKey) is { } json)
                loaded = JsonDocument.Parse(json).RootElement.GetProperty("nicks").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
        }
        catch (Exception) { /* зіпсований запис — просто без корони */ }
        lock (_lock) { return _crown ??= loaded; }
    }

    public object Snapshot()
    {
        var crown = Crown();
        lock (_lock)
        {
            if (_t is { } t0) Refresh(t0);
            if (_t is not { } t) return new { active = false, crown };
            var room = t.Room is null ? null : rooms.Find(t.Room);
            return new
            {
                active = t.Stage != Done,
                id = t.Id,
                host = t.Host,
                stage = t.Stage,
                index = t.Index,
                games = t.Games.Select(g => new { id = g, title = Title(g) }).ToArray(),
                players = t.Players.ToArray(),
                online = t.Players.Where(presence.IsOnline).ToArray(),
                standings = t.Points.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new { nick = kv.Key, points = kv.Value }).ToArray(),
                results = t.Results.Select(r => new
                {
                    game = r.GameId, title = r.Title, skipped = r.Skipped,
                    places = r.Places.Select(p => new { nick = p.Nick, place = p.Place, points = p.Points, score = p.Score }).ToArray(),
                }).ToArray(),
                room = room is null ? null : new { id = room.Id, status = room.Status.ToString().ToLowerInvariant() },
                champions = t.Champions,
                crown,
            };
        }
    }

    void Changed()
    {
        object snap;
        try { snap = Snapshot(); }
        catch (Exception ex) { log.LogWarning(ex, "знімок турніру не склався"); return; }
        _ = hub.Clients.All.SendAsync("tournament", snap);
    }

    /// <summary>Хтось зайшов або вийшов — список «хто зараз тут» у турнірі міг змінитись.</summary>
    public void PresenceChanged()
    {
        bool any;
        lock (_lock) any = _t is { Stage: not Done };
        if (any) Changed();
    }
}
