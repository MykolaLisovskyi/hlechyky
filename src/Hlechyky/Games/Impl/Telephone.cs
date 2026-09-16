using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Зіпсований телефон (specs/telephone.md). Кожен пише фразу; далі ланцюжки йдуть по колу: наступний малює
/// чужу фразу, наступний описує чужий малюнок словами, і так поки кожен не торкнувся кожного ланцюжка (або
/// скільки кроків обрав господар). Усі працюють одночасно, кожен над своїм ланцюжком. Потім показ: ланцюжки
/// відкриваються по одному запису, за смішні записи ставлять ❤, у кого більше ❤ — той і виграв.
///
/// Тик потрібен лише для таймерів кроку й розсилки видів: малюнок кожен малює у себе, а на сервер його штрихи
/// летять через Input і лежать тут, поки крок не скінчиться (з них же відновлюється полотно після F5).
/// </summary>
public sealed class Telephone : Game
{
    public const int TickMs = 250;
    public const int MaxText = 80;
    /// <summary>Межі одного малюнка: менші, ніж у Піктіонарі — на показі їх розсилають пачками.</summary>
    public const int MaxOps = 2_500, MaxPoints = 30_000;
    /// <summary>Між «Далі» на показі — щоб подвійний клік не перегортав два записи.</summary>
    public const int NextEveryMs = 600;
    const int Seats = 10;
    /// <summary>Скільки фраз-підказок отримує кожен для 🎲.</summary>
    public const int IdeaCount = 8;

    /// <summary>Темп: секунди на фразу, на малюнок і на опис.</summary>
    public static readonly IReadOnlyDictionary<string, (int Phrase, int Draw, int Describe)> Tempos =
        new Dictionary<string, (int, int, int)>
        {
            ["fast"] = (40, 60, 35),
            ["normal"] = (60, 90, 45),
            ["slow"] = (90, 150, 70),
        };

    public const string Phrase = "phrase", Draw = "draw", Describe = "describe";
    const string Step = "step", Reveal = "reveal", Done = "done";
    /// <summary>Що підставляємо за того, хто не встиг описати малюнок.</summary>
    public const string Shrug = "🤷 не встиг";

    public override GameInfo Info { get; } = new(
        "telephone", "Зіпсований телефон", "зіпсований телефон", GameGroup.Party, 3, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("tempo", "Темп", [("fast", "Швидкий"), ("normal", "Звичайний"), ("slow", "Спокійний")], "normal"),
            new GameOption("steps", "Кроків", [("all", "Скільки гравців"), ("4", "4"), ("6", "6"), ("8", "8")], "all"),
        ],
        Hint: "Пишеш фразу — сусід її малює — наступний описує малюнок — і так по колу. А потім усі разом дивляться, що вийшло");

    sealed class Entry
    {
        public required int Seat { get; init; }
        public required string Kind { get; init; }   // "text" | "drawing"
        public string? Text { get; init; }
        public int[][]? Ops { get; init; }
        public HashSet<int> Likes { get; } = [];
    }

    sealed class Job
    {
        public required int Chain { get; init; }
        public required string Kind { get; init; }
        public string Text { get; set; } = "";
        public Sketch Sketch { get; } = new(MaxOps, MaxPoints);
        public bool Ready { get; set; }
        /// <summary>Підказки для 🎲 — лише в завданні «фраза».</summary>
        public string[] Ideas { get; init; } = [];
    }

    // ---------- налаштування ----------
    TelephonePhrases _phrases = null!;
    (int Phrase, int Draw, int Describe) _tempo = Tempos["normal"];
    int? _stepsOption;

    // ---------- партія ----------
    int[] _order = [];
    List<Entry>[] _chains = [];
    readonly Dictionary<int, Job> _tasks = [];
    readonly HashSet<int> _left = [];
    string _phase = Step;
    int _step;
    int _steps;
    DateTimeOffset _until;
    int _totalMs;
    // показ
    int _chain;
    int _shown;
    DateTimeOffset _lastNext;
    object? _result;
    bool _dirty;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _phrases = Ctx.Services.GetService<TelephonePhrases>() ?? TelephonePhrases.Default;
        if (options.TryGetValue("tempo", out var t) && Tempos.TryGetValue(t, out var tempo)) _tempo = tempo;
        if (options.TryGetValue("steps", out var s) && int.TryParse(s, out var n) && n is 4 or 6 or 8) _stepsOption = n;
    }

    public override void Start()
    {
        _order = [.. Enumerable.Range(0, Seats).Where(Ctx.Seated)];
        _chains = [.. _order.Select(_ => new List<Entry>())];
        _steps = Math.Min(_order.Length, _stepsOption ?? _order.Length);
        _left.Clear();
        _result = null;
        _chain = 0;
        _shown = 0;
        _lastNext = default;
        _step = -1;
        NextStep();
    }

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    bool Present(int seat) => Ctx.Seated(seat) && !_left.Contains(seat);

    // =========================================================================================
    // Кроки
    // =========================================================================================

    void NextStep()
    {
        _step++;
        _tasks.Clear();
        if (_step < _steps)
        {
            var n = _order.Length;
            for (var i = 0; i < n; i++)
            {
                var seat = _order[i];
                if (!Present(seat)) continue;
                var chain = ((i - _step) % n + n) % n;
                var last = _chains[chain].LastOrDefault();
                var kind = last is null ? Phrase : last.Kind == "text" ? Draw : Describe;
                _tasks[seat] = new Job { Chain = chain, Kind = kind, Ideas = kind == Phrase ? _phrases.Pick(Ctx.Rng, IdeaCount) : [] };
            }
        }
        if (_tasks.Count == 0) { BeginReveal(); return; }

        // Крок міряємо найдовшим завданням у ньому: на другому кроці хтось малює, а хтось, чий ланцюжок
        // почався з порожнечі, пише фразу — чекати мають усі разом.
        var seconds = _tasks.Values.Max(t => t.Kind switch { Draw => _tempo.Draw, Describe => _tempo.Describe, _ => _tempo.Phrase });
        _totalMs = seconds * 1000;
        _until = Now.AddMilliseconds(_totalMs);
        _phase = Step;
        _dirty = true;
    }

    /// <summary>Крок скінчився: усе, що встигли (або не встигли), лягає в ланцюжки.</summary>
    void EndStep()
    {
        foreach (var (seat, task) in _tasks)
        {
            if (!Present(seat)) continue;   // пішов — його запис просто пропускаємо, ланцюжок іде далі
            var entry = task.Kind switch
            {
                Draw => new Entry { Seat = seat, Kind = "drawing", Ops = task.Sketch.Ops() },
                Describe => new Entry { Seat = seat, Kind = "text", Text = task.Text.Length > 0 ? task.Text : Shrug },
                _ => new Entry { Seat = seat, Kind = "text", Text = task.Text.Length > 0 ? task.Text : _phrases.Random(Ctx.Rng) },
            };
            _chains[task.Chain].Add(entry);
        }
        NextStep();
    }

    void BeginReveal()
    {
        _tasks.Clear();
        _phase = Reveal;
        _chain = NextChain(-1);
        _shown = 1;
        _dirty = true;
        if (_chain < 0) Over();
    }

    int NextChain(int after)
    {
        for (var c = after + 1; c < _chains.Length; c++) if (_chains[c].Count > 0) return c;
        return -1;
    }

    // =========================================================================================
    // Дії
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        return action switch
        {
            "text" => SetText(seat, payload),
            "done" => Submit(seat, payload),
            "edit" => Edit(seat),
            "draw" => Ink(seat, t => t.Sketch.Line(payload)),
            "fill" => Ink(seat, t => t.Sketch.Fill(payload)),
            "undo" => Ink(seat, t => { t.Sketch.Undo(); return null; }),
            "clear" => Ink(seat, t => { t.Sketch.Clear(); return null; }),
            "next" => Next(seat),
            "like" => Like(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    Job? TaskOf(int seat) => _phase == Step && Present(seat) && _tasks.TryGetValue(seat, out var t) ? t : null;

    /// <summary>Чернетка тексту (летить через Input, поки людина пише) — щоб не згоріла, якщо час вийде.</summary>
    ActResult SetText(int seat, JsonElement payload)
    {
        if (TaskOf(seat) is not { } task) return ActResult.Fail("Зараз нічого писати");
        if (task.Kind == Draw) return ActResult.Fail("Тут треба малювати");
        if (task.Ready) return ActResult.Fail("Уже здано — тисни «Змінити»");
        task.Text = Clean(Str(payload, "text"));
        return ActResult.Done;
    }

    ActResult Submit(int seat, JsonElement payload)
    {
        if (TaskOf(seat) is not { } task) return ActResult.Fail("Зараз нічого здавати");
        if (task.Kind != Draw)
        {
            var text = Clean(Str(payload, "text"));
            if (text.Length == 0) return ActResult.Fail(task.Kind == Phrase ? "Напиши фразу або тисни 🎲" : "Напиши, що бачиш на малюнку");
            task.Text = text;
        }
        else
        {
            // Клієнт каже, скільки операцій у нього на полотні. Не збіглось — якийсь шматок загубився
            // дорогою: віддаємо серверний малюнок назад, хай людина подивиться, перш ніж здавати.
            var n = Int(payload, "n", -1);
            if (n >= 0 && n != task.Sketch.Count)
            {
                _dirty = true;
                return ActResult.Fail("Малюнок дійшов не цілком — глянь і здай ще раз");
            }
        }
        task.Ready = true;
        _dirty = true;
        return ActResult.Accept("Здано!");
    }

    ActResult Edit(int seat)
    {
        if (TaskOf(seat) is not { } task) return ActResult.Fail("Зараз нічого змінювати");
        task.Ready = false;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Ink(int seat, Func<Job, string?> apply)
    {
        if (TaskOf(seat) is not { Kind: Draw } task) return ActResult.Fail("Зараз малювати не треба");
        if (task.Ready) return ActResult.Fail("Уже здано — тисни «Змінити»");
        return apply(task) is { } error ? ActResult.Fail(error) : ActResult.Done;
    }

    /// <summary>Показ: наступний запис ланцюжка або наступний ланцюжок. Тисне будь-хто за столом.</summary>
    ActResult Next(int seat)
    {
        if (_phase != Reveal) return ActResult.Fail("Показ ще не почався");
        if (!Present(seat)) return ActResult.Fail("Ти вже не за столом");
        var now = Now;
        if ((now - _lastNext).TotalMilliseconds < NextEveryMs) return ActResult.Done;
        _lastNext = now;

        if (_shown < _chains[_chain].Count) _shown++;
        else
        {
            var next = NextChain(_chain);
            if (next < 0) { Over(); return ActResult.Done; }
            _chain = next;
            _shown = 1;
        }
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Like(int seat, JsonElement payload)
    {
        if (_phase != Reveal) return ActResult.Fail("❤ ставлять на показі");
        var chain = Int(payload, "chain", -1);
        var index = Int(payload, "index", -1);
        if (chain < 0 || chain >= _chains.Length || index < 0 || index >= _chains[chain].Count) return ActResult.Fail("Нема такого запису");
        // лише те, що вже показали
        if (chain > _chain || chain == _chain && index >= _shown) return ActResult.Fail("Цього ще не показували");
        var entry = _chains[chain][index];
        if (entry.Seat == seat) return ActResult.Fail("Собі ❤ не ставлять 🙂");
        if (!entry.Likes.Remove(seat)) entry.Likes.Add(seat);
        _dirty = true;
        return ActResult.Done;
    }

    // =========================================================================================
    // Час і кінець
    // =========================================================================================

    public override TickResult Tick()
    {
        if (_phase == Step)
        {
            var waiting = _tasks.Where(kv => Present(kv.Key)).ToList();
            if (Now >= _until || waiting.Count == 0 || waiting.All(kv => kv.Value.Ready)) EndStep();
        }
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(Frame: false, View: true);
    }

    int[] Likes()
    {
        var likes = new int[Seats];
        foreach (var e in _chains.SelectMany(c => c)) likes[e.Seat] += e.Likes.Count;
        return likes;
    }

    void Over()
    {
        if (_phase == Done) return;
        _phase = Done;
        _dirty = true;
        var likes = Likes();
        var seats = _order.Where(Present).ToArray();
        var best = seats.Length == 0 ? 0 : seats.Max(s => likes[s]);
        int[] winners = best > 0 ? [.. seats.Where(s => likes[s] == best)] : [];
        foreach (var s in seats) Ctx.Score(s, likes[s]);
        _result = new { winners, likes };
        var tail = winners.Length == 0 ? "без ❤, зате всі посміялись" : "найбільше ❤ у " + string.Join(" і ", winners.Select(Ctx.NickOf));
        Ctx.Finish(winners, $"{Info.Title}: {_chains.Count(c => c.Count > 0)} ланцюжків — {tail}",
            seats.ToDictionary(s => s, s => (long)likes[s]));
    }

    public override void OnLeave(int seat)
    {
        _left.Add(seat);
        _dirty = true;
        if (_order.Count(Present) < 2)
        {
            if (_phase == Step) BeginReveal();
            // з одним глядачем показ не має сенсу — закриваємо партію
            if (_phase == Reveal) Over();
        }
    }

    // =========================================================================================
    // Вид
    // =========================================================================================

    object? EntryView(Entry e, int index, int? seat) => new
    {
        index,
        seat = e.Seat,
        kind = e.Kind,
        text = e.Text,
        ops = e.Ops,
        likes = e.Likes.Count,
        liked = seat is { } s && e.Likes.Contains(s),
    };

    object? TaskView(int? seat)
    {
        if (seat is not { } s || TaskOf(s) is not { } task) return null;
        var prev = _chains[task.Chain].LastOrDefault();
        return new
        {
            kind = task.Kind,
            chain = task.Chain,
            prompt = prev is null ? null : new { kind = prev.Kind, text = prev.Text, ops = prev.Ops },
            ready = task.Ready,
            text = task.Text,
            n = task.Sketch.Count,
            ops = task.Kind == Draw ? task.Sketch.Ops() : null,
            ideas = task.Ideas,
        };
    }

    public override object View(int? seat) => new
    {
        phase = _phase,
        step = Math.Min(_step + 1, _steps),
        steps = _steps,
        until = _until,
        totalMs = _totalMs,
        task = TaskView(seat),
        ready = _tasks.Where(kv => kv.Value.Ready && Present(kv.Key)).Select(kv => kv.Key).Order().ToArray(),
        waiting = _tasks.Where(kv => !kv.Value.Ready && Present(kv.Key)).Select(kv => kv.Key).Order().ToArray(),
        reveal = _phase != Reveal ? null : new
        {
            chain = _chain,
            owner = _order[_chain],
            no = _chains.Take(_chain + 1).Count(c => c.Count > 0),
            chains = _chains.Count(c => c.Count > 0),
            shown = _shown,
            total = _chains[_chain].Count,
            entries = _chains[_chain].Take(_shown).Select((e, i) => EntryView(e, i, seat)).ToArray(),
        },
        likes = Likes(),
        left = _left.Order().ToArray(),
        result = _result,
    };

    // =========================================================================================
    // Дрібниці
    // =========================================================================================

    static string Clean(string raw)
    {
        var s = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > MaxText ? s[..MaxText].TrimEnd() : s;
    }

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

/// <summary>
/// Фрази-підказки з <c>data/telephone/phrases.txt</c>: для 🎲 і для тих, хто не написав фрази вчасно.
/// Нема файла — одна вшита фраза, щоб гра не стояла.
/// </summary>
public sealed class TelephonePhrases(IReadOnlyList<string> phrases)
{
    public const string FileName = "data/telephone/phrases.txt";
    const string Fallback = "кіт їде на велосипеді";

    static readonly Lazy<TelephonePhrases> Cached = new(() => Load(Paths.Resolve(FileName)));
    public static TelephonePhrases Default => Cached.Value;

    public IReadOnlyList<string> All { get; } = phrases.Count > 0 ? phrases : [Fallback];

    public string Random(Random rng) => All[rng.Next(All.Count)];

    /// <summary>До <paramref name="count"/> різних фраз у випадковому порядку.</summary>
    public string[] Pick(Random rng, int count)
    {
        var pool = All.ToList();
        var picked = new List<string>(count);
        for (var i = 0; i < pool.Count && picked.Count < count; i++)
        {
            var j = rng.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
            picked.Add(pool[i]);
        }
        return [.. picked];
    }

    public static TelephonePhrases Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new([]);
            return new([.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').Distinct()]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new([]); }
    }
}
