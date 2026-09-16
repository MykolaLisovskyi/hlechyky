using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games;

/// <summary>Кому летить повідомлення.</summary>
public abstract record SendTarget;
/// <summary>Усім підключеним.</summary>
public sealed record ToAll : SendTarget;
/// <summary>Групі SignalR (<c>room:&lt;id&gt;</c>).</summary>
public sealed record ToGroup(string Name) : SendTarget;
/// <summary>Групі, крім названих з'єднань — ті вже отримали свій, персональний вид.</summary>
public sealed record ToGroupExcept(string Name, IReadOnlyList<string> Except) : SendTarget;
/// <summary>Переліченим з'єднанням.</summary>
public sealed record ToConnections(IReadOnlyList<string> Ids) : SendTarget;

/// <summary>Одне готове відправлення: кому, яка подія, що в тілі.</summary>
public sealed record Send(SendTarget Target, string Event, object? Payload);

/// <summary>
/// Outbox → SignalR. Уся розкладка (кому який вид, що склеїти, що викинути) робиться чистою функцією
/// <see cref="Plan"/>, і саме її перевіряють тести; <see cref="FlushAsync"/> лише відправляє готове.
/// Заразом Broadcaster — це <see cref="IOutbox"/> для сервісів: вони кладуть свої повідомлення в чергу
/// з будь-якого потоку, а зливає її TickEngine на найближчому колі.
/// </summary>
public sealed class Broadcaster(
    IHubContext<RadioHub> hub,
    Rooms rooms,
    Presence presence,
    Db db,
    RadioEngine engine,
    IOptionsMonitor<SiteOptions> site,
    ILogger<Broadcaster> log) : IOutbox
{
    readonly ConcurrentQueue<Outgoing> _posted = new();

    /// <summary>Група глядачів кімнати.</summary>
    public static string RoomGroup(string id) => "room:" + id;

    /// <summary>Сервіси (WP1) кладуть сюди своє з інших потоків; зливає TickEngine.</summary>
    public void Post(Outgoing message) => _posted.Enqueue(message);

    /// <summary>Розіслати. Разом із чергою від сервісів, щоб нічого не зависало до наступного тика.</summary>
    public async Task FlushAsync(IEnumerable<Outgoing> messages, CancellationToken ct = default)
    {
        var all = Drain(messages);
        if (all.Count == 0) return;

        // Дедлайн на всю пачку: один клієнт із забитим каналом (телефон у ліфті) не має тримати цикл тика.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));

        List<Send> sends;
        try
        {
            sends = Plan(all, rooms.Snapshot, rooms.ViewsFor, presence.Get, presence.ConnectionsOf,
                (text, roomId) => db.AddChat(site.CurrentValue.Name, text, "system", roomId));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "не вдалось скласти розсилку");
            return;
        }

        foreach (var send in sends)
        {
            try { await Dispatch(send, deadline.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { log.LogWarning("розсилка {Event} не вклалась у дедлайн", send.Event); }
            catch (Exception ex) { log.LogWarning(ex, "не відправилось {Event}", send.Event); }
        }

        foreach (var say in all.OfType<DjSays>())
        {
            try { await engine.SayAsync(say.Text); }
            catch (Exception ex) { log.LogWarning(ex, "Глек не сказав своє слово"); }
        }
    }

    /// <summary>
    /// Пачка на відправку: те, що дала дія, плюс усе, що сервіси (WP1) поклали через <see cref="Post"/> з
    /// інших потоків. Черга спорожняється навіть тоді, коли своїх повідомлень нема.
    /// </summary>
    public List<Outgoing> Drain(IEnumerable<Outgoing> messages)
    {
        var all = new List<Outgoing>(messages);
        while (_posted.TryDequeue(out var extra)) all.Add(extra);
        return all;
    }

    Task Dispatch(Send send, CancellationToken ct) => send.Target switch
    {
        ToAll => hub.Clients.All.SendAsync(send.Event, send.Payload, ct),
        ToGroup g => hub.Clients.Group(g.Name).SendAsync(send.Event, send.Payload, ct),
        ToGroupExcept g => g.Except.Count == 0
            ? hub.Clients.Group(g.Name).SendAsync(send.Event, send.Payload, ct)
            : hub.Clients.GroupExcept(g.Name, g.Except).SendAsync(send.Event, send.Payload, ct),
        ToConnections c => c.Ids.Count == 0 ? Task.CompletedTask : hub.Clients.Clients(c.Ids).SendAsync(send.Event, send.Payload, ct),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Розкладка без жодного SignalR — саме тому її можна перевірити тестом. Склеює повторення: кілька
    /// <see cref="LobbyChanged"/> в одному Outbox стають одним, кілька <see cref="RoomViews"/> однієї кімнати —
    /// теж (лишається останнє: воно й так рахується від свіжого стану), а з кадрів однієї кімнати лишається
    /// останній. <see cref="DjSays"/> сюди не потрапляє: його вміє лише RadioEngine.
    /// </summary>
    public static List<Send> Plan(
        IReadOnlyList<Outgoing> messages,
        Func<List<RoomSummary>> snapshot,
        Func<string, RoomBroadcast?> viewsFor,
        Func<string, string?> nickOf,
        Func<string, IReadOnlyList<string>> connectionsOf,
        Func<string, string?, object> journal)
    {
        var keep = Coalesce(messages);
        var sends = new List<Send>();
        Send? lobby = null;
        foreach (var index in keep)
        {
            switch (messages[index])
            {
                case LobbyChanged:
                    // Знімок лобі рахується від живого стану, а не від місця в черзі, тож і летить першим
                    // (нижче). Інакше рядок «Оля і Петро сіли грати» доходив би до браузера раніше за
                    // новину, що за тим столом уже нема місця, і кнопка на ньому кликала б сідати.
                    lobby = new Send(new ToAll(), "rooms", snapshot());
                    break;
                case RoomViews views:
                    if (viewsFor(views.RoomId) is { } b) sends.AddRange(ViewSends(b, nickOf));
                    break;
                case RoomFrame frame:
                    sends.Add(new Send(new ToGroup(RoomGroup(frame.RoomId)), "frame", new { id = frame.RoomId, f = frame.Frame }));
                    break;
                case Journal line:
                    // Рядок Журналу дорогою в чат заходить у SQLite. Впала база — це біда одного рядка,
                    // а не всієї пачки: види, кадри й лобі мають полетіти однаково.
                    try { sends.Add(new Send(new ToAll(), "chat", journal(line.Text, line.RoomId))); }
                    catch (Exception) { }
                    break;
                case Invite invite:
                    sends.Add(new Send(new ToAll(), "invite", new { roomId = invite.RoomId, by = invite.By, text = invite.Text }));
                    break;
                case WalletChanged w:
                    sends.Add(new Send(new ToConnections(connectionsOf(w.Nick)), "wallet",
                        new { balance = w.Balance, delta = w.Delta, reason = w.Reason, text = w.Text }));
                    break;
                case AchievementUnlocked a:
                    sends.Add(new Send(new ToConnections(connectionsOf(a.Nick)), "achievement",
                        new { key = a.Key, title = a.Title, text = a.Text, icon = a.Icon, reward = a.Reward }));
                    break;
                case ToastFor t:
                    sends.Add(new Send(new ToConnections(connectionsOf(t.Nick)), "toast", new { text = t.Text, kind = t.Kind }));
                    break;
            }
        }
        if (lobby is not null) sends.Insert(0, lobby);
        return sends;
    }

    /// <summary>Індекси повідомлень, які варто відправити: повторення лобі, видів і кадрів згортаються в останнє.</summary>
    static List<int> Coalesce(IReadOnlyList<Outgoing> messages)
    {
        var lobby = -1;
        var views = new Dictionary<string, int>(StringComparer.Ordinal);
        var frames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
            switch (messages[i])
            {
                case LobbyChanged: lobby = i; break;
                case RoomViews v: views[v.RoomId] = i; break;
                case RoomFrame f: frames[f.RoomId] = i; break;
            }

        var keep = new List<int>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var drop = messages[i] switch
            {
                LobbyChanged => i != lobby,
                RoomViews v => views[v.RoomId] != i,
                RoomFrame f => frames[f.RoomId] != i,
                DjSays => true,
                _ => false,
            };
            if (!drop) keep.Add(i);
        }
        return keep;
    }

    /// <summary>
    /// Хто сидить — отримує свій вид на своє місце персонально (у Hidden-іграх він в інших і не такий);
    /// решта групи бачить вид глядача одним повідомленням. Гравець, який зараз не дивиться на кімнату,
    /// не отримує нічого — він на іншій вкладці, і це правильно.
    /// </summary>
    static IEnumerable<Send> ViewSends(RoomBroadcast b, Func<string, string?> nickOf)
    {
        var seated = new Dictionary<int, List<string>>();
        foreach (var conn in b.Watchers)
        {
            if (b.SeatOf(nickOf(conn)) is not { } seat) continue;
            if (!seated.TryGetValue(seat, out var list)) seated[seat] = list = [];
            list.Add(conn);
        }
        foreach (var (seat, conns) in seated)
            yield return new Send(new ToConnections(conns), "room",
                new RoomView(b.Summary, seat, b.SeatViews.TryGetValue(seat, out var v) ? v : b.WatcherView));

        var mine = seated.Values.SelectMany(x => x).ToList();
        yield return new Send(new ToGroupExcept(RoomGroup(b.RoomId), mine), "room", new RoomView(b.Summary, null, b.WatcherView));
    }
}
