using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Танчики на двох–шістьох: арена згори, цегла ламається, хто перший набере фрагів — той і взяв.
/// Правила поля живуть у <see cref="TanksCore"/>, а тут — фази («готуйсь», гра, кінець), опція «до
/// скількох», вид і кадр. Кадр летить 25 разів на секунду, тож у ньому лише короткі числа.
/// </summary>
public sealed class Tanks : Game
{
    /// <summary>«Готуйсь» на свіжій мапі — дві секунди.</summary>
    public const int StartTicks = 50;
    public const string PhaseStart = "start", PhaseGo = "go", PhaseOver = "over";
    /// <summary>«За столом»: на двох — до 5, на трьох-чотирьох — до 8, на п'ятьох-шістьох — до 12.</summary>
    public static int FragsFor(int players) => players <= 2 ? 5 : players <= 4 ? 8 : 12;

    public override GameInfo Info { get; } = new(
        "tanks", "Танчики", "танчики", GameGroup.Live, 2, TanksCore.Seats, TickMs: TanksCore.TickMs,
        Start: StartMode.ByHost, Score: ScoreOrder.HigherIsBetter,
        Options: [new GameOption("frags", "Грати до", [("auto", "За столом"), ("5", "5 фрагів"), ("10", "10 фрагів"), ("15", "15 фрагів")], "auto")],
        Hint: "Танчики згори: їдеш, стріляєш, ламаєш цеглу. Хто перший набере фрагів — той і взяв. На п'ятьох-шістьох мапа більша");

    TanksCore? _core;
    string _phase = PhaseStart;
    int _startIn = StartTicks;
    /// <summary>Скільки фрагів до перемоги; 0 — «за столом», рахується на «Почати» зі складу.</summary>
    int _frags;
    int _need = FragsFor(2);
    bool _started;

    /// <summary>
    /// Поле готове ще до старту (рамка й танки по стартах) — стіл у лобі виглядає як поле, а не як порожнеча.
    /// До старту воно звичне; скільки людей сіло, відомо лише на «Почати» — тоді й вирішується розмір.
    /// </summary>
    TanksCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new TanksCore(Ctx.Rng);
            _core.Layout();
            return _core;
        }
    }

    /// <summary>Нова мапа під склад: на п'ятьох-шістьох — велика. Намір-напрямок кожного переживає заміну.</summary>
    void Rebuild(bool[] plays)
    {
        var (w, h) = TanksCore.SizeFor(plays.Count(p => p));
        if (_core is null || _core.W != w || _core.H != h)
        {
            var fresh = new TanksCore(Ctx.Rng, w, h);
            if (_core is not null)
                for (var i = 0; i < TanksCore.Seats; i++) fresh.Tanks[i].Want = _core.Tanks[i].Want;
            _core = fresh;
        }
        _core.Reset(plays);
    }

    public override string SeatName(int seat) => seat switch
    {
        0 => "жовтий",
        1 => "зелений",
        2 => "рудий",
        3 => "сірий",
        4 => "синій",
        _ => "рожевий",
    };

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _frags = options.TryGetValue("frags", out var v) && int.TryParse(v, out var n) && n is 5 or 10 or 15 ? n : 0;
        _need = _frags > 0 ? _frags : FragsFor(2);
    }

    public override void Start()
    {
        _started = true;
        _phase = PhaseStart;
        _startIn = StartTicks;
        var plays = Enumerable.Range(0, TanksCore.Seats).Select(Ctx.Seated).ToArray();
        _need = _frags > 0 ? _frags : FragsFor(plays.Count(p => p));
        Rebuild(plays);
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= TanksCore.Seats) return ActResult.Fail("Ти тут не граєш");
        if (_phase == PhaseOver) return ActResult.Fail("Партію вже зіграно");

        switch (action)
        {
            case "move":
                // Намір, а не хід: стрілку тиснуть ще на відліку, і танк рушає з першого тика.
                var dir = Dir(payload);
                if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
                Core.Turn(seat, dir.Value);
                return ActResult.Done;
            case "fire":
                if (_phase != PhaseGo) return ActResult.Fail("Зачекай, зараз почнемо");
                if (!Core.Tanks[seat].Alive) return ActResult.Fail("Тебе підбили, зачекай пару секунд");
                return Core.Fire(seat) ? ActResult.Done : ActResult.Fail("Перезарядка");
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Напрямок приймаємо і як <c>{dir:1}</c>, і як голе число.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    // ---------- тик ----------

    public override TickResult Tick()
    {
        switch (_phase)
        {
            case PhaseOver:
                return TickResult.None;
            case PhaseStart:
                if (--_startIn > 0) return TickResult.FrameOnly;
                _phase = PhaseGo;
                return TickResult.FrameOnly;
            default:
                Core.Step();
                var top = Playing().Where(s => Core.Tanks[s].Frags >= _need).ToArray();
                if (top.Length > 0) return Over(top);
                if (Core.Ticks >= TanksCore.MatchTicks) return Over(Leaders());
                return TickResult.FrameOnly;
        }
    }

    int[] Playing() => [.. Enumerable.Range(0, TanksCore.Seats).Where(s => Ctx.Seated(s) && Core.Tanks[s].Plays)];

    /// <summary>Хто попереду за фрагами; порожньо — нічия (попереду всі одразу).</summary>
    int[] Leaders()
    {
        var playing = Playing();
        if (playing.Length == 0) return [];
        var best = playing.Max(s => Core.Tanks[s].Frags);
        var leaders = playing.Where(s => Core.Tanks[s].Frags == best).ToArray();
        return leaders.Length == playing.Length ? [] : leaders;
    }

    /// <summary>Кінець партії: рядок Журналу — фраги, переможець першим; фраги кожного — у таблицю.</summary>
    TickResult Over(int[] winners)
    {
        _phase = PhaseOver;
        winners = [.. winners.Where(Ctx.Seated)];
        var rest = Enumerable.Range(0, TanksCore.Seats).Where(s => Ctx.Seated(s) && !winners.Contains(s));
        var score = string.Join(" : ", winners.Concat(rest).Select(s => $"{Ctx.NickOf(s)} {Core.Tanks[s].Frags}"));
        foreach (var s in Playing()) Ctx.Score(s, Core.Tanks[s].Frags);
        Ctx.Finish(winners, winners.Length > 0 ? $"{Info.Title}: {score}" : $"{Info.Title}: {score} — нічия");
        return TickResult.Both;
    }

    /// <summary>Хтось устав: на трьох-чотирьох танк утікача зникає, партія триває; лишився один — партія його.</summary>
    public override void OnLeave(int seat)
    {
        if (_started) Core.Drop(seat);
        var left = Enumerable.Range(0, TanksCore.Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        if (left.Length > 1 && _phase != PhaseOver) return;
        _phase = PhaseOver;
        Ctx.Finish(left, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
    }

    // ---------- вид і кадр ----------

    public override object? Frame() => new
    {
        t = Core.Ticks,
        p = Men(),
        s = Shots(),
        pw = Loot(),
        bricks = Core.BrickCells(),
        phase = _phase,
        startIn = _startIn,
        left = Math.Max(0, TanksCore.MatchTicks - Core.Ticks),
    };

    public override object View(int? seat) => new
    {
        width = Core.W,
        height = Core.H,
        sub = TanksCore.Sub,
        turn = (int?)null,
        need = _need,
        walls = Core.SteelCells(),          // сталь не міняється за партію — клієнт малює її раз
        t = Core.Ticks,
        p = Men(),
        s = Shots(),
        pw = Loot(),
        bricks = Core.BrickCells(),
        phase = _phase,
        startIn = _startIn,
        left = Math.Max(0, TanksCore.MatchTicks - Core.Ticks),
    };

    object[] Shots() => [.. Core.Shells.Select(s => (object)new { i = s.Id, x = s.X, y = s.Y, d = s.Dir, big = s.Pierce })];

    object[] Loot() => [.. Core.Drops.Select(d => (object)new { x = Core.X(d.Cell), y = Core.Y(d.Cell), kind = Kind(d.Kind) })];

    static string Kind(TankBonus kind) => kind switch
    {
        TankBonus.Speed => "speed",
        TankBonus.Twin => "twin",
        TankBonus.Rapid => "rapid",
        TankBonus.Shield => "shield",
        _ => "pierce",
    };

    /// <summary>Танки по місцях; до старту «живий» = «за місцем хтось сидить», щоб лобі показувало, кого чекати.</summary>
    object[] Men() =>
    [
        .. Core.Tanks.Select((t, i) => (object)new
        {
            x = Core.PosX(t),
            y = Core.PosY(t),
            d = t.Dir,
            alive = _started ? t.Alive : Ctx.Seated(i),
            shield = t.Shield,
            frags = t.Frags,
            reload = t.Reload,
            back = t.Respawn,
            perks = (t.Fast ? "s" : "") + (t.Twin ? "t" : "") + (t.Rapid ? "r" : "") + (t.Pierce ? "p" : ""),
        }),
    ];
}
