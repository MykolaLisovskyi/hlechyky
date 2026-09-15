using Hlechyky.Games;
using Hlechyky.Mcp;

namespace Hlechyky.Tests.Support;

/// <summary>Балачки в пам'яті: та сама стрічка, що й у проді, тільки без бази й без SignalR.</summary>
public sealed class FakeChat : IAgentChat
{
    readonly List<AgentChatLine> _lines = [];
    long _next = 1;

    public IReadOnlyList<AgentChatLine> Recent(int limit) =>
        [.. _lines.TakeLast(limit)];

    public long LastId() => _lines.Count == 0 ? 0 : _lines[^1].Id;

    public ChatSendResult Send(string nick, string text)
    {
        var line = new AgentChatLine(_next++, nick, text, "chat", DateTimeOffset.UtcNow);
        _lines.Add(line);
        return new ChatSendResult(true, "", line);
    }
}

/// <summary>
/// Село, у якому всі гравці — агенти: кімнати каркаса, інструменти MCP і по сесії на кожного бота.
/// Це той самий шлях, яким ходить справжній агент, тільки без HTTP (його перевіряє <c>McpServerTests</c>).
/// </summary>
public sealed class VillageHarness
{
    public VillageHarness(int seed = 1)
    {
        Registry = RoomHarness.NewRegistry();
        Rooms = new Rooms(Registry, Clock, Events, Stakes, Store, RoomHarness.Empty()) { SeedOverride = seed };
        Sessions = new AgentSessions(Clock);
        Tools = new AgentTools(Rooms, Registry, Chat, new NoFlush());
    }

    public FakeClock Clock { get; } = new();
    public GameEvents Events { get; } = new();
    public FakeStakes Stakes { get; } = new();
    public FakeStore Store { get; } = new();
    public FakeChat Chat { get; } = new();
    public Registry Registry { get; }
    public Rooms Rooms { get; }
    public AgentSessions Sessions { get; }
    public AgentTools Tools { get; }

    /// <summary>Новий агент із іменем — рівно те, що робить клієнт MCP на initialize + set_nick.</summary>
    public async Task<AgentSession> Agent(string nick)
    {
        var s = Sessions.Open("") ?? throw new InvalidOperationException("сесію не відкрито");
        await Tools.SetNick(s, nick);
        return s;
    }

    /// <summary>Стільки тиків, скільки просять: годинник іде рівно на секунду, як у проді.</summary>
    public void Tick(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Clock.AdvanceMs(1000);
            foreach (var room in Rooms.TickDue(Clock.UtcNow)) Rooms.Tick(room);
        }
    }

    /// <summary>Тикати, доки не справдиться умова. Ліміт — щоб зациклений тест падав, а не висів.</summary>
    public void Until(Func<bool> done, int limit = 600)
    {
        for (var i = 0; i < limit && !done(); i++) Tick();
        Assert.True(done(), "не дочекались того, чого чекав тест");
    }
}
