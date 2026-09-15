using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky.Mcp;

/// <summary>
/// Балачки для агента. Робить рівно те саме, що <see cref="RadioHub.SendChat"/> для браузера: пише в базу,
/// розсилає всім і дає Глеку почути. Інакше денна суперечка в мафії йшла б у двох різних чатах.
/// </summary>
public sealed class VillageChat(Db db, IHubContext<RadioHub> hub, DjBrain brain, IClock clock, ILogger<VillageChat> log) : IAgentChat
{
    /// <summary>Та сама пауза між командами, що й у хабі: /кубик від бота не має сипатись частіше, ніж від людини.</summary>
    static readonly TimeSpan CommandGap = TimeSpan.FromMilliseconds(1200);
    readonly Dictionary<string, DateTimeOffset> _lastCommand = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AgentChatLine> Recent(int limit)
    {
        try
        {
            return [.. db.RecentChat(limit, Math.Min(limit, 60)).Select(m => new AgentChatLine(m.Id, m.Nick, m.Text, m.Kind, m.At))];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "не вдалось прочитати Балачки для агента");
            return [];
        }
    }

    public long LastId()
    {
        try
        {
            var tail = db.RecentChat(1, 1);
            return tail.Count == 0 ? 0 : tail.Max(m => m.Id);
        }
        catch (Exception) { return 0; }
    }

    public ChatSendResult Send(string nick, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return new ChatSendResult(false, "Порожнє нікому не цікаво", null);
        // Обрізаємо по символах, але не посеред смайла — так само, як це робить хаб.
        if (text.Length > 500) text = text[..(char.IsHighSurrogate(text[499]) ? 499 : 500)];

        var (said, kind) = (text, "chat");
        if (text.StartsWith('/'))
        {
            var now = clock.UtcNow;
            lock (_lastCommand)
            {
                if (_lastCommand.TryGetValue(nick, out var last) && now - last < CommandGap)
                    return new ChatSendResult(false, Games.Say.TooFast, null);
                var r = ChatCommands.Run(text);
                if (r.Error is not null) return new ChatSendResult(false, r.Error, null);
                _lastCommand[nick] = now;
                (said, kind) = (r.Text!, r.Kind);
            }
        }

        var message = db.AddChat(nick, said, kind);
        var line = new AgentChatLine(message.Id, message.Nick, message.Text, message.Kind, message.At);
        // Розсилка нікому не винна: як і в хабі, відповідь агентові від неї не залежить.
        _ = hub.Clients.All.SendAsync("chat", message).ContinueWith(
            t => log.LogWarning(t.Exception, "не вдалось розіслати репліку агента"),
            TaskContinuationOptions.OnlyOnFaulted);
        if (kind == "chat") brain.OnChat(nick, said);
        return new ChatSendResult(true, "", line);
    }
}

/// <summary>Розсилка того, що повернув каркас. Окремим класом, бо в тестах Broadcaster не піднімається.</summary>
public sealed class VillageFlush(Broadcaster broadcaster) : IAgentFlush
{
    public Task FlushAsync(Outbox outbox) => broadcaster.FlushAsync(outbox);
}
