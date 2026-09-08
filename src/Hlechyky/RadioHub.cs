using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky;

public sealed class RadioHub(Presence presence, RadioEngine engine, Db db) : Hub
{
    static readonly HashSet<string> Emojis = ["🔥", "❤️", "😂", "🕺", "🤘", "😴", "🤮", "🫠"];
    static readonly ConcurrentDictionary<string, DateTime> LastReaction = new();

    public override async Task OnConnectedAsync()
    {
        var nick = Auth.SanitizeNick(Context.GetHttpContext()?.Request.Query["nick"].ToString());
        presence.Set(Context.ConnectionId, nick);
        await Clients.Caller.SendAsync("chatHistory", db.RecentChat(100, 120));
        await Clients.All.SendAsync("state", engine.Snapshot());
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Remove(Context.ConnectionId);
        await Clients.All.SendAsync("state", engine.Snapshot());
    }

    public async Task SendChat(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 500) text = text[..500];
        var nick = presence.Get(Context.ConnectionId) ?? "гість";
        var msg = db.AddChat(nick, text, "chat");
        await Clients.All.SendAsync("chat", msg);
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
        presence.Set(Context.ConnectionId, Auth.SanitizeNick(nick));
        await Clients.All.SendAsync("state", engine.Snapshot());
    }
}
