using System.Numerics;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Трипільська знахідка з глинища: ключ і назва (опис — у каталозі альбому).</summary>
public sealed record ClickerFind(string Key, string Name);

/// <summary>Кахля в печі: її розпис (порожній — проста) і якість (2 добра, 3 дзвінка).</summary>
public sealed record StoveTile(string Style, int Quality);

/// <summary>
/// Альбом майстра (пакет B3 сьомого оновлення, docs/games/specs/clicker-v7.md §4 і clicker-v7-album.md): те, що
/// лишається від ремесла назавжди, — навіть обпал-престиж його не чіпає.
///
/// 1) Сітка «виріб × розпис» 12 × 9: клітинку відкриває перший обпалений виріб цієї пари, зірку — перший дзвінкий.
///    Клітинка +0,2 % до всього, повний рядок виробу +2 %, повний стовпчик розпису +3 %.
/// 2) Майстерність за кожним виробом — від лічильника обпалених (<see cref="FiredOf"/>): десять рівнів, кожен −4 %
///    роботи й +5 % ціни виробу.
/// 3) Кахляна піч: дванадцять гнізд для добрих і дзвінких кахлів із комори, кожне +1 %, повна піч ще +5 %.
/// 4) Трипільський музей: кожен виліплений виріб — 1,5 % знайти в глинищі одну з восьми речей; дубль — уламок, п'ять
///    уламків склеюються в річ, якої бракує. Кожна річ +1 %, усі вісім ще +5 %.
///
/// Усі бонуси складаються між собою і йдуть одним множником <see cref="AlbumAllMult"/>. Правила відкриття й рівнів —
/// чисті статичні функції (їх тестують напряму), стан — лише бітові маски й списки.
/// </summary>
public sealed partial class Clicker
{
    public const double AlbumCellBonus = 0.002, AlbumRowBonus = 0.02, AlbumColumnBonus = 0.03;
    public const int StoveSlots = 12, StoveMinQuality = 2;
    public const double StoveTileBonus = 0.01, StoveFullBonus = 0.05;
    /// <summary>Шанс знахідки на кожен виліплений виріб.</summary>
    public const double FindChance = 0.015;
    public const double FindBonus = 0.01, MuseumFullBonus = 0.05;
    /// <summary>Стільки уламків склеюються в річ, якої в музеї ще нема.</summary>
    public const int ShardsForGlue = 5;
    /// <summary>Обпалених виробів одного виду для рівнів майстерності 1…10.</summary>
    public static readonly long[] MasteryAt = [5, 15, 40, 100, 250, 600, 1_500, 4_000, 10_000, 25_000];
    public const double MasteryWorkStep = 0.04, MasteryValueStep = 0.05;

    /// <summary>Вісім реальних типів трипільських знахідок.</summary>
    public static readonly ClickerFind[] Finds =
    [
        new("spiral", "Черепок зі спіраллю"),
        new("binocular", "Біноклеподібна посудина"),
        new("figurine", "Жіноча статуетка"),
        new("house", "Модель житла"),
        new("grain", "Миска зі знаком зерна"),
        new("krater", "Кратер"),
        new("ladle", "Ложка-черпак"),
        new("whorl", "Пряслице"),
    ];

    /// <summary>Стовпчиків у сітці: простий + усі розписи.</summary>
    public static int AlbumColumns => Styles.Length + 1;
    public static int AlbumSize => Wares.Length * AlbumColumns;
    static int FullRowMask => (1 << AlbumColumns) - 1;
    static int FullMuseumMask => (1 << Finds.Length) - 1;

    readonly int[] _albumCells = new int[Wares.Length];
    readonly int[] _albumStars = new int[Wares.Length];
    readonly List<StoveTile> _stove = [];
    int _finds;
    int _findShards;
    LastFindRow? _lastFind;

    // ---------- чисті правила ----------

    /// <summary>Стовпчик розпису: 0 — простий, 1…8 — <see cref="Styles"/> по черзі; −1 — такого нема.</summary>
    public static int AlbumStyleIndex(string? style)
    {
        if (string.IsNullOrEmpty(style)) return 0;
        var i = Array.FindIndex(Styles, s => s.Key == style);
        return i < 0 ? -1 : i + 1;
    }

    /// <summary>Рядок виробу в сітці (порядок <see cref="Wares"/>); −1 — такого нема.</summary>
    public static int AlbumWareIndex(string? ware) => Array.FindIndex(Wares, w => w.Key == ware);

    /// <summary>Рівень майстерності 0…10 за кількістю обпалених виробів одного виду.</summary>
    public static int MasteryLevel(long fired)
    {
        var level = 0;
        while (level < MasteryAt.Length && fired >= MasteryAt[level]) level++;
        return level;
    }

    /// <summary>Скільки роботи лишається від майстерності: 1 − 4 % за рівень (основа все одно не пускає нижче 40 %).</summary>
    public static double MasteryWorkMult(int level) => 1 - MasteryWorkStep * Math.Clamp(level, 0, MasteryAt.Length);

    /// <summary>Ціна виробу від майстерності: +5 % за рівень.</summary>
    public static double MasteryValueMult(int level) => 1 + MasteryValueStep * Math.Clamp(level, 0, MasteryAt.Length);

    /// <summary>Скільки клітинок відкрито в масках рядків.</summary>
    public static int AlbumOpenCount(IReadOnlyList<int> cells) => cells.Sum(m => BitOperations.PopCount((uint)(m & FullRowMask)));

    /// <summary>Скільки виробів зібрано в усіх розписах.</summary>
    public static int AlbumFullRows(IReadOnlyList<int> cells) => cells.Count(m => (m & FullRowMask) == FullRowMask);

    /// <summary>Скільки розписів зібрано на всіх виробах.</summary>
    public static int AlbumFullColumns(IReadOnlyList<int> cells)
    {
        if (cells.Count < Wares.Length) return 0;
        var all = FullRowMask;
        foreach (var m in cells) all &= m;
        return BitOperations.PopCount((uint)all);
    }

    /// <summary>
    /// Бонус альбому до всього (частка, 0,12 = +12 %): сітка (клітинки, рядки, стовпчики), кахляна піч і музей.
    /// </summary>
    public static double AlbumBonusFor(IReadOnlyList<int> cells, int tiles, int finds)
    {
        var grid = AlbumCellBonus * AlbumOpenCount(cells) + AlbumRowBonus * AlbumFullRows(cells) + AlbumColumnBonus * AlbumFullColumns(cells);
        tiles = Math.Clamp(tiles, 0, StoveSlots);
        var stove = StoveTileBonus * tiles + (tiles == StoveSlots ? StoveFullBonus : 0);
        var found = BitOperations.PopCount((uint)(finds & FullMuseumMask));
        var museum = FindBonus * found + (found == Finds.Length ? MuseumFullBonus : 0);
        return grid + stove + museum;
    }

    // ---------- гачки основи ----------

    double AlbumAllMult => 1 + AlbumBonusFor(_albumCells, _stove.Count, _finds);

    double AlbumWorkMult(string ware) => MasteryWorkMult(MasteryLevel(FiredOf(ware)));

    double AlbumValueMult(string ware) => MasteryValueMult(MasteryLevel(FiredOf(ware)));

    /// <summary>Горно видало вироби: нова клітинка, зірка за дзвінкий, рівень майстерності (лічильник основа вже збільшила).</summary>
    void AlbumOnFired(ItemInfo item, int n)
    {
        if (n <= 0) return;
        var row = AlbumWareIndex(item.Ware);
        var col = AlbumStyleIndex(item.Style);
        if (row < 0 || col < 0) return;
        var bit = 1 << col;
        if ((_albumCells[row] & bit) == 0)
        {
            _albumCells[row] |= bit;
            AwayNote($"📒 Альбом: нова клітинка — {WareOf(item.Ware)!.Name.ToLowerInvariant()}, {StyleWord(item.Style)}");
            if (_albumCells[row] == FullRowMask) Ctx.Award(0, 0, "ach:potter-album-row");
            if (AlbumOpenCount(_albumCells) == AlbumSize) Ctx.Award(0, 0, "ach:potter-album-all");
        }
        if (item.Quality >= 3) _albumStars[row] |= bit;
        var after = MasteryLevel(FiredOf(item.Ware));
        var before = MasteryLevel(Math.Max(0, FiredOf(item.Ware) - n));
        if (after > before && after == MasteryAt.Length) Ctx.Award(0, 0, "ach:potter-mastery");
    }

    /// <summary>Виліплено виріб: копнули глини — може, щось трипільське. Повний музей більше не шукає.</summary>
    void AlbumOnFormed(string ware)
    {
        if ((_finds & FullMuseumMask) == FullMuseumMask) return;
        if (Ctx.Rng.NextDouble() >= FindChance) return;
        var i = Ctx.Rng.Next(Finds.Length);
        var find = Finds[i];
        var now = Ctx.Clock.UtcNow;
        if ((_finds & (1 << i)) != 0)
        {
            _findShards++;
            _lastFind = new LastFindRow(find.Key, now, true);
            AwayNote($"🏺 У глинищі ще один «{find.Name.ToLowerInvariant()}» — уламок у скарбничку");
            return;
        }
        Found(i, now);
        AwayNote($"🏺 У глинищі знайшли: {find.Name.ToLowerInvariant()}");
    }

    void Found(int i, DateTimeOffset now)
    {
        _finds |= 1 << i;
        _lastFind = new LastFindRow(Finds[i].Key, now, false);
        if ((_finds & FullMuseumMask) == FullMuseumMask) Ctx.Award(0, 0, "ach:potter-museum-shards");
    }

    static string StyleWord(string style) =>
        string.IsNullOrEmpty(style) ? "простий" : Styles.FirstOrDefault(s => s.Key == style)?.Name.ToLowerInvariant() ?? style;

    // ---------- дія album ----------

    ActResult? ActAlbum(string action, JsonElement payload)
    {
        if (action != "album") return null;
        return Str(payload, "op") switch
        {
            "tile" => StoveTileIn(payload),
            "glue" => GlueShards(),
            _ => ActResult.Fail("Такого в альбомі не роблять"),
        };
    }

    /// <summary>Вставити кахлю з комори в піч: <c>{ key }</c> — саме цю, без ключа — будь-яку добру (спершу найгіршу).</summary>
    ActResult StoveTileIn(JsonElement payload)
    {
        if (_stove.Count >= StoveSlots) return ActResult.Fail("Піч уже вся в кахлях — краще не буває");
        var key = Str(payload, "key");
        ItemInfo? pick;
        if (key.Length > 0)
        {
            if (ParseItem(key) is not { } it || it.Ware != "tile") return ActResult.Fail("У піч кладуть лише кахлі");
            if (it.Quality < StoveMinQuality) return ActResult.Fail("Звичайна кахля на піч не йде — треба добра чи дзвінка");
            pick = ItemCount(x => x == it) > 0 ? it : null;
        }
        else
        {
            pick = AllItems().Where(x => x.Item.Ware == "tile" && x.Item.Quality >= StoveMinQuality)
                .OrderBy(x => x.Item.Quality).Select(x => x.Item).FirstOrDefault();
        }
        if (pick is null) return ActResult.Fail("У коморі нема доброї кахлі — обпали кахлю якості «добра» чи «дзвінка»");
        if (TakeItems(x => x == pick, 1) != 1) return ActResult.Fail("У коморі нема такої кахлі");
        _stove.Add(new StoveTile(pick.Style, pick.Quality));
        if (_stove.Count == StoveSlots)
        {
            Ctx.Award(0, 0, "ach:potter-stove");
            return ActResult.Accept("🧱 Кахляна піч готова! +17 % до всього — у хаті тепло й гарно");
        }
        return ActResult.Accept($"🧱 Кахля в печі: {_stove.Count} з {StoveSlots} · +1 % до всього");
    }

    /// <summary>П'ять уламків — склеїти річ, якої в музеї ще нема (котру саме — вирішує випадок).</summary>
    ActResult GlueShards()
    {
        var missing = Enumerable.Range(0, Finds.Length).Where(i => (_finds & (1 << i)) == 0).ToList();
        if (missing.Count == 0) return ActResult.Fail("Музей уже повний — уламки лишаються на згадку");
        if (_findShards < ShardsForGlue) return ActResult.Fail($"Бракує уламків: треба {ShardsForGlue}, є {_findShards}");
        _findShards -= ShardsForGlue;
        var i = missing[Ctx.Rng.Next(missing.Count)];
        Found(i, Ctx.Clock.UtcNow);
        return ActResult.Accept($"🏺 Склеїв з уламків: {Finds[i].Name.ToLowerInvariant()} — +1 % до всього");
    }

    // ---------- життя ----------

    void ResetAlbum(DateTimeOffset now)
    {
        Array.Clear(_albumCells);
        Array.Clear(_albumStars);
        _stove.Clear();
        _finds = 0;
        _findShards = 0;
        _lastFind = null;
    }

    /// <summary>Обпал-престиж альбому не чіпає: клітинки, зірки, кахлі й музей — назавжди (майстерність — лічильник основи, теж лишається).</summary>
    void FireAlbum(DateTimeOffset now) { }

    void SyncAlbum(DateTimeOffset now, TimeSpan paid) { }

    object? ViewAlbum(DateTimeOffset now)
    {
        var open = AlbumOpenCount(_albumCells);
        return new
        {
            // Рядок на виріб (порядок Wares): біт 0 — простий, біт i — i-й розпис.
            cells = _albumCells,
            stars = _albumStars,
            open,
            size = AlbumSize,
            rows = AlbumFullRows(_albumCells),
            cols = AlbumFullColumns(_albumCells),
            mastery = Wares.Select(w => MasteryLevel(FiredOf(w.Key))),
            stove = _stove.Select(t => new { style = t.Style, q = t.Quality }),
            tiles = ItemCount(x => x.Ware == "tile" && x.Quality >= StoveMinQuality),
            finds = _finds,
            shards = _findShards,
            find = _lastFind is { } f ? new { key = f.Key, at = f.At, dup = f.Dup } : null,
            bonus = AlbumBonusFor(_albumCells, _stove.Count, _finds),
        };
    }

    // ---------- каталог ----------

    object? CatalogAlbum() => new
    {
        masteryAt = MasteryAt,
        masteryWork = MasteryWorkStep,
        masteryValue = MasteryValueStep,
        cell = AlbumCellBonus,
        row = AlbumRowBonus,
        column = AlbumColumnBonus,
        stoveSlots = StoveSlots,
        stoveTile = StoveTileBonus,
        stoveFull = StoveFullBonus,
        findChance = FindChance,
        find = FindBonus,
        museumFull = MuseumFullBonus,
        glue = ShardsForGlue,
        finds = Finds.Select(f => new { key = f.Key, name = f.Name, desc = FindFacts[f.Key] }),
        wares = WareFacts,
        styles = StyleFacts,
    };

    /// <summary>Короткі факти про вироби — лише перевірені (див. docs/games/specs/clicker-v7-album.md).</summary>
    static readonly Dictionary<string, string> WareFacts = new(StringComparer.Ordinal)
    {
        ["pot"] = "Головна посудина української кухні: у горщику варили борщ і кашу просто в печі.",
        ["bowl"] = "З миски їли всією родиною. Бубнівські полив'яні миски з квітами й гронами — окрема гордість Вінниччини.",
        ["jug"] = "Посудина з вузькою шийкою для молока й води.",
        ["makitra"] = "Широка глибока посудина: у ній макогоном розтирали мак і замішували тісто.",
        ["dish"] = "Велика широка миска, в якій страву подавали на стіл.",
        ["candle"] = "Глиняна підставка для свічки — прості й фігурні, з кількома ріжками.",
        ["whistle"] = "Звукова іграшка. Вірили, що свист глиняного півника відганяє зло.",
        ["barrel"] = "Посудина для напоїв — з вузьким горлом, щоб не розхлюпати.",
        ["tile"] = "Кахлями облицьовували печі. Косів славиться мальованими кахлями.",
        ["kumanets"] = "Кільцеподібна посудина з отвором посередині, весільна. Куманець — символ Опішні.",
        ["ram"] = "Баранець-свищик — звукова іграшка, як і півник: свист, вірили, відганяв зло.",
        ["lion"] = "Фігурна посудина у вигляді звіра — вершина гончарного вміння: її ліплять частинами і з'єднують сирими.",
    };

    /// <summary>Осередки й розписи: звідки і що в них особливого.</summary>
    static readonly Dictionary<string, object> StyleFacts = new(StringComparer.Ordinal)
    {
        [""] = new { place = "У кожному гончарному селі", text = "Без розпису — чиста глина: так ліпили щоденний посуд." },
        ["gavarets"] = new { place = "Гавареччина, Львівщина", text = "Чорнодимлена кераміка: виріб лощать камінцем і випалюють без доступу повітря — дим робить його чорним." },
        ["vasylkiv"] = new { place = "Васильків, Київщина", text = "Майоліка. Васильківський півник уцілів на шафі в зруйнованій Бородянці у 2022-му й став символом стійкості." },
        ["bubnivka"] = new { place = "Бубнівка, Вінниччина", text = "Полив'яні миски з квітами й гронами, розпис ріжком. У Національному переліку нематеріальної спадщини з 2018 року." },
        ["kosiv"] = new { place = "Косів, Івано-Франківщина", text = "Мальована кераміка — у списку нематеріальної спадщини ЮНЕСКО з 2019. Розпис продряпують по білому ангобу; зелений, жовтий, коричневий; вершники, олені, птахи." },
        ["opishnia"] = new { place = "Опішня, Полтавщина", text = "Найвідоміший гончарний осередок, з 1986 року тут Національний музей-заповідник українського гончарства. Техніки — фляндрування й ріжкування." },
        ["mezhyhirya"] = new { place = "Межигір'я, під Києвом", text = "Межигірська фаянсова фабрика (XVIII–XIX ст.) робила фаянс — тонкий білий посуд." },
        ["petrykivka"] = new { place = "Петриківка, Дніпропетровщина", text = "Петриківський розпис — у списку ЮНЕСКО з 2013, але це розпис не на кераміці: його малюють на папері, дереві, стінах. На глеку — фантазія гончаря." },
        ["trypillia"] = new { place = "Трипільська культура", text = "Мальована кераміка зі спіралями, біноклеподібні посудини, жіночі статуетки. Посуд ліпили без гончарного кола." },
    };

    static readonly Dictionary<string, string> FindFacts = new(StringComparer.Ordinal)
    {
        ["spiral"] = "Спіраль — найчастіший мотив трипільського розпису: червоним, чорним і білим по глині.",
        ["binocular"] = "Дві з'єднані посудинки, схожі на бінокль. Навіщо — досі гадають; найімовірніше, для обрядів.",
        ["figurine"] = "Глиняні жіночі фігурки — одна з найчастіших трипільських знахідок.",
        ["house"] = "Глиняна модель оселі з деталями всередини — трипільці ліпили й такі.",
        ["grain"] = "Усередині миски — знак, у якому дослідники бачать зерно: наче миска повна колосків.",
        ["krater"] = "Велика посудина з широким розхиленим горлом — у таких зберігали зерно.",
        ["ladle"] = "З глини ліпили не лише горщики, а й ложки, ковші та черпаки.",
        ["whorl"] = "Глиняний тягарець на веретено: з ним пряли нитку.",
    };

    // ---------- збереження ----------

    sealed record LastFindRow(string Key, DateTimeOffset At, bool Dup);

    /// <summary>
    /// Альбом у збереженні. Клітинки й зірки — «виріб → ключі розписів» (порожній ключ — простий): так збереження
    /// читається очима й переживає новий розпис у каталозі. Усі поля необов'язкові.
    /// </summary>
    sealed record AlbumRow(
        Dictionary<string, List<string>>? Cells = null, Dictionary<string, List<string>>? Stars = null,
        List<StoveTile>? Stove = null, List<string>? Finds = null, int Shards = 0, LastFindRow? Last = null);

    AlbumRow? SaveAlbum() => new(MasksToKeys(_albumCells), MasksToKeys(_albumStars), _stove.ToList(),
        Finds.Where((_, i) => (_finds & (1 << i)) != 0).Select(f => f.Key).ToList(), _findShards, _lastFind);

    void LoadAlbum(AlbumRow? row)
    {
        ResetAlbum(Ctx.Clock.UtcNow);
        if (row is null) return;
        KeysToMasks(row.Cells, _albumCells);
        KeysToMasks(row.Stars, _albumStars);
        // Зірка без клітинки — зіпсоване збереження: зірка й відкриває.
        for (var i = 0; i < _albumCells.Length; i++) _albumCells[i] |= _albumStars[i];
        foreach (var t in row.Stove ?? [])
            if (t is not null && _stove.Count < StoveSlots && t.Quality is >= StoveMinQuality and <= 3 && AlbumStyleIndex(t.Style) >= 0)
                _stove.Add(new StoveTile(t.Style ?? "", t.Quality));
        foreach (var key in row.Finds ?? [])
        {
            var i = Array.FindIndex(Finds, f => f.Key == key);
            if (i >= 0) _finds |= 1 << i;
        }
        _findShards = Math.Clamp(row.Shards, 0, 10_000);
        _lastFind = row.Last is { } l && Finds.Any(f => f.Key == l.Key) ? l : null;
    }

    static Dictionary<string, List<string>> MasksToKeys(int[] masks)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var r = 0; r < Wares.Length; r++)
        {
            if (masks[r] == 0) continue;
            var list = new List<string>();
            for (var c = 0; c < AlbumColumns; c++)
                if ((masks[r] & (1 << c)) != 0) list.Add(c == 0 ? "" : Styles[c - 1].Key);
            d[Wares[r].Key] = list;
        }
        return d;
    }

    static void KeysToMasks(Dictionary<string, List<string>>? from, int[] masks)
    {
        foreach (var (ware, styles) in from ?? [])
        {
            var r = AlbumWareIndex(ware);
            if (r < 0) continue;
            foreach (var s in styles ?? [])
            {
                var c = AlbumStyleIndex(s);
                if (c >= 0) masks[r] |= 1 << c;
            }
        }
    }
}
