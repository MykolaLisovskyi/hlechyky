using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ранг цеху: що треба, щоб його дістати (обпалено виробів за весь час, покладено на вози, розписів у колекції) і
/// майстерштук — дзвінкий виріб одного з <paramref name="PieceWares"/> у розписі з <paramref name="PieceStyles"/>
/// (порожній рядок — розпис будь-який). Котрий саме — від зерна ніка, щоб у друзів були різні.
/// </summary>
public sealed record ClickerGuildRank(string Key, string Name, long Fired, long Given, int Styles,
    string[] PieceWares, string[] PieceStyles, string Perk);

/// <summary>
/// Цех: тижневий віз для всіх гончарів, дарунки, хата друга, похвала в Журнал, цехові ранги (контракт гачків —
/// docs/games/specs/clicker-v7.md §2.7, як реалізовано — docs/games/specs/clicker-v7-guild.md). Спільне живе в
/// <see cref="ClickerGuildService"/>; тут — те, що належить самому гончареві: ранг, скільки поклав на вози й
/// подарував, полиця дарунків, коли хвалився. Без сервісу (тести, гола гра) цех зачинений, але гра не падає, а
/// ранг і його перки — зі збереження — лишаються.
///
/// Нічого змагального: віз один на всіх, нагорода кожному однакова, пропущений тиждень нічого не забирає, престиж
/// «Обпал» цеху не чіпає.
/// </summary>
public sealed partial class Clicker
{
    public static readonly ClickerGuildRank[] GuildRanks =
    [
        new("apprentice", "Учень", 0, 0, 0, [], [], "Кожен починав з глини на руках"),
        new("journeyman", "Челядник", 50, 10, 0, ["jug", "makitra"], [""],
            "Автогорно: підмайстри самі палять повну сушарню (якість звичайна)"),
        new("master", "Майстер", 500, 100, 3, ["dish", "candle", "whistle", "barrel"], ["gavarets", "vasylkiv", "bubnivka", "kosiv"],
            "+2 місця в горні й наступний виріб відкривається на щабель раніше"),
        new("guildmaster", "Цехмістр", 3000, 400, 6, ["tile", "kumanets", "ram"], ["kosiv", "opishnia", "mezhyhirya"],
            "Нагорода воза ×1,5, титул у хаті й у похвалах"),
    ];

    public const int GuildJourneyman = 1, GuildMaster = 2, GuildMasterOfGuild = 3;
    /// <summary>Похвалитись у Журнал — не частіше.</summary>
    public static readonly TimeSpan BragEvery = TimeSpan.FromMinutes(15);
    /// <summary>Цехмістрові нагорода воза більша.</summary>
    public const double GuildmasterWagon = 1.5;
    /// <summary>Майстер цеху: стільки ще місць у горні (підключає горно).</summary>
    public const int MasterKilnSlots = 2;
    /// <summary>Дно нагороди воза на голому колі: стільки кліків за кожну хвилину.</summary>
    public const double WagonClicksPerMinute = 20;
    public const int GiftsForAchievement = 10;

    /// <summary>Рід виробу для узгодження «дзвінкий/дзвінка/дзвінке» в похвалі.</summary>
    static readonly Dictionary<string, char> WareGender = new(StringComparer.Ordinal)
    {
        ["bowl"] = 'f', ["makitra"] = 'f', ["tile"] = 'f', ["barrel"] = 'n',
    };

    /// <summary>Розпис у похвалі: «косівський розпис», «межигірський фаянс».</summary>
    static readonly Dictionary<string, string> StylePhrase = new(StringComparer.Ordinal)
    {
        [""] = "без розпису, чиста глина",
        ["gavarets"] = "гаварецька чорнодимлена",
        ["vasylkiv"] = "васильківська майоліка",
        ["bubnivka"] = "бубнівський розпис",
        ["kosiv"] = "косівський розпис",
        ["opishnia"] = "опішнянський розпис",
        ["mezhyhirya"] = "межигірський фаянс",
        ["petrykivka"] = "петриківський розпис",
        ["trypillia"] = "трипільський орнамент",
    };

    ClickerGuildService? _guildSvc;
    /// <summary>Чи вже сказали цеху «я тут» за життя цієї кімнати.</summary>
    bool _guildHello;
    int _guildRank;
    long _guildGiven;
    int _guildClaims;
    int _giftsSent;
    int _giftsGot;
    readonly List<GuildGift> _giftShelf = [];
    DateTimeOffset _bragAt;
    bool _autoKilnOff;

    string GuildNick => (Ctx.NickOf(0) ?? "").Trim();
    string GuildKey => ClickerGuildService.Key(Ctx.NickOf(0));

    // ---------- перки для інших пакетів ----------

    /// <summary>
    /// Челядник: «автогорно» — підмайстри самі палять повну сушарню з якістю 1, без мінігри й без тріщин. Горно (B2)
    /// перевіряє цей прапорець у своєму Sync: сушарня повна й горно холодне → <c>TakeDry</c> усе сухе, <c>AddFired</c>,
    /// <c>PutItems(…, quality: 1)</c>. Гравець може вимкнути (дія <c>guild { op: "auto", on }</c>).
    /// </summary>
    internal bool GuildAutoKiln => _guildRank >= GuildJourneyman && !_autoKilnOff;

    /// <summary>Майстер цеху: +2 місця в горні (горно додає до своєї місткості).</summary>
    internal int GuildKilnSlots => _guildRank >= GuildMaster ? MasterKilnSlots : 0;

    /// <summary>Майстер цеху: наступний за порогом виріб відкривається на щабель раніше (коли відкритий попередній).</summary>
    bool GuildWareOpen(string ware)
    {
        if (_guildRank < GuildMaster) return false;
        var i = Array.FindIndex(Wares, w => w.Key == ware);
        return i > 0 && _total >= Wares[i - 1].Unlock;
    }

    double GuildAllMult => 1;

    /// <summary>Ранг рахує обпалені з <see cref="FiredTotal"/>, тож окремий лічильник тут не потрібен.</summary>
    void GuildOnFired(ItemInfo item, int n) { }

    // ---------- життя партії ----------

    sealed record GuildRow(
        int Rank = 0, long Given = 0, int Claims = 0, int GiftsSent = 0, int GiftsGot = 0,
        List<GuildGift>? Shelf = null, DateTimeOffset BragAt = default, bool AutoOff = false);

    GuildRow? SaveGuild() => new(_guildRank, _guildGiven, _guildClaims, _giftsSent, _giftsGot, _giftShelf.ToList(), _bragAt, _autoKilnOff);

    void LoadGuild(GuildRow? row)
    {
        ClearGuild();
        if (row is null) return;
        _guildRank = Math.Clamp(row.Rank, 0, GuildRanks.Length - 1);
        _guildGiven = Math.Max(0, row.Given);
        _guildClaims = Math.Max(0, row.Claims);
        _giftsSent = Math.Max(0, row.GiftsSent);
        _giftsGot = Math.Max(0, row.GiftsGot);
        foreach (var g in row.Shelf ?? [])
            if (ValidGift(g) is { } ok && _giftShelf.Count < ClickerGuildService.ShelfSize) _giftShelf.Add(ok);
        _bragAt = row.BragAt;
        _autoKilnOff = row.AutoOff;
    }

    /// <summary>Дарунок зі збереження чи скриньки: відомий виріб, розпис і якість; ім'я дарувальника — коротке.</summary>
    static GuildGift? ValidGift(GuildGift? g)
    {
        if (g is null || WareOf(g.Ware) is null || g.Quality is < 1 or > 3) return null;
        var style = g.Style ?? "";
        if (style.Length > 0 && Styles.All(s => s.Key != style)) return null;
        var from = (g.From ?? "").Trim();
        if (from.Length > 24) from = from[..24];
        return g with { From = from.Length == 0 ? "друг" : from, Style = style };
    }

    void ClearGuild()
    {
        _guildRank = 0;
        _guildGiven = 0;
        _guildClaims = 0;
        _giftsSent = 0;
        _giftsGot = 0;
        _giftShelf.Clear();
        _bragAt = default;
        _autoKilnOff = false;
    }

    void ResetGuild(DateTimeOffset now)
    {
        // Сервіс — з Ctx.Services, як словники в інших іграх. У тестах без нього цех просто зачинений.
        _guildSvc = Ctx.Services.GetService<ClickerGuildService>();
        _guildHello = false;
        ClearGuild();
    }

    /// <summary>Обпал цеху не чіпає: ранг, внески у вози, дарунки на полиці й лічильники — назавжди.</summary>
    void FireGuild(DateTimeOffset now) { }

    /// <summary>Кожна синхронізація: вперше — «я тут» у список цеху; і дарунки зі скриньки — на полицю.</summary>
    void SyncGuild(DateTimeOffset now, TimeSpan paid)
    {
        if (_guildSvc is not { } svc || GuildKey.Length == 0) return;
        if (!_guildHello)
        {
            _guildHello = true;
            svc.Hello(GuildKey, GuildNick, _guildRank, now);
        }
        if (svc.TakeMail(GuildKey) is not { } mail) return;
        foreach (var raw in mail)
        {
            if (ValidGift(raw) is not { } g) continue;
            // На полицю, а не в комору: дарунок — пам'ять, а не товар. Інакше двоє друзів ганяли б вироби туди-сюди
            // й продавали. Зате дарунок відкриває клітинку альбому (без зірки й майстерності — див. AlbumOnGift).
            _giftShelf.Insert(0, g with { At = now });
            _giftsGot++;
            AlbumOnGift(new ItemInfo(g.Ware, g.Style, g.Quality));
            AwayNote($"🎁 Дарунок від {g.From}: {ItemWords(new ItemInfo(g.Ware, g.Style, g.Quality))}");
        }
        if (_giftShelf.Count > ClickerGuildService.ShelfSize)
            _giftShelf.RemoveRange(ClickerGuildService.ShelfSize, _giftShelf.Count - ClickerGuildService.ShelfSize);
    }

    // ---------- дія guild { op, … } ----------

    ActResult? ActGuild(string action, JsonElement payload)
    {
        if (action != "guild") return null;
        if (_guildSvc is not { } svc || GuildKey.Length == 0) return ActResult.Fail("Цех зараз зачинений");
        var now = Ctx.Clock.UtcNow;
        return Str(payload, "op") switch
        {
            "give" => GuildGive(svc, payload, now),
            "claim" => GuildClaim(svc, payload, now),
            "gift" => GuildGiftTo(svc, payload, now),
            "brag" => GuildBrag(payload, now),
            "masterpiece" => GuildMasterpiece(svc, now),
            "auto" => GuildAuto(payload),
            _ => ActResult.Fail("Такого в цеху не роблять"),
        };
    }

    /// <summary>Покласти вироби з комори на віз тижня.</summary>
    ActResult GuildGive(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        if (ParseItem(Str(payload, "key")) is not { } it) return ActResult.Fail("Такого виробу в коморі нема");
        var raw = Num(payload, "n");
        if (raw is <= 0) return ActResult.Fail("Скільки покласти — хоч один");
        var have = ItemCount(x => x == it);
        if (have <= 0) return ActResult.Fail("Такого виробу в коморі нема");
        var n = TakeItems(x => x == it, (int)Math.Min(Math.Clamp(raw ?? 1, 1, StoreCap), have));
        var r = svc.Give(GuildKey, GuildNick, it.Ware, n, now);
        _guildGiven += n;
        if (r.Reached > 0)
        {
            var who = string.Join(", ", r.Wagon.Givers.Select(g => g.Nick));
            Ctx.Log($"🐴 Віз цеху гончарів — {ClickerGuildService.TierNames[r.Reached]}! Разом клали: {who}. Хто поклав хоч "
                + $"{ClickerGuildService.MinGive}, забирайте нагороду у вкладці «Цех»");
        }
        var text = $"🐴 {n} × {WareOf(it.Ware)!.Name.ToLowerInvariant()} на віз — він повний на {Pct(r.Wagon)} %";
        if (r.Wagon.Mine < ClickerGuildService.MinGive) text += $" · ще {ClickerGuildService.MinGive - r.Wagon.Mine} — і нагорода твоя";
        return ActResult.Accept(text);
    }

    static string Pct(WagonInfo w) => w.Goal > 0 ? Math.Floor(100.0 * w.Total / w.Goal).ToString("0", Uk) : "0";

    /// <summary>Скільки глеків дасть нагорода воза за рівні від <paramref name="was"/> до <paramref name="tier"/>.</summary>
    long WagonReward(int tier, int was, out double minutes)
    {
        minutes = (ClickerGuildService.TierMinutes[Math.Clamp(tier, 0, 3)] - ClickerGuildService.TierMinutes[Math.Clamp(was, 0, 3)])
            * (_guildRank >= GuildMasterOfGuild ? GuildmasterWagon : 1);
        if (minutes <= 0) return 0;
        var byPassive = PassiveBase * minutes * 60;
        var floor = (double)ClickBase * minutes * WagonClicksPerMinute;
        return ToLong(Math.Max(byPassive, floor));
    }

    ActResult GuildClaim(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        var week = Str(payload, "week");
        var r = svc.Claim(GuildKey, week.Length > 0 ? week : null, now);
        if (r.Error is { } why) return ActResult.Fail(why);
        var gain = WagonReward(r.Tier, r.Was, out var minutes);
        Add(gain);
        _guildClaims++;
        if (_guildClaims == 1) Ctx.Award(0, 0, "ach:potter-wagon");
        return ActResult.Accept($"🛒 Віз повернувся з ярмарку ({ClickerGuildService.TierNames[r.Tier]}): +{Short(gain)} {Pots(gain)} — "
            + $"{minutes.ToString("0", Uk)} хв твого пасиву");
    }

    /// <summary>Дарунок другові: один виріб із комори.</summary>
    ActResult GuildGiftTo(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        if (ParseItem(Str(payload, "key")) is not { } it || ItemCount(x => x == it) <= 0)
            return ActResult.Fail("Такого виробу в коморі нема");
        var to = Str(payload, "nick").Trim();
        if (svc.Gift(GuildKey, GuildNick, to, it, now) is { } why) return ActResult.Fail(why);
        TakeItems(x => x == it, 1);
        _giftsSent++;
        if (_giftsSent == GiftsForAchievement) Ctx.Award(0, 0, "ach:potter-gift");
        return ActResult.Accept($"🎁 {Capital(ItemWords(it, style: false))} — дарунок для {to} уже в дорозі");
    }

    /// <summary>Похвалитись виробом із комори в Журнал — раз на чверть години.</summary>
    ActResult GuildBrag(JsonElement payload, DateTimeOffset now)
    {
        if (ParseItem(Str(payload, "key")) is not { } it || ItemCount(x => x == it) <= 0)
            return ActResult.Fail("Хвалитись можна лише тим, що лежить у коморі");
        if (now < _bragAt) return ActResult.Fail($"Дай селу надивуватись: наступна похвала за {Wait(_bragAt - now)}");
        _bragAt = now + BragEvery;
        Ctx.Log(BragText(GuildNick, _guildRank, it));
        return ActResult.Accept("🏺 Усе село вже знає!");
    }

    /// <summary>«🏺 Цехмістр Микола хвалиться: дзвінкий куманець — косівський розпис».</summary>
    internal static string BragText(string nick, int rank, ItemInfo it) =>
        $"🏺 {(rank >= GuildMasterOfGuild ? "Цехмістр " : "")}{nick} хвалиться: {ItemWords(it)}";

    /// <summary>«дзвінка макітра — бубнівський розпис» (без <paramref name="style"/> — лише «дзвінка макітра»).</summary>
    internal static string ItemWords(ItemInfo it, bool style = true)
    {
        var name = (WareOf(it.Ware)?.Name ?? it.Ware).ToLowerInvariant();
        var g = WareGender.GetValueOrDefault(it.Ware, 'm');
        string[] words = g switch
        {
            'f' => ["", "звичайна", "добра", "дзвінка"],
            'n' => ["", "звичайне", "добре", "дзвінке"],
            _ => ["", "звичайний", "добрий", "дзвінкий"],
        };
        var head = $"{words[Math.Clamp(it.Quality, 1, 3)]} {name}";
        return style ? $"{head} — {StylePhrase.GetValueOrDefault(it.Style, it.Style)}" : head;
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Майстерштук для рангу: виріб і розпис від зерна ніка, щоб друзі не здавали однакових.</summary>
    internal (string Ware, string Style) PieceFor(int rank)
    {
        var r = GuildRanks[Math.Clamp(rank, 1, GuildRanks.Length - 1)];
        var seed = Days.Seed("clicker-masterpiece", $"{GuildKey}:{rank}");
        return (r.PieceWares[seed % r.PieceWares.Length], r.PieceStyles[(seed / 7) % r.PieceStyles.Length]);
    }

    static bool PieceMatch(ItemInfo x, (string Ware, string Style) piece) =>
        x.Ware == piece.Ware && x.Quality == 3 && (piece.Style.Length == 0 || x.Style == piece.Style);

    /// <summary>Чого бракує до наступного рангу (без майстерштука); порожньо — готовий.</summary>
    List<string> RankMissing(ClickerGuildRank next)
    {
        var miss = new List<string>();
        if (FiredTotal < next.Fired) miss.Add($"обпалити ще {Short(next.Fired - FiredTotal)} {WaresWord(next.Fired - FiredTotal)}");
        if (_guildGiven < next.Given) miss.Add($"покласти на вози ще {Short(next.Given - _guildGiven)}");
        if (_styles.Count < next.Styles) miss.Add($"зібрати ще {next.Styles - _styles.Count} розписи");
        return miss;
    }

    /// <summary>Здати майстерштук: умови рангу виконано й дзвінкий виріб потрібного розпису лежить у коморі.</summary>
    ActResult GuildMasterpiece(ClickerGuildService svc, DateTimeOffset now)
    {
        if (_guildRank >= GuildRanks.Length - 1) return ActResult.Fail("Ти вже цехмістр — вище в цеху лише небо");
        var next = GuildRanks[_guildRank + 1];
        if (RankMissing(next) is { Count: > 0 } miss) return ActResult.Fail($"До рангу «{next.Name}» ще: {string.Join(", ", miss)}");
        var piece = PieceFor(_guildRank + 1);
        if (ItemCount(x => PieceMatch(x, piece)) <= 0)
            return ActResult.Fail($"Цех чекає майстерштук: {PieceWords(piece)}");
        TakeItems(x => PieceMatch(x, piece), 1);
        _guildRank++;
        svc.Hello(GuildKey, GuildNick, _guildRank, now);
        Ctx.Log($"🎓 {GuildNick} склав(ла) майстерштук — тепер {next.Name.ToLowerInvariant()} цеху гончарів");
        if (_guildRank == GuildMasterOfGuild) Ctx.Award(0, 0, "ach:potter-rank");
        return ActResult.Accept($"🎓 Цех прийняв майстерштук: ти {next.Name.ToLowerInvariant()}! {next.Perk}");
    }

    static string PieceWords((string Ware, string Style) piece)
    {
        var w = WareOf(piece.Ware)!;
        var g = WareGender.GetValueOrDefault(piece.Ware, 'm');
        var adj = g switch { 'f' => "дзвінка", 'n' => "дзвінке", _ => "дзвінкий" };
        return $"{adj} {w.Name.ToLowerInvariant()}" + (piece.Style.Length > 0 ? $" — {StylePhrase[piece.Style]}" : " у будь-якому розписі");
    }

    ActResult GuildAuto(JsonElement payload)
    {
        if (_guildRank < GuildJourneyman) return ActResult.Fail("Автогорно — перк челядника");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
        _autoKilnOff = !on;
        return ActResult.Accept(on ? "🔥 Підмайстри палитимуть повну сушарню самі" : "🔥 Горно — лише твоїми руками");
    }

    // ---------- вид і каталог ----------

    object? ViewGuild(DateTimeOffset now)
    {
        if (_guildSvc is not { } svc || GuildKey.Length == 0)
            return new { enabled = false, rank = _guildRank };
        var s = svc.Summary(GuildKey, now);
        return new
        {
            enabled = true,
            week = ClickerGuildService.WagonView(s.Week),
            // Минулий тиждень — лише коли там є що забрати чи на що глянути.
            prev = s.Prev.Total > 0 ? ClickerGuildService.WagonView(s.Prev) : null,
            claims = new[] { s.Prev, s.Week }
                .Where(w => w.Mine >= ClickerGuildService.MinGive && w.Tier > w.Claimed)
                .Select(w => new { week = w.Week, tier = w.Tier, was = w.Claimed, pots = WagonReward(w.Tier, w.Claimed, out var m), minutes = m }),
            gifts = new { left = s.GiftsLeft, sent = _giftsSent, got = _giftsGot },
            shelf = _giftShelf.Select(g => new { from = g.From, ware = g.Ware, style = g.Style, q = g.Quality, at = g.At }),
            rank = _guildRank,
            given = _guildGiven,
            next = NextRankView(),
            autoKiln = GuildAutoKiln,
            autoOff = _autoKilnOff,
            kilnSlots = GuildKilnSlots,
            bragAt = _bragAt,
        };
    }

    object? NextRankView()
    {
        if (_guildRank >= GuildRanks.Length - 1) return null;
        var next = GuildRanks[_guildRank + 1];
        var piece = PieceFor(_guildRank + 1);
        return new
        {
            rank = _guildRank + 1,
            fired = new { have = FiredTotal, need = next.Fired },
            given = new { have = _guildGiven, need = next.Given },
            styles = new { have = _styles.Count, need = next.Styles },
            piece = new { ware = piece.Ware, style = piece.Style, have = ItemCount(x => PieceMatch(x, piece)) > 0 },
            ready = RankMissing(next).Count == 0,
        };
    }

    object? CatalogGuild() => new
    {
        ranks = GuildRanks.Select(r => new
        {
            key = r.Key, name = r.Name, perk = r.Perk, fired = r.Fired, given = r.Given, styles = r.Styles,
        }),
        tiers = ClickerGuildService.TierNames.Select((name, i) => new
        {
            name, share = ClickerGuildService.TierShare[i], minutes = ClickerGuildService.TierMinutes[i],
        }),
        minGive = ClickerGuildService.MinGive,
        perPotter = ClickerGuildService.PerPotter,
        giftsPerDay = ClickerGuildService.GiftsPerDay,
        shelfSize = ClickerGuildService.ShelfSize,
        bragMinutes = BragEvery.TotalMinutes,
        guildmasterWagon = GuildmasterWagon,
        styleWords = StylePhrase,
    };
}
