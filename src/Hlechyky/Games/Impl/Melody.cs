using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Вгадай мелодію» (specs/melody.md). Звучить уривок треку, який уже грав на радіо, — хто перший назве виконавця
/// й назву, той бере більше очок. Треки й уривки — з кешу радіо через ffmpeg (<see cref="MelodyLibrary"/>).
///
/// Уривки ріжуться у фоні, поза замком кімнати: на старті партія чекає лише перший, решта готується, поки грають.
/// Браузер тягне уривок за випадковим токеном (<see cref="MelodyClips"/>) — ні в адресі, ні у файлі назви нема.
/// </summary>
public sealed class Melody : Game
{
    public const int TickMs = 250;
    /// <summary>Скільки часу на здогадки понад довжину уривка.</summary>
    public const int ExtraMs = 15_000;
    public const int RevealMs = 7_000;
    /// <summary>Скільки чекати, поки наріжуться уривки, перш ніж здатись.</summary>
    public const int LoadTimeoutMs = 60_000;
    public const int GuessEveryMs = 700;
    public const int ArtistPoints = 50, TitlePoints = 100, ArtistFirst = 20, TitleFirst = 30;
    /// <summary>Скільки зайвих треків беремо про запас — на випадок, коли уривок не наріжеться.</summary>
    const int Spare = 4;
    const int Seats = 8;
    const int MaxGuess = 80;

    public static readonly int[] RoundChoices = [5, 10, 15];
    public static readonly int[] ClipChoices = [10, 15, 20];

    const string Loading = "loading", Play = "play", Reveal = "reveal", Done = "done";

    public override GameInfo Info { get; } = new(
        "melody", "Вгадай мелодію", "«Вгадай мелодію»", GameGroup.Party, 1, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Треків", [.. RoundChoices.Select(n => (n.ToString(), n.ToString()))], "10"),
            new GameOption("clip", "Звучить", [.. ClipChoices.Select(n => (n.ToString(), $"{n} с"))], "15"),
        ],
        Hint: "Звучить уривок пісні, яка вже грала на радіо. Пиши виконавця й назву — хто перший, той бере більше");

    sealed record Prepared(MelodyTrack Track, string Token);

    sealed class Found
    {
        public bool Artist, Title;
        public int Points;
    }

    IMelodySource _source = null!;
    int _rounds = 10;
    int _clipSec = 15;

    // ---------- підготовка (фон) ----------
    CancellationTokenSource? _cts;
    /// <summary>Номер раунду → готовий уривок. Пише фонова задача, читає тик.</summary>
    ConcurrentDictionary<int, Prepared> _ready = new();
    /// <summary>Скільки раундів точно буде (фон уже знає, скільки треків вдалось нарізати). -1 — ще невідомо.</summary>
    volatile int _available = -1;

    // ---------- партія ----------
    string _phase = Loading;
    int _round;
    DateTimeOffset _until;
    DateTimeOffset _loadStarted;
    int _totalMs;
    readonly int[] _scores = new int[Seats];
    Dictionary<int, Found> _found = [];
    /// <summary>Хто в цьому раунді готовий пропустити трек.</summary>
    readonly HashSet<int> _skip = [];
    bool _artistTaken, _titleTaken;
    readonly HashSet<int> _left = [];
    readonly Dictionary<int, DateTimeOffset> _lastGuess = [];
    object? _result;
    string? _error;
    bool _dirty;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _source = Ctx.Services.GetService<IMelodySource>()
            ?? new MelodyLibrary(Ctx.Services.GetService<Db>(), Ctx.Services.GetService<IOptionsMonitor<YtDlpOptions>>());
        if (options.TryGetValue("rounds", out var r) && int.TryParse(r, out var rn) && RoundChoices.Contains(rn)) _rounds = rn;
        if (options.TryGetValue("clip", out var c) && int.TryParse(c, out var cn) && ClipChoices.Contains(cn)) _clipSec = cn;
    }

    public override void Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ready = new ConcurrentDictionary<int, Prepared>();
        _available = -1;
        Array.Clear(_scores);
        _left.Clear();
        _lastGuess.Clear();
        _found = [];
        _result = null;
        _error = null;
        _round = 0;
        _phase = Loading;
        _loadStarted = Now;
        _dirty = true;
        // Сід для фону беремо з Rng кімнати тут, під замком: сама фонова задача Ctx.Rng не чіпає.
        var seed = Ctx.Rng.Next();
        var ready = _ready;
        var ct = _cts.Token;
        _ = Task.Run(() => PrepareAsync(ready, seed, ct), ct);
    }

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    async Task PrepareAsync(ConcurrentDictionary<int, Prepared> ready, int seed, CancellationToken ct)
    {
        try
        {
            var rng = new Random(seed);
            var tracks = await _source.PickAsync(_rounds + Spare, rng, ct);
            var round = 0;
            foreach (var t in tracks)
            {
                if (ct.IsCancellationRequested || round >= _rounds) break;
                var len = t.DurationSec > 0 ? t.DurationSec : 180;
                // з першої чверті до 60% — там зазвичай куплет або приспів, а не тиша інтро
                var from = len * 0.25;
                var to = Math.Max(from, Math.Min(len * 0.6, len - _clipSec - 2));
                var start = Math.Max(0, from + rng.NextDouble() * (to - from));
                var data = await _source.ClipAsync(t, start, _clipSec, ct);
                if (data is null) continue;
                ready[++round] = new Prepared(t, MelodyClips.Put(data));
            }
            if (!ct.IsCancellationRequested) _available = round;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested) _available = ready.Count;
        }
    }

    // =========================================================================================
    // Хід часу
    // =========================================================================================

    public override TickResult Tick()
    {
        var now = Now;
        switch (_phase)
        {
            case Loading:
                if (_ready.ContainsKey(_round + 1)) BeginRound();
                else if (_available >= 0 && _round >= _available) Over(_available == 0 ? "На радіо ще нема скачаних треків — грати нема в що" : null);
                else if ((now - _loadStarted).TotalMilliseconds > LoadTimeoutMs) Over(_round == 0 ? "Уривки не нарізались — ffmpeg мовчить" : null);
                break;
            case Play:
                var players = Present().ToList();
                // Раунд закінчується, коли кожен або вгадав усе, або готовий пропустити (а не всі вже вгадали — не чекати ж).
                if (now >= _until || players.Count > 0 && players.All(s => Finished(s) || _skip.Contains(s)))
                    EndRound();
                break;
            case Reveal:
                if (now < _until) break;
                if (_round >= _rounds) { Over(null); break; }
                _phase = Loading;
                _loadStarted = now;
                _dirty = true;
                if (_ready.ContainsKey(_round + 1)) BeginRound();
                break;
        }
        if (_phase != Done && !Present().Any()) Over(null);
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(Frame: false, View: true);
    }

    IEnumerable<int> Present() => Enumerable.Range(0, Seats).Where(s => Ctx.Seated(s) && !_left.Contains(s));

    bool Finished(int seat) => _found.TryGetValue(seat, out var f) && f.Artist && f.Title;

    void BeginRound()
    {
        _skip.Clear();
        _round++;
        _found = [];
        _artistTaken = _titleTaken = false;
        _phase = Play;
        _totalMs = _clipSec * 1000 + ExtraMs;
        _until = Now.AddMilliseconds(_totalMs);
        _dirty = true;
    }

    void EndRound()
    {
        _phase = Reveal;
        _totalMs = RevealMs;
        _until = Now.AddMilliseconds(RevealMs);
        _dirty = true;
    }

    void Over(string? error)
    {
        if (_phase == Done) return;
        _cts?.Cancel();
        _phase = Done;
        _error = error;
        _dirty = true;
        var seats = Present().ToArray();
        if (error is not null && _round == 0)
        {
            Ctx.Finish([], $"{Info.Title}: {error}");
            return;
        }
        var best = seats.Length == 0 ? 0 : seats.Max(s => _scores[s]);
        int[] winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats) Ctx.Score(s, _scores[s]);
        _result = new { winners, scores = (int[])_scores.Clone() };
        var parts = seats.OrderByDescending(s => _scores[s]).Select(s => $"{Ctx.NickOf(s)} {_scores[s]}");
        var tail = winners.Length == 0 ? "жодної пісні не впізнали" : "найкраще вухо в " + string.Join(" і ", winners.Select(Ctx.NickOf));
        Ctx.Finish(winners, $"{Info.Title}: {string.Join(", ", parts)} — {tail}", seats.ToDictionary(s => s, s => (long)_scores[s]));
    }

    public override void OnLeave(int seat)
    {
        _left.Add(seat);
        _dirty = true;
        if (!Present().Any()) Over(null);
    }

    // =========================================================================================
    // Здогадки
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "skip") return Skip(seat);
        if (action != "guess") return ActResult.Fail("Тут так не ходять");
        if (_phase != Play) return ActResult.Fail(_phase == Done ? "Партію зіграно, тисни «Ще раз»" : "Зараз не вгадують");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже встав з-за столу");

        var text = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "" : payload.ValueKind == JsonValueKind.String ? payload.GetString() ?? "" : "";
        text = text.Trim();
        if (text.Length == 0) return ActResult.Fail("Напиши виконавця або назву");
        if (text.Length > MaxGuess) text = text[..MaxGuess];

        var now = Now;
        if (_lastGuess.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < GuessEveryMs) return ActResult.Fail("Не так швидко");
        _lastGuess[seat] = now;

        if (!_found.TryGetValue(seat, out var mine)) _found[seat] = mine = new Found();
        if (mine.Artist && mine.Title) return ActResult.Fail("Ти вже все вгадав 🎉");

        var track = _ready[_round].Track;
        var said = new List<string>();
        var points = 0;
        if (!mine.Artist && MelodyAnswer.Hits(text, MelodyAnswer.Artists(track)))
        {
            mine.Artist = true;
            var p = ArtistPoints + (_artistTaken ? 0 : ArtistFirst);
            _artistTaken = true;
            points += p;
            said.Add($"🎤 виконавець +{p}");
        }
        if (!mine.Title && MelodyAnswer.Hits(text, MelodyAnswer.Titles(track)))
        {
            mine.Title = true;
            var p = TitlePoints + (_titleTaken ? 0 : TitleFirst);
            _titleTaken = true;
            points += p;
            said.Add($"🎵 назва +{p}");
        }
        if (points == 0) return ActResult.Fail("Мимо");

        mine.Points += points;
        _scores[seat] += points;
        _dirty = true;
        return ActResult.Accept(string.Join(", ", said));
    }

    /// <summary>«Готовий пропустити» — перемикач: натиснув ще раз — передумав.</summary>
    ActResult Skip(int seat)
    {
        if (_phase != Play) return ActResult.Fail("Зараз нема чого пропускати");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже встав з-за столу");
        if (Finished(seat)) return ActResult.Fail("Ти вже все вгадав — чекаємо інших");
        if (!_skip.Remove(seat)) _skip.Add(seat);
        _dirty = true;
        return ActResult.Done;
    }

    // =========================================================================================
    // Вид
    // =========================================================================================

    public override object View(int? seat)
    {
        var prepared = _round > 0 && _ready.TryGetValue(_round, out var p) ? p : null;
        var mine = seat is { } s && _found.TryGetValue(s, out var f) ? f : null;
        var open = _phase is Reveal or Done;
        return new
        {
            phase = _phase,
            round = _round,
            rounds = _available > 0 ? Math.Min(_rounds, _available) : _rounds,
            clipSec = _clipSec,
            until = _until,
            totalMs = _totalMs,
            clip = prepared is null || _phase == Loading ? null : $"/api/games/melody/{prepared.Token}.mp3",
            me = mine is null ? null : new { artist = mine.Artist, title = mine.Title, points = mine.Points },
            found = _found.Where(kv => kv.Value.Artist || kv.Value.Title)
                .Select(kv => new { seat = kv.Key, artist = kv.Value.Artist, title = kv.Value.Title, points = kv.Value.Points }).ToArray(),
            skip = _phase == Play ? _skip.Order().ToArray() : [],
            answer = open && prepared is not null ? new { artist = prepared.Track.Artist, title = prepared.Track.Title, thumb = prepared.Track.Thumb } : null,
            scores = (int[])_scores.Clone(),
            left = _left.Order().ToArray(),
            error = _error,
            result = _result,
        };
    }
}
