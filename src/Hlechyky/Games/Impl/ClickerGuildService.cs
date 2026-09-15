using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>Дарунок у скриньці чи на полиці: від кого, що саме і коли.</summary>
public sealed record GuildGift(string From, string Ware, string Style, int Quality, DateTimeOffset At);

/// <summary>Підціль воза: стільки виробів цього виду (будь-який розпис і якість).</summary>
public sealed record WagonSub(string Ware, int Need, int Have);

/// <summary>Хто скільки поклав на віз (без місць і «ти останній» — лише внесок).</summary>
public sealed record WagonGiver(string Nick, int N);

/// <summary>
/// Віз одного тижня очима одного гончаря: ціль, що вже лежить, рівень (0 — ще ні, 1 бронза, 2 срібло, 3 золото),
/// скільки дав він сам і на який рівень уже забрав нагороду.
/// </summary>
public sealed record WagonInfo(
    string Week, DateTimeOffset EndsAt, int Potters, int Goal, int Total, IReadOnlyList<WagonSub> Subs,
    IReadOnlyList<WagonGiver> Givers, int Tier, int Mine, int Claimed);

/// <summary>Усе, що кімнаті треба від цеху для виду: цей тиждень, минулий і скільки дарунків лишилось сьогодні.</summary>
public sealed record GuildSummary(WagonInfo Week, WagonInfo Prev, int GiftsLeft);

/// <summary>Що сталось після внеску: віз після нього і рівень, якого він щойно вперше досяг (0 — нічого нового).</summary>
public sealed record WagonGive(WagonInfo Wagon, int Reached);

/// <summary>Нагорода воза: за який тиждень, який рівень і на який уже платили раніше. <c>Error</c> — відмова.</summary>
public sealed record WagonClaim(string? Error, string Week, int Tier, int Was);

/// <summary>
/// Цех гончарів — спільне для всіх кімнат Гончарного кола: тижневий віз, скринька дарунків і список гончарів. Один
/// на сервер, свій замок, стан — JSON у <see cref="IGameStore"/> під ключем <see cref="StoreKey"/>, пишеться після
/// кожної зміни. Посилань на кімнати не тримає — лише дані за ніком у нижньому регістрі (як <c>Rooms.NickKey</c>).
///
/// Кімната кличе його з-під свого замка (Act/Sync), тож усередині — жодних викликів у кімнати: інакше два замки
/// в різному порядку. Час приходить параметром від гри (<c>Ctx.Clock</c>): так дві кімнати в тестах живуть кожна
/// своїм годинником, а сервіс нічого не вгадує. Власний <see cref="IClock"/> — лише для ендпоінтів.
///
/// Дух: друзів 2–4, тож нічого змагального. Ціль воза росте з кількістю гончарів минулого тижня, нагорода однакова
/// кожному, хто поклав хоч п'ять виробів, пропущений тиждень нічого не забирає.
/// </summary>
public sealed class ClickerGuildService
{
    public const string StoreKey = "clicker-guild";
    /// <summary>Скільки виробів на воза «важить» один гончар (бронза). Мінімум — двоє, навіть коли грає один.</summary>
    public const int PerPotter = 50, MinPotters = 2;
    /// <summary>Хто поклав на віз хоч стільки — забирає нагороду.</summary>
    public const int MinGive = 5;
    public const int GiftsPerDay = 3, ShelfSize = 12, MailMax = 40;
    /// <summary>Скільки тижнів тримати в стані: поточний, минулий (його нагорода ще забирається) і ще два про запас.</summary>
    public const int WeeksKept = 4;
    public const int RosterMax = 100;
    /// <summary>Пороги рівнів у частках цілі: бронза 100 %, срібло 150 %, золото 200 %.</summary>
    public static readonly double[] TierShare = [0, 1, 1.5, 2];
    /// <summary>Хвилини власного пасиву за рівень.</summary>
    public static readonly int[] TierMinutes = [0, 30, 60, 120];
    public static readonly string[] TierNames = ["", "бронза", "срібло", "золото"];
    /// <summary>«Нагороду за бронзу» — знахідний відмінок.</summary>
    static readonly string[] TierNamesAcc = ["", "бронзу", "срібло", "золото"];

    /// <summary>З яких виробів складаються підцілі: найпоширеніші, відкриті майже в кожного, і їхні частки від цілі.</summary>
    static readonly (string Ware, double Share)[] SubPool =
        [("pot", 0.25), ("bowl", 0.2), ("jug", 0.15), ("makitra", 0.12), ("dish", 0.1)];

    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    readonly object _lock = new();
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger? _log;
    State? _state;

    public ClickerGuildService(IGameStore store, IClock clock, ILogger<ClickerGuildService>? log = null)
    {
        _store = store;
        _clock = clock;
        _log = log;
    }

    // ---------- стан ----------

    sealed class State
    {
        public Dictionary<string, WeekRow> Weeks { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, PotterRow> Potters { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<GuildGift>> Mail { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, SentRow> Sent { get; set; } = new(StringComparer.Ordinal);
    }

    sealed class WeekRow
    {
        public int Total { get; set; }
        public Dictionary<string, int> Wares { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, GiverRow> Givers { get; set; } = new(StringComparer.Ordinal);
        /// <summary>На який рівень кожен уже забрав нагороду.</summary>
        public Dictionary<string, int> Claimed { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Найвищий рівень, про який уже сказали в Журнал: двічі одне й те саме не кажемо.</summary>
        public int Logged { get; set; }
    }

    sealed class GiverRow
    {
        public string Nick { get; set; } = "";
        public int N { get; set; }
    }

    sealed class PotterRow
    {
        public string Nick { get; set; } = "";
        public int Rank { get; set; }
        public DateTimeOffset Seen { get; set; }
    }

    sealed class SentRow
    {
        public string Day { get; set; } = "";
        public int N { get; set; }
    }

    /// <summary>Нік у ключ — так само, як <c>Rooms.NickKey</c>: без пробілів по краях, у нижньому регістрі.</summary>
    public static string Key(string? nick) => (nick ?? "").Trim().ToLowerInvariant();

    /// <summary>Стан читаємо з бази при першому зверненні, а не в конструкторі: DI будує сервіс раніше, ніж база готова.</summary>
    State S()
    {
        if (_state is not null) return _state;
        State? s = null;
        try
        {
            if (_store.LoadState(StoreKey) is { Length: > 0 } json) s = JsonSerializer.Deserialize<State>(json, Wire);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "стан цеху гончарів не прочитався — починаємо з чистого");
        }
        s ??= new State();
        // Чого не було в старому стані (чи в руках, що правили базу) — порожнє, а не null.
        s.Weeks = Clean(s.Weeks);
        s.Potters = Clean(s.Potters);
        s.Mail = Clean(s.Mail);
        s.Sent = Clean(s.Sent);
        foreach (var w in s.Weeks.Values)
        {
            w.Wares = Clean(w.Wares);
            w.Givers = Clean(w.Givers);
            w.Claimed = Clean(w.Claimed);
        }
        return _state = s;
    }

    static Dictionary<string, T> Clean<T>(Dictionary<string, T>? d)
    {
        var r = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (k, v) in d ?? []) if (k is not null && v is not null) r[k] = v;
        return r;
    }

    /// <summary>Записати стан. Кличеться під замком — порядок записів той самий, що й порядок змін.</summary>
    void Save()
    {
        try { _store.SaveState(StoreKey, JsonSerializer.Serialize(_state, Wire)); }
        catch (Exception ex) { _log?.LogWarning(ex, "стан цеху гончарів не записався"); }
    }

    // ---------- тиждень за Києвом ----------

    static DateTime KyivDate(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Days.Kyiv).Date;

    static string WeekOfDate(DateTime d) =>
        $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d).ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>ISO-тиждень за київським календарем: «2026-W38». Рядки порівнюються як дати.</summary>
    public static string WeekOf(DateTimeOffset now) => WeekOfDate(KyivDate(now));

    /// <summary>Тиждень перед тим, у якому <paramref name="now"/>.</summary>
    public static string WeekBefore(DateTimeOffset now) => WeekOfDate(KyivDate(now).AddDays(-7));

    /// <summary>Київська північ наступного понеділка — коли віз цього тижня від'їжджає.</summary>
    public static DateTimeOffset WeekEnds(DateTimeOffset now)
    {
        var d = KyivDate(now);
        var monday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7) + 7);
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(monday, DateTimeKind.Unspecified), Days.Kyiv);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    // ---------- ціль воза ----------

    /// <summary>
    /// Ціль воза: <see cref="PerPotter"/> виробів на гончаря ×0,85–1,15 (зерно тижня), до п'ятірок; і дві-три підцілі
    /// з найпоширеніших виробів — кожна своя частка цілі ×0,8–1,2. Та сама пара (тиждень, гончарі) — та сама ціль.
    /// </summary>
    public static (int Goal, IReadOnlyList<(string Ware, int Need)> Subs) GoalFor(string week, int potters)
    {
        potters = Math.Max(MinPotters, potters);
        var rng = new Random(Days.Seed("clicker-wagon", week));
        var goal = Fives(PerPotter * potters * (0.85 + 0.3 * rng.NextDouble()));
        var pool = SubPool.OrderBy(_ => rng.Next()).ToList();
        var count = 2 + rng.Next(2);
        var subs = pool.Take(count)
            .OrderBy(x => Array.FindIndex(SubPool, p => p.Ware == x.Ware))
            .Select(x => (x.Ware, Math.Max(5, Fives(goal * x.Share * (0.8 + 0.4 * rng.NextDouble())))))
            .ToList();
        return (goal, subs);
    }

    static int Fives(double v) => Math.Max(5, (int)Math.Round(v / 5, MidpointRounding.AwayFromZero) * 5);

    /// <summary>Рівень воза: доки хоч одна підціль не закрита — нуль; далі за часткою цілі.</summary>
    public static int TierOf(int goal, int total, IEnumerable<WagonSub> subs)
    {
        if (goal <= 0 || subs.Any(s => s.Have < s.Need)) return 0;
        for (var t = TierShare.Length - 1; t >= 1; t--)
            if (total >= (int)Math.Ceiling(goal * TierShare[t])) return t;
        return 0;
    }

    /// <summary>Скільки гончарів «важить» тиждень: ті, хто давав хоч щось тижнем раніше (не менше двох).</summary>
    static int PottersFor(State s, string before) =>
        Math.Max(MinPotters, s.Weeks.TryGetValue(before, out var w) ? w.Givers.Values.Count(g => g.N > 0) : 0);

    WagonInfo Info(State s, string week, string before, DateTimeOffset endsAt, string nickKey)
    {
        var potters = PottersFor(s, before);
        var (goal, subs) = GoalFor(week, potters);
        s.Weeks.TryGetValue(week, out var row);
        var subRows = subs.Select(x => new WagonSub(x.Ware, x.Need, row?.Wares.GetValueOrDefault(x.Ware) ?? 0)).ToList();
        var total = row?.Total ?? 0;
        var givers = (row?.Givers.Values ?? Enumerable.Empty<GiverRow>())
            .Where(g => g.N > 0)
            // За абеткою, не за внеском: у цеху не змагаються, хто більше.
            .OrderBy(g => g.Nick, StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), true))
            .Select(g => new WagonGiver(g.Nick, g.N))
            .ToList();
        return new WagonInfo(week, endsAt, potters, goal, total, subRows, givers, TierOf(goal, total, subRows),
            row?.Givers.GetValueOrDefault(nickKey)?.N ?? 0, row?.Claimed.GetValueOrDefault(nickKey) ?? 0);
    }

    WagonInfo Current(State s, DateTimeOffset now, string nickKey) =>
        Info(s, WeekOf(now), WeekBefore(now), WeekEnds(now), nickKey);

    WagonInfo Previous(State s, DateTimeOffset now, string nickKey)
    {
        var d = KyivDate(now).AddDays(-7);
        return Info(s, WeekOfDate(d), WeekOfDate(d.AddDays(-7)), WeekEnds(now).AddDays(-7), nickKey);
    }

    /// <summary>Віз цього тижня, минулого і скільки дарунків ще можна сьогодні — для виду кімнати.</summary>
    public GuildSummary Summary(string nickKey, DateTimeOffset now)
    {
        lock (_lock)
        {
            var s = S();
            return new GuildSummary(Current(s, now, nickKey), Previous(s, now, nickKey), GiftsLeftLocked(s, nickKey, now));
        }
    }

    /// <summary>Покласти вироби на віз цього тижня. Вироби вже забрала з комори кімната — тут лише облік.</summary>
    public WagonGive Give(string nickKey, string nick, string ware, int n, DateTimeOffset now)
    {
        lock (_lock)
        {
            var s = S();
            var week = WeekOf(now);
            if (n <= 0) return new WagonGive(Current(s, now, nickKey), 0);
            if (!s.Weeks.TryGetValue(week, out var row)) s.Weeks[week] = row = new WeekRow();
            row.Total += n;
            row.Wares[ware] = row.Wares.GetValueOrDefault(ware) + n;
            if (!row.Givers.TryGetValue(nickKey, out var giver)) row.Givers[nickKey] = giver = new GiverRow();
            giver.Nick = nick;
            giver.N += n;
            var info = Current(s, now, nickKey);
            var reached = info.Tier > row.Logged ? info.Tier : 0;
            row.Logged = Math.Max(row.Logged, info.Tier);
            Prune(s, now);
            Save();
            return new WagonGive(info, reached);
        }
    }

    /// <summary>
    /// Забрати нагороду воза: цього тижня або минулого (до кінця цього). Хто поклав хоч <see cref="MinGive"/>, забирає
    /// рівень, якого віз досяг; віз потім доріс — можна добрати різницю. Без <paramref name="week"/> — спершу минулий.
    /// </summary>
    public WagonClaim Claim(string nickKey, string? week, DateTimeOffset now)
    {
        lock (_lock)
        {
            var s = S();
            var cur = Current(s, now, nickKey);
            var prev = Previous(s, now, nickKey);
            WagonInfo w;
            if (string.IsNullOrEmpty(week)) w = Claimable(prev) ? prev : cur;
            else if (week == cur.Week) w = cur;
            else if (week == prev.Week) w = prev;
            else return new WagonClaim("Той віз уже давно поїхав — нагороди за нього не забрати", week, 0, 0);

            if (w.Mine < MinGive)
                return new WagonClaim($"Нагорода — тим, хто поклав на віз хоч {MinGive} виробів (у тебе {w.Mine})", w.Week, 0, 0);
            if (w.Tier == 0) return new WagonClaim("Віз ще не наповнився навіть до бронзи — докладаймо разом", w.Week, 0, 0);
            if (w.Claimed >= w.Tier) return new WagonClaim($"Нагороду за {TierNamesAcc[w.Tier]} ти вже забрав", w.Week, 0, 0);

            if (!s.Weeks.TryGetValue(w.Week, out var row)) return new WagonClaim("Такого воза нема", w.Week, 0, 0);
            row.Claimed[nickKey] = w.Tier;
            Save();
            return new WagonClaim(null, w.Week, w.Tier, w.Claimed);
        }
    }

    static bool Claimable(WagonInfo w) => w.Mine >= MinGive && w.Tier > w.Claimed;

    /// <summary>Старі тижні — геть: нагорода за них однаково вже не забирається.</summary>
    static void Prune(State s, DateTimeOffset now)
    {
        var oldest = WeekOfDate(KyivDate(now).AddDays(-7 * (WeeksKept - 1)));
        foreach (var key in s.Weeks.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) s.Weeks.Remove(key);
    }

    // ---------- дарунки ----------

    int GiftsLeftLocked(State s, string nickKey, DateTimeOffset now) =>
        s.Sent.TryGetValue(nickKey, out var r) && r.Day == Days.Of(now) ? Math.Max(0, GiftsPerDay - r.N) : GiftsPerDay;

    public int GiftsLeft(string nickKey, DateTimeOffset now)
    {
        lock (_lock) return GiftsLeftLocked(S(), nickKey, now);
    }

    /// <summary>
    /// Відправити дарунок у скриньку друга. null — відправлено; інакше — чому ні. Виріб з комори забирає кімната
    /// лише після «так». Отримувач мусить мати збереження кола — дарувати в нікуди не даємо.
    /// </summary>
    public string? Gift(string fromKey, string fromNick, string toNick, ItemInfo item, DateTimeOffset now)
    {
        var toKey = Key(toNick);
        if (toKey.Length == 0) return "Кому дарувати? Обери гончаря";
        if (toKey == fromKey) return "Собі дарувати — то вже не дарунок 🙂";
        // База — поза замком: читати її довго, а скринька від цього не зміниться.
        string? save;
        try { save = _store.LoadState("clicker:" + toKey); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "не вдалось глянути, чи є збереження в {Nick}", toKey);
            save = null;
        }
        if (string.IsNullOrEmpty(save)) return $"{toNick.Trim()} ще не сідав за гончарне коло — дарунку нікуди стати";
        lock (_lock)
        {
            var s = S();
            var today = Days.Of(now);
            if (!s.Sent.TryGetValue(fromKey, out var sent) || sent.Day != today) s.Sent[fromKey] = sent = new SentRow { Day = today };
            if (sent.N >= GiftsPerDay) return $"Сьогодні вже {GiftsPerDay} дарунки — щедрість повернеться завтра";
            if (!s.Mail.TryGetValue(toKey, out var box)) s.Mail[toKey] = box = [];
            box.Add(new GuildGift(fromNick.Trim(), item.Ware, item.Style, item.Quality, now));
            if (box.Count > MailMax) box.RemoveRange(0, box.Count - MailMax);
            sent.N++;
            Save();
            return null;
        }
    }

    /// <summary>Забрати все зі скриньки (кличе Sync кімнати отримувача). Порожньо — null, і нічого не пишемо.</summary>
    public List<GuildGift>? TakeMail(string nickKey)
    {
        lock (_lock)
        {
            var s = S();
            if (!s.Mail.Remove(nickKey, out var box) || box.Count == 0) return null;
            Save();
            return box;
        }
    }

    // ---------- гончарі ----------

    /// <summary>Гончар відкрив коло (чи дістав новий ранг): у список цеху. Пишемо, лише коли щось змінилось чи настав новий день.</summary>
    public void Hello(string nickKey, string nick, int rank, DateTimeOffset now)
    {
        if (nickKey.Length == 0) return;
        lock (_lock)
        {
            var s = S();
            if (s.Potters.TryGetValue(nickKey, out var p) && p.Nick == nick && p.Rank == rank && Days.Of(p.Seen) == Days.Of(now)) return;
            s.Potters[nickKey] = new PotterRow { Nick = nick, Rank = rank, Seen = now };
            if (s.Potters.Count > RosterMax)
                foreach (var old in s.Potters.OrderBy(x => x.Value.Seen).Take(s.Potters.Count - RosterMax).Select(x => x.Key).ToList())
                    s.Potters.Remove(old);
            Save();
        }
    }

    /// <summary><c>GET /api/games/clicker/guild</c>: гончарі цеху (за абеткою) і віз цього тижня.</summary>
    public object Roster(string? meNick)
    {
        var now = _clock.UtcNow;
        var me = Key(meNick);
        lock (_lock)
        {
            var s = S();
            var week = Current(s, now, me);
            s.Weeks.TryGetValue(week.Week, out var row);
            return new
            {
                week = WagonView(week),
                potters = s.Potters
                    .OrderBy(x => x.Value.Nick, StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), true))
                    .Select(x => new
                    {
                        nick = x.Value.Nick, rank = x.Value.Rank, seenAt = x.Value.Seen,
                        gave = row?.Givers.GetValueOrDefault(x.Key)?.N ?? 0, me = x.Key == me,
                    })
                    .ToList(),
            };
        }
    }

    /// <summary>Віз у форму для дроту — однакова в розі кімнати й у списку цеху.</summary>
    public static object WagonView(WagonInfo w) => new
    {
        id = w.Week, endsAt = w.EndsAt, potters = w.Potters, goal = w.Goal, total = w.Total,
        subs = w.Subs.Select(x => new { ware = x.Ware, need = x.Need, have = x.Have }),
        givers = w.Givers.Select(g => new { nick = g.Nick, n = g.N }),
        tier = w.Tier, mine = w.Mine, claimed = w.Claimed,
        pct = w.Goal > 0 ? Math.Round(100.0 * w.Total / w.Goal, 1) : 0,
    };

    // ---------- хата друга ----------

    /// <summary><c>GET /api/games/clicker/house?nick=</c>: публічний знімок зі збереження друга або null.</summary>
    public object? House(string? nick)
    {
        var key = Key(nick);
        if (key.Length == 0) return null;
        string? json;
        try { json = _store.LoadState("clicker:" + key); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "хата {Nick} не прочиталась", key);
            return null;
        }
        string display;
        lock (_lock) display = S().Potters.TryGetValue(key, out var p) ? p.Nick : (nick ?? "").Trim();
        return HouseSnapshot(display, json);
    }

    /// <summary>
    /// Публічне зі збереження кола — і ніщо інше. Чиста функція: береться лише перелічене (рівні драбини, прикраси,
    /// знаряддя, розписи, альбом і кахлі — якщо такі поля є, ранг, полиця дарунків, вироби, найкращі з комори, глеки
    /// за весь час). Око майстра, глеки в кишені, купці, скринька — не йдуть. Зіпсований JSON — null.
    /// </summary>
    public static object? HouseSnapshot(string nick, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonObject root;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            root = o;
        }
        catch (JsonException) { return null; }

        var upgrades = root["upgrades"] as JsonObject;
        var house = root["house"] as JsonObject;
        var craft = root["craft"] as JsonObject;
        var guild = root["guild"] as JsonObject;
        var album = root["album"] as JsonObject;

        var ladder = Clicker.Shop
            .Select(u => new { key = u.Key, name = u.Name, level = Math.Max(0, Int(upgrades?[u.Key])) })
            .Where(x => x.level > 0)
            .ToList();

        var fired = 0L;
        if (craft?["firedBy"] is JsonObject firedBy)
            foreach (var (ware, n) in firedBy)
                if (Clicker.WareOf(ware) is not null) fired += Math.Max(0, Long(n));

        var best = new List<(ItemInfo Item, int N)>();
        if (craft?["items"] is JsonObject items)
            foreach (var (key, n) in items)
                if (Clicker.ParseItem(key) is { } it && Int(n) > 0) best.Add((it, Int(n)));

        var gifts = new List<object>();
        if (guild?["shelf"] is JsonArray shelf)
            foreach (var g in shelf.OfType<JsonObject>())
            {
                var ware = Str(g["ware"]);
                var style = Str(g["style"]);
                var q = Int(g["quality"]);
                if (Clicker.WareOf(ware) is null || q is < 1 or > 3 || (style.Length > 0 && Clicker.Styles.All(x => x.Key != style))) continue;
                gifts.Add(new { from = Str(g["from"]), ware, style, q, at = Str(g["at"]) });
                if (gifts.Count >= ShelfSize) break;
            }

        return new
        {
            nick,
            total = Math.Max(0, Long(root["total"])),
            stamps = Math.Max(0, Int(root["stamps"])),
            firings = Math.Max(0, Int(root["firings"])),
            ladder,
            decor = Known(house?["decor"], k => Clicker.Decor.Any(d => d.Key == k)),
            tools = Known(house?["tools"], k => Clicker.Tools.Any(t => t.Key == k)),
            styles = Known(root["styles"], k => Clicker.Styles.Any(s => s.Key == k)),
            wear = Clicker.Styles.Any(s => s.Key == Str(root["wear"])) ? Str(root["wear"]) : "",
            // Альбом і кахлі пише інший пакет: беремо лише кількість, якщо таке поле взагалі є.
            album = CountOf(album?["cells"]),
            tiles = CountOf(album?["stove"]),
            rank = Math.Clamp(Int(guild?["rank"]), 0, Clicker.GuildRanks.Length - 1),
            gifts,
            formed = Math.Max(0, Long(craft?["formed"])),
            fired,
            best = best
                .OrderByDescending(x => x.Item.Quality)
                .ThenByDescending(x => Clicker.StyleValue(x.Item.Style))
                .ThenByDescending(x => Array.FindIndex(Clicker.Wares, w => w.Key == x.Item.Ware))
                .Take(3)
                .Select(x => new { ware = x.Item.Ware, style = x.Item.Style, q = x.Item.Quality, n = x.N })
                .ToList(),
        };
    }

    static List<string> Known(JsonNode? node, Func<string, bool> known) =>
        node is JsonArray a ? a.Select(Str).Where(k => k.Length > 0 && known(k)).Distinct(StringComparer.Ordinal).ToList() : [];

    static int? CountOf(JsonNode? node) => node switch
    {
        JsonArray a => a.Count,
        JsonObject o => o.Count,
        _ => null,
    };

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l
        : n is JsonValue d && d.TryGetValue<double>(out var x) && double.IsFinite(x) ? (long)Math.Clamp(x, long.MinValue, long.MaxValue) : 0;

    static int Int(JsonNode? n) => (int)Math.Clamp(Long(n), int.MinValue, int.MaxValue);
}

/// <summary>
/// Підключення цеху: один рядок у <c>GamesSetup.AddHlechykyGames</c> і один у <c>MapHlechykyGames</c>.
/// </summary>
public static class ClickerGuildSetup
{
    public static IServiceCollection AddClickerGuild(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ClickerGuildService(
            sp.GetRequiredService<IGameStore>(), sp.GetRequiredService<IClock>(), sp.GetService<ILogger<ClickerGuildService>>()));
        return services;
    }

    public static WebApplication MapClickerGuild(this WebApplication app)
    {
        // Список гончарів і віз — будь-кому з сайту: це те саме, що й так видно в Журналі.
        app.MapGet("/api/games/clicker/guild", (HttpContext c, ClickerGuildService guild) => Results.Ok(guild.Roster(Auth.Nick(c))));

        // Хата друга: лише публічне зі збереження (див. HouseSnapshot).
        app.MapGet("/api/games/clicker/house", (string? nick, ClickerGuildService guild) =>
            guild.House(nick) is { } house
                ? Results.Ok(house)
                : Results.NotFound(new { ok = false, message = "Такої хати нема — гончар ще не сідав за коло" }));
        return app;
    }
}
