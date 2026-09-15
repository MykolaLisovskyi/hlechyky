using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Сорт глини на колі — це стратегія: червона для того, хто клацає, біла для того, хто чекає, чорна для
/// ловця глеків. <paramref name="Events"/> множить проміжки між розписними й падаючими глеками (менше — частіше),
/// <paramref name="Loot"/> — глек з полиці й щедрого купця. <paramref name="Body"/> — колір простого глека на колі.
/// </summary>
public sealed record ClickerClay(string Key, string Name, string Desc, long Price, double Click, double Passive, double Events, double Loot, string Body);

/// <summary>Знаряддя гончаря: одноразова покупка зі своїм ефектом, видима на стіні майстерні, обпал не спалює.</summary>
public sealed record ClickerTool(string Key, string Name, string Desc, long Price);

/// <summary>Прикраса хати: краса, +2 % до всього, деякі ще щось уміють. Лишається назавжди.</summary>
public sealed record ClickerDecor(string Key, string Name, string Desc, long Price);

/// <summary>
/// Замовлення на дошці купців. <c>invest</c> — купець бере глеки і за <paramref name="Minutes"/> повертає більше;
/// <c>style</c> — бере глеки в розписі <paramref name="Style"/> і платить одразу. <paramref name="Until"/> — коли
/// купець поїде (дошка оновлюється).
/// </summary>
public sealed record ClickerOrder(int Id, string Kind, string Merchant, long Need, long Pay, int Minutes, string Style, DateTimeOffset Until);

/// <summary>Купець у дорозі: повернеться о <paramref name="PayAt"/> з <paramref name="Pay"/> глеками.</summary>
public sealed record ClickerTaken(int Id, string Merchant, long Pay, DateTimeOffset PayAt);

/// <summary>Купець повернувся: клієнт малює «+N» над сценою, коли бачить новий запис.</summary>
public sealed record ClickerPaid(int Id, string Merchant, long Pay, DateTimeOffset At);

/// <summary>
/// Хата гончаря: те, що купується не рівнями, а речами, і що видно на сцені — глина, знаряддя, прикраси — і
/// дошка купців із замовленнями. Правила доходу лишаються в <c>Clicker.cs</c>; тут — каталоги, дії, вид і
/// збереження цього шматка. Знаряддя, прикраси й куплені глини обпал не чіпає; глина на колі, дошка й купці в
/// дорозі — згорають.
/// </summary>
public sealed partial class Clicker
{
    // ---------- глина ----------

    /// <summary>Замішана глина відлежується: перемкнути наступну можна не раніше. Інакше глину міняли б на кожен клік.</summary>
    public static readonly TimeSpan ClayRest = TimeSpan.FromMinutes(10);

    public static readonly ClickerClay[] Clays =
    [
        new("", "Звичайна глина", "Як є: без сильних і слабких сторін", 0, 1, 1, 1, 1, ""),
        new("red", "Червона глина", "Клік ×2, пасив ×0,7 — для того, хто клацає", 3_000, 2, 0.7, 1, 1, "#a8402c"),
        new("white", "Біла глина", "Пасив ×1,5, клік ×0,5 — для того, хто чекає", 30_000, 0.5, 1.5, 1, 1, "#e6ddcb"),
        new("black", "Чорна глина", "Розписні й падаючі глеки на 40 % частіші, глек з полиці й купець ×1,5 — для ловця", 300_000, 1, 1, 0.6, 1.5, "#2f2b2e"),
    ];

    readonly HashSet<string> _clays = new(StringComparer.Ordinal);
    string _clay = "";
    DateTimeOffset _clayRestUntil;

    ClickerClay ClayNow => Clays.FirstOrDefault(c => c.Key == _clay) ?? Clays[0];

    /// <summary>Купити глину (одноразово) або замісити вже куплену на коло. Звичайна — завжди своя.</summary>
    ActResult Knead(JsonElement payload)
    {
        var key = Str(payload, "kind");
        if (Clays.FirstOrDefault(c => c.Key == key) is not { } clay) return ActResult.Fail("Такої глини нема");
        var now = Ctx.Clock.UtcNow;
        if (key.Length > 0 && !_clays.Contains(key))
        {
            if (_pots < clay.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(clay.Price - _pots)}");
            _pots -= clay.Price;
            _clays.Add(key);
            _clay = key;
            _clayRestUntil = now + ClayRest;
            return ActResult.Accept($"🪣 {clay.Name} — куплена й уже на колі");
        }
        if (_clay == key) return ActResult.Fail("Ця глина вже на колі");
        if (now < _clayRestUntil) return ActResult.Fail($"Глина ще відлежується: {Wait(_clayRestUntil - now)}");
        _clay = key;
        _clayRestUntil = now + ClayRest;
        return ActResult.Accept($"🪣 {clay.Name} — на колі");
    }

    // ---------- знаряддя ----------

    /// <summary>Скільки до розгону додає лопатка: тепло спадає вдвічі повільніше.</summary>
    public const double PaddleTau = 2;
    public const double RibsClick = 1.25, BucketPassive = 1.1, WhistleEvents = 0.75, ScalesBonus = 0.2;
    public static readonly TimeSpan LanternHours = TimeSpan.FromHours(2);
    /// <summary>З губкою глек з полиці летить довше — ловити легше.</summary>
    public static readonly TimeSpan FallShownLong = TimeSpan.FromSeconds(4.5);
    /// <summary>Тавро майстра: кожен обпал дає ще одне клеймо (лише коли обпал і так щось дає).</summary>
    public const int IronStamps = 1;

    public static readonly ClickerTool[] Tools =
    [
        new("paddle", "Дерев'яна лопатка", "Розгін спадає вдвічі повільніше", 5_000),
        new("string", "Струна для зрізання", "Клік бере ще +1 % пасиву", 20_000),
        new("sponge", "Губка", "Глек з полиці летить 4,5 с замість 3,2", 50_000),
        new("ribs", "Гончарні ребра", "Клік ×1,25", 100_000),
        new("lantern", "Ліхтар", "Коло крутиться без тебе ще +2 години", 200_000),
        new("apron", "Шкіряний фартух", "Один розбитий глек серії не обриває", 500_000),
        new("bucket", "Відро з водою", "Пасив +10 %", 1_000_000),
        new("whistle", "Глиняний свисток", "Розписні глеки на 25 % частіші", 2_000_000),
        new("scales", "Ваги купця", "Купці платять на 20 % більше", 5_000_000),
        new("iron", "Тавро майстра", "Кожен обпал дає ще одне клеймо", 50_000_000),
    ];

    readonly HashSet<string> _tools = new(StringComparer.Ordinal);
    /// <summary>Фартух уже вибачив один промах у цій серії.</summary>
    bool _apronUsed;

    bool Tool(string key) => _tools.Contains(key);

    ActResult BuyTool(JsonElement payload)
    {
        if (Tools.FirstOrDefault(t => t.Key == Str(payload, "key")) is not { } tool) return ActResult.Fail("Такого знаряддя нема");
        if (_tools.Contains(tool.Key)) return ActResult.Fail($"{tool.Name} уже на стіні");
        if (_pots < tool.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(tool.Price - _pots)}");
        _pots -= tool.Price;
        _tools.Add(tool.Key);
        return ActResult.Accept($"🔧 {tool.Name}: {tool.Desc.ToLowerInvariant()}");
    }

    // ---------- прикраси ----------

    public const double DecorBonus = 0.02;
    /// <summary>Собака стереже: розписний глек стоїть довше.</summary>
    public static readonly TimeSpan DogGuard = TimeSpan.FromSeconds(3);

    public static readonly ClickerDecor[] Decor =
    [
        new("towel", "Рушник над колом", "+2 % до всього", 10_000),
        new("icon", "Ікона в куті", "+2 % до всього", 100_000),
        new("window", "Вікно з калиною", "+2 % до всього", 1_000_000),
        new("rooster", "Півень на тину", "+2 % до всього", 10_000_000),
        new("dog", "Собака під лавою", "+2 % до всього, розписний глек стоїть на 3 с довше", 100_000_000),
        new("chest", "Мальована скриня", "+2 % до всього", 1_000_000_000),
    ];

    readonly HashSet<string> _decor = new(StringComparer.Ordinal);

    bool Adorned(string key) => _decor.Contains(key);

    ActResult Adorn(JsonElement payload)
    {
        if (Decor.FirstOrDefault(d => d.Key == Str(payload, "key")) is not { } decor) return ActResult.Fail("Такої прикраси нема");
        if (_decor.Contains(decor.Key)) return ActResult.Fail($"{decor.Name} уже в хаті");
        if (_pots < decor.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(decor.Price - _pots)}");
        _pots -= decor.Price;
        _decor.Add(decor.Key);
        return ActResult.Accept($"🏠 {decor.Name} — тепер у хаті, +{DecorBonus * 100:0} % до всього");
    }

    // ---------- купці ----------

    /// <summary>Як часто дошка оновлюється: хто не взяв — той купець поїхав.</summary>
    public static readonly TimeSpan BoardEvery = TimeSpan.FromMinutes(4);
    public const int BoardSize = 3, MaxTaken = 3;
    /// <summary>На скільки купець їде. Довше — щедріше: 1,2 + хвилини/30 (5 хв → ×1,37, 30 хв → ×2,2).</summary>
    public static readonly int[] OrderMinutes = [5, 10, 20, 30];
    public const double InvestBase = 1.2, InvestPerHalfHour = 1.0;
    /// <summary>Замовлення на розпис: три хвилини пасиву в цьому розписі, платить ×1,6 одразу.</summary>
    public const double StyleOrderSeconds = 180, StyleOrderPay = 1.6;
    /// <summary>Скільки пасиву купець просить у дорогу: від двох до десяти хвилин.</summary>
    public const double InvestMinSeconds = 120, InvestMaxSeconds = 600;

    static readonly string[] Merchants =
    [
        "Пані з Полтави", "Козак Мамай", "Чумак Іван", "Пан з Умані", "Циганка з ярмарку", "Дід Панас з Опішні",
        "Шинкарка Гапка", "Купець із Царграда", "Отаман Сірко", "Кума з Сорочинців", "Мандрівний дяк", "Коваль Вакула",
    ];

    readonly List<ClickerOrder> _board = [];
    readonly List<ClickerTaken> _taken = [];
    /// <summary>Останні повернення купців — не зберігаються, клієнт лише показує їх «+N».</summary>
    readonly List<ClickerPaid> _paid = [];
    DateTimeOffset _boardUntil;
    int _orderId;

    /// <summary>Нова дошка: два купці в дорогу і, якщо є розписи, один за розпис.</summary>
    void RefreshBoard(DateTimeOffset now)
    {
        _board.Clear();
        _boardUntil = now + BoardEvery;
        var passive = PassiveBase;
        var styles = _styles.Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < BoardSize; i++)
        {
            var merchant = Merchants[Ctx.Rng.Next(Merchants.Length)];
            if (i == BoardSize - 1 && styles.Count > 0)
            {
                var style = styles[Ctx.Rng.Next(styles.Count)];
                var need = Nice(Math.Max(200, passive * StyleOrderSeconds));
                _board.Add(new(++_orderId, "style", merchant, need, ToLong(need * StyleOrderPay), 0, style, _boardUntil));
                continue;
            }
            var minutes = OrderMinutes[Ctx.Rng.Next(OrderMinutes.Length)];
            var seconds = InvestMinSeconds + Ctx.Rng.NextDouble() * (InvestMaxSeconds - InvestMinSeconds);
            var ask = Nice(Math.Max(100 + Ctx.Rng.Next(0, 300), passive * seconds));
            _board.Add(new(++_orderId, "invest", merchant, ask, ToLong(ask * (InvestBase + minutes / 30.0 * InvestPerHalfHour)), minutes, "", _boardUntil));
        }
    }

    /// <summary>Округлити до двох значущих цифр: «12 000», а не «11 873».</summary>
    public static long Nice(double v)
    {
        if (!(v > 0)) return 0;
        if (v < 100) return (long)Math.Ceiling(v);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)) - 1);
        return ToLong(Math.Round(v / mag) * mag);
    }

    /// <summary>Дошка й купці в дорозі: поїхали — нова дошка; повернулись — глеки на купу.</summary>
    void SyncOrders(DateTimeOffset now)
    {
        if (_boardUntil == default || now >= _boardUntil) RefreshBoard(now);
        for (var i = _taken.Count - 1; i >= 0; i--)
        {
            var t = _taken[i];
            if (t.PayAt > now) continue;
            Add(t.Pay);
            _taken.RemoveAt(i);
            _paid.Add(new(t.Id, t.Merchant, t.Pay, t.PayAt));
        }
        if (_paid.Count > 5) _paid.RemoveRange(0, _paid.Count - 5);
    }

    /// <summary>Взяти замовлення з дошки.</summary>
    ActResult Take(JsonElement payload)
    {
        var id = Num(payload, "id") ?? -1;
        if (_board.FirstOrDefault(o => o.Id == id) is not { } order) return ActResult.Fail("Цей купець уже поїхав");
        var now = Ctx.Clock.UtcNow;
        var styleName = Styles.FirstOrDefault(s => s.Key == order.Style)?.Name ?? "";
        if (order.Kind == "style" && !_styles.Contains(order.Style))
            return ActResult.Fail($"Купець хоче «{styleName}», а цього розпису в колекції ще нема");
        if (order.Kind == "invest" && _taken.Count >= MaxTaken)
            return ActResult.Fail("Три купці вже в дорозі — почекай, поки хтось повернеться");
        if (_pots < order.Need) return ActResult.Fail($"Бракує глеків: треба ще {Short(order.Need - _pots)}");

        var pay = Tool("scales") ? ToLong(order.Pay * (1 + ScalesBonus)) : order.Pay;
        _pots -= order.Need;
        _board.Remove(order);
        if (order.Kind == "style")
        {
            Add(pay);
            return ActResult.Accept($"🧺 {order.Merchant} забрав {Short(order.Need)} глеків «{styleName}» і заплатив {Short(pay)}");
        }
        _taken.Add(new(order.Id, order.Merchant, pay, now + TimeSpan.FromMinutes(order.Minutes)));
        return ActResult.Accept($"🐴 {order.Merchant} поїхав із {Short(order.Need)} глеками, повернеться за {order.Minutes} хв із {Short(pay)}");
    }

    // ---------- життя хати ----------

    void ResetHouse(DateTimeOffset now)
    {
        _clays.Clear();
        _tools.Clear();
        _decor.Clear();
        _taken.Clear();
        _paid.Clear();
        _orderId = 0;
        FireHouse(now);
    }

    /// <summary>Що з хати згорає при обпалі: глина на колі, дошка, купці в дорозі. Речі й куплені глини лишаються.</summary>
    void FireHouse(DateTimeOffset now)
    {
        _clay = "";
        _clayRestUntil = default;
        _apronUsed = false;
        _taken.Clear();
        RefreshBoard(now);
    }

    object HouseView(DateTimeOffset now) => new
    {
        clays = Clays.Select(c => new
        {
            key = c.Key, name = c.Name, desc = c.Desc, price = c.Price, body = c.Body,
            owned = c.Key.Length == 0 || _clays.Contains(c.Key), on = _clay == c.Key,
        }),
        clay = _clay,
        clayBody = ClayNow.Body,
        clayRestUntil = _clayRestUntil,
        tools = Tools.Select(t => new { key = t.Key, name = t.Name, desc = t.Desc, price = t.Price, owned = _tools.Contains(t.Key) }),
        decor = Decor.Select(d => new { key = d.Key, name = d.Name, desc = d.Desc, price = d.Price, owned = _decor.Contains(d.Key) }),
        orders = new
        {
            board = _board.Select(o => new
            {
                id = o.Id, kind = o.Kind, merchant = o.Merchant, need = o.Need,
                pay = Tool("scales") ? ToLong(o.Pay * (1 + ScalesBonus)) : o.Pay,
                minutes = o.Minutes, style = o.Style,
                styleName = Styles.FirstOrDefault(s => s.Key == o.Style)?.Name ?? "",
                can = o.Kind != "style" || _styles.Contains(o.Style),
            }),
            taken = _taken.Select(t => new { id = t.Id, merchant = t.Merchant, pay = t.Pay, payAt = t.PayAt }),
            paid = _paid.Select(p => new { id = p.Id, merchant = p.Merchant, pay = p.Pay, at = p.At }),
            refreshAt = _boardUntil,
            maxTaken = MaxTaken,
        },
    };

    sealed record HouseRow(
        List<string>? Clays, string? Clay, DateTimeOffset ClayRestUntil,
        List<string>? Tools, bool ApronUsed, List<string>? Decor,
        List<ClickerOrder>? Board, List<ClickerTaken>? Taken, DateTimeOffset BoardUntil, int OrderId);

    HouseRow SaveHouse() => new(
        _clays.Order(StringComparer.Ordinal).ToList(), _clay, _clayRestUntil,
        _tools.Order(StringComparer.Ordinal).ToList(), _apronUsed, _decor.Order(StringComparer.Ordinal).ToList(),
        _board.ToList(), _taken.ToList(), _boardUntil, _orderId);

    /// <summary>Старе збереження без хати — чиста хата зі свіжою дошкою.</summary>
    void LoadHouse(HouseRow? row)
    {
        var now = Ctx.Clock.UtcNow;
        Fill(_clays, row?.Clays, k => Clays.Any(c => c.Key == k && k.Length > 0));
        _clay = row?.Clay is { } c && (c.Length == 0 || _clays.Contains(c)) ? c : "";
        _clayRestUntil = row?.ClayRestUntil ?? default;
        Fill(_tools, row?.Tools, k => Tools.Any(t => t.Key == k));
        _apronUsed = row?.ApronUsed ?? false;
        Fill(_decor, row?.Decor, k => Decor.Any(d => d.Key == k));
        _board.Clear();
        _taken.Clear();
        _paid.Clear();
        foreach (var o in row?.Board ?? [])
            if (o is not null && o.Need > 0 && o.Pay > 0) _board.Add(o);
        foreach (var t in row?.Taken ?? [])
            if (t is not null && t.Pay > 0) _taken.Add(t);
        _boardUntil = row?.BoardUntil ?? default;
        _orderId = Math.Max(0, row?.OrderId ?? 0);
        // Порожня дошка з майбутнім строком — це «усіх купців узято», хай чекає свого часу; без строку — хати ще не було.
        if (_boardUntil == default) RefreshBoard(now);
    }
}
