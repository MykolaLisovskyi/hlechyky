using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Позивні (specs/pozyvni.md) — гра за правилами «кодових імен». На столі 25 слів; розклад, котре слово
/// чиє, бачать лише два капітани. Капітан дає одне слово й число, його команда тикає в слова: своє —
/// відкрилось і можна далі, чуже чи нейтральне — хід суперника, чорне — програш на місці.
///
/// Каркас команд не знає, він знає лише місця, тому склад гра тримає сама (<see cref="_side"/>,
/// <see cref="_boss"/>) і роздає командою ж переможців у Finish. Перша фаза партії — <c>setup</c>: розкид
/// уже зроблено (місця через одне), 45 секунд дається лише на те, щоб помінятися.
///
/// Уся таємниця — в одному полі виду: <c>key</c> віддається капітанам і нікому більше. Польовий гравець
/// розкладу не має в даних узагалі, не лише в стилях.
/// </summary>
public sealed class Pozyvni : Game
{
    /// <summary>Тик рідкий: годинник і кінець фази складу — усе, що тут рухається саме.</summary>
    public const int TickMs = 250;
    /// <summary>Скільки дається на розбір складу, поки хтось не натиснув «Почати».</summary>
    public const int SetupMs = 45_000;

    public const int Cards = 25;
    const int Seats = 12;
    /// <summary>Слів у більшої команди (вона й ходить першою) і в меншої.</summary>
    const int FirstTeamWords = 9, SecondTeamWords = 8;
    const int MaxClueLength = 24, MinClueLength = 2, MaxCount = 9;
    /// <summary>З якого числа підказка вважається великою (ачівка «Одним словом»).</summary>
    const int BigClueCount = 4;
    /// <summary>Скільки перших літер вважаємо коренем при звірці підказки зі словами столу.</summary>
    const int RootLength = 5;
    /// <summary>Скільки здогадок дає підказка «нуль»: стільки, скільки слів на столі, тобто фактично без ліміту.</summary>
    const int Unlimited = Cards;

    public static readonly int[] ClockChoices = [60, 90, 120];

    const string Setup = "setup", Clue = "clue", Guess = "guess", Done = "done";
    const string Red = "red", Blue = "blue", Grey = "grey", Black = "black";

    public override GameInfo Info { get; } = new(
        "pozyvni", "Позивні", "позивні", GameGroup.Party, 4, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true,
        Options:
        [
            new GameOption("topic", "Теми слів", PictionaryWords.Topics, PictionaryWords.AnyTopic, Multi: true),
            new GameOption("clock", "Годинник", [("off", "Без нього"), .. ClockChoices.Select(n => (n.ToString(), $"{n} с"))], "off"),
            new GameOption("black", "Чорних слів", [("1", "Одне"), ("2", "Двоє")], "1"),
            new GameOption("zero", "Підказка «нуль»", [("on", "Можна"), ("off", "Не можна")], "on"),
        ],
        Hint: "Дві команди, два капітани. Капітан каже одне слово і число, команда вгадує свої слова. Чорне слово — миттєвий програш");

    // ---------- налаштування столу ----------
    PictionaryWords _words = null!;
    IReadOnlySet<string>? _topics;
    int _blacks = 1;
    bool _zero = true;
    /// <summary>0 — без годинника; інакше стільки мілісекунд на підказку і стільки ж на здогадки.</summary>
    int _clockMs;

    // ---------- стіл ----------
    readonly string[] _board = new string[Cards];
    readonly string[] _key = new string[Cards];
    readonly bool[] _open = new bool[Cards];
    /// <summary>Слова, які вже були за цим столом: наступна партія бере інші, поки в темах є свіжі.</summary>
    readonly HashSet<string> _used = new(StringComparer.Ordinal);

    // ---------- склад ----------
    /// <summary>Місце → сторона (<see cref="Red"/>/<see cref="Blue"/>); null — місце не грає.</summary>
    readonly string?[] _side = new string?[Seats];
    /// <summary>Сторона → місце капітана (-1, поки нема).</summary>
    readonly Dictionary<string, int> _boss = new(StringComparer.Ordinal) { [Red] = -1, [Blue] = -1 };

    // ---------- партія ----------
    string _phase = Setup;
    string _turn = Red;
    /// <summary>Хто програв минулу партію — той і починає наступну (і бере зайве, дев'яте, слово).</summary>
    string? _lastLoser;
    (string Word, int Count, int Left)? _clue;
    /// <summary>Скільки своїх слів команда взяла за поточну підказку (для ачівки «Одним словом»).</summary>
    int _taken;
    /// <summary>
    /// На що показує кожен польовий гравець: місце → картка. Слово відкривається, коли на нього показали
    /// всі польові команди, що ходить. Це публічно — за живим столом пальці теж бачать усі.
    /// </summary>
    readonly Dictionary<int, int> _fingers = [];
    DateTimeOffset _phaseStart, _until;
    readonly List<string> _log = [];
    object? _result;
    bool _viewDirty;

    // =========================================================================================
    // Налаштування і старт
    // =========================================================================================

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _words = Ctx.Services.GetService<PictionaryWords>() ?? PictionaryWords.Default;
        if (options.TryGetValue("topic", out var t)) _topics = PictionaryWords.ParseTopics(t);
        if (options.TryGetValue("black", out var b) && b == "2") _blacks = 2;
        if (options.TryGetValue("zero", out var z)) _zero = z != "off";
        if (options.TryGetValue("clock", out var c) && int.TryParse(c, out var cn) && ClockChoices.Contains(cn)) _clockMs = cn * 1000;
        if (_words.Count < Cards) throw new GameError("Замало слів для столу, позивні відпочивають");
    }

    public override void Start()
    {
        _result = null;
        _clue = null;
        _taken = 0;
        _fingers.Clear();
        _log.Clear();
        Array.Clear(_open);

        var picked = _words.Pick(Ctx.Rng, _topics, _used, Cards);
        if (picked.Length < Cards) picked = PictionaryWords.Default.Pick(Ctx.Rng, null, _used, Cards);
        if (picked.Length < Cards)
        {
            Ctx.Finish([], $"{Info.Title}: слів на стіл не набралось");
            _phase = Done;
            return;
        }
        for (var i = 0; i < Cards; i++)
        {
            _board[i] = picked[i];
            _used.Add(PictionaryWords.Normalize(picked[i]));
        }

        // Першою ходить та команда, у якої дев'ять слів. Після «Ще раз» це ті, хто програв: реванш
        // має сенс лише тоді, коли фора дістається не переможцям.
        _turn = _lastLoser ?? (Ctx.Rng.Next(2) == 0 ? Red : Blue);
        DealKey();
        Split();

        _phase = Setup;
        _phaseStart = Now;
        _until = _phaseStart.AddMilliseconds(SetupMs);
        _viewDirty = true;
    }

    /// <summary>Розклад: дев'ять слів тим, хто ходить першим, вісім суперникові, чорні, решта — нейтральні.</summary>
    void DealKey()
    {
        var other = Other(_turn);
        var key = new List<string>(Cards);
        for (var i = 0; i < FirstTeamWords; i++) key.Add(_turn);
        for (var i = 0; i < SecondTeamWords; i++) key.Add(other);
        for (var i = 0; i < _blacks; i++) key.Add(Black);
        while (key.Count < Cards) key.Add(Grey);
        for (var i = key.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (key[i], key[j]) = (key[j], key[i]);
        }
        for (var i = 0; i < Cards; i++) _key[i] = key[i];
    }

    /// <summary>Типовий розкид: місця через одне, капітани — перші в командах. Далі люди міняються самі.</summary>
    void Split()
    {
        Array.Clear(_side);
        _boss[Red] = _boss[Blue] = -1;
        var n = 0;
        foreach (var seat in Seated())
        {
            var side = n++ % 2 == 0 ? Red : Blue;
            _side[seat] = side;
            if (_boss[side] < 0) _boss[side] = seat;
        }
    }

    IEnumerable<int> Seated() => Enumerable.Range(0, Seats).Where(Ctx.Seated);

    int[] SeatsOf(string side) => [.. Seated().Where(s => _side[s] == side)];

    static string Other(string side) => side == Red ? Blue : Red;

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    bool IsBoss(int seat) => _side[seat] is { } side && _boss[side] == seat;

    /// <summary>Скільки своїх слів команда ще не відкрила.</summary>
    int LeftFor(string side) => Enumerable.Range(0, Cards).Count(i => _key[i] == side && !_open[i]);

    public override string SeatName(int seat)
    {
        var side = seat >= 0 && seat < Seats ? _side[seat] : null;
        side ??= seat % 2 == 0 ? Red : Blue;   // до старту команд ще нема — показуємо типовий розкид
        return side == Red ? "червоні" : "сині";
    }

    // =========================================================================================
    // Дії
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        return action switch
        {
            "team" => JoinTeam(seat, payload),
            "boss" => TakeBoss(seat),
            "go" => Go(),
            "clue" => GiveClue(seat, payload),
            "pick" => Pick(seat, payload),
            "pass" => Pass(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult JoinTeam(int seat, JsonElement payload)
    {
        if (_phase != Setup) return ActResult.Fail("Партія вже почалась, склад не міняють");
        var side = Str(payload, "side");
        if (side != Red && side != Blue) return ActResult.Fail("Є лише червоні й сині");
        if (_side[seat] == side) return ActResult.Done;
        Unseat(seat);
        _side[seat] = side;
        _viewDirty = true;
        return ActResult.Done;
    }

    ActResult TakeBoss(int seat)
    {
        if (_phase != Setup) return ActResult.Fail("Капітана міняють до початку партії");
        if (_side[seat] is not { } side) return ActResult.Fail("Спершу обери команду");
        if (_boss[side] == seat) return ActResult.Done;
        _boss[side] = seat;
        _viewDirty = true;
        return ActResult.Done;
    }

    /// <summary>Хтось за столом вирішив, що складу досить. Господаря гра не знає, тож почати може будь-хто із сидячих.</summary>
    ActResult Go()
    {
        if (_phase != Setup) return ActResult.Fail("Партія вже йде");
        Begin();
        return ActResult.Done;
    }

    ActResult GiveClue(int seat, JsonElement payload)
    {
        if (_phase != Clue) return ActResult.Fail(_phase == Setup ? "Спершу розберіться зі складом" : "Підказка вже є");
        if (_side[seat] != _turn) return ActResult.Fail("Зараз ходить не твоя команда");
        if (!IsBoss(seat)) return ActResult.Fail("Підказку дає капітан");

        var word = Str(payload, "word").Trim();
        if (CheckClue(word) is { } error) return ActResult.Fail(error);
        var count = Int(payload, "count", -1);
        if (count < 0 || count > MaxCount) return ActResult.Fail($"Число — від {(_zero ? 0 : 1)} до {MaxCount}");
        if (count == 0 && !_zero) return ActResult.Fail("За цим столом «нуль» не кажуть");

        _clue = (word, count, count == 0 ? Unlimited : count + 1);
        _taken = 0;
        _fingers.Clear();
        Say($"{TeamName(_turn)}: «{word} {(count == 0 ? "нуль" : count.ToString())}»");
        _phase = Guess;
        StartPhase();
        return ActResult.Done;
    }

    /// <summary>
    /// Що приймається за підказку: одне слово з літер (апостроф усередині можна — «м'ясо» теж підказка),
    /// і воно не мусить збігатися з нерозкритим словом столу. Точнішої морфології тут свідомо нема:
    /// корінь у п'ять літер ловить «море» при «морський», а решта лишається на совість гравців, як за столом.
    /// </summary>
    string? CheckClue(string word)
    {
        if (word.Length == 0) return "Напиши підказку";
        if (word.Length > MaxClueLength) return "Задовга підказка";
        if (word.Length < MinClueLength) return "Закоротка підказка";
        if (word.Any(c => c is ' ' or '-' or '—')) return "Підказка — одне слово";
        if (!word.All(c => char.IsLetter(c) || c is '\'' or '’' or 'ʼ')) return "Підказка — саме слово, без цифр і значків";
        if (!char.IsLetter(word[0])) return "Підказка — саме слово, без цифр і значків";
        for (var i = 0; i < Cards; i++)
            if (!_open[i] && SameRoot(word, _board[i]))
                return "Так не можна: це слово на столі";
        return null;
    }

    /// <summary>
    /// Чи це те саме слово, що на столі: збіг, одне є початком іншого (коротше — від чотирьох літер) або
    /// спільні перші п'ять літер. Ловить відмінки й найближчі похідні («криниця» — «криниці» — «криничний»),
    /// але не чергування в корені («море» — «морський»): для такого потрібен справжній стемер, а його тут
    /// нема свідомо. Решта — на совість гравців, як за живим столом.
    /// </summary>
    public static bool SameRoot(string a, string b)
    {
        var x = PictionaryWords.Normalize(a);
        var y = PictionaryWords.Normalize(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        if (shorter.Length >= RootLength - 1 && longer.StartsWith(shorter, StringComparison.Ordinal)) return true;
        return shorter.Length >= RootLength && string.Equals(x[..RootLength], y[..RootLength], StringComparison.Ordinal);
    }

    ActResult Pick(int seat, JsonElement payload)
    {
        if (_phase != Guess) return ActResult.Fail(_phase == Setup ? "Партія ще не почалась" : "Чекаємо на підказку капітана");
        if (_side[seat] != _turn) return ActResult.Fail("Зараз ходить не твоя команда");
        if (IsBoss(seat)) return ActResult.Fail("Капітан свого розкладу не тикає");
        var i = Int(payload, "i", -1);
        if (i < 0 || i >= Cards) return ActResult.Fail("Нема такого слова");
        if (_open[i]) return ActResult.Fail("Це слово вже відкрите");

        var mates = Field(_turn);
        if (mates.Length > 1)
        {
            // Показуємо пальцем. Той самий палець удруге — передумав; на інше слово — палець переїхав.
            if (_fingers.TryGetValue(seat, out var was) && was == i) _fingers.Remove(seat);
            else _fingers[seat] = i;
            _viewDirty = true;
            if (!mates.All(s => _fingers.TryGetValue(s, out var at) && at == i))
                return ActResult.Accept(_fingers.ContainsKey(seat)
                    ? $"Показуєш на «{_board[i]}». Відкриється, коли покажуть усі"
                    : "Палець прибрано");
        }
        return Reveal(i);
    }

    /// <summary>Польові гравці команди: ті, хто тикає в слова. Капітан сюди не входить.</summary>
    int[] Field(string side) => [.. SeatsOf(side).Where(s => _boss[side] != s)];

    /// <summary>Слово відкривається. Далі все вирішує його колір.</summary>
    ActResult Reveal(int i)
    {
        _fingers.Clear();
        // «На волосині»: ця здогадка — остання дозволена в ході. Рахуємо до того, як щось відкрилось.
        var lastChance = _clue!.Value.Left <= 1;
        _open[i] = true;
        var colour = _key[i];
        var mine = colour == _turn;
        Say($"{TeamName(_turn)}: {_board[i]} — {Mark(colour)}");
        _viewDirty = true;

        if (colour == Black)
        {
            Win(Other(_turn), $"{TeamName(_turn)} наткнулись на чорне слово «{_board[i]}»", black: true);
            return ActResult.Done;
        }
        if (mine) _taken++;

        // Відкрити останнє слово суперника можна й помилково — партія на цьому все одно закінчується.
        if (LeftFor(colour) == 0 && colour != Grey)
        {
            if (mine) BigClue();
            Win(colour, colour == _turn
                ? $"{TeamName(colour)} знайшли всіх своїх (останнє — «{_board[i]}»)"
                : $"{TeamName(colour)} перемогли чужими руками: останнє їхнє слово відкрили суперники",
                edge: mine && lastChance);
            return ActResult.Done;
        }
        if (!mine) { EndTurn(colour == Grey ? "нейтральне слово" : "слово суперника"); return ActResult.Done; }

        BigClue();
        var clue = _clue!.Value;
        var left = clue.Left - 1;
        _clue = (clue.Word, clue.Count, left);
        if (left <= 0) EndTurn("здогадки скінчились");
        return ActResult.Done;
    }

    ActResult Pass(int seat)
    {
        if (_phase != Guess) return ActResult.Fail("Зараз не ваш хід");
        if (_side[seat] != _turn) return ActResult.Fail("Зараз ходить не твоя команда");
        if (IsBoss(seat)) return ActResult.Fail("Капітан хід не здає — це справа команди");
        EndTurn("самі сказали «досить»");
        return ActResult.Done;
    }

    // =========================================================================================
    // Хід часу
    // =========================================================================================

    public override TickResult Tick()
    {
        if (_phase == Done) return Flush();
        var now = Now;

        if (_phase == Setup)
        {
            if (now >= _until) Begin();
        }
        else if (_clockMs > 0 && now >= _until)
        {
            if (_phase == Clue) { Say($"{TeamName(_turn)}: капітан не встиг із підказкою"); EndTurn(null); }
            else EndTurn("час вийшов");
        }
        return Flush();
    }

    TickResult Flush()
    {
        var result = new TickResult(false, _viewDirty);
        _viewDirty = false;
        return result;
    }

    /// <summary>Фаза складу скінчилась: вирівнюємо команди й починаємо з підказки.</summary>
    void Begin()
    {
        Balance();
        if (_phase == Done) return;
        _phase = Clue;
        StartPhase();
        Say($"Стіл готовий. Першими ходять {TeamName(_turn)}");
    }

    /// <summary>
    /// Склад перед боєм: у кожній команді щонайменше двоє і рівно один капітан. Хто не встиг обрати
    /// команду — іде в меншу; забракло людей — переводимо останніх із більшої.
    /// </summary>
    void Balance()
    {
        // Менше чотирьох — двох команд не буде, хоч як їх переставляй (хтось устиг вийти під час складу).
        if (Seated().Count() < Info.MinPlayers) { Fold(); return; }

        foreach (var seat in Seated().Where(s => _side[s] is null))
            _side[seat] = SeatsOf(Red).Length <= SeatsOf(Blue).Length ? Red : Blue;

        // Переводимо з більшої команди в меншу, поки в меншій не стане двоє. Гравців за столом уже
        // щонайменше четверо, тож більша завжди має кого віддати, не лишаючись сама вдвох.
        while (SeatsOf(Red).Length < 2 || SeatsOf(Blue).Length < 2)
        {
            var thin = SeatsOf(Red).Length < 2 ? Red : Blue;
            var fat = Other(thin);
            var donor = SeatsOf(fat).LastOrDefault(s => s != _boss[fat], -1);
            if (donor < 0) { Fold(); return; }
            _side[donor] = thin;
        }

        foreach (var side in (string[])[Red, Blue])
        {
            var mine = SeatsOf(side);
            if (!mine.Contains(_boss[side])) _boss[side] = mine[0];
        }
        _viewDirty = true;
    }

    /// <summary>Гравців на дві команди не вистачає — партії нема, і це нічия, а не чиясь перемога.</summary>
    void Fold()
    {
        if (_phase == Done) return;
        _phase = Done;
        _viewDirty = true;
        _result = new { winners = Array.Empty<int>(), side = (string?)null, black = false };
        Ctx.Finish([], $"{Info.Title}: за столом не набралось двох команд");
    }

    void StartPhase()
    {
        _phaseStart = Now;
        _until = _clockMs > 0 ? _phaseStart.AddMilliseconds(_clockMs) : DateTimeOffset.MaxValue;
        _viewDirty = true;
    }

    /// <summary>Хід переходить суперникові: підказки більше нема, чекаємо на капітана з того боку.</summary>
    void EndTurn(string? why)
    {
        if (why is not null) Say($"{TeamName(_turn)}: хід закінчено — {why}");
        _clue = null;
        _fingers.Clear();
        _turn = Other(_turn);
        _phase = Clue;
        StartPhase();
    }

    void Win(string side, string why, bool black = false, bool edge = false)
    {
        if (_phase == Done) return;
        _phase = Done;
        _clue = null;
        _lastLoser = Other(side);
        var winners = SeatsOf(side);
        Say(why);
        _result = new { winners, side, black };
        _viewDirty = true;
        // «На волосині» — перемога останньою дозволеною здогадкою ходу; дістається всій команді.
        if (edge) foreach (var seat in winners) Ctx.Award(seat, 0, "ach:pozyvni-edge");
        Ctx.Finish(winners, $"{Info.Title}: {why}");
    }

    /// <summary>
    /// «Одним словом»: капітан назвав число від чотирьох, і команда взяла за цю підказку всі слова. Ачівка
    /// капітанові, і в момент, коли це сталось, — партія на цьому може ще й не закінчитись.
    /// </summary>
    void BigClue()
    {
        if (_clue is not { } clue || clue.Count < BigClueCount || _taken != clue.Count) return;
        var boss = _boss[_turn];
        if (boss >= 0 && Ctx.Seated(boss)) Ctx.Award(boss, 0, "ach:pozyvni-4");
    }

    // =========================================================================================
    // Вихід із-за столу
    // =========================================================================================

    /// <summary>
    /// Компанійна гра: один вихід партію не рве. Пішов капітан — команда обирає нового з польових; лишився
    /// в команді один — грати нікому, і це технічна поразка саме цієї команди.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_phase == Done) return;
        var side = _side[seat];
        Unseat(seat);
        _viewDirty = true;
        if (side is null) return;
        Say($"{Ctx.NickOf(seat)} встав з-за столу");

        if (_phase == Setup) return;   // склад вирівняється сам, коли фаза скінчиться

        var mine = SeatsOf(side);
        if (mine.Length < 2) { Win(Other(side), $"{TeamName(side)} лишились без команди"); return; }
        if (_boss[side] < 0) _boss[side] = mine[0];
        // Капітан пішов посеред свого ж ходу — підказка з ним і пішла, хід віддаємо суперникові.
        if (_phase == Clue && _turn == side) EndTurn(null);
    }

    void Unseat(int seat)
    {
        _fingers.Remove(seat);
        if (_side[seat] is { } side && _boss[side] == seat) _boss[side] = -1;
        _side[seat] = null;
    }

    // =========================================================================================
    // Вид
    // =========================================================================================

    static string TeamName(string side) => side == Red ? "червоні" : "сині";

    static string Mark(string colour) => colour switch
    {
        Red => "червоне", Blue => "синє", Black => "чорне", _ => "нейтральне",
    };

    void Say(string line)
    {
        _log.Add(line);
        if (_log.Count > 60) _log.RemoveRange(0, _log.Count - 60);
        _viewDirty = true;
    }

    /// <summary>Розклад бачать лише капітани — і всі, коли партія скінчилась.</summary>
    bool KnowsKey(int? seat) => _phase == Done || (seat is { } s && s >= 0 && s < Seats && IsBoss(s));

    public override object View(int? seat) => new
    {
        phase = _phase,
        turn = _phase == Clue && _boss[_turn] >= 0 ? _boss[_turn] : (int?)null,
        side = _turn,
        board = Enumerable.Range(0, Cards)
            .Select(i => new { w = _board[i] ?? "", open = _open[i] ? _key[i] : null })
            .ToArray(),
        key = KnowsKey(seat) ? (string[])_key.Clone() : null,
        clue = _clue is { } c ? new { word = c.Word, count = c.Count, left = c.Left } : null,
        // Хто на що показує: картка → місця. Публічно, як підняті руки за столом.
        fingers = _fingers.GroupBy(f => f.Value).ToDictionary(g => g.Key.ToString(), g => g.Select(f => f.Key).Order().ToArray()),
        teams = new
        {
            red = new { seats = SeatsOf(Red), boss = _boss[Red] < 0 ? (int?)null : _boss[Red] },
            blue = new { seats = SeatsOf(Blue), boss = _boss[Blue] < 0 ? (int?)null : _boss[Blue] },
        },
        me = seat is { } s && s >= 0 && s < Seats && _side[s] is { } side
            ? new { side, boss = IsBoss(s) }
            : null,
        left = new { red = LeftFor(Red), blue = LeftFor(Blue) },
        endsAt = _clockMs > 0 || _phase == Setup ? _until : (DateTimeOffset?)null,
        phaseMs = _phase == Setup ? SetupMs : _clockMs,
        zero = _zero,
        log = _log.ToArray(),
        result = _result,
    };

    // =========================================================================================
    // Дрібниці
    // =========================================================================================

    static int Int(JsonElement payload, string field, int fallback) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && !double.IsNaN(d) && Math.Abs(d) < int.MaxValue
            ? (int)Math.Round(d) : fallback;

    static string Str(JsonElement payload, string field) => payload.ValueKind switch
    {
        JsonValueKind.String => payload.GetString() ?? "",
        JsonValueKind.Object when payload.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String => v.GetString() ?? "",
        _ => "",
    };
}
