namespace Hlechyky;

/// <summary>
/// Правила покрокової гри на сітці: "постав свою мітку і збери Need підряд". Різниця між нашими
/// покроковими іграми лише в розмірі поля і в тому, чи падає фішка вниз, як у «Чотирьох у ряд».
/// </summary>
/// <param name="Need">Скільки в ряд треба зібрати.</param>
/// <param name="Gravity">Ходом називають колонку, а фішка падає на найнижче вільне місце.</param>
public sealed record GridRules(int Width, int Height, int Need, bool Gravity)
{
    public int Cells => Width * Height;
}

/// <summary>Гра, у яку можна поставити стіл. Grid — покрокова на сітці, RealTime — та, що живе від тика.</summary>
/// <param name="Title">Як гра зветься в Журналі, називний відмінок.</param>
/// <param name="Acc">Знахідний відмінок для «сіли грати в …»; збігається з Title, коли він не міняється.</param>
public sealed record GameRules(string Title, string XName, string OName, GridRules? Grid = null, bool RealTime = false, string? Acc = null)
{
    public string Name(string seat) => seat == "x" ? XName : OName;
    public string Accusative => Acc ?? Title;
}

/// <summary>
/// Ігровий стіл. Строго на двох: хто створив — за перших, хто сів другим — за других, решта дивиться.
/// Один нік тримає одне місце, і то лише за одним столом. Столи живуть у пам'яті: партія це п'ять хвилин
/// на перекур, а не те, що варто переживати рестарт сервера (на відміну від черги).
/// </summary>
public sealed class GameTable
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Яка гра за столом: "ttt", "c4" або "snake".</summary>
    public required string Game { get; init; }
    public required GameRules Rules { get; init; }
    /// <summary>Покрокові ігри: клітинки зліва направо, зверху вниз ("x", "o" або null).</summary>
    public string?[]? Cells { get; init; }
    /// <summary>Змійка: стан партії, який рухає SnakeEngine.</summary>
    public SnakeState? Snake { get; init; }
    public string? X { get; set; }
    public string? O { get; set; }
    /// <summary>Чий хід: "x" або "o".</summary>
    public string Turn { get; set; } = "x";
    /// <summary>"x", "o" чи "draw"; null поки грають.</summary>
    public string? Winner { get; set; }
    /// <summary>Клітинки виграшної лінії, щоб підсвітити.</summary>
    public int[]? Line { get; set; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public bool Full => X is not null && O is not null;
    public bool Has(string nick) => Seat(nick) is not null;
    public string? Seat(string nick) =>
        string.Equals(X, nick, StringComparison.OrdinalIgnoreCase) ? "x" :
        string.Equals(O, nick, StringComparison.OrdinalIgnoreCase) ? "o" : null;
    public string? Nick(string mark) => mark == "x" ? X : O;

    public void Reset()
    {
        if (Cells is not null) Array.Clear(Cells);
        Snake?.Reset();
        Turn = "x";
        Winner = null;
        Line = null;
    }
}

public sealed record GameTableDto(string Id, string Game, int Width, int Height, string?[]? Cells, string? X, string? O, string Turn, string? Winner, int[]? Line, SnakeFrame? Snake);

/// <summary>Результат ходу: Message бачить той, хто натиснув, Log (якщо є) іде в Журнал для всіх.</summary>
public sealed record GameResult(bool Ok, string Message, string? Log = null);

/// <summary>Столи для ігор.</summary>
public sealed class Games
{
    /// <summary>Більше столів за раз усе одно не роздивитись.</summary>
    public const int MaxTables = 6;

    /// <summary>Ігри, у які можна поставити стіл. Нова покрокова гра — рядок сюди і рядок у GAMES на фронті.</summary>
    static readonly Dictionary<string, GameRules> Known = new()
    {
        ["ttt"] = new("хрестики-нолики", "✕", "◯", new GridRules(3, 3, 3, false)),
        ["c4"] = new("чотири в ряд", "жовті", "зелені", new GridRules(7, 6, 4, true)),
        ["snake"] = new("змійка", "жовта", "зелена", RealTime: true, Acc: "змійку"),
    };

    /// <summary>Напрямки, у яких шукаємо ряд: вправо, вниз і дві діагоналі.</summary>
    static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (1, 1), (1, -1)];

    readonly object _lock = new();
    readonly List<GameTable> _tables = [];

    public List<GameTableDto> Snapshot()
    {
        lock (_lock) return _tables.Select(Dto).ToList();
    }

    static GameTableDto Dto(GameTable t) => new(
        t.Id, t.Game,
        t.Rules.Grid?.Width ?? SnakeState.W, t.Rules.Grid?.Height ?? SnakeState.H,
        t.Cells is null ? null : (string?[])t.Cells.Clone(),
        t.X, t.O, t.Turn, t.Winner, t.Line,
        t.Snake is null ? null : Frame(t));

    static SnakeFrame Frame(GameTable t)
    {
        var s = t.Snake!;
        return new(t.Id, [.. s.A], [.. s.B], s.Apple, s.WinsA, s.WinsB, s.StartIn, t.Winner);
    }

    public GameResult Create(string nick, string game)
    {
        if (!Named(nick, out var no)) return no;
        if (!Known.TryGetValue(game, out var rules)) return new(false, "Такої гри тут поки нема");
        lock (_lock)
        {
            if (_tables.FirstOrDefault(t => t.Has(nick)) is not null)
                return new(false, "Ти вже за столом. Встань, якщо хочеш новий");
            if (_tables.Count >= MaxTables)
                return new(false, "Столів уже задосить, дограйте ті, що є");
            var snake = rules.RealTime ? new SnakeState() : null;
            snake?.Reset();   // щоб стіл, який чекає на суперника, уже виглядав як поле, а не як порожнеча
            _tables.Add(new GameTable
            {
                Game = game,
                Rules = rules,
                Cells = rules.Grid is { } grid ? new string?[grid.Cells] : null,
                Snake = snake,
                X = nick,
            });
            return new(true, "Стіл готовий. Треба ще одного гравця");
        }
    }

    public GameResult Sit(string id, string nick)
    {
        if (!Named(nick, out var no)) return no;
        lock (_lock)
        {
            if (Find(id) is not { } t) return new(false, "Такого столу вже нема");
            if (t.Has(nick)) return new(false, "Ти вже за цим столом");
            if (_tables.Any(x => x.Has(nick))) return new(false, "Спершу встань з-за іншого столу");
            if (t.Full) return new(false, "Стіл на двох, місць уже нема");
            if (t.X is null) t.X = nick; else t.O = nick;
            t.Reset();
            if (t.Snake is { } s) (s.WinsA, s.WinsB) = (0, 0);   // новий склад — новий рахунок
            var start = t.Rules.RealTime ? "Зараз почнемо" : $"Починають {t.Rules.XName}";
            return new(true, $"Сів за {t.Rules.Name(t.Seat(nick)!)}. {start}",
                $"{t.X} і {t.O} сіли грати в {t.Rules.Accusative}");
        }
    }

    public GameResult Leave(string id, string nick)
    {
        lock (_lock)
        {
            if (Find(id) is not { } t) return new(false, "Такого столу вже нема");
            if (t.Seat(nick) is not { } seat) return new(false, "Ти й не сидів за цим столом");
            if (seat == "x") t.X = null; else t.O = null;
            t.Reset();
            if (t.X is null && t.O is null) _tables.Remove(t);
            return new(true, "Встав з-за столу");
        }
    }

    public GameResult Move(string id, string nick, int cell)
    {
        lock (_lock)
        {
            if (Find(id) is not { } t) return new(false, "Такого столу вже нема");
            if (t.Cells is null) return new(false, "За цим столом ходів не роблять");
            if (t.Seat(nick) is not { } seat) return new(false, "Ти за цим столом не граєш");
            if (!t.Full) return new(false, "Чекаємо на другого гравця");
            if (t.Winner is not null) return new(false, "Партію зіграно, тисни «Ще раз»");
            if (t.Turn != seat) return new(false, "Зараз не твій хід");
            var index = Place(t, cell);
            if (index < 0) return new(false, t.Rules.Grid!.Gravity ? "Ця колонка вже повна" : "Ця клітинка вже зайнята");

            t.Cells[index] = seat;
            var other = seat == "x" ? "o" : "x";
            if (WinLine(t, index, seat) is { } line)
            {
                t.Winner = seat;
                t.Line = line;
                // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
                return new(true, "Твоя взяла!", $"{t.Rules.Title}: {nick} {t.Rules.Name(seat)} 1:0 {t.Nick(other)} {t.Rules.Name(other)}");
            }
            if (t.Cells!.All(c => c is not null))
            {
                t.Winner = "draw";
                return new(true, "Нічия", $"{t.Rules.Title}: {t.X} {t.Rules.XName} і {t.O} {t.Rules.OName} зіграли внічию");
            }
            t.Turn = seat == "x" ? "o" : "x";
            return new(true, "");
        }
    }

    /// <summary>Ще одна партія тим самим складом; місця міняються, щоб починав інший.</summary>
    public GameResult Rematch(string id, string nick)
    {
        lock (_lock)
        {
            if (Find(id) is not { } t) return new(false, "Такого столу вже нема");
            if (!t.Has(nick)) return new(false, "Ти за цим столом не граєш");
            if (t.Winner is null) return new(false, "Партія ще не скінчилась");
            (t.X, t.O) = (t.O, t.X);
            if (t.Snake is { } s) (s.WinsA, s.WinsB) = (s.WinsB, s.WinsA);   // місця помінялись, рахунок їде за гравцями
            t.Reset();
            return new(true, t.Rules.RealTime ? "Нова партія. Готуйсь" : $"Нова партія. Починає {t.X}");
        }
    }

    /// <summary>Гравець пішов зі сторінки: звільняємо місця, порожні столи прибираємо.</summary>
    public bool DropPlayer(string nick)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var t in _tables.Where(t => t.Has(nick)).ToList())
            {
                if (t.Seat(nick) == "x") t.X = null; else t.O = null;
                t.Reset();
                if (t.X is null && t.O is null) _tables.Remove(t);
                changed = true;
            }
            return changed;
        }
    }

    /// <summary>Гравець крутить змійку. Помилки тут нікого не цікавлять: наступний тик усе одно все перемалює.</summary>
    public void Turn(string id, string nick, int dir)
    {
        lock (_lock)
        {
            if (Find(id) is not { Snake: { } s } t || t.Winner is not null) return;
            if (t.Seat(nick) is { } seat) s.Turn(seat, dir);
        }
    }

    /// <summary>Крок усіх живих партій у змійку. Викликає SnakeEngine раз на тик.</summary>
    public List<SnakeUpdate> TickSnakes()
    {
        lock (_lock)
        {
            var updates = new List<SnakeUpdate>();
            foreach (var t in _tables.Where(t => t.Snake is not null && t.Full && t.Winner is null))
            {
                var s = t.Snake!;
                if (s.StartIn > 0)
                {
                    s.StartIn--;
                    updates.Add(new(t.Id, Frame(t), null, false));
                    continue;
                }
                var (deadA, deadB) = s.Step();
                string? log = null;
                if (deadA || deadB)
                {
                    t.Winner = deadA && deadB ? "draw" : deadA ? "o" : "x";
                    if (t.Winner == "x") s.WinsA++;
                    else if (t.Winner == "o") s.WinsB++;
                    if (t.Winner == "draw")
                    {
                        log = $"{t.Rules.Title}: {t.X} {t.Rules.XName} і {t.O} {t.Rules.OName} врізались одночасно";
                    }
                    else
                    {
                        // Рахунок пишемо з боку переможця, щоб «2:1» читалось на його користь.
                        var lost = t.Winner == "x" ? "o" : "x";
                        var (won, lose) = t.Winner == "x" ? (s.WinsA, s.WinsB) : (s.WinsB, s.WinsA);
                        log = $"{t.Rules.Title}: {t.Nick(t.Winner)} {t.Rules.Name(t.Winner)} {won}:{lose} {t.Nick(lost)} {t.Rules.Name(lost)}";
                    }
                }
                updates.Add(new(t.Id, Frame(t), log, t.Winner is not null));
            }
            return updates;
        }
    }

    GameTable? Find(string id) => _tables.FirstOrDefault(t => t.Id == id);

    /// <summary>Куди насправді ляже хід: у грі з гравітацією cell — це колонка, інакше сама клітинка.
    /// Повертає -1, якщо туди не можна.</summary>
    static int Place(GameTable t, int cell)
    {
        var (w, h) = (t.Rules.Grid!.Width, t.Rules.Grid!.Height);
        if (!t.Rules.Grid!.Gravity) return cell >= 0 && cell < t.Cells!.Length && t.Cells[cell] is null ? cell : -1;
        if (cell < 0 || cell >= w) return -1;
        for (var row = h - 1; row >= 0; row--)
            if (t.Cells![row * w + cell] is null) return row * w + cell;
        return -1;
    }

    /// <summary>Ряд потрібної довжини через щойно поставлену клітинку, або null.</summary>
    static int[]? WinLine(GameTable t, int index, string seat)
    {
        var (w, h) = (t.Rules.Grid!.Width, t.Rules.Grid!.Height);
        var (x0, y0) = (index % w, index / w);
        foreach (var (dx, dy) in Dirs)
        {
            var line = new List<int> { index };
            foreach (var step in (int[])[1, -1])
                for (int x = x0 + dx * step, y = y0 + dy * step;
                     x >= 0 && x < w && y >= 0 && y < h && t.Cells![y * w + x] == seat;
                     x += dx * step, y += dy * step)
                    line.Add(y * w + x);
            if (line.Count >= t.Rules.Grid!.Need) return [.. line.Order()];
        }
        return null;
    }

    static bool Named(string nick, out GameResult no)
    {
        no = new(false, "Спершу скажи, як тебе кликати");
        return !string.IsNullOrWhiteSpace(nick) && nick != "гість";
    }
}
