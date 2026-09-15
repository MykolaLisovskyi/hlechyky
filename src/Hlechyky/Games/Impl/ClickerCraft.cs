using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Виріб, який ліпиться на колі. <paramref name="Work"/> — скільки роботи (зарахованих кліків чи ліплення
/// підмайстрів) треба, <paramref name="Seconds"/> — чого він вартий у секундах пасиву, <paramref name="Unlock"/> —
/// з яких глеків за весь час відкривається.
/// </summary>
public sealed record ClickerWare(string Key, string Name, int Work, double Seconds, long Unlock);

/// <summary>Виріб у коморі: вид, розпис (порожній — простий) і якість (1 звичайний, 2 добрий, 3 дзвінкий).</summary>
public sealed record ItemInfo(string Ware, string Style, int Quality);

/// <summary>Сирець на сушарні: що це, з якої глини і коли висохне.</summary>
public sealed record RackRow(string Ware, string Clay, DateTimeOffset DryAt);

/// <summary>
/// Ремесло — основа сьомого оновлення (docs/games/specs/clicker-v7.md): глеки перестають бути лише числом. Кліки
/// (і підмайстри) ліплять виріб на колі; готовий сохне на сушарні; висохлий обпалюють у горні (ClickerKiln.cs) і він
/// лягає в комору з розписом і якістю; з комори — на базар, купцям, у віз цеху чи в дарунок. Тут — каталог
/// виробів, ліплення, сушарня, комора, базар, «поки тебе не було» і спільні помічники для пакетів.
///
/// Важливе обмеження: ліплення само глеків не дає. Старі тести рахують точні суми після кліків, а баланс драбини
/// підбирали симуляцією — ремесло приносить глеки лише тоді, коли виріб продано.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>
    /// Каталог виробів. Ціна в секундах пасиву свідомо мала — ~0,075 с на одиницю роботи в горщика до 0,12 у лева: у
    /// пізній грі клік ≈ 0,1 пасиву (з маховиком), тож виліплений простий звичайний виріб додає до кліків ще ~60 %, а
    /// з якістю, розписом, майстерністю й замовленнями — до ~2–3 пасивів зверху (ціль контракту). Перша версія
    /// (0,6–1 с на роботу) давала дзвінкому розписному куманцю ~30 пасивів за секунду клацання.
    /// </summary>
    public static readonly ClickerWare[] Wares =
    [
        new("pot", "Горщик", 40, 3, 0),
        new("bowl", "Миска", 50, 3.6, 0),
        new("jug", "Глечик", 80, 6, 1_000),
        new("makitra", "Макітра", 120, 9.6, 10_000),
        new("dish", "Полумисок", 100, 8.4, 100_000),
        new("candle", "Свічник", 90, 7.6, 1_000_000),
        new("whistle", "Свищик-півник", 70, 6.6, 5_000_000),
        new("barrel", "Барило", 200, 18, 50_000_000),
        new("tile", "Кахля", 150, 14, 500_000_000),
        new("kumanets", "Куманець", 320, 32, 5_000_000_000),
        new("ram", "Баранець-свищик", 260, 27, 50_000_000_000),
        new("lion", "Лев-посудина", 500, 60, 1_000_000_000_000),
    ];

    /// <summary>Скільки сохне сирець (до погоди).</summary>
    public static readonly TimeSpan DryTime = TimeSpan.FromSeconds(90);
    public const int RackBase = 8, RackMax = 20, RackPerWorkshop = 5;
    /// <summary>Підмайстри ліплять самі: стільки роботи за секунду з рівня, але не більше стелі.</summary>
    public const double ApprenticeWork = 0.02, ApprenticeWorkMax = 0.5;
    /// <summary>Нижче цієї частки від роботи виробу жодні множники не опускають.</summary>
    public const double MinWorkShare = 0.4;
    /// <summary>Скільки виробів уміщає комора: що понад — одразу на базар (не губиться).</summary>
    public const int StoreCap = 200;
    /// <summary>Якість множить ціну.</summary>
    public static readonly double[] QualityMult = [0, 1, 1.6, 2.6];
    /// <summary>Дно ціни: на голому колі виріб вартий половини кліків, що на нього пішли.</summary>
    public const double ValueFloorClicks = 0.5;
    /// <summary>З якого простою показувати «поки тебе не було».</summary>
    public static readonly TimeSpan AwayFrom = TimeSpan.FromMinutes(5);
    public const int WaresForAchievement = 1_000;

    string _formWare = "pot";
    double _formWork;
    readonly List<RackRow> _rack = [];
    readonly Dictionary<string, int> _items = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _firedBy = new(StringComparer.Ordinal);
    long _formed;
    AwayRow? _away;
    readonly List<string> _awayNotes = [];
    /// <summary>Каталоги (тексти, списки) у вид — лише першим видом після Start/Load і на прохання клієнта.</summary>
    bool _catalogWanted = true;

    // ---------- вироби ----------

    internal static ClickerWare? WareOf(string key) => Wares.FirstOrDefault(w => w.Key == key);

    internal bool WareOpen(string key) => WareOf(key) is { } w && (_total >= w.Unlock || GuildWareOpen(key));

    /// <summary>Скільки роботи просить виріб зараз: майстерність і бонуси пришвидшують, але не нижче 40 %.</summary>
    internal int WorkOf(ClickerWare w)
    {
        var mult = Math.Max(MinWorkShare, AlbumWorkMult(w.Key) * FairWorkMult(w.Key));
        return Math.Max(1, (int)Math.Ceiling(w.Work * mult));
    }

    internal int RackSize => Math.Min(RackMax, RackBase + Level("workshop") / RackPerWorkshop) + KilnRackBonus();

    internal IReadOnlyList<RackRow> Rack => _rack;

    internal static string ItemKey(string ware, string style, int quality) => $"{ware}|{style}|{quality}";

    internal static ItemInfo? ParseItem(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var parts = key.Split('|');
        if (parts.Length != 3 || WareOf(parts[0]) is null) return null;
        if (parts[1].Length > 0 && Styles.All(s => s.Key != parts[1])) return null;
        return int.TryParse(parts[2], out var q) && q is >= 1 and <= 3 ? new ItemInfo(parts[0], parts[1], q) : null;
    }

    internal int ItemTotal => _items.Values.Sum();

    internal IEnumerable<(ItemInfo Item, int Count)> AllItems() =>
        _items.Where(kv => kv.Value > 0)
            .Select(kv => (ParseItem(kv.Key)!, kv.Value))
            .Where(x => x.Item1 is not null)
            .OrderBy(x => Array.FindIndex(Wares, w => w.Key == x.Item1.Ware))
            .ThenBy(x => x.Item1.Style, StringComparer.Ordinal)
            .ThenByDescending(x => x.Item1.Quality);

    internal int ItemCount(Func<ItemInfo, bool> match) => AllItems().Where(x => match(x.Item)).Sum(x => x.Count);

    /// <summary>Забрати до n виробів, що підходять, — спершу найгіршої якості: купцеві «якість ≥ добра» не віддаємо дзвінких, поки є добрі.</summary>
    internal int TakeItems(Func<ItemInfo, bool> match, int n)
    {
        if (n <= 0) return 0;
        var taken = 0;
        foreach (var (item, count) in AllItems().Where(x => match(x.Item)).OrderBy(x => x.Item.Quality).ToList())
        {
            var take = Math.Min(count, n - taken);
            var key = ItemKey(item.Ware, item.Style, item.Quality);
            if (_items[key] == take) _items.Remove(key);
            else _items[key] -= take;
            taken += take;
            if (taken >= n) break;
        }
        return taken;
    }

    /// <summary>Покласти в комору. Що не влізло — одразу продано на базарі; повертає, скільки глеків за це прийшло.</summary>
    internal long PutItems(string ware, string style, int quality, int n)
    {
        if (n <= 0 || WareOf(ware) is null || quality is < 1 or > 3) return 0;
        var room = Math.Max(0, StoreCap - ItemTotal);
        var put = Math.Min(room, n);
        if (put > 0)
        {
            var key = ItemKey(ware, style, quality);
            _items[key] = (_items.TryGetValue(key, out var c) ? c : 0) + put;
        }
        var over = n - put;
        if (over <= 0) return 0;
        var pots = Mul(ItemValue(ware, style, quality), over);
        Add(pots);
        return pots;
    }

    /// <summary>Висохлі сирці — для горна; мокрі лишаються на сушарні.</summary>
    internal List<RackRow> TakeDry(DateTimeOffset now, int max)
    {
        var dry = _rack.Where(r => r.DryAt <= now).Take(Math.Max(0, max)).ToList();
        foreach (var r in dry) _rack.Remove(r);
        return dry;
    }

    /// <summary>Горно видало виріб: лічильник майстерності й гачки альбому та цеху. Сам виріб кладе горно (PutItems).</summary>
    internal void AddFired(ItemInfo item, int n)
    {
        if (n <= 0) return;
        _firedBy[item.Ware] = (_firedBy.TryGetValue(item.Ware, out var c) ? c : 0) + n;
        AlbumOnFired(item, n);
        GuildOnFired(item, n);
    }

    internal long FiredOf(string ware) => _firedBy.TryGetValue(ware, out var n) ? n : 0;
    internal long FiredTotal => _firedBy.Values.Sum();

    /// <summary>
    /// Ціна виробу в глеках: секунди пасиву × якість × розпис × множники пакетів, але не менше за половину кліків,
    /// що на нього пішли (інакше на голому колі без підмайстрів виріб нічого б не вартував).
    /// </summary>
    internal long ItemValue(string ware, string style, int quality)
    {
        if (WareOf(ware) is not { } w) return 0;
        var q = QualityMult[Math.Clamp(quality, 1, 3)];
        var byPassive = (_memoOn ? _memoPassive : PassiveBase) * w.Seconds * q * StyleValue(style) * AlbumValueMult(ware) * FairValueMult(ware);
        var floor = (double)(_memoOn ? _memoClick : ClickBase) * WorkOf(w) * ValueFloorClicks * q;
        return Math.Max(1, ToLong(Math.Max(byPassive, floor)));
    }

    /// <summary>
    /// Пам'ять пасиву й кліка на час одного виду: ItemValue кличеться на кожен виріб комори, замовлення й віз, а
    /// PassiveBase/ClickBase щоразу обходять драбину. У межах виду стан не міняється, тож рахуємо раз.
    /// </summary>
    bool _memoOn;
    double _memoPassive;
    long _memoClick;

    /// <summary>Простий — ×1, далі від гаварецького ×1,2 до трипільського ×1,9.</summary>
    internal static double StyleValue(string style)
    {
        if (string.IsNullOrEmpty(style)) return 1;
        var i = Array.FindIndex(Styles, s => s.Key == style);
        return i < 0 ? 1 : 1.2 + 0.1 * i;
    }

    // ---------- ліплення ----------

    /// <summary>Додати роботи виробу на колі: готовий — на сушарню, залишок переходить у наступний. Сушарня повна — стоїмо.</summary>
    void FormBy(double work, DateTimeOffset now, bool byApprentice = false, bool dried = false)
    {
        if (!(work > 0) || WareOf(_formWare) is not { } w) return;
        var need = WorkOf(w);
        _formWork += work;
        var made = 0;
        while (_formWork >= need)
        {
            if (_rack.Count >= RackSize) { _formWork = need; break; }
            _formWork -= need;
            // Виліплене підмайстрами за довгий простій уже встигло висохнути.
            _rack.Add(new RackRow(w.Key, _clay, dried ? now : now + TimeSpan.FromSeconds(DryTime.TotalSeconds * FairDryMult())));
            _formed++;
            made++;
            if (_formed == 1) Achieve("potter-ware-1");
            if (_formed == WaresForAchievement) Achieve("potter-ware-1k");
            AlbumOnFormed(w.Key);
            if (made > 50) { _formWork = 0; break; }                   // запобіжник від зіпсованого збереження
        }
        if (byApprentice && _awayOpen) _awayFormed += made;
    }

    int _awayFormed;

    double ApprenticeRate => Math.Min(ApprenticeWorkMax, ApprenticeWork * Level("apprentice"));

    ActResult Form(JsonElement payload)
    {
        var key = Str(payload, "ware");
        if (WareOf(key) is not { } w) return ActResult.Fail("Такого виробу гончарі не ліплять");
        if (!WareOpen(key)) return ActResult.Fail($"{w.Name} відкриється на {Short(w.Unlock)} глеків за весь час");
        if (_formWare == key) return ActResult.Done;
        _formWare = key;
        _formWork = Math.Min(_formWork, WorkOf(w));
        return ActResult.Accept($"🏺 На колі тепер {w.Name.ToLowerInvariant()}");
    }

    /// <summary>Базар: продати вироби з комори за глеки. <c>{ key, n }</c> або <c>{ all: true }</c>.</summary>
    ActResult Bazaar(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True)
        {
            var sold = 0;
            long sum = 0;
            foreach (var (item, count) in AllItems().ToList())
            {
                sum = Sum(sum, Mul(ItemValue(item.Ware, item.Style, item.Quality), count));
                sold += count;
            }
            if (sold == 0) return ActResult.Fail("У коморі порожньо — нічого везти на базар");
            _items.Clear();
            Add(sum);
            return ActResult.Accept($"🧺 Базар забрав {sold} {WaresWord(sold)}: +{Short(sum)} {Pots(sum)}");
        }
        if (ParseItem(Str(payload, "key")) is not { } it) return ActResult.Fail("Такого виробу в коморі нема");
        var want = (int)Math.Clamp(Num(payload, "n") ?? 1, 1, StoreCap);
        var have = ItemCount(x => x == it);
        if (have <= 0) return ActResult.Fail("Такого виробу в коморі нема");
        var n = Math.Min(want, have);
        TakeItems(x => x == it, n);
        var pots = Mul(ItemValue(it.Ware, it.Style, it.Quality), n);
        Add(pots);
        return ActResult.Accept($"🧺 Продав {n} × {WareOf(it.Ware)!.Name.ToLowerInvariant()}: +{Short(pots)} {Pots(pots)}");
    }

    static string WaresWord(long n) => Plural(n, "виріб", "вироби", "виробів");

    // ---------- ачівки з черги ----------

    /// <summary>
    /// Ачівки пакетів сьомого оновлення. Частина з них народжується в Sync, а Sync кличе й View — поза дією, де каркас
    /// не приймає Ctx.Award (його скринька відкрита лише в Act/Tick/Start). Тому — у чергу (вона ще й у збереженні), а
    /// видає її найближча дія. Повторна видача безпечна: Achievements.Unlock ідемпотентний.
    /// </summary>
    internal void Achieve(string key)
    {
        if (!_achQueue.Contains(key)) _achQueue.Add(key);
        if (_inAct) FlushAchievements();
    }

    readonly List<string> _achQueue = [];
    /// <summary>Зараз іде дія гравця (а не вид): Ctx.Award і Ctx.Log доходять, пошту цеху можна забирати.</summary>
    internal bool _inAct;

    void FlushAchievements()
    {
        foreach (var key in _achQueue) Ctx.Award(0, 0, "ach:" + key);
        _achQueue.Clear();
    }

    // ---------- Око майстра для мінігор ----------

    /// <summary>
    /// null — можна нагороджувати. Інакше — готова відповідь: коло стоїть чи майстер чекає відповіді (відмова), або
    /// перевірка якраз назріла (тоді майстер питає замість нагороди — як у catch/grab).
    /// </summary>
    internal ActResult? GuardGate(DateTimeOffset now)
    {
        if (_guard.Locked(now) || _guard.Pending) return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");
        if (_guard.Due && !FairOn && !InspireOn)
        {
            _guard.Check();
            return ActResult.Accept("👁 Майстер хоче глянути на твої руки — торкнись глечиків");
        }
        return null;
    }

    internal void GuardSpend(int weight) => _guard.Spend(Math.Max(0, weight));

    // ---------- поки тебе не було ----------

    /// <summary>Рядок для «поки тебе не було» від пакетів (купці, дарунки…). Лише під час довгого простою.</summary>
    internal void AwayNote(string text)
    {
        if (_awayOpen) { if (_awayNotes.Count < 8) _awayNotes.Add(text); return; }
        // Дещо з простою приїжджає вже після виду, що склав запис, — на першій дії (пошта цеху, ачівки): дописуємо в щойно
        // складений запис, поки клієнт його ще показує, а не губимо.
        if (_away is { } a && Ctx.Clock.UtcNow - a.At < AwayLate && a.Notes.Count < 8) _away = a with { Notes = [.. a.Notes, text] };
    }

    /// <summary>Скільки після запису «поки тебе не було» до нього ще можна дописувати.</summary>
    static readonly TimeSpan AwayLate = TimeSpan.FromMinutes(2);

    bool _awayOpen;

    /// <summary>Кличе Sync: відкрити запис простою до пасиву й закрити після всіх пакетів.</summary>
    void AwayBegin(TimeSpan gap)
    {
        _awayOpen = gap >= AwayFrom;
        _awayFormed = 0;
        _awayNotes.Clear();
        _awayPotsFrom = _pots;
    }

    long _awayPotsFrom;

    void AwayEnd(DateTimeOffset now, TimeSpan gap)
    {
        if (!_awayOpen) return;
        _awayOpen = false;
        _away = new AwayRow(now, (long)gap.TotalSeconds, Math.Max(0, _pots - _awayPotsFrom), _awayFormed, [.. _awayNotes]);
    }

    // ---------- синхронізація й вид ----------

    void SyncCraft(DateTimeOffset now, TimeSpan paid)
    {
        if (paid > TimeSpan.Zero && ApprenticeRate > 0 && _rack.Count < RackSize)
            FormBy(ApprenticeRate * paid.TotalSeconds, now, byApprentice: true, dried: paid >= DryTime);
    }

    void ResetCraft()
    {
        _formWare = "pot";
        _formWork = 0;
        _rack.Clear();
        _items.Clear();
        _firedBy.Clear();
        _formed = 0;
        _away = null;
        _catalogWanted = true;
    }

    /// <summary>Обпал (престиж): сирці й комора згорають разом із глеками; майстерність (лічильники) лишається.</summary>
    void FireCraft()
    {
        _formWork = 0;
        _rack.Clear();
        _items.Clear();
        if (!WareOpen(_formWare)) _formWare = "pot";
    }

    object CraftView(DateTimeOffset now)
    {
        var w = WareOf(_formWare) ?? Wares[0];
        return new
        {
            ware = w.Key,
            work = _formWork,
            need = WorkOf(w),
            perClick = 1.0,
            apprentice = ApprenticeRate,
            rack = _rack.Select(r => new { ware = r.Ware, clay = r.Clay, dryAt = r.DryAt }),
            rackSize = RackSize,
            rackFull = _rack.Count >= RackSize,
            dryMs = DryTime.TotalMilliseconds * FairDryMult(),
            wares = Wares.Select(x => new
            {
                key = x.Key, name = x.Name, open = WareOpen(x.Key), unlock = x.Unlock, need = WorkOf(x), fired = FiredOf(x.Key),
                value = ItemValue(x.Key, "", 1),
            }),
            items = AllItems().Select(x => new
            {
                key = ItemKey(x.Item.Ware, x.Item.Style, x.Item.Quality), ware = x.Item.Ware, style = x.Item.Style, q = x.Item.Quality,
                n = x.Count, value = ItemValue(x.Item.Ware, x.Item.Style, x.Item.Quality),
            }),
            storeCap = StoreCap,
            formed = _formed,
            fired = FiredTotal,
        };
    }

    object? AwayView() => _away is { } a
        ? new { at = a.At, seconds = a.Seconds, pots = a.Pots, formed = a.Formed, notes = a.Notes }
        : null;

    /// <summary>Каталоги для клієнта: у видах від Start/Load до першої дії і після look { catalog: true } до наступної.</summary>
    object? CatalogView()
    {
        // Флаг не гаситься тут: вид мусить бути чистою функцією стану (два види без дії — однакові). Гасить його
        // наступна дія (Clicker.Act), а look { catalog: true } знову вмикає.
        if (!_catalogWanted) return null;
        return new
        {
            wares = Wares.Select(w => new { key = w.Key, name = w.Name, work = w.Work, seconds = w.Seconds, unlock = w.Unlock }),
            styles = Styles.Select((s, i) => new { key = s.Key, name = s.Name, value = StyleValue(s.Key) }),
            quality = new[] { "", "звичайний", "добрий", "дзвінкий" },
            kiln = CatalogKiln(),
            album = CatalogAlbum(),
            fair = CatalogFair(),
            guild = CatalogGuild(),
        };
    }

    // ---------- збереження ----------

    sealed record AwayRow(DateTimeOffset At, long Seconds, long Pots, int Formed, List<string> Notes);

    sealed record CraftRow(
        string? Ware, double Work, List<RackRow>? Rack, Dictionary<string, int>? Items,
        Dictionary<string, long>? FiredBy, long Formed, AwayRow? Away);

    CraftRow SaveCraft() => new(_formWare, _formWork, _rack.ToList(),
        new Dictionary<string, int>(_items, StringComparer.Ordinal), new Dictionary<string, long>(_firedBy, StringComparer.Ordinal),
        _formed, _away);

    void LoadCraft(CraftRow? row)
    {
        ResetCraft();
        if (row is null) return;
        _formWare = row.Ware is { } k && WareOf(k) is not null ? k : "pot";
        _formWork = double.IsFinite(row.Work) ? Math.Max(0, row.Work) : 0;
        foreach (var r in row.Rack ?? [])
            if (r is not null && WareOf(r.Ware) is not null && _rack.Count < RackMax + 8)
                _rack.Add(r with { Clay = Clays.Any(c => c.Key == r.Clay) ? r.Clay : "" });
        foreach (var (key, n) in row.Items ?? [])
            if (n > 0 && ParseItem(key) is not null && ItemTotal + n <= StoreCap * 2) _items[key] = n;
        foreach (var (key, n) in row.FiredBy ?? [])
            if (n > 0 && WareOf(key) is not null) _firedBy[key] = n;
        _formed = Math.Max(0, row.Formed);
        _away = row.Away is { } a ? a with { Seconds = Math.Max(0, a.Seconds), Pots = Math.Max(0, a.Pots), Formed = Math.Max(0, a.Formed), Notes = a.Notes ?? [] } : null;
    }
}
