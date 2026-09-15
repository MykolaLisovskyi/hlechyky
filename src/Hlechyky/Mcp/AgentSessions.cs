using System.Collections.Concurrent;
using System.Security.Cryptography;
using Hlechyky.Games;

namespace Hlechyky.Mcp;

/// <summary>
/// Сесія аі-агента: те саме, чим для браузера є вкладка. Живе, поки агент хоч раз на <see cref="AgentSessions.Ttl"/>
/// смикає якийсь інструмент; поки живе — нік вважається онлайн, і місце за столом нікуди не дінеться.
/// </summary>
public sealed class AgentSession
{
    public required string Id { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    /// <summary>Як агента звуть у селі. Порожній — ще не назвався, за стіл не пустять.</summary>
    public string Nick { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>Останній рядок Балачок, який агент уже бачив: далі йому віддають лише нове.</summary>
    public long SeenChatId { get; set; }
    /// <summary>Версія протоколу, про яку домовились на initialize.</summary>
    public string Protocol { get; set; } = "";
    public int Calls { get; set; }

    /// <summary>Псевдо-з'єднання для Presence: у списку слухачів агент має бути видимий, як і людина.</summary>
    public string ConnectionId => "mcp:" + Id;
}

/// <summary>
/// Усі живі сесії агентів. Окремо від <see cref="Presence"/> навмисно: presence знає про з'єднання, а тут
/// лежить те, що між викликами інструментів має пам'ятати сам агент (нік, докуди він дочитав Балачки).
/// </summary>
public sealed class AgentSessions(IClock clock)
{
    /// <summary>Скільки сесія живе без жодного виклику. Агент думає повільно, тож не скупимось.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    /// <summary>Скільки агентів пускаємо за раз: сайт відкритий, а столів усе одно дванадцять.</summary>
    public const int MaxSessions = 32;

    readonly ConcurrentDictionary<string, AgentSession> _live = new(StringComparer.Ordinal);

    public int Count => _live.Count;

    public IReadOnlyList<AgentSession> All => [.. _live.Values];

    /// <summary>Нова сесія; null — агентів уже забагато.</summary>
    public AgentSession? Open(string nick)
    {
        Sweep();
        if (_live.Count >= MaxSessions) return null;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var now = clock.UtcNow;
        var session = new AgentSession { Id = id, OpenedAt = now, LastSeen = now, Nick = nick };
        return _live.TryAdd(id, session) ? session : null;
    }

    public AgentSession? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_live.TryGetValue(id, out var s)) return null;
        if (clock.UtcNow - s.LastSeen > Ttl) { _live.TryRemove(id, out _); return null; }
        return s;
    }

    /// <summary>
    /// Жива сесія цього ніка. Потрібна клієнтам, які не повертають <c>Mcp-Session-Id</c>: без цього
    /// кожен їхній виклик відкривав би нову сесію, і стеля в <see cref="MaxSessions"/> вичерпалась би
    /// за тридцять два ходи. Один нік — один гравець у селі, тож ділити сесію тут чесно.
    /// </summary>
    public AgentSession? Find(string? nick)
    {
        if (string.IsNullOrEmpty(nick)) return null;
        AgentSession? best = null;
        foreach (var s in _live.Values)
        {
            if (!string.Equals(s.Nick, nick, StringComparison.OrdinalIgnoreCase)) continue;
            if (clock.UtcNow - s.LastSeen > Ttl) continue;
            if (best is null || s.LastSeen > best.LastSeen) best = s;
        }
        return best;
    }

    public void Touch(AgentSession session)
    {
        session.LastSeen = clock.UtcNow;
        session.Calls++;
    }

    public AgentSession? Close(string? id) =>
        !string.IsNullOrEmpty(id) && _live.TryRemove(id, out var s) ? s : null;

    /// <summary>Прибрати тих, хто вже не озивається. Повертає їхні ніки — їх треба зняти з присутності.</summary>
    public IReadOnlyList<AgentSession> Sweep()
    {
        var now = clock.UtcNow;
        var gone = new List<AgentSession>();
        foreach (var (id, s) in _live)
            if (now - s.LastSeen > Ttl && _live.TryRemove(id, out var removed)) gone.Add(removed);
        return gone;
    }
}

/// <summary>
/// Тримає агентів «онлайн», поки їхні сесії живі. Без цього прибирання каркаса (grace 20 с) звільняло б
/// місце щоразу, коли агент задумався довше, ніж людина встигає перезавантажити вкладку.
/// </summary>
public sealed class AgentPresence(AgentSessions sessions, Presence presence, Rooms rooms, IClock clock, ILogger<AgentPresence> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                foreach (var gone in sessions.Sweep())
                {
                    presence.Remove(gone.ConnectionId);
                    if (!string.IsNullOrEmpty(gone.Nick) && !presence.IsOnline(gone.Nick))
                        rooms.NoteOffline(gone.Nick, clock.UtcNow);
                    log.LogInformation("агент {Nick} пішов із села (сесія {Id})", gone.Nick, gone.Id);
                }
                foreach (var s in sessions.All)
                {
                    if (string.IsNullOrEmpty(s.Nick)) continue;
                    presence.Set(s.ConnectionId, s.Nick);
                    rooms.NoteOnline(s.Nick);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "не вдалось оновити присутність агентів");
            }
        }
    }
}
