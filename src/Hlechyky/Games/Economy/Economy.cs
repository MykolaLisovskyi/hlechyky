using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Паспорти всіх ігор платформи — щоб людські тексти («перемога в шахах»), таблиці й ачівка «Настільний»
/// знали відмінок, групу і порядок соло-результату. Список приходить ззовні (у проді — з
/// <see cref="Registry"/>, який і так уже просканував збірку), а події з <see cref="RoomFinishedEvent"/>
/// дозаповнюють мапу для ігор, яких у реєстрі не було.
///
/// Тести будують цей список явно (<c>new GameNames([])</c> і <see cref="Learn"/>): інакше кожна нова гра
/// в збірці тихо міняла б умову ачівки «перемога в кожній настільній грі» і ламала чужі тести.
/// </summary>
public sealed class GameNames
{
    readonly ConcurrentDictionary<string, GameInfo> _map = new(StringComparer.Ordinal);
    readonly List<string> _daily = new();
    // GET /daily і GET /profile ходять сюди на кожен запит: тримаємо готові списки й перебудовуємо
    // їх лише тоді, коли мапа справді змінилась (Learn)
    volatile IReadOnlyList<GameInfo>? _all;

    /// <param name="games">Паспорти ігор платформи; порожній список — «нічого не знаємо, вчимось із подій».</param>
    /// <param name="daily">Id ігор «Щоденного глека» (маркер <see cref="IDailyGame"/>).</param>
    public GameNames(IEnumerable<GameInfo> games, IEnumerable<string>? daily = null)
    {
        foreach (var info in games) _map[info.Id] = info;
        foreach (var id in daily ?? []) if (!_daily.Contains(id, StringComparer.Ordinal)) _daily.Add(id);
        _daily.Sort(StringComparer.Ordinal);
    }

    /// <summary>Список із реєстру каркаса: другий скан збірки тут ні до чого.</summary>
    public GameNames(Registry registry)
        : this(registry.Games, registry.Catalog.Where(c => c.Daily).Select(c => c.Id))
    {
    }

    public void Learn(GameInfo info)
    {
        if (_map.TryGetValue(info.Id, out var was) && was == info) return;
        _map[info.Id] = info;
        _all = null;
    }

    public GameInfo? Get(string id) => _map.TryGetValue(id, out var i) ? i : null;
    /// <summary>«шахи» — для «перемога в шахах». Нема паспорта — лишається Id, і це видно, а не падає.</summary>
    public string Accusative(string id) => Get(id)?.Accusative ?? id;
    public string Title(string id) => Get(id)?.Title ?? id;
    public IReadOnlyList<GameInfo> All => _all ??= _map.Values.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    /// <summary>Id ігор, що входять у «Щоденний глек» (маркер <see cref="IDailyGame"/>).</summary>
    public IReadOnlyList<string> Daily => _daily;
}

/// <summary>Куди складати повідомлення, поки каркас розсилки ще не піднявся (і в тестах, де він не потрібен).</summary>
public sealed class NullOutbox : IOutbox
{
    public void Post(Outgoing message) { }
}

/// <summary>
/// Черепки: гаманці, леджер, стелі, ідемпотентність. Леджер — істина, wallets — кеш, який
/// <see cref="Rebuild"/> завжди може перерахувати. Кожен успішний рух грошей іде в
/// <see cref="IOutbox"/> як <see cref="WalletChanged"/> з людським текстом («+5 черепків: перемога в шахах»).
/// </summary>
public sealed class Economy(EconomyStore store, GameNames names, IClock clock,
    IOptionsMonitor<EconomyOptions> opts, IOutbox outbox, ILogger<Economy> log) : IStakes
{
    /// <summary>Баланс змінився: нік і новий баланс. Слухають ачівки («Сотня»).</summary>
    public event Action<string, int>? Changed;

    EconomyOptions O => opts.CurrentValue;
    string Today => Days.Today(clock);

    public static string Key(string? nick) => EconomyStore.Key(nick);

    public int Balance(string nick) => store.Balance(Key(nick));

    public WalletRow Wallet(string nick)
    {
        var key = Key(nick);
        return store.Wallet(key) ?? new WalletRow(key, nick, 0, 0, 0);
    }

    /// <summary>Топ гаманців: by = "balance" (типово) або "earned".</summary>
    public List<(string Nick, int Balance, int Earned)> Top(int n, string by = "balance") =>
        store.TopShards(n, DateTimeOffset.MinValue, byBalance: by != "earned")
            .Select(r => (r.Nick, r.Balance, r.Earned)).ToList();

    /// <summary>Перерахувати гаманці з леджера (після ручного втручання в базу або збою).</summary>
    public void Rebuild() => store.Rebuild(clock.UtcNow);

    // ---------- нарахування ----------

    /// <summary>Нарахувати без стелі. <paramref name="refKey"/> — ключ ідемпотентності (повтор → Duplicate).</summary>
    public GrantResult Grant(string nick, int amount, string reason, string? refKey = null, string? text = null) =>
        Apply(nick, amount, reason, refKey, null, null, 0, 0, text);

    /// <summary>Нарахувати зі стелею дня: <paramref name="capUnits"/> — скільки «з'їдає» ця операція.</summary>
    public GrantResult GrantCapped(string nick, int amount, string reason, string? refKey,
        string capKey, int capLimit, int capUnits = 1, string? text = null) =>
        Apply(nick, amount, reason, refKey, null, capKey, capLimit, capUnits, text);

    /// <summary>
    /// Нарахувати зі стелею, де сам лічильник дає номер для ref: «десята хвилина слухання»,
    /// «третій обмін глеків». Так повтор не потребує окремої нумерації в тому, хто кличе.
    /// </summary>
    public GrantResult GrantSequenced(string nick, int amount, string reason, Func<int, string> refFor,
        string capKey, int capLimit, int capUnits = 1, string? text = null) =>
        Apply(nick, amount, reason, null, refFor, capKey, capLimit, capUnits, text);

    GrantResult Apply(string nick, int amount, string reason, string? refKey, Func<int, string>? refFor,
        string? capKey, int capLimit, int capUnits, string? text)
    {
        // нічого не рухаємо — і кажемо про це прямо: Applied тут ввів би в оману того, хто перевіряє виплату
        if (amount <= 0) return GrantResult.Skipped;
        var key = Key(nick);
        if (key.Length == 0) return GrantResult.Skipped;
        var (result, balance) = store.Grant(key, nick, amount, reason, refKey, refFor, capKey, Today, capLimit, capUnits, clock.UtcNow);
        if (result == GrantResult.Applied) Announce(nick, balance, amount, reason, text);
        return result;
    }

    // ---------- списання ----------

    /// <summary>
    /// Списати. Атомарно: або вистачило, або нічого не сталось. Повтор із тим самим ref — true без
    /// другого списання (щоб подвійний старт партії не з'їв дві ставки).
    /// </summary>
    public bool TrySpend(string nick, int amount, string reason, string? refKey = null)
    {
        if (amount <= 0) return true;
        var key = Key(nick);
        if (key.Length == 0) return false;
        var (ok, duplicate, balance) = store.Spend(key, nick, amount, reason, refKey, clock.UtcNow);
        if (ok && !duplicate) Announce(nick, balance, -amount, reason, null);
        return ok;
    }

    /// <summary>
    /// Розповісти про рух грошей. Гроші на цей момент уже закомічені, тому жоден підписник не має права
    /// зробити операцію «невдалою»: виняток із розсилки чи з ачівок летів би з <see cref="TrySpend"/>
    /// назовні, і каркас кімнат вирішив би, що ставку не списано, коли її вже списано.
    /// </summary>
    void Announce(string nick, int balance, int delta, string reason, string? text)
    {
        try { outbox.Post(new WalletChanged(nick, balance, delta, reason, text ?? Text(delta, reason))); }
        catch (Exception ex) { log.LogWarning(ex, "не розіслав зміну гаманця {Nick} ({Reason})", nick, reason); }
        try { Changed?.Invoke(nick, balance); }
        catch (Exception ex) { log.LogWarning(ex, "підписник на зміну балансу {Nick} спіткнувся", nick); }
    }

    // ---------- IStakes (те, що просить каркас кімнат) ----------

    void IStakes.Grant(string nick, int amount, string reason, string refKey) => Grant(nick, amount, reason, refKey);

    // ---------- людські тексти ----------

    /// <summary>«+5 черепків: перемога в шахах», «−10 черепків: ставка».</summary>
    public string Text(int delta, string reason) =>
        $"{(delta < 0 ? "−" : "+")}{Math.Abs(delta)} {Shards(delta)}: {Reason(reason)}";

    /// <summary>Черепок / черепки / черепків — бо «+2 черепків» ріже око.</summary>
    public static string Shards(int n)
    {
        var abs = Math.Abs(n);
        if (abs % 100 is >= 11 and <= 14) return "черепків";
        return (abs % 10) switch { 1 => "черепок", 2 or 3 or 4 => "черепки", _ => "черепків" };
    }

    /// <summary>
    /// Код причини → те, що людина прочитає в тості. Назва гри йде через тире в називному відмінку:
    /// «перемога в шахах» вимагає місцевого відмінка, якого в <see cref="GameInfo"/> нема (там знахідний,
    /// «сіли грати в шахи»), а вигадувати відмінювання на льоту — гарантовано наступити на «дурня» і «Глек-слово».
    /// </summary>
    public string Reason(string reason)
    {
        var (head, tail) = Split(reason);
        return head switch
        {
            "listen" => "за те, що слухаєш",
            "win" => $"перемога — {names.Title(tail)}",
            "draw" => $"нічия — {names.Title(tail)}",
            "play" => $"за партію — {names.Title(tail)}",
            "solo" => $"результат — {names.Title(tail)}",
            "points" => $"очки за партію — {names.Title(tail)}",
            "daily" => $"щоденний глек — {names.Title(tail)}",
            "ach" => "ачівка",
            "stake" => "ставка",
            "stake-win" => "банк за ставку",
            "stake-refund" => "ставку повернуто",
            "clicker" => "обмін глеків на черепки",
            "ad" => tail switch
            {
                "winner" => "перемога в конкурсі реклами",
                "vote" => "за голос у конкурсі реклами",
                "listen" => "за прослухану рекламу",
                _ => "за конкурс реклами",
            },
            "award" => "нагорода за гру",
            "ban" => "бан треку",
            "unban" => "викуп треку з бану",
            "ban-refund" => "бан не записався, повертаю",
            _ => reason,
        };
    }

    static (string Head, string Tail) Split(string reason)
    {
        var i = reason.IndexOf(':');
        return i < 0 ? (reason, "") : (reason[..i], reason[(i + 1)..]);
    }
}
