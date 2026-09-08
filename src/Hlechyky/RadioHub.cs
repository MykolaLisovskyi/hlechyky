using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky;

public sealed class RadioHub(Presence presence, RadioEngine engine, Db db, Games games, IOptionsMonitor<SiteOptions> site) : Hub
{
    static readonly HashSet<string> Emojis = ["🔥", "❤️", "😂", "🕺", "🤘", "😴", "🤮", "🫠"];
    static readonly ConcurrentDictionary<string, DateTime> LastReaction = new();
    static readonly ConcurrentDictionary<string, DateTime> LastCommand = new();

    public override async Task OnConnectedAsync()
    {
        var nick = Auth.SanitizeNick(Context.GetHttpContext()?.Request.Query["nick"].ToString());
        presence.Set(Context.ConnectionId, nick);
        await Clients.Caller.SendAsync("chatHistory", db.RecentChat(100, 120));
        await Clients.Caller.SendAsync("games", games.Snapshot());
        await Clients.All.SendAsync("state", engine.Snapshot());
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var gone = presence.Get(Context.ConnectionId);
        presence.Remove(Context.ConnectionId);
        await Clients.All.SendAsync("state", engine.Snapshot());
        await FreeSeatsAsync(gone);
    }

    /// <summary>Повертає текст помилки тому, хто писав (нікому більше), або null, якщо все гаразд.</summary>
    public async Task<string?> SendChat(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        if (text.Length > 500) text = text[..500];
        var nick = Nick();
        var (chatText, kind) = (text, "chat");
        if (text.StartsWith('/'))
        {
            var now = DateTime.UtcNow;
            if (LastCommand.TryGetValue(nick, out var last) && (now - last).TotalMilliseconds < 1200) return "Не так швидко";
            var r = ChatCommands.Run(text);
            if (r.Error is not null) return r.Error;   // на друкарську помилку паузу не вішаємо
            LastCommand[nick] = now;
            (chatText, kind) = (r.Text!, r.Kind);
        }
        await Clients.All.SendAsync("chat", db.AddChat(nick, chatText, kind));
        return null;
    }

    /// <summary>Emoji flying over the cover for everyone. Not persisted, lightly rate-limited per nick.</summary>
    public async Task React(string emoji)
    {
        if (!Emojis.Contains(emoji ?? "")) return;
        var nick = presence.Get(Context.ConnectionId) ?? "гість";
        var now = DateTime.UtcNow;
        if (LastReaction.TryGetValue(nick, out var last) && (now - last).TotalMilliseconds < 400) return;
        LastReaction[nick] = now;
        await Clients.All.SendAsync("reaction", new { nick, emoji });
    }

    public async Task SetNick(string nick)
    {
        var old = presence.Get(Context.ConnectionId);
        presence.Set(Context.ConnectionId, Auth.SanitizeNick(nick));
        await Clients.All.SendAsync("state", engine.Snapshot());
        await FreeSeatsAsync(old);
    }

    // ---------- ігри ----------

    public Task<GameResult> CreateTable(string game) => ApplyAsync(games.Create(Nick(), game ?? ""));
    public Task<GameResult> SitTable(string id) => ApplyAsync(games.Sit(id ?? "", Nick()));
    public Task<GameResult> LeaveTable(string id) => ApplyAsync(games.Leave(id ?? "", Nick()));
    public Task<GameResult> PlayMove(string id, int cell) => ApplyAsync(games.Move(id ?? "", Nick(), cell));
    public Task<GameResult> Rematch(string id) => ApplyAsync(games.Rematch(id ?? "", Nick()));

    /// <summary>Вдалий хід бачать усі; те, чим варто похвалитись, іде ще й у Журнал.</summary>
    async Task<GameResult> ApplyAsync(GameResult r)
    {
        if (!r.Ok) return r;
        await Clients.All.SendAsync("games", games.Snapshot());
        if (r.Log is not null) await Clients.All.SendAsync("chat", db.AddChat(site.CurrentValue.Name, r.Log, "system"));
        return r;
    }

    /// <summary>Пішов зі сторінки (або перейменувався) і більше ніде не онлайн — місце за столом звільняється.</summary>
    async Task FreeSeatsAsync(string? nick)
    {
        if (string.IsNullOrEmpty(nick) || presence.Online.Contains(nick, StringComparer.OrdinalIgnoreCase)) return;
        if (games.DropPlayer(nick)) await Clients.All.SendAsync("games", games.Snapshot());
    }

    string Nick() => presence.Get(Context.ConnectionId) ?? "гість";
}
