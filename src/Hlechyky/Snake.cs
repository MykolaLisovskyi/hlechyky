using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Партія в змійку на двох. На відміну від хрестиків, вона живе не від кліку до кліку, а від тика:
/// сервер рухає обох, він же вирішує, хто в що врізався, тож підкрутити з консолі нічого не вийде.
/// Поле зі стінами: виїхав за край — програв.
/// </summary>
public sealed class SnakeState
{
    public const int W = 26, H = 18;
    public const int TickMs = 120;
    /// <summary>Скільки тиків «готуйсь» перед стартом (десь три секунди).</summary>
    public const int StartTicks = 25;
    const int StartLen = 3;

    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>Клітинки змійок, голова перша.</summary>
    public List<int> A { get; } = [];
    public List<int> B { get; } = [];
    public int DirA { get; set; }
    public int DirB { get; set; }
    public int Apple { get; set; }
    /// <summary>Рахунок за столом; переживає «Ще раз», бо цікаво грати до трьох.</summary>
    public int WinsA { get; set; }
    public int WinsB { get; set; }
    public int StartIn { get; set; } = StartTicks;

    static int Cell(int x, int y) => y * W + x;

    /// <summary>
    /// Нова партія: змійки по різних краях і на різних рядах — щоб ті, хто задумався на старті,
    /// не влетіли одне в одного лоб у лоб через десять тиків. Яблуко посередині.
    /// </summary>
    public void Reset()
    {
        A.Clear();
        B.Clear();
        var (yA, yB) = (H / 2 - 3, H / 2 + 3);
        for (var i = 0; i < StartLen; i++) A.Add(Cell(StartLen - i, yA));          // голова праворуч від хвоста
        for (var i = 0; i < StartLen; i++) B.Add(Cell(W - 1 - StartLen + i, yB));  // дзеркально
        DirA = 0;
        DirB = 2;
        Apple = Cell(W / 2, H / 2);
        StartIn = StartTicks;
    }

    /// <summary>Гравець просить повернути. Розворот на 180° ігноруємо — це була б миттєва смерть.</summary>
    public void Turn(string seat, int dir)
    {
        if (dir is < 0 or > 3) return;
        var cur = seat == "x" ? DirA : DirB;
        if ((dir + 2) % 4 == cur) return;
        if (seat == "x") DirA = dir; else DirB = dir;
    }

    /// <summary>Один крок обох змійок. Повертає, хто цього тика загинув.</summary>
    public (bool DeadA, bool DeadB) Step()
    {
        var (nextA, okA) = Ahead(A[0], DirA);
        var (nextB, okB) = Ahead(B[0], DirB);
        var growA = okA && nextA == Apple;
        var growB = okB && nextB == Apple;

        // Хвіст звільняє клітинку того ж тика, коли голова рушила — якщо змійка не росте.
        var bodyA = A.Take(A.Count - (growA ? 0 : 1)).ToHashSet();
        var bodyB = B.Take(B.Count - (growB ? 0 : 1)).ToHashSet();

        var deadA = !okA || bodyA.Contains(nextA) || bodyB.Contains(nextA) || (okB && nextA == nextB);
        var deadB = !okB || bodyB.Contains(nextB) || bodyA.Contains(nextB) || (okA && nextA == nextB);

        if (okA) { A.Insert(0, nextA); if (!growA) A.RemoveAt(A.Count - 1); }
        if (okB) { B.Insert(0, nextB); if (!growB) B.RemoveAt(B.Count - 1); }
        if (growA || growB) PlaceApple();
        return (deadA, deadB);
    }

    /// <summary>Куди дивиться голова; ok = false, якщо це вже стіна.</summary>
    static (int Cell, bool Ok) Ahead(int head, int dir)
    {
        var (dx, dy) = Deltas[dir];
        var (x, y) = (head % W + dx, head / W + dy);
        return x < 0 || x >= W || y < 0 || y >= H ? (head, false) : (Cell(x, y), true);
    }

    void PlaceApple()
    {
        var busy = A.Concat(B).ToHashSet();
        if (busy.Count >= W * H) return;
        int cell;
        do { cell = Random.Shared.Next(W * H); } while (busy.Contains(cell));
        Apple = cell;
    }
}

/// <summary>Кадр партії для браузера: тіла змійок, яблуко, рахунок і скільки лишилось до старту.</summary>
public sealed record SnakeFrame(string Id, int[] A, int[] B, int Apple, int WinsA, int WinsB, int StartIn, string? Winner);

/// <summary>Що змінилось за тик: кадр усім за столом, рядок у Журнал і, коли партія скінчилась, оновлення лобі.</summary>
public sealed record SnakeUpdate(string Id, SnakeFrame Frame, string? Log, bool LobbyChanged);

/// <summary>
/// Годинник для змійки: раз на TickMs рухає всі активні столи і шле кадр лише тим, хто на цей стіл дивиться
/// (група SignalR), щоб решта радіо не отримувала десять повідомлень на секунду ні за що.
/// </summary>
public sealed class SnakeEngine(Games games, IHubContext<RadioHub> hub, Db db, IOptionsMonitor<SiteOptions> site, ILogger<SnakeEngine> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SnakeState.TickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var updates = games.TickSnakes();
                foreach (var u in updates)
                {
                    await hub.Clients.Group(RadioHub.TableGroup(u.Id)).SendAsync("snake", u.Frame, ct);
                    if (u.Log is not null)
                        await hub.Clients.All.SendAsync("chat", db.AddChat(site.CurrentValue.Name, u.Log, "system"), ct);
                }
                if (updates.Any(u => u.LobbyChanged))
                    await hub.Clients.All.SendAsync("games", games.Snapshot(), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "snake tick failed"); }
        }
    }
}
