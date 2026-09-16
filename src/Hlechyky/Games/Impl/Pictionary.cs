using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Піктіонарі на компанію (specs/pictionary.md). Художник по черзі обирає одне з трьох слів і малює його, решта
/// вгадує, пишучи здогадки. Хто вгадав раніше — більше очок; художник отримує частку від усіх, хто вгадав.
/// Партія — кожен за столом малює стільки разів, скільки обрав господар.
///
/// Малюнок — це список операцій (лінія або заливка), які художник шле через Input шматками по кілька точок.
/// Кадр раз на тик несе лише те, що додалось, плюс версію: undo і «очистити» міняють версію, і тоді кадр везе
/// малюнок цілком. Вид (per-seat, бо слово бачить лише художник) теж несе весь малюнок — з нього стартує той,
/// хто щойно відкрив картку.
/// </summary>
public sealed class Pictionary : Game
{
    /// <summary>Тик: штрих художника доходить до інших не пізніше ніж за стільки мілісекунд.</summary>
    public const int TickMs = 100;
    /// <summary>Час художникові обрати слово; не обрав — обираємо за нього.</summary>
    public const int PickMs = 15_000;
    /// <summary>Пауза після ходу: побачити слово і хто скільки взяв.</summary>
    public const int RevealMs = 6_000;
    /// <summary>Скільки слів на вибір.</summary>
    public const int Choices = 3;
    /// <summary>Мінімальний проміжок між здогадками одного гравця.</summary>
    public const int GuessEveryMs = 400;
    /// <summary>Як часто гравець може попросити повний малюнок.</summary>
    public const int SyncEveryMs = 2_000;

    /// <summary>Логічне полотно: 1000 × 750. Координати точок — цілі в цих межах.</summary>
    public const int CanvasW = 1000, CanvasH = 750;
    /// <summary>Кольорів у палітрі клієнта (web/games/pictionary.js, PALETTE).</summary>
    public const int Colors = 20;
    public const int MinWidth = 1, MaxWidth = 60;
    /// <summary>Точок в одному шматку штриха (пар x,y). 8 КБ payload вистачає з запасом.</summary>
    public const int MaxChunkPoints = 300;
    /// <summary>Межі одного малюнка: щоб один художник не роздув пам'ять і розсилку.</summary>
    public const int MaxOps = 6_000, MaxPoints = 120_000;

    /// <summary>Очки вгадувача: від <see cref="MinGuessPoints"/> до <see cref="MaxGuessPoints"/> залежно від часу, плюс перший.</summary>
    public const int MaxGuessPoints = 100, MinGuessPoints = 10, FirstBonus = 20;
    /// <summary>Здогадок у стрічці, які тримаємо й показуємо.</summary>
    public const int FeedKeep = 40;
    const int FeedInFrame = 12;
    const int MaxGuessLength = 40;
    const int Seats = 10;

    public static readonly int[] RoundChoices = [1, 2, 3];
    public static readonly int[] SecondChoices = [60, 80, 100, 120];
    public const int DefaultRounds = 2, DefaultSeconds = 80;

    const string Pick = "pick", Draw = "draw", Reveal = "reveal", Done = "done";

    public override GameInfo Info { get; } = new(
        "pictionary", "Піктіонарі", "піктіонарі", GameGroup.Party, 2, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Кола", [.. RoundChoices.Select(n => (n.ToString(), n == 1 ? "1 коло" : $"{n} кола"))], DefaultRounds.ToString()),
            new GameOption("seconds", "Час на малюнок", [.. SecondChoices.Select(n => (n.ToString(), $"{n} с"))], DefaultSeconds.ToString()),
            new GameOption("topic", "Теми", PictionaryWords.Topics, PictionaryWords.AnyTopic, Multi: true),
        ],
        Hint: "Один малює слово, решта вгадує. Хто вгадав швидше — більше очок. Малюють по черзі");

    // ---------- налаштування ----------
    PictionaryWords _words = null!;
    IReadOnlySet<string>? _topics;
    int _rounds = DefaultRounds;
    int _drawMs = DefaultSeconds * 1000;

    // ---------- партія ----------
    int[] _order = [];
    int _turn = -1;
    int _turns;
    string _phase = Pick;
    int _drawer = -1;
    string[] _choices = [];
    string _word = "";
    /// <summary>Які позиції слова вже підказані (відкриті всім).</summary>
    readonly HashSet<int> _hinted = [];
    DateTimeOffset _phaseStart;
    DateTimeOffset _until;
    readonly int[] _scores = new int[Seats];
    readonly int[] _gained = new int[Seats];
    readonly List<int> _guessed = [];
    readonly HashSet<string> _used = new(StringComparer.Ordinal);
    readonly HashSet<int> _left = [];
    readonly Dictionary<int, DateTimeOffset> _lastGuess = [];
    readonly Dictionary<int, DateTimeOffset> _lastSync = [];
    object? _result;

    // ---------- малюнок ----------
    /// <summary>Операція: [вид, штрих, колір, товщина, x0, y0, x1, y1, …]. Вид 0 — лінія, 1 — заливка (одна точка).</summary>
    readonly List<int[]> _ops = [];
    int _points;
    int _ver;
    /// <summary>Скільки операцій уже поїхало кадрами; кадр везе решту.</summary>
    int _sent;
    int _frameFrom;

    // ---------- стрічка ----------
    readonly List<FeedItem> _feed = [];
    int _feedId;

    bool _frameDirty, _viewDirty;

    sealed record FeedItem(int Id, int Seat, string Kind, string? Text);

    // =========================================================================================
    // Налаштування і старт
    // =========================================================================================

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _words = Ctx.Services.GetService<PictionaryWords>() ?? PictionaryWords.Default;
        if (options.TryGetValue("rounds", out var r) && int.TryParse(r, out var rn) && RoundChoices.Contains(rn)) _rounds = rn;
        if (options.TryGetValue("seconds", out var s) && int.TryParse(s, out var sn) && SecondChoices.Contains(sn)) _drawMs = sn * 1000;
        if (options.TryGetValue("topic", out var t)) _topics = PictionaryWords.ParseTopics(t);
        if (_words.Count == 0) throw new GameError("Нема словника, піктіонарі відпочиває");
    }

    public override void Start()
    {
        Array.Clear(_scores);
        Array.Clear(_gained);
        _used.Clear();
        _left.Clear();
        _lastGuess.Clear();
        _lastSync.Clear();
        _feed.Clear();
        _result = null;
        _order = [.. Enumerable.Range(0, Seats).Where(Ctx.Seated)];
        _turns = _order.Length * _rounds;
        _turn = -1;
        NextTurn();
    }

    IEnumerable<int> Present() => Enumerable.Range(0, Seats).Where(s => Ctx.Seated(s) && !_left.Contains(s));

    /// <summary>Хто може вгадувати в цьому ході: усі присутні, крім художника.</summary>
    IEnumerable<int> Guessers() => Present().Where(s => s != _drawer);

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    // =========================================================================================
    // Дії
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        return action switch
        {
            "pick" => PickWord(seat, payload),
            "guess" => Guess(seat, payload),
            "draw" => AddLine(seat, payload),
            "fill" => AddFill(seat, payload),
            "undo" => Undo(seat),
            "clear" => Clear(seat),
            "sync" => Sync(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult PickWord(int seat, JsonElement payload)
    {
        if (_phase != Pick) return ActResult.Fail("Слово вже обрано");
        if (seat != _drawer) return ActResult.Fail("Слово обирає художник");
        var i = Int(payload, "i", -1);
        if (i < 0 || i >= _choices.Length) return ActResult.Fail("Нема такого слова");
        BeginDraw(_choices[i]);
        return ActResult.Done;
    }

    ActResult Guess(int seat, JsonElement payload)
    {
        if (_phase != Draw) return ActResult.Fail(_phase == Pick ? "Художник ще обирає слово" : "Зараз не вгадують");
        if (seat == _drawer) return ActResult.Fail("Ти малюєш — словами не підказуй 🙂");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже встав з-за столу");
        if (_guessed.Contains(seat)) return ActResult.Fail("Ти вже вгадав — дай іншим");

        var raw = Str(payload, "text").Trim();
        if (raw.Length == 0) return ActResult.Fail("Напиши здогадку");
        if (raw.Length > MaxGuessLength) raw = raw[..MaxGuessLength];

        var now = Now;
        if (_lastGuess.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < GuessEveryMs)
            return ActResult.Fail("Не так швидко");
        _lastGuess[seat] = now;

        if (Matches(raw, _word))
        {
            var left = Math.Max(0, (_until - now).TotalMilliseconds);
            var points = MinGuessPoints + (int)Math.Round((MaxGuessPoints - MinGuessPoints) * left / _drawMs);
            if (_guessed.Count == 0) points += FirstBonus;
            _guessed.Add(seat);
            _scores[seat] += points;
            _gained[seat] += points;
            AddFeed(seat, "ok", null);
            _viewDirty = true;
            if (!Guessers().Any(s => !_guessed.Contains(s))) EndTurn();
            return ActResult.Accept($"Вгадав! +{points}");
        }

        // Майже вгадав — кажемо лише йому, у стрічку не пишемо: інакше решта отримала б підказку задарма.
        if (Close(raw, _word)) return ActResult.Fail("Гаряче! Майже вгадав");

        AddFeed(seat, "guess", raw);
        return ActResult.Done;
    }

    /// <summary>
    /// Здогадка збігається зі словом без огляду на регістр, дефіси, пробіли й апострофи — або одне з її
    /// слів збігається з однословною відповіддю («це кіт!» на «кіт»).
    /// </summary>
    public static bool Matches(string guess, string word)
    {
        var w = PictionaryWords.Normalize(word);
        if (w.Length == 0) return false;
        if (PictionaryWords.Normalize(guess) == w) return true;
        if (word.Contains(' ') || word.Contains('-')) return false;
        return guess.Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => PictionaryWords.Normalize(part) == w);
    }

    /// <summary>«Гаряче»: різниця в одну літеру (для слів від 4 літер) або у дві (від 8).</summary>
    public static bool Close(string guess, string word)
    {
        var g = PictionaryWords.Normalize(guess);
        var w = PictionaryWords.Normalize(word);
        if (w.Length < 4 || g.Length == 0 || g == w) return false;
        var max = w.Length >= 8 ? 2 : 1;
        return Math.Abs(g.Length - w.Length) <= max && Distance(g, w, max) <= max;
    }

    /// <summary>Левенштейн з раннім виходом: далі за <paramref name="max"/> рахувати нема чого.</summary>
    static int Distance(string a, string b, int max)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                best = Math.Min(best, cur[j]);
            }
            if (best > max) return best;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    bool CanDraw(int seat) => _phase == Draw && seat == _drawer;

    ActResult AddLine(int seat, JsonElement payload)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        if (payload.ValueKind != JsonValueKind.Object) return ActResult.Fail("Кривий штрих");
        var stroke = Int(payload, "s", -1);
        var color = Int(payload, "c", -1);
        var width = Int(payload, "w", 0);
        if (stroke < 0 || color is < 0 or >= Colors || width is < MinWidth or > MaxWidth) return ActResult.Fail("Кривий штрих");
        if (!payload.TryGetProperty("p", out var p) || p.ValueKind != JsonValueKind.Array) return ActResult.Fail("Кривий штрих");

        var n = p.GetArrayLength();
        if (n < 2 || n % 2 != 0 || n / 2 > MaxChunkPoints) return ActResult.Fail("Кривий штрих");
        if (_ops.Count >= MaxOps || _points + n / 2 > MaxPoints) return ActResult.Fail("Полотно переповнене — очисть його");

        var op = new int[4 + n];
        op[0] = 0; op[1] = stroke; op[2] = color; op[3] = width;
        var k = 0;
        foreach (var v in p.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || double.IsNaN(d)) return ActResult.Fail("Кривий штрих");
            var limit = k % 2 == 0 ? CanvasW : CanvasH;
            op[4 + k] = (int)Math.Clamp(Math.Round(d), 0, limit);
            k++;
        }
        _ops.Add(op);
        _points += n / 2;
        _frameDirty = true;
        return ActResult.Done;
    }

    ActResult AddFill(int seat, JsonElement payload)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        var stroke = Int(payload, "s", -1);
        var color = Int(payload, "c", -1);
        var x = Int(payload, "x", -1);
        var y = Int(payload, "y", -1);
        if (stroke < 0 || color is < 0 or >= Colors || x is < 0 or > CanvasW || y is < 0 or > CanvasH) return ActResult.Fail("Крива заливка");
        if (_ops.Count >= MaxOps) return ActResult.Fail("Полотно переповнене — очисть його");
        _ops.Add([1, stroke, color, 0, x, y]);
        _points++;
        _frameDirty = true;
        return ActResult.Done;
    }

    /// <summary>Прибрати останній штрих цілком (усі його шматки).</summary>
    ActResult Undo(int seat)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        if (_ops.Count == 0) return ActResult.Done;
        var stroke = _ops[^1][1];
        while (_ops.Count > 0 && _ops[^1][1] == stroke)
        {
            _points -= Math.Max(1, (_ops[^1].Length - 4) / 2);
            _ops.RemoveAt(_ops.Count - 1);
        }
        Rewrite();
        return ActResult.Done;
    }

    ActResult Clear(int seat)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        WipeCanvas();
        return ActResult.Done;
    }

    /// <summary>Гравець помітив дірку в кадрах — розішлемо всім повні види (не частіше за раз на пару секунд).</summary>
    ActResult Sync(int seat)
    {
        var now = Now;
        if (_lastSync.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < SyncEveryMs) return ActResult.Done;
        _lastSync[seat] = now;
        _viewDirty = true;
        return ActResult.Done;
    }

    /// <summary>Малюнок змінився не дописуванням: нова версія, і наступний кадр везе його цілком.</summary>
    void Rewrite()
    {
        _ver++;
        _sent = 0;
        _frameDirty = true;
    }

    void WipeCanvas()
    {
        _ops.Clear();
        _points = 0;
        Rewrite();
    }

    // =========================================================================================
    // Хід часу
    // =========================================================================================

    public override TickResult Tick()
    {
        var now = Now;
        if (_phase != Done && Present().Count() < 2)
        {
            Over();
        }
        else if (_phase == Pick)
        {
            if (_left.Contains(_drawer) || !Ctx.Seated(_drawer)) EndTurn();
            else if (now >= _until) BeginDraw(_choices[Ctx.Rng.Next(_choices.Length)]);
        }
        else if (_phase == Draw)
        {
            if (_left.Contains(_drawer) || !Ctx.Seated(_drawer) || now >= _until) EndTurn();
            else Hint(now);
        }
        else if (_phase == Reveal && now >= _until)
        {
            NextTurn();
        }

        if (_frameDirty)
        {
            _frameFrom = _sent;
            _sent = _ops.Count;
        }
        var result = new TickResult(_frameDirty, _viewDirty);
        _frameDirty = _viewDirty = false;
        return result;
    }

    /// <summary>
    /// Підказки: коли минула половина часу, 70% і 85% — відкриваємо по літері, але не більше третини слова
    /// і не більше трьох. Короткі слова (до 4 літер) без підказок: там одна літера — вже пів відповіді.
    /// </summary>
    void Hint(DateTimeOffset now)
    {
        var letters = Enumerable.Range(0, _word.Length).Where(i => char.IsLetter(_word[i])).ToList();
        var allowed = letters.Count < 5 ? 0 : Math.Min(3, letters.Count / 3);
        if (_hinted.Count >= allowed) return;
        var passed = (now - _phaseStart).TotalMilliseconds / _drawMs;
        double[] marks = [0.5, 0.7, 0.85];
        if (passed < marks[_hinted.Count]) return;
        var closed = letters.Where(i => !_hinted.Contains(i)).ToList();
        if (closed.Count <= 1) return;
        _hinted.Add(closed[Ctx.Rng.Next(closed.Count)]);
        _frameDirty = true;
        _viewDirty = true;
    }

    void BeginDraw(string word)
    {
        _word = word;
        _used.Add(PictionaryWords.Normalize(word));
        _phase = Draw;
        _phaseStart = Now;
        _until = _phaseStart.AddMilliseconds(_drawMs);
        _frameDirty = _viewDirty = true;
    }

    /// <summary>Хід скінчився: усі вгадали, минув час або художник пішов. Художник бере частку від вгадувачів.</summary>
    void EndTurn()
    {
        var possible = Math.Max(1, _order.Count(s => s != _drawer && Ctx.Seated(s) && !_left.Contains(s)));
        if (_guessed.Count > 0 && Ctx.Seated(_drawer) && !_left.Contains(_drawer))
        {
            // половина середнього: хороший малюнок вигідний, але вгадувати все одно вигідніше
            var share = _guessed.Sum(s => _gained[s]) / (2 * possible);
            _scores[_drawer] += share;
            _gained[_drawer] += share;
        }
        if (_word.Length > 0) AddFeed(-1, "word", _word);
        else AddFeed(_drawer, "skip", null);
        _phase = Reveal;
        _phaseStart = Now;
        _until = _phaseStart.AddMilliseconds(RevealMs);
        _frameDirty = _viewDirty = true;
    }

    void NextTurn()
    {
        _turn++;
        while (_turn < _turns && !Present().Contains(_order[_turn % _order.Length])) _turn++;
        if (_turn >= _turns) { Over(); return; }

        _drawer = _order[_turn % _order.Length];
        _choices = _words.Pick(Ctx.Rng, _topics, _used, Choices);
        if (_choices.Length == 0) _choices = PictionaryWords.Default.Pick(Ctx.Rng, null, _used, Choices);
        if (_choices.Length == 0) { Over(); return; }
        _word = "";
        _hinted.Clear();
        _guessed.Clear();
        Array.Clear(_gained);
        WipeCanvas();
        _phase = Pick;
        _phaseStart = Now;
        _until = _phaseStart.AddMilliseconds(PickMs);
        _frameDirty = _viewDirty = true;
    }

    void Over()
    {
        if (_phase == Done) return;
        _phase = Done;
        _frameDirty = _viewDirty = true;
        var seats = Present().ToArray();
        var best = seats.Length == 0 ? 0 : seats.Max(s => _scores[s]);
        int[] winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats) Ctx.Score(s, _scores[s]);
        _result = new { winners, scores = (int[])_scores.Clone() };
        Ctx.Finish(winners, Told(seats, winners), seats.ToDictionary(s => s, s => (long)_scores[s]));
    }

    string Told(int[] seats, int[] winners)
    {
        var parts = seats.OrderByDescending(s => _scores[s]).Select(s => $"{Ctx.NickOf(s)} {_scores[s]}");
        var tail = winners.Length == 0 ? "ніхто нічого не вгадав" : "попереду " + string.Join(" і ", winners.Select(Ctx.NickOf));
        return $"{Info.Title}: {string.Join(", ", parts)} — {tail}";
    }

    /// <summary>Компанійна гра: хтось встав — решта грає далі. Пішов художник — його хід закінчується.</summary>
    public override void OnLeave(int seat)
    {
        _left.Add(seat);
        AddFeed(seat, "left", null);
        if (_phase is Pick or Draw && seat == _drawer) EndTurn();
        else if (_phase == Draw && Guessers().Any() && !Guessers().Any(s => !_guessed.Contains(s))) EndTurn();
        if (_phase != Done && Present().Count() < 2) Over();
        _viewDirty = _frameDirty = true;
    }

    void AddFeed(int seat, string kind, string? text)
    {
        _feed.Add(new FeedItem(++_feedId, seat, kind, text));
        if (_feed.Count > FeedKeep) _feed.RemoveRange(0, _feed.Count - FeedKeep);
        _frameDirty = true;
    }

    // =========================================================================================
    // Вид і кадр
    // =========================================================================================

    /// <summary>«к_т» для тих, хто слова не знає: відкриті підказки, пробіли й дефіси на своїх місцях.</summary>
    string Mask()
    {
        if (_word.Length == 0) return "";
        return string.Concat(_word.Select((c, i) => !char.IsLetter(c) || _hinted.Contains(i) ? c : '_'));
    }

    bool Knows(int? seat) =>
        _phase is Reveal or Done || seat is { } s && (s == _drawer || _guessed.Contains(s));

    object[] Feed(int take) => [.. _feed.TakeLast(take).Select(f => new { id = f.Id, seat = f.Seat, kind = f.Kind, text = f.Text })];

    int[][] Ops(int from) => [.. _ops.Skip(from).Select(o => (int[])o.Clone())];

    public override object View(int? seat) => new
    {
        phase = _phase,
        turn = _phase is Pick or Draw ? _drawer : (int?)null,
        drawer = _drawer,
        turnNo = _turn + 1,
        turns = _turns,
        round = _order.Length == 0 ? 0 : Math.Min(_rounds, _turn / _order.Length + 1),
        rounds = _rounds,
        choices = _phase == Pick && seat == _drawer ? (string[])_choices.Clone() : null,
        word = Knows(seat) && _word.Length > 0 ? _word : null,
        mask = Mask(),
        until = _until,
        totalMs = _phase switch { Pick => PickMs, Draw => _drawMs, Reveal => RevealMs, _ => 0 },
        scores = (int[])_scores.Clone(),
        gained = (int[])_gained.Clone(),
        guessed = _guessed.ToArray(),
        left = _left.Order().ToArray(),
        drawing = new { ver = _ver, n = _ops.Count, ops = Ops(0) },
        feed = Feed(FeedKeep),
        result = _result,
    };

    /// <summary>Публічний кадр: без слова. Дописані операції від <see cref="_frameFrom"/>, маска, таймер, стрічка.</summary>
    public override object? Frame() => new
    {
        ph = _phase,
        drawer = _drawer,
        ver = _ver,
        from = _frameFrom,
        n = _ops.Count,
        ops = Ops(_frameFrom),
        mask = Mask(),
        until = _until,
        guessed = _guessed.ToArray(),
        feed = Feed(FeedInFrame),
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
