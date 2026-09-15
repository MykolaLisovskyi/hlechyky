using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Games;

/// <summary>Відповідь хаба тому, хто натиснув (PROTOCOL §1). Порожній Message — тоста не буде.</summary>
public sealed record RoomReply(bool Ok, string Message = "", string? RoomId = null)
{
    public static readonly RoomReply Done = new(true);
    public static RoomReply Fail(string message) => new(false, message);
}

/// <summary>
/// Опції кімнати так, як вони приходять з браузера. PROTOCOL §1 обіцяє, що <c>stake</c> — число, а решта
/// опцій — рядки, тож приймати треба сирий JSON: <c>{"stake": 5}</c> у <c>Dictionary&lt;string, string&gt;</c>
/// не поклався б узагалі (System.Text.Json кидає на число в рядковому полі).
/// </summary>
public static class RoomOptions
{
    /// <summary>
    /// Зводить будь-яке значення до рядка: рядок лишається собою, масив (кілька значень однієї опції) —
    /// рядком через кому, число й решта — своїм текстом.
    /// </summary>
    public static Dictionary<string, string>? From(IReadOnlyDictionary<string, JsonElement>? raw)
    {
        if (raw is null) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
            result[key] = Text(value);
        return result;
    }

    static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Array => string.Join(GameOption.Separator, value.EnumerateArray().Select(Text)),
        _ => value.GetRawText(),
    };
}

/// <summary>
/// Що розіслати після дії. Крім самих повідомлень тримає роботу, яку не можна робити під замком кімнати
/// (SQLite економіки, події сервісів): Rooms виконує її сама, щойно замок відпущено.
/// </summary>
public sealed class Outbox : List<Outgoing>
{
    readonly List<Action> _after = [];

    /// <summary>Відкласти роботу на «після замка».</summary>
    public void After(Action work) => _after.Add(work);

    public void Adopt(Outbox other)
    {
        AddRange(other);
        _after.AddRange(other._after);
    }

    /// <summary>Виконати відкладене. Впав підписник — це його біда, кімната від цього не ламається.</summary>
    public void RunAfter(ILogger log)
    {
        foreach (var work in _after)
            try { work(); }
            catch (Exception ex) { log.LogWarning(ex, "відкладена робота кімнати впала"); }
        _after.Clear();
    }
}

/// <summary>Наслідок дії над кімнатою: що розіслати всім і що відповісти тому, хто натиснув.</summary>
public sealed record RoomOutcome(Outbox Out, RoomReply Reply)
{
    public static RoomOutcome Fail(string message) => new(new Outbox(), RoomReply.Fail(message));
}

/// <summary>
/// Готова розкладка однієї кімнати для розсилки: шапка, вид кожного зайнятого місця, вид глядача і список
/// з'єднань, що дивляться. Будується під замком кімнати, летить уже поза ним.
/// </summary>
public sealed record RoomBroadcast(
    string RoomId,
    RoomSummary Summary,
    bool Hidden,
    IReadOnlyList<string?> Seats,
    IReadOnlyDictionary<int, object?> SeatViews,
    object? WatcherView,
    IReadOnlyList<string> Watchers)
{
    /// <summary>Яке місце тримає цей нік у цій кімнаті; null — глядач.</summary>
    public int? SeatOf(string? nick)
    {
        if (string.IsNullOrEmpty(nick)) return null;
        for (var i = 0; i < Seats.Count; i++)
            if (string.Equals(Seats[i], nick, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}

/// <summary>Тексти, які каркас каже гравцеві сам (PROTOCOL §1). Гра їх не дублює.</summary>
static class Say
{
    public const string NoNick = "Спершу скажи, як тебе кликати";
    public const string NoGame = "Такої гри тут нема";
    public const string NoRoom = "Такої кімнати вже нема";
    public const string Seated = "Ти вже за столом. Встань, якщо хочеш новий";
    public const string TooMany = "Столів уже задосить, дограйте ті, що є";
    public const string Already = "Ти вже в цій кімнаті";
    public const string NoSeats = "Місць уже нема";
    public const string Waiting = "Чекаємо на гравців";
    public const string Played = "Партію зіграно, тисни «Ще раз»";
    public const string NotPlaying = "Ти тут не граєш";
    public const string NoShards = "Бракує черепків на ставку";
    public const string TooFast = "Не так швидко";
    public const string HostOnly = "Почати може лише господар";
    public const string NotFinished = "Партія ще не скінчилась";
    public const string TooBig = "Забагато даних";
    public static string TooFew(int n) => $"Замало гравців, треба щонайменше {n}";
    public const string Broken = "партія зламалась, вибачте";
}

/// <summary>
/// Реєстр кімнат: створити, сісти, встати, почати, походити, «ще раз», подивитись збоку. Тримає список під
/// коротким <c>_lock</c>, а все, що міняє одну кімнату, — під її власним <see cref="Room.Sync"/>. SignalR тут
/// не згадується жодного разу: кожен метод повертає <see cref="Outbox"/>, а розсилає вже Broadcaster
/// (ARCHITECTURE §4.4, §5).
/// </summary>
public sealed class Rooms
{
    /// <summary>Більше живих мультиплеєрних кімнат за раз усе одно не роздивитись.</summary>
    public const int MaxRooms = 12;
    /// <summary>Скільки чекаємо на реконект, перш ніж звільнити місця (F5 і метро в тунелі).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    static readonly TimeSpan LobbyLife = TimeSpan.FromMinutes(30);
    static readonly TimeSpan FinishedLife = TimeSpan.FromMinutes(15);
    static readonly TimeSpan SoloLife = TimeSpan.FromMinutes(30);
    /// <summary>Ставки, які можна обрати при створенні кімнати.</summary>
    public static readonly int[] Stakes = [0, 5, 10, 25];
    /// <summary>Більший payload — це вже не хід, а щось не те (ARCHITECTURE §9).</summary>
    public const int MaxPayloadBytes = 8 * 1024;

    readonly Registry _registry;
    readonly IClock _clock;
    readonly GameEvents _events;
    readonly IStakes _stakes;
    readonly IGameStore _store;
    readonly IServiceProvider _services;
    readonly ILogger _log;

    readonly object _lock = new();
    readonly List<Room> _rooms = [];
    /// <summary>Ніки, чиї з'єднання зникли: тримаємо місце ще <see cref="Grace"/>, раптом це був F5.</summary>
    readonly Dictionary<string, DateTimeOffset> _offline = new(StringComparer.OrdinalIgnoreCase);
    long _seedCounter;

    public Rooms(Registry registry, IClock clock, GameEvents events, IStakes stakes, IGameStore store,
        IServiceProvider services, ILogger<Rooms>? log = null)
    {
        _registry = registry;
        _clock = clock;
        _events = events;
        _stakes = stakes;
        _store = store;
        _services = services;
        _log = log ?? NullLogger<Rooms>.Instance;
    }

    /// <summary>Сід кімнати замість випадкового — щоб тест міг відтворити партію. Проду не потрібен.</summary>
    public int? SeedOverride { get; set; }

    // ---------- лобі ----------

    /// <summary>Усі неприватні кімнати для події <c>rooms</c>.</summary>
    public List<RoomSummary> Snapshot()
    {
        var rooms = Live();
        var list = new List<RoomSummary>(rooms.Count);
        foreach (var room in rooms)
        {
            if (room.Info.Private) continue;
            // Крива гра не має коштувати нам усього лобі: без цього виняток із Game.SeatName вилетів би
            // аж в OnConnectedAsync, і до сайту не підключився б ніхто.
            try { lock (room.Sync) list.Add(room.Summary()); }
            catch (Exception ex) { _log.LogWarning(ex, "кімната {Room} не змогла показатись у лобі", room.Id); }
        }
        return list;
    }

    /// <summary>Кімната за id; null — уже нема. Broadcaster і хаб більше нічого про список не знають.</summary>
    public Room? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return _rooms.FirstOrDefault(r => r.Id == id);
    }

    List<Room> Live()
    {
        lock (_lock) return [.. _rooms];
    }

    // ---------- створення ----------

    /// <summary>Нова кімната; творець сідає на місце 0 і стає господарем. Соло-ігри йдуть через OpenSolo.</summary>
    public RoomOutcome Create(string nick, string gameId, IReadOnlyDictionary<string, string>? options)
    {
        if (!Named(nick)) return RoomOutcome.Fail(Say.NoNick);
        if (_registry.Info(gameId) is not { } info) return RoomOutcome.Fail(Say.NoGame);
        if (info.Solo) return OpenSolo(nick, gameId, null);

        var stake = ReadStake(info, options);
        if (stake > 0 && _stakes.Balance(nick) < stake) return RoomOutcome.Fail(Say.NoShards);

        // Гру будуємо до замка: Configure() — чужий код, під спільним замком йому не місце.
        Room room;
        try { room = Build(info, gameId, options, stake, null); }
        catch (GameError ex) { return RoomOutcome.Fail(ex.Message); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "гра {Game} не змогла налаштуватись", gameId);
            return RoomOutcome.Fail(Say.NoGame);
        }

        room.Seats[0] = nick;
        room.Host = nick;
        // Перевірка й додавання — однією секцією: інакше дві вкладки одного ніка створять два столи,
        // а дванадцятеро одночасних творців проб'ють MaxRooms.
        lock (_lock)
        {
            if (_rooms.Any(r => !r.Info.Solo && r.Has(nick))) return RoomOutcome.Fail(Say.Seated);
            if (_rooms.Count(r => !r.Info.Solo) >= MaxRooms) return RoomOutcome.Fail(Say.TooMany);
            _rooms.Add(room);
        }

        var outbox = new Outbox();
        var reply = new RoomReply(true, info.MinPlayers <= 1
            ? "Стіл готовий. Можна почати самому або дочекатись друзів"
            : "Стіл готовий. Треба ще одного гравця", room.Id);
        string? failed = null;
        lock (room.Sync)
        {
            if (info.Start == StartMode.Immediate || room.Full)
            {
                failed = StartRound(room, outbox);
                if (failed is null) reply = new RoomReply(true, "", room.Id);
            }
        }
        // Drop бере спільний замок, тому робиться поза замком кімнати: один напрямок вкладення на весь файл.
        if (failed is not null) { Drop(room); return new RoomOutcome(outbox, RoomReply.Fail(failed)); }
        if (!info.Private) outbox.Add(new LobbyChanged());
        outbox.Add(new RoomViews(room.Id));
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, reply);
    }

    /// <summary>
    /// Особиста кімната соло-гри: та сама, якщо вже відкрита, інакше нова — з відновленим станом, коли гра
    /// Persistent і в сховищі щось лежить.
    /// </summary>
    public RoomOutcome OpenSolo(string nick, string gameId, string? key)
    {
        if (!Named(nick)) return RoomOutcome.Fail(Say.NoNick);
        if (_registry.Info(gameId) is not { } info) return RoomOutcome.Fail(Say.NoGame);
        if (!info.Solo) return Create(nick, gameId, null);

        Game game;
        string wanted;
        try
        {
            game = _registry.Create(gameId)!;
            wanted = game.SoloKey(NickKey(nick), _clock);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "соло-гра {Game} не змогла назвати свій ключ", gameId);
            return RoomOutcome.Fail(Say.NoGame);
        }
        // Ключ від клієнта — лише якщо це його власний. Інакше «OpenSolo('clicker', 'clicker:оля')» відкривав би
        // Олине збереження в чужій кімнаті: продаж її глеків ішов би в чужий гаманець, а стан писався б під її ключем.
        if (!string.IsNullOrWhiteSpace(key) && !string.Equals(key, wanted, StringComparison.Ordinal))
            return RoomOutcome.Fail("Чужу кімнату не відкрити");

        Room? mine;
        lock (_lock) mine = _rooms.FirstOrDefault(r => r.Info.Solo && r.Key == wanted && r.Has(nick));
        if (mine is not null)
        {
            // Замок кімнати беремо вже поза спільним: _lock → room.Sync тут був би зворотним боком
            // вкладення, яке є в Create, тобто готовим дедлоком.
            lock (mine.Sync) mine.LastActivity = _clock.UtcNow;
            var back = new Outbox();
            back.Add(new RoomViews(mine.Id));
            return new RoomOutcome(back, new RoomReply(true, "", mine.Id));
        }

        Room room;
        try { room = Build(info, gameId, null, 0, wanted, game); }
        catch (GameError ex) { return RoomOutcome.Fail(ex.Message); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "гра {Game} не змогла налаштуватись", gameId);
            return RoomOutcome.Fail(Say.NoGame);
        }
        room.Seats[0] = nick;
        room.Host = nick;
        // Перевірка вище й додавання — під різними замками: дві вкладки одночасно збудували б дві кімнати на один ключ, і
        // спільні сервіси (цех гончарів) побачили б ті самі вироби з обох. Тож під замком шукаємо ще раз.
        Room? raced;
        lock (_lock)
        {
            raced = _rooms.FirstOrDefault(r => r.Info.Solo && r.Key == wanted && r.Has(nick));
            if (raced is null) _rooms.Add(room);
        }
        if (raced is not null)
        {
            var again = new Outbox();
            again.Add(new RoomViews(raced.Id));
            return new RoomOutcome(again, new RoomReply(true, "", raced.Id));
        }

        var outbox = new Outbox();
        string? failed;
        lock (room.Sync)
        {
            failed = StartRound(room, outbox);
            // Persistent-гра могла лишити стан із минулого разу — тоді чиста партія одразу поверх нього.
            if (failed is null && info.Persistent && _store.LoadState(wanted) is { Length: > 0 } saved)
            {
                try { room.Game.Load(saved); }
                catch (Exception ex) { _log.LogWarning(ex, "не вдалось відновити стан {Key}, граємо з чистого", wanted); }
            }
        }
        if (failed is not null) { Drop(room); return new RoomOutcome(outbox, RoomReply.Fail(failed)); }
        if (!info.Private) outbox.Add(new LobbyChanged());
        outbox.Add(new RoomViews(room.Id));
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, new RoomReply(true, "", room.Id));
    }

    Room Build(GameInfo info, string gameId, IReadOnlyDictionary<string, string>? options, int stake, string? key, Game? game = null)
    {
        game ??= _registry.Create(gameId)!;
        var effective = Effective(info, options);
        var now = _clock.UtcNow;
        var room = new Room
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Info = info,
            Game = game,
            Seats = new string?[info.MaxPlayers],
            Options = effective,
            Stake = stake,
            Key = key,
            Seed = SeedOverride ?? unchecked((int)(now.Ticks ^ Interlocked.Increment(ref _seedCounter))),
            CreatedAt = now,
            LastActivity = now,
        };
        game.Ctx = new RoomContext(room, this);
        game.Configure(effective);
        return room;
    }

    /// <summary>Опції з лобі, перевірені за паспортом гри: чого не передали — беремо типове, чого не знаємо — відкидаємо.</summary>
    static Dictionary<string, string> Effective(GameInfo info, IReadOnlyDictionary<string, string>? options)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var opt in info.Options ?? [])
        {
            var value = options is not null && options.TryGetValue(opt.Key, out var v) ? v : opt.Default;
            if (opt.Multi) value = Several(opt, value);
            else if (opt.Values.Count > 0 && !opt.Values.Any(x => x.Value == value)) value = opt.Default;
            result[opt.Key] = value;
        }
        return result;
    }

    /// <summary>
    /// Кілька значень однієї опції: лише знайомі, без повторів і в порядку паспорта — «science,космос,ukraine»
    /// стає «ukraine,science». «Усе» (<see cref="GameOption.Default"/>) серед обраних перемагає решту, а
    /// порожній вибір до нього й зводиться.
    /// </summary>
    static string Several(GameOption opt, string value)
    {
        var picked = GameOption.Split(value);
        if (opt.Values.Count == 0) return string.Join(GameOption.Separator, picked.Distinct(StringComparer.Ordinal));
        if (picked.Contains(opt.Default, StringComparer.Ordinal)) return opt.Default;
        var known = opt.Values.Select(x => x.Value).Where(x => picked.Contains(x, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        return known.Count == 0 ? opt.Default : string.Join(GameOption.Separator, known);
    }

    /// <summary>Ставка з опцій лобі. Дозволена лише там, де є що ділити: рівно двоє і партія рейтингова.</summary>
    static int ReadStake(GameInfo info, IReadOnlyDictionary<string, string>? options)
    {
        if (options is null || !options.TryGetValue("stake", out var raw)) return 0;
        if (!int.TryParse(raw, out var stake) || !Stakes.Contains(stake)) return 0;
        return info is { MaxPlayers: 2, Rated: true } ? stake : 0;
    }

    // ---------- місця ----------

    public RoomOutcome Join(string id, string nick)
    {
        if (!Named(nick)) return RoomOutcome.Fail(Say.NoNick);
        if (Find(id) is not { } room) return RoomOutcome.Fail(Say.NoRoom);
        if (room.Info.Private) return RoomOutcome.Fail(Say.NoRoom);

        lock (_lock)
        {
            if (_rooms.Any(r => r != room && !r.Info.Solo && r.Has(nick))) return RoomOutcome.Fail(Say.Seated);
        }

        var outbox = new Outbox();
        RoomReply reply;
        lock (room.Sync)
        {
            if (room.Has(nick)) return RoomOutcome.Fail(Say.Already);
            // Хтось дограв і пішов, а стіл лишився: новий гравець відкриває кімнату наново, як було зі столами.
            // Спершу — ВСІ перевірки: невдалий вхід не має псувати чужу дограну партію (результат зникав би
            // з картки, а «Ще раз» переможцеві вже не натиснути).
            var reopen = room.Status == RoomStatus.Finished && room.FreeSeat >= 0;
            if (room.Status != RoomStatus.Lobby && !reopen) return RoomOutcome.Fail(Say.NoSeats);
            var seat = room.FreeSeat;
            if (seat < 0) return RoomOutcome.Fail(Say.NoSeats);
            if (room.Stake > 0 && _stakes.Balance(nick) < room.Stake) return RoomOutcome.Fail(Say.NoShards);

            var was = (room.Status, room.Result, room.FinishedAt, room.Round);
            if (reopen)
            {
                room.Status = RoomStatus.Lobby;
                room.Result = null;
                room.FinishedAt = null;
                room.Round++;   // щоб ключі ставок наступної партії не збіглися з минулою
            }
            room.Seats[seat] = nick;
            room.LastActivity = _clock.UtcNow;
            reply = new RoomReply(true, $"Сів за {room.SafeSeatName(seat)}", room.Id);
            if (room.Info.Start == StartMode.WhenFull && room.Full && StartRound(room, outbox) is { } no)
            {
                room.Seats[seat] = null;
                (room.Status, room.Result, room.FinishedAt, room.Round) = was;
                return new RoomOutcome(outbox, RoomReply.Fail(no));
            }
        }
        outbox.Add(new LobbyChanged());
        outbox.Add(new RoomViews(room.Id));
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, reply);
    }

    /// <summary>Встати. Посеред партії це техпоразка — гра вирішує сама через OnLeave.</summary>
    public RoomOutcome Leave(string id, string nick)
    {
        if (Find(id) is not { } room) return RoomOutcome.Fail(Say.NoRoom);
        var outbox = new Outbox();
        lock (room.Sync)
        {
            if (room.SeatOf(nick) is not { } seat) return RoomOutcome.Fail(Say.NotPlaying);
            Vacate(room, seat, outbox);
        }
        Sweep(room, outbox);
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, new RoomReply(true, "Встав з-за столу", room.Id));
    }

    /// <summary>Звільнити місце. Кличеться під замком кімнати.</summary>
    void Vacate(Room room, int seat, Outbox outbox)
    {
        var nick = room.Seats[seat];
        room.LastActivity = _clock.UtcNow;
        // Місце звільняємо після OnLeave: типовий OnLeave пише в Журнал ім'я того, хто пішов, і рахує решту
        // сам (за «s != seat»), а RoomFinishedEvent має бачити повний склад — інакше рейтинг не знатиме, хто програв.
        // Соло — приватна головоломка: закрив вкладку з клікером — це не «встав з-за столу», і в спільні
        // Балачки про це писати нема чого (та й RoomFinishedEvent рейтингам тут ні до чого).
        if (room.Status == RoomStatus.Playing && !room.Info.Solo)
        {
            var ctx = (RoomContext)room.Game.Ctx;
            using (ctx.Collect(outbox))
            {
                try { room.Game.OnLeave(seat); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "OnLeave впав у кімнаті {Room}", room.Id);
                    ctx.Finish([], $"{room.Info.Title}: {Say.Broken}");
                }
            }
        }
        room.Seats[seat] = null;
        if (string.Equals(room.Host, nick, StringComparison.OrdinalIgnoreCase))
            room.Host = room.Seats.FirstOrDefault(s => s is not null) ?? room.Host;
        if (!room.Info.Private) outbox.Add(new LobbyChanged());
        outbox.Add(new RoomViews(room.Id));
    }

    /// <summary>Кімната без людей зникає одразу: дивитись у порожній стіл нема сенсу.</summary>
    void Sweep(Room room, Outbox outbox)
    {
        if (room.Occupied > 0) return;
        Drop(room);
        if (!room.Info.Private) outbox.Add(new LobbyChanged());
    }

    void Drop(Room room)
    {
        lock (_lock) _rooms.Remove(room);
    }

    // ---------- партія ----------

    /// <summary>Господар тисне «Почати» в грі з <see cref="StartMode.ByHost"/>.</summary>
    public RoomOutcome StartByHost(string id, string nick)
    {
        if (Find(id) is not { } room) return RoomOutcome.Fail(Say.NoRoom);
        var outbox = new Outbox();
        lock (room.Sync)
        {
            if (!string.Equals(room.Host, nick, StringComparison.OrdinalIgnoreCase)) return RoomOutcome.Fail(Say.HostOnly);
            if (room.Status != RoomStatus.Lobby) return RoomOutcome.Fail(room.Status == RoomStatus.Playing ? Say.Waiting : Say.Played);
            if (room.Occupied < room.Info.MinPlayers) return RoomOutcome.Fail(Say.TooFew(room.Info.MinPlayers));
            if (StartRound(room, outbox) is { } no) return new RoomOutcome(outbox, RoomReply.Fail(no));
        }
        outbox.Add(new LobbyChanged());
        outbox.Add(new RoomViews(room.Id));
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, new RoomReply(true, "", room.Id));
    }

    /// <summary>«Ще раз» тим самим складом: місця зсуваються, щоб починав інший, раунд росте, ставка списується знову.</summary>
    public RoomOutcome Rematch(string id, string nick)
    {
        if (Find(id) is not { } room) return RoomOutcome.Fail(Say.NoRoom);
        var outbox = new Outbox();
        lock (room.Sync)
        {
            if (!room.Has(nick)) return RoomOutcome.Fail(Say.NotPlaying);
            if (room.Status != RoomStatus.Finished) return RoomOutcome.Fail(Say.NotFinished);
            if (room.Occupied < room.Info.MinPlayers) return RoomOutcome.Fail(Say.TooFew(room.Info.MinPlayers));

            // Зсуваємо тих, хто сидить, по зайнятих місцях: у грі на двох це звичайний обмін ✕↔◯,
            // у компанії — «наступний починає», а порожні місця лишаються порожніми.
            var was = (string?[])room.Seats.Clone();
            var taken = Enumerable.Range(0, was.Length).Where(i => was[i] is not null).ToArray();
            for (var i = 0; i < taken.Length; i++) room.Seats[taken[i]] = was[taken[(i + 1) % taken.Length]];
            room.Round++;
            if (StartRound(room, outbox) is { } no)
            {
                room.Round--;
                room.Status = RoomStatus.Finished;
                Array.Copy(was, room.Seats, was.Length);
                return new RoomOutcome(outbox, RoomReply.Fail(no));
            }
        }
        outbox.Add(new LobbyChanged());
        outbox.Add(new RoomViews(room.Id));
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, new RoomReply(true, "Нова партія", room.Id));
    }

    /// <summary>Старт партії під замком кімнати. Повертає текст помилки або null, якщо все гаразд.</summary>
    string? StartRound(Room room, Outbox outbox)
    {
        room.Charged.Clear();
        if (room.Stake > 0)
        {
            foreach (var nick in room.Seats.Where(s => s is not null).Select(s => s!))
                if (_stakes.Balance(nick) < room.Stake) return Say.NoShards;
            foreach (var nick in room.Seats.Where(s => s is not null).Select(s => s!))
                if (_stakes.TrySpend(nick, room.Stake, "stake", $"stake:{room.Id}:{room.Round}:{NickKey(nick)}"))
                    room.Charged.Add(nick);
        }

        var now = _clock.UtcNow;
        room.Status = RoomStatus.Playing;
        room.StartedAt = now;
        room.FinishedAt = null;
        room.Result = null;
        room.Moves = 0;
        room.LastActivity = now;
        room.NextTickAt = now.AddMilliseconds(room.Info.TickMs);

        var ctx = (RoomContext)room.Game.Ctx;
        using (ctx.Collect(outbox))
        {
            try { room.Game.Start(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Start впав у кімнаті {Room}", room.Id);
                ctx.Finish([], $"{room.Info.Title}: {Say.Broken}");
                return null;
            }
        }

        // Журнал пишемо на кожен НОВИЙ склад, а не лише в першому раунді: «Ще раз» тими самими двома
        // (Rematch обертає місця) мовчить, а пара, що зібралась за тим самим столом наново, — оголошується.
        if (!room.Info.Solo && !SameCrew(room.LoggedSeats, room.Seats))
        {
            room.LoggedSeats = (string?[])room.Seats.Clone();
            outbox.Add(new Journal($"{Nicks(room)} сіли грати в {room.Info.Accusative}"));
        }
        return null;
    }

    /// <summary>Той самий склад за столом; порядок місць не рахується, бо «Ще раз» їх обертає.</summary>
    static bool SameCrew(string?[]? was, string?[] now)
    {
        if (was is null) return false;
        var a = was.Where(s => s is not null).Select(s => NickKey(s!)).Order(StringComparer.Ordinal).ToList();
        var b = now.Where(s => s is not null).Select(s => NickKey(s!)).Order(StringComparer.Ordinal).ToList();
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    static string Nicks(Room room)
    {
        var names = room.Seats.Where(s => s is not null).Select(s => s!).ToList();
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " і " + names[^1],
        };
    }

    // ---------- ходи ----------

    /// <summary>Покроковий хід. Помилку бачить лише той, хто ходив.</summary>
    public RoomOutcome Act(string id, string nick, string action, JsonElement payload)
    {
        if (!Named(nick)) return RoomOutcome.Fail(Say.NoNick);
        if (TooBig(payload)) return RoomOutcome.Fail(Say.TooBig);
        if (Find(id) is not { } room) return RoomOutcome.Fail(Say.NoRoom);

        var outbox = new Outbox();
        ActResult result;
        lock (room.Sync)
        {
            if (room.SeatOf(nick) is not { } seat) return RoomOutcome.Fail(Say.NotPlaying);
            if (room.Status == RoomStatus.Lobby) return RoomOutcome.Fail(Say.Waiting);
            if (room.Status == RoomStatus.Finished) return RoomOutcome.Fail(Say.Played);

            var ctx = (RoomContext)room.Game.Ctx;
            var before = room.Status;
            // Реалтайм ходів не має: RoomFinishedEvent.Moves для нього — 0 (так каже Contracts.cs), а види
            // після кожного повороту слати нема сенсу — кадри й так летять із тика.
            var counts = !room.Info.RealTime;
            if (counts) room.Moves++;   // хід рахуємо на вході, щоб Finish усередині Act бачив уже правильне число
            using (ctx.Collect(outbox))
            {
                try { result = room.Game.Act(seat, action ?? "", payload); }
                catch (GameError ex) { result = ActResult.Fail(ex.Message); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Act «{Action}» впав у кімнаті {Room}", action, room.Id);
                    ctx.Finish([], $"{room.Info.Title}: {Say.Broken}");
                    result = ActResult.Fail(Say.Broken);
                }
            }
            if (result.Ok)
            {
                room.LastActivity = _clock.UtcNow;
                Persist(room, outbox);
                if (counts) outbox.Add(new RoomViews(room.Id));
                if (room.Status != before) outbox.Add(new LobbyChanged());
            }
            else if (counts && room.Status == before)
            {
                room.Moves--;   // нелегальний хід ходом не був
            }
        }
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, new RoomReply(result.Ok, result.Message, room.Id));
    }

    /// <summary>
    /// Реалтайм-ввід: відповіді нема, помилки нікого не цікавлять — наступний кадр усе перемалює. Лише для ігор із
    /// тиком: Input не зберігає стан, і в покроковій persistent-грі це була б дія «на пробу» — наприклад, відповідь
    /// майстрові в Гончарному колі, чия невдача ніколи не дійшла б до бази (вийшов-зайшов — і спроби знову повні).
    /// </summary>
    public Outbox Input(string id, string nick, string action, JsonElement payload)
    {
        var outbox = new Outbox();
        if (!Named(nick) || TooBig(payload)) return outbox;
        if (Find(id) is not { } room || !room.Info.RealTime) return outbox;
        lock (room.Sync)
        {
            if (room.Status != RoomStatus.Playing || room.SeatOf(nick) is not { } seat) return outbox;
            var ctx = (RoomContext)room.Game.Ctx;
            using (ctx.Collect(outbox))
            {
                try { room.Game.Act(seat, action ?? "", payload); }
                catch (GameError) { /* мовчки: наступний кадр покаже правду */ }
                catch (Exception ex) { _log.LogWarning(ex, "Input «{Action}» впав у кімнаті {Room}", action, room.Id); }
            }
            room.LastActivity = _clock.UtcNow;
        }
        outbox.RunAfter(_log);
        return outbox;
    }

    static bool TooBig(JsonElement payload) =>
        payload.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
        && Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxPayloadBytes;

    void Persist(Room room, Outbox outbox)
    {
        if (!room.Info.Persistent || room.Key is null) return;
        var json = room.Game.Save();
        if (json is null) return;
        var key = room.Key;
        outbox.After(() => _store.SaveState(key, json));
    }

    // ---------- глядачі ----------

    /// <summary>Підписати з'єднання на кімнату. У приватну пускаємо лише господаря.</summary>
    public Outbox Watch(string id, string connId, string nick)
    {
        var outbox = new Outbox();
        if (Find(id) is not { } room) return outbox;
        if (room.Info.Private && !string.Equals(room.Host, nick, StringComparison.OrdinalIgnoreCase)) return outbox;
        // Повторний Watch тим самим з'єднанням — то вже підписаний: інакше клієнт у циклі множив би
        // розсилку на всіх глядачів кімнати в обхід квот хаба.
        if (!room.Watchers.TryAdd(connId, 0)) return outbox;
        outbox.Add(new RoomViews(room.Id));
        return outbox;
    }

    public Outbox Unwatch(string id, string connId)
    {
        var outbox = new Outbox();
        if (Find(id) is { } room) room.Watchers.TryRemove(connId, out _);
        return outbox;
    }

    /// <summary>З'єднання закрилось — прибрати його з усіх кімнат.</summary>
    public void DropWatcher(string connId)
    {
        foreach (var room in Live()) room.Watchers.TryRemove(connId, out _);
    }

    /// <summary>Готова розкладка кімнати для Broadcaster: види по місцях, вид глядача, список з'єднань.</summary>
    public RoomBroadcast? ViewsFor(string roomId)
    {
        if (Find(roomId) is not { } room) return null;
        try
        {
            lock (room.Sync)
            {
                var seatViews = new Dictionary<int, object?>();
                for (var i = 0; i < room.Seats.Length; i++)
                {
                    if (room.Seats[i] is null) continue;
                    seatViews[i] = SafeView(room, i);
                }
                return new RoomBroadcast(
                    room.Id, room.Summary(), room.Info.Hidden, (string?[])room.Seats.Clone(),
                    seatViews, SafeView(room, null), [.. room.Watchers.Keys]);
            }
        }
        catch (Exception ex)
        {
            // Пачку розсилки псувати не можна: одна крива кімната мовчки з'їдала б види й кадри всіх інших.
            _log.LogWarning(ex, "не вдалось скласти розкладку кімнати {Room}", room.Id);
            return null;
        }
    }

    object? SafeView(Room room, int? seat)
    {
        try { return room.Game.View(seat); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "View({Seat}) впав у кімнаті {Room}", seat, room.Id);
            return null;
        }
    }

    // ---------- присутність ----------

    /// <summary>Нік знову онлайн — жодних відліків, місце нікуди не дінеться.</summary>
    public void NoteOnline(string nick)
    {
        if (!Named(nick)) return;
        lock (_lock) _offline.Remove(nick);
    }

    /// <summary>Останнє з'єднання ніка закрилось: тримаємо місце ще <see cref="Grace"/>.</summary>
    public void NoteOffline(string nick, DateTimeOffset now)
    {
        if (!Named(nick)) return;
        lock (_lock) _offline[nick] = now;
    }

    /// <summary>Хто не повернувся за grace — того місця звільняються. Кличе TickEngine раз на секунду.</summary>
    public Outbox DropIfGone(DateTimeOffset now)
    {
        var outbox = new Outbox();
        List<string> gone;
        lock (_lock)
        {
            gone = [.. _offline.Where(p => now - p.Value >= Grace).Select(p => p.Key)];
            foreach (var nick in gone) _offline.Remove(nick);
        }
        foreach (var nick in gone) outbox.Adopt(DropNick(nick));
        return outbox;
    }

    /// <summary>Той самий відлік, але для одного ніка (зручно в тестах).</summary>
    public Outbox DropIfGone(string nick, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_offline.TryGetValue(nick, out var since) || now - since < Grace) return new Outbox();
            _offline.Remove(nick);
        }
        return DropNick(nick);
    }

    /// <summary>Звільнити всі місця ніка негайно: свідома зміна ніка — це те саме, що встати.</summary>
    public Outbox DropNick(string nick)
    {
        var outbox = new Outbox();
        if (!Named(nick)) return outbox;
        foreach (var room in Live())
        {
            // Соло-кімната переживає зникнення вкладки: її прибирає Housekeeping за SoloLife, а стан
            // Persistent-гри вже збережено. Інакше приватна головоломка гинула б через 20 с.
            if (room.Info.Solo) continue;
            lock (room.Sync)
            {
                if (room.SeatOf(nick) is not { } seat) continue;
                Vacate(room, seat, outbox);
            }
            Sweep(room, outbox);
        }
        outbox.RunAfter(_log);
        return outbox;
    }

    // ---------- тик і прибирання ----------

    /// <summary>Кімнати, яким час тикати.</summary>
    public List<Room> TickDue(DateTimeOffset now)
    {
        List<Room>? due = null;
        foreach (var room in Live())
        {
            if (room.Info.TickMs <= 0 || room.Status != RoomStatus.Playing || room.NextTickAt > now) continue;
            (due ??= []).Add(room);
        }
        return due ?? [];
    }

    /// <summary>
    /// Один крок реалтайм-кімнати. Пропущені тики не надолужуються: сервер міг спати, а гравці — ні.
    /// Виняток усередині гри не валить цикл: партія закривається нічиєю, решта кімнат живе далі.
    /// </summary>
    public Outbox Tick(Room room)
    {
        var outbox = new Outbox();
        lock (room.Sync)
        {
            if (room.Status != RoomStatus.Playing) return outbox;
            var now = _clock.UtcNow;
            room.NextTickAt = now.AddMilliseconds(room.Info.TickMs);
            var ctx = (RoomContext)room.Game.Ctx;
            TickResult result;
            using (ctx.Collect(outbox))
            {
                try { result = room.Game.Tick(); }
                catch (Exception ex)
                {
                    // Виходити звідси не можна: у _after уже лежать виплати, SaveState і подія для WP1,
                    // а їх ми обіцяли виконувати поза замком кімнати.
                    _log.LogWarning(ex, "тик впав у кімнаті {Room}", room.Id);
                    ctx.Finish([], $"{room.Info.Title}: {Say.Broken}");
                    result = TickResult.None;
                }
            }
            if (result.Frame && SafeFrame(room) is { } frame) outbox.Add(new RoomFrame(room.Id, frame));
            if (result.View) outbox.Add(new RoomViews(room.Id));
        }
        outbox.RunAfter(_log);
        return outbox;
    }

    object? SafeFrame(Room room)
    {
        try { return room.Game.Frame() ?? room.Game.View(null); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Frame() впав у кімнаті {Room}", room.Id);
            return null;
        }
    }

    /// <summary>Прибирання за ARCHITECTURE §4.4: порожні, засиджені в лобі й дограні кімнати.</summary>
    public Outbox Housekeeping(DateTimeOffset now)
    {
        var outbox = new Outbox();
        var removed = 0;
        var lobbyChanged = false;
        foreach (var room in Live())
        {
            bool drop;
            lock (room.Sync)
            {
                drop =
                    room.Occupied == 0
                    // самотній стіл у лобі — засиджений, навіть якщо гра дозволяє почати самому («Скільки?»)
                    || (room.Status == RoomStatus.Lobby && room.Occupied < Math.Max(2, room.Info.MinPlayers) && now - room.LastActivity > LobbyLife)
                    || (room.Status == RoomStatus.Finished && room.FinishedAt is { } at && now - at > FinishedLife)
                    || (room.Info.Solo && room.Watchers.IsEmpty && now - room.LastActivity > SoloLife);
            }
            if (!drop) continue;
            Drop(room);
            removed++;
            lobbyChanged |= !room.Info.Private;
        }
        if (lobbyChanged) outbox.Add(new LobbyChanged());
        if (removed > 0) _log.LogDebug("прибрано кімнат: {Count}", removed);
        return outbox;
    }

    // ---------- дрібниці ----------

    internal IClock Clock => _clock;
    internal IServiceProvider Services => _services;
    internal IStakes StakesService => _stakes;
    internal IGameStore Store => _store;
    internal GameEvents Events => _events;
    internal ILogger Log => _log;

    internal static string NickKey(string nick) => nick.Trim().ToLowerInvariant();

    static bool Named(string? nick) => !string.IsNullOrWhiteSpace(nick) && nick != "гість";
}

/// <summary>
/// Те, що гра просить у каркаса (реалізація <see cref="IRoomContext"/>). Живе один на кімнату; усі методи
/// кличуться під замком кімнати з Start/Act/Tick, тому нічого важкого тут не робиться — виплати ставок,
/// збереження стану і події сервісів відкладаються в <see cref="Outbox.After"/> і виконуються поза замком.
/// </summary>
sealed class RoomContext(Room room, Rooms rooms) : IRoomContext
{
    readonly Random _rng = new(room.Seed);
    Outbox? _out;

    public string RoomId => room.Id;
    public int Players => room.Occupied;
    public int Round => room.Round;
    public Random Rng => _rng;
    public IClock Clock => rooms.Clock;
    public IReadOnlyDictionary<string, string> Options => room.Options;
    public IServiceProvider Services => rooms.Services;

    public string? NickOf(int seat) => seat >= 0 && seat < room.Seats.Length ? room.Seats[seat] : null;

    public bool Seated(int seat) => NickOf(seat) is not null;

    /// <summary>Куди складати розсилку, поки гра щось робить. Поза цим блоком контекст мовчить.</summary>
    public IDisposable Collect(Outbox outbox)
    {
        _out = outbox;
        return new Scope(this);
    }

    public void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null)
    {
        if (room.Status == RoomStatus.Finished)
        {
            rooms.Log.LogWarning("другий Finish у кімнаті {Room} — ігнорую", room.Id);
            return;
        }
        var now = rooms.Clock.UtcNow;
        winners = winners.Where(s => s >= 0 && s < room.Seats.Length).Distinct().Order().ToArray();
        room.Result = new RoomResult(winners, winners.Length == 0, log, scores);
        room.Status = RoomStatus.Finished;
        room.FinishedAt = now;
        room.LastActivity = now;

        var outbox = _out;
        if (!string.IsNullOrWhiteSpace(log)) outbox?.Add(new Journal(log));
        outbox?.Add(new LobbyChanged());
        outbox?.Add(new RoomViews(room.Id));

        Payout(winners);
        Store();

        var finished = new RoomFinishedEvent(
            room.Id, room.Info.Id, room.Info, room.Round, (string?[])room.Seats.Clone(),
            room.Result, room.Stake, room.StartedAt ?? now, now, room.Moves);
        outbox?.After(() => rooms.Events.Raise(finished));
    }

    /// <summary>Банк переможцям порівну, нічия — повернення. Ідемпотентність тримає ref, тож подвійний Finish не подвоїть виплату.</summary>
    void Payout(int[] winners)
    {
        if (room.Stake <= 0 || room.Charged.Count == 0) return;
        var bank = room.Stake * room.Charged.Count;
        var round = room.Round;
        var id = room.Id;
        var stakes = rooms.StakesService;

        var won = winners.Select(s => room.Seats[s]).Where(n => n is not null).Select(n => n!).ToList();
        if (won.Count == 0)
        {
            foreach (var nick in room.Charged.ToList())
                _out?.After(() => stakes.Grant(nick, room.Stake, "stake-refund", $"stake-refund:{id}:{round}:{Rooms.NickKey(nick)}"));
            return;
        }
        var share = bank / won.Count;
        var extra = bank - share * won.Count;   // решту від ділення забирає перший переможець, щоб банк зійшовся
        for (var i = 0; i < won.Count; i++)
        {
            var nick = won[i];
            var amount = share + (i == 0 ? extra : 0);
            if (amount <= 0) continue;
            _out?.After(() => stakes.Grant(nick, amount, "stake-win", $"stake-win:{id}:{round}:{Rooms.NickKey(nick)}"));
        }
    }

    void Store()
    {
        if (!room.Info.Persistent || room.Key is null) return;
        var json = room.Game.Save();
        if (json is null) return;
        var key = room.Key;
        var store = rooms.Store;
        _out?.After(() => store.SaveState(key, json));
    }

    public void Log(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) _out?.Add(new Journal(text));
    }

    public void Say(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) _out?.Add(new DjSays(text));
    }

    public void Score(int seat, long value)
    {
        if (NickOf(seat) is not { } nick) return;
        var e = new SoloScoreEvent(room.Info.Id, nick, value, room.Info.Score, room.Key, rooms.Clock.UtcNow);
        _out?.After(() => rooms.Events.Raise(e));
    }

    /// <summary>
    /// Нуль черепків — не «нічого не сталось», а домовлений сигнал: <c>Award(seat, 0, "ach:&lt;key&gt;")</c> —
    /// це прохання видати ачівку, якої платформа сама не побачить (ARCHITECTURE §4.3, §8), а
    /// <c>Award(seat, 0, "daily:&lt;гра&gt;")</c> — «заплати типову щоденну». Тому відсікаємо лише мінус.
    /// </summary>
    public void Award(int seat, int shards, string reason)
    {
        if (shards < 0 || string.IsNullOrWhiteSpace(reason) || NickOf(seat) is not { } nick) return;
        var e = new AwardEvent(room.Info.Id, room.Id, nick, shards, reason);
        _out?.After(() => rooms.Events.Raise(e));
    }

    sealed class Scope(RoomContext ctx) : IDisposable
    {
        public void Dispose() => ctx._out = null;
    }
}
