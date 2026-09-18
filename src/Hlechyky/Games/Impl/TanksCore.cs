namespace Hlechyky.Games.Impl;

/// <summary>Що стоїть у клітинці: порожньо, сталь (незламна) або цегла (снаряд ламає).</summary>
public enum TankTile { Free, Steel, Brick }

/// <summary>
/// Що випадає з розбитої цегли: ⚡ швидкість, 🔫 два снаряди в польоті, 🚀 швидкий снаряд і коротша
/// перезарядка, 🛡 щит на п'ять секунд, 💥 пробивний снаряд (ламає сталь, крізь цеглу летить далі).
/// </summary>
public enum TankBonus { Speed, Twin, Rapid, Shield, Pierce }

/// <summary>Бонус, що лежить на клітинці й чекає, поки хтось на нього наїде. Довго не лежить.</summary>
public sealed class TankDrop
{
    public required int Cell { get; init; }
    public required TankBonus Kind { get; init; }
    public int Ttl;
}

/// <summary>
/// Танк одного місця. Рух клітинний, як у бомбера: їде з <see cref="Cell"/> у сусідню в напрямку
/// <see cref="Move"/>, <see cref="Step"/> — скільки дванадцятих шляху позаду. Куди повернувся востаннє
/// (<see cref="Dir"/>) — туди й дуло.
/// </summary>
public sealed class Tank
{
    public int Cell;
    /// <summary>Напрямок поточного кроку; -1 — стоїть рівно в клітинці.</summary>
    public int Move = -1;
    public int Step;
    /// <summary>Напрямок, який гравець тримає; -1 — відпустив.</summary>
    public int Want = -1;
    /// <summary>Дуло: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    public int Dir;
    /// <summary>На полі (не підбитий і не чекає повернення).</summary>
    public bool Alive;
    /// <summary>Чи це місце взагалі грає (порожнє чи той, хто встав, не з'являється).</summary>
    public bool Plays;
    /// <summary>Тиків щита після повернення: снаряди пролітають крізь.</summary>
    public int Shield;
    /// <summary>Тиків до наступного пострілу.</summary>
    public int Reload;
    /// <summary>Тиків до повернення на поле; 0 — не чекає.</summary>
    public int Respawn;
    public int Frags;
    /// <summary>Скільки своїх снарядів зараз летить; ліміт — один, з 🔫 — два.</summary>
    public int ShellsOut;
    /// <summary>Стартовий кут.</summary>
    public int Home;
    // Бонуси. Згорають разом із танком: той, хто вирвався вперед, не тікає назавжди.
    /// <summary>⚡ клітинка за 3 тики замість 4.</summary>
    public bool Fast;
    /// <summary>🔫 два снаряди в польоті.</summary>
    public bool Twin;
    /// <summary>🚀 снаряд 12/тик і перезарядка 6 тиків.</summary>
    public bool Rapid;
    /// <summary>💥 наступний снаряд пробивний.</summary>
    public bool Pierce;

    public void Strip() => Fast = Twin = Rapid = Pierce = false;
}

/// <summary>Снаряд у дванадцятих частках клітинки. Id — щоб клієнт вів саме його між кадрами.</summary>
public sealed class Shell
{
    public required int Id { get; init; }
    public required int Owner { get; init; }
    public required int Dir { get; init; }
    public required int Speed { get; init; }
    /// <summary>💥: ламає сталь (крім рамки) і летить крізь цеглу, ламаючи її дорогою.</summary>
    public bool Pierce { get; init; }
    public int X, Y;
}

/// <summary>
/// Поле, танки й снаряди — правила однієї партії без жодного слова про кімнати й Журнал. Мапа з
/// <paramref name="rng"/>, але дзеркальна по обох осях: кути рівні, скільки б там не випало.
/// Розмір — за складом: до чотирьох — 21×15, на п'ятьох-шістьох — 27×19 (див. <see cref="SizeFor"/>).
/// </summary>
public sealed class TanksCore
{
    public const int SmallW = 21, SmallH = 15, BigW = 27, BigH = 19;
    /// <summary>Скільки людей уміщає звична мапа; більше — велика.</summary>
    public const int SmallSeats = 4;
    public readonly int W, H;
    readonly Random _rng;

    public TanksCore(Random rng, int w = SmallW, int h = SmallH)
    {
        _rng = rng;
        W = w;
        H = h;
        Tiles = new TankTile[W * H];
        Tanks = [.. Enumerable.Range(0, Seats).Select(_ => new Tank())];
        Starts = [Cell(1, 1), Cell(W - 2, H - 2), Cell(W - 2, 1), Cell(1, H - 2), Cell(1, H / 2), Cell(W - 2, H / 2)];
    }

    /// <summary>Розмір мапи під стількох гравців.</summary>
    public static (int W, int H) SizeFor(int players) => players > SmallSeats ? (BigW, BigH) : (SmallW, SmallH);

    /// <summary>25 кадрів на секунду: крок каркаса 20 мс ділить його рівно.</summary>
    public const int TickMs = 40;
    public const int Sub = 12;
    /// <summary>Клітинка за 4 тики — 160 мс; з ⚡ — 4/тик, клітинка за 3 тики.</summary>
    public const int StepSub = 3, FastStepSub = 4;
    /// <summary>Снаряд: 8 дванадцятих за тик, ≈ 17 клітинок/с; з 🚀 — 12, рівно клітинка за тик. Жодну не перескочить.</summary>
    public const int ShellSpeed = 8, RapidShellSpeed = 12;
    public const int RapidReloadTicks = 6;
    /// <summary>🛡 із цегли — п'ять секунд.</summary>
    public const int BonusShieldTicks = 125;
    /// <summary>З якою ймовірністю розбита цегла лишає бонус і скільки він лежить (25 с — щоб устигнути доїхати через пів мапи).</summary>
    public const int DropChance = 40, DropTicks = 625;
    /// <summary>Звідки вилітає снаряд: центр танка плюс стільки в напрямку дула.</summary>
    public const int ShellNose = 7;
    /// <summary>Пів танка в дванадцятих — для влучання снаряда.</summary>
    public const int Half = 6;
    public const int ReloadTicks = 10;
    public const int RespawnTicks = 50;
    public const int ShieldTicks = 38;
    /// <summary>Дві хвилини.</summary>
    public const int MatchTicks = 3000;
    public const int SteelChance = 6, BrickChance = 28;
    public const int Seats = 6;

    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору — як скрізь на платформі.</summary>
    public static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>
    /// Старти по місцях: чотири кути (перші два — по діагоналі, щоб на двох стіл був чесним), далі
    /// середина лівого й правого краю. Мапа дзеркальна, тож усі шість — рівні.
    /// </summary>
    public int[] Starts { get; }

    public TankTile[] Tiles { get; }
    public Tank[] Tanks { get; }
    public List<Shell> Shells { get; } = [];
    public List<TankDrop> Drops { get; } = [];
    public int Ticks { get; private set; }
    int _nextShell;

    public int Cell(int x, int y) => y * W + x;
    public int X(int cell) => cell % W;
    public int Y(int cell) => cell / W;

    public int Ahead(int cell, int dir)
    {
        if (dir is < 0 or > 3) return -1;
        var (dx, dy) = Deltas[dir];
        var (x, y) = (X(cell) + dx, Y(cell) + dy);
        return x < 0 || x >= W || y < 0 || y >= H ? -1 : Cell(x, y);
    }

    /// <summary>Лівий верхній кут танка в дванадцятих; сам танк — квадрат Sub×Sub від нього.</summary>
    public int PosX(Tank t) => X(t.Cell) * Sub + (t.Move >= 0 ? Deltas[t.Move].Dx * t.Step : 0);
    public int PosY(Tank t) => Y(t.Cell) * Sub + (t.Move >= 0 ? Deltas[t.Move].Dy * t.Step : 0);
    /// <summary>Центр танка — від нього рахуються дуло і влучання.</summary>
    public int CenterX(Tank t) => PosX(t) + Sub / 2;
    public int CenterY(Tank t) => PosY(t) + Sub / 2;

    /// <summary>Клітинки, які танк зараз займає: та, звідки їде, і та, куди (коли посеред кроку).</summary>
    IEnumerable<int> Footprint(Tank t)
    {
        yield return t.Cell;
        if (t.Move >= 0 && t.Step > 0) yield return Ahead(t.Cell, t.Move);
    }

    bool NearStart(int cell) => Starts.Any(c => Math.Abs(X(c) - X(cell)) <= 1 && Math.Abs(Y(c) - Y(cell)) <= 1);

    // ---------- поле ----------

    /// <summary>Лише рамка й танки по кутах: таке поле можна показати в лобі, не витративши жодного числа з Rng.</summary>
    public void Layout()
    {
        Ticks = 0;
        Shells.Clear();
        Drops.Clear();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                Tiles[Cell(x, y)] = x == 0 || y == 0 || x == W - 1 || y == H - 1 ? TankTile.Steel : TankTile.Free;
        for (var i = 0; i < Seats; i++)
            Tanks[i] = new Tank { Cell = Starts[i], Home = Starts[i], Want = Tanks[i].Want, Dir = X(Starts[i]) < W / 2 ? 0 : 2 };
    }

    /// <summary>
    /// Партія: мапа з Rng, дзеркальна по обох осях (генерується чверть із середніми рядом і стовпцем,
    /// решта — відбиття), 3×3 біля кутів порожні, танки на місцях, які цього разу грають.
    /// </summary>
    public void Reset(bool[] plays)
    {
        Layout();
        for (var y = 1; y <= H / 2; y++)
            for (var x = 1; x <= W / 2; x++)
            {
                var roll = _rng.Next(100);
                var tile = roll < SteelChance ? TankTile.Steel : roll < SteelChance + BrickChance ? TankTile.Brick : TankTile.Free;
                foreach (var cell in new[] { Cell(x, y), Cell(W - 1 - x, y), Cell(x, H - 1 - y), Cell(W - 1 - x, H - 1 - y) })
                    Tiles[cell] = NearStart(cell) ? TankTile.Free : tile;
            }
        for (var i = 0; i < Seats; i++)
        {
            var active = i < plays.Length && plays[i];
            Tanks[i].Plays = active;
            Tanks[i].Alive = active;
        }
    }

    // ---------- наміри ----------

    /// <summary>
    /// Тримає напрямок (0..3) або відпустив (-1). Танк, що стоїть, одразу повертає дуло: коротке натискання
    /// — це й розворот, і одна клітинка ходу. Посеред кроку дуло повернеться на межі клітинок.
    /// </summary>
    public void Turn(int seat, int dir)
    {
        if (seat < 0 || seat >= Tanks.Length) return;
        var t = Tanks[seat];
        t.Want = dir is >= 0 and <= 3 ? dir : -1;
        if (t.Want >= 0 && t.Alive && t.Move < 0) t.Dir = t.Want;
    }

    /// <summary>Постріл із дула. false — не на полі, снарядів у польоті вже досить або перезарядка.</summary>
    public bool Fire(int seat)
    {
        if (seat < 0 || seat >= Tanks.Length) return false;
        var t = Tanks[seat];
        if (!t.Alive || t.ShellsOut >= (t.Twin ? 2 : 1) || t.Reload > 0) return false;
        var (dx, dy) = Deltas[t.Dir];
        Shells.Add(new Shell
        {
            Id = ++_nextShell, Owner = seat, Dir = t.Dir, Speed = t.Rapid ? RapidShellSpeed : ShellSpeed, Pierce = t.Pierce,
            X = CenterX(t) + dx * ShellNose, Y = CenterY(t) + dy * ShellNose,
        });
        t.Pierce = false;   // 💥 — на один постріл
        t.ShellsOut++;
        t.Reload = t.Rapid ? RapidReloadTicks : ReloadTicks;
        return true;
    }

    // ---------- тик ----------

    /// <summary>
    /// Один крок світу: спершу лічильники (перезарядка, щит, повернення), потім їдуть танки, потім летять
    /// снаряди — і той, хто виїхав під снаряд, дістає його того самого тика.
    /// </summary>
    public void Step()
    {
        Ticks++;
        Timers();
        Walk();
        Collect();
        Fly();
    }

    void Timers()
    {
        foreach (var t in Tanks)
        {
            if (!t.Plays) continue;
            if (t.Reload > 0) t.Reload--;
            if (t.Shield > 0) t.Shield--;
            if (t.Respawn > 0 && --t.Respawn == 0) Spawn(t);
        }
        Drops.RemoveAll(d => --d.Ttl <= 0);
    }

    /// <summary>Наїхав на бонус — забрав. Клітинка «в якій центр», як у бомбера.</summary>
    void Collect()
    {
        foreach (var t in Tanks)
        {
            if (!t.Alive) continue;
            var cell = Cell(CenterX(t) / Sub, CenterY(t) / Sub);
            var i = Drops.FindIndex(d => d.Cell == cell);
            if (i < 0) continue;
            Apply(t, Drops[i].Kind);
            Drops.RemoveAt(i);
        }
    }

    public static void Apply(Tank t, TankBonus kind)
    {
        switch (kind)
        {
            case TankBonus.Speed: t.Fast = true; break;
            case TankBonus.Twin: t.Twin = true; break;
            case TankBonus.Rapid: t.Rapid = true; break;
            case TankBonus.Shield: t.Shield = Math.Max(t.Shield, BonusShieldTicks); break;
            case TankBonus.Pierce: t.Pierce = true; break;
        }
    }

    /// <summary>Цегла розлетілась: іноді під нею щось лежить.</summary>
    void Break(int cell)
    {
        Tiles[cell] = TankTile.Free;
        if (_rng.Next(100) >= DropChance || Drops.Any(d => d.Cell == cell)) return;
        var kinds = Enum.GetValues<TankBonus>();
        Drops.Add(new TankDrop { Cell = cell, Kind = kinds[_rng.Next(kinds.Length)], Ttl = DropTicks });
    }

    /// <summary>Повернення на свій старт, а як він зайнятий — на найближчий вільний; зі щитом.</summary>
    void Spawn(Tank t)
    {
        var spot = Starts.OrderBy(c => c == t.Home ? 0 : 1 + Math.Abs(X(c) - X(t.Home)) + Math.Abs(Y(c) - Y(t.Home)))
            .FirstOrDefault(c => !Tanks.Any(o => o != t && o.Alive && Footprint(o).Contains(c)), t.Home);
        t.Cell = spot;
        t.Move = -1;
        t.Step = 0;
        t.Alive = true;
        t.Shield = ShieldTicks;
        t.Dir = X(spot) < W / 2 ? 0 : 2;
        if (t.Want >= 0) t.Dir = t.Want;
    }

    /// <summary>Чи можна заїхати: порожня клітинка, яку не займає інший живий танк.</summary>
    public bool Drivable(int cell, Tank who) =>
        cell >= 0 && Tiles[cell] == TankTile.Free && !Tanks.Any(o => o != who && o.Alive && Footprint(o).Contains(cell));

    void Walk()
    {
        foreach (var t in Tanks)
        {
            if (!t.Alive) continue;
            if (t.Move < 0)
            {
                if (t.Want < 0) continue;
                t.Dir = t.Want;                                  // уперся — все одно дивиться туди
                if (!Drivable(Ahead(t.Cell, t.Want), t)) continue;
                t.Move = t.Want;
                t.Step = 0;
            }
            t.Step += t.Fast ? FastStepSub : StepSub;
            if (t.Step < Sub) continue;
            t.Cell = Ahead(t.Cell, t.Move);
            t.Step = 0;
            t.Move = -1;
        }
    }

    /// <summary>Крок підльоту: ділить і 8, і 12, тож снаряд оглядає кожну клітинку, крізь яку пролітає.</summary>
    const int FlySub = 4;

    void Fly()
    {
        if (Shells.Count == 0) return;
        var gone = new HashSet<Shell>();
        foreach (var s in Shells)
        {
            var (dx, dy) = Deltas[s.Dir];
            // Не «стрибком» на всю швидкість, а підкроками: інакше швидкий снаряд перескакував би клітинку
            // з цеглою (стартує на першій дванадцятій сусідньої клітинки — і за тик опинявся вже за нею).
            for (var flown = 0; flown < s.Speed && !gone.Contains(s); flown += FlySub)
            {
                s.X += dx * FlySub;
                s.Y += dy * FlySub;
                Land(s, gone);
            }
        }
        // Два снаряди в одній точці гасять один одного — і лоб у лоб, і навздогін.
        for (var a = 0; a < Shells.Count; a++)
            for (var b = a + 1; b < Shells.Count; b++)
            {
                var (p, q) = (Shells[a], Shells[b]);
                if (gone.Contains(p) || gone.Contains(q)) continue;
                if (Math.Abs(p.X - q.X) <= Half && Math.Abs(p.Y - q.Y) <= Half) { gone.Add(p); gone.Add(q); }
            }
        foreach (var s in gone)
        {
            Shells.Remove(s);
            Tanks[s.Owner].ShellsOut = Math.Max(0, Tanks[s.Owner].ShellsOut - 1);
        }
    }

    /// <summary>Де снаряд зараз: край, сталь, цегла чи чужий танк. Снаряд — точка; клітинка — та, в якій вона.</summary>
    void Land(Shell s, HashSet<Shell> gone)
    {
        var (cx, cy) = (s.X / Sub, s.Y / Sub);
        if (s.X < 0 || s.Y < 0 || cx >= W || cy >= H) { gone.Add(s); return; }
        var cell = Cell(cx, cy);
        var border = cx == 0 || cy == 0 || cx == W - 1 || cy == H - 1;
        switch (Tiles[cell])
        {
            case TankTile.Steel:
                if (s.Pierce && !border) Tiles[cell] = TankTile.Free;   // 💥 ламає сталь, але не рамку
                gone.Add(s);
                return;
            case TankTile.Brick:
                Break(cell);
                if (!s.Pierce) { gone.Add(s); return; }                  // 💥 летить далі крізь цеглу
                break;
        }
        for (var i = 0; i < Tanks.Length; i++)
        {
            var t = Tanks[i];
            if (i == s.Owner || !t.Alive) continue;
            if (Math.Abs(s.X - CenterX(t)) > Half || Math.Abs(s.Y - CenterY(t)) > Half) continue;
            gone.Add(s);
            if (t.Shield == 0) Kill(t, s.Owner);
            return;
        }
    }

    void Kill(Tank t, int by)
    {
        t.Alive = false;
        t.Move = -1;
        t.Step = 0;
        t.Shield = 0;
        t.Respawn = RespawnTicks;
        t.Strip();
        if (by >= 0 && by < Tanks.Length) Tanks[by].Frags++;
    }

    /// <summary>Хтось устав із-за столу: танк зникає, фраги лишаються в рахунку.</summary>
    public void Drop(int seat)
    {
        if (seat < 0 || seat >= Tanks.Length) return;
        var t = Tanks[seat];
        t.Alive = false;
        t.Plays = false;
        t.Respawn = 0;
        t.Want = -1;
    }

    public int[] BrickCells() => [.. Enumerable.Range(0, Tiles.Length).Where(c => Tiles[c] == TankTile.Brick)];
    public int[] SteelCells() => [.. Enumerable.Range(0, Tiles.Length).Where(c => Tiles[c] == TankTile.Steel)];
}
