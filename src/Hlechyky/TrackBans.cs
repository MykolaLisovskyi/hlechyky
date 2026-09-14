using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>Те, що бан-листу треба від ефіру. Окремим інтерфейсом, щоб бан перевірявся тестами без liquidsoap.</summary>
public interface IOnAir
{
    /// <summary>Id треку, що грає з черги чи від Глека; null — тиша або Spotify-резерв.</summary>
    string? TrackId { get; }

    /// <summary>Прибрати забанений трек із черги й з наступного в Глека, скіпнути, якщо грає.</summary>
    Task EvictAsync(string trackId);

    /// <summary>Рядок у Журнал.</summary>
    void Journal(string text);
}

/// <summary>
/// Бан-лист. Адмін банить і розбанює будь-що безкоштовно. Решта банить лише те, що зараз в ефірі, за
/// <see cref="EconomyOptions.BanPrice"/> черепків і викуповує будь-який трек із бану за
/// <see cref="EconomyOptions.UnbanPrice"/>; ціна 0 — за черепки не можна. Черепки назад не вертаються:
/// бан, який потім розбанили, своє вже відробив.
/// </summary>
public sealed class TrackBans(Db db, Economy economy, IOnAir air, IOptionsMonitor<EconomyOptions> opts, ILogger<TrackBans> log)
{
    // «чи вже в бані», списання і запис — одним шматком: інакше двоє, що натиснули разом, заплатили б обидва
    readonly object _gate = new();

    public int BanPrice => Math.Max(0, opts.CurrentValue.BanPrice);
    public int UnbanPrice => Math.Max(0, opts.CurrentValue.UnbanPrice);

    public List<Db.BanRow> List() => db.Bans();

    public int Balance(string nick) => economy.Balance(nick);

    public async Task<(bool Ok, string Message)> BanAsync(string trackId, string nick, bool isAdmin)
    {
        var track = db.GetTrack(trackId);
        if (track is null) return (false, "Не знаю такого треку");
        var price = isAdmin ? 0 : BanPrice;
        lock (_gate)
        {
            if (!isAdmin)
            {
                if (price == 0) return (false, "Бан за черепки вимкнено");
                if (VoiceService.IsVoice(trackId)) return (false, "Голосові не банять");
                if (air.TrackId != trackId) return (false, "За черепки банять лише те, що зараз в ефірі");
            }
            if (db.IsBanned(trackId)) return (false, "Уже в бані");
            if (!economy.TrySpend(nick, price, "ban:" + trackId)) return (false, NotEnough(nick, price));
            try { db.Ban(trackId, nick, price); }
            catch (Exception ex)
            {
                Refund(nick, price, trackId, ex);
                return (false, "Не вийшло записати бан" + (price > 0 ? ", черепки повернуто" : ""));
            }
        }
        air.Journal(price > 0 ? $"{nick} банить {track.Label} за {Shards(price)}" : $"{nick} банить {track.Label}");
        await air.EvictAsync(trackId);
        // скільки списали, людина й так побачить тостом гаманця («−100 черепків: бан треку»)
        return (true, "Забанено");
    }

    public (bool Ok, string Message) Unban(string trackId, string nick, bool isAdmin)
    {
        var price = isAdmin ? 0 : UnbanPrice;
        lock (_gate)
        {
            if (!db.IsBanned(trackId)) return (false, "Цього треку нема в бані");
            if (!isAdmin && price == 0) return (false, "Викуп за черепки вимкнено");
            if (!economy.TrySpend(nick, price, "unban:" + trackId)) return (false, NotEnough(nick, price));
            try { db.Unban(trackId); }
            catch (Exception ex)
            {
                Refund(nick, price, trackId, ex);
                return (false, "Не вийшло зняти бан" + (price > 0 ? ", черепки повернуто" : ""));
            }
        }
        var label = db.GetTrack(trackId)?.Label ?? trackId;
        air.Journal(price > 0 ? $"{nick} викуповує {label} з бану за {Shards(price)}" : $"{nick} розбанює {label}");
        return (true, price > 0 ? "Викуплено з бану" : "Розбанено");
    }

    string NotEnough(string nick, int price) => $"Треба {Shards(price)}, а в тебе {economy.Balance(nick)}";

    void Refund(string nick, int price, string trackId, Exception ex)
    {
        log.LogWarning(ex, "бан/розбан {Track} від {Nick} не записався", trackId, nick);
        economy.Grant(nick, price, "ban-refund:" + trackId);
    }

    static string Shards(int n) => $"{n} {Economy.Shards(n)}";
}
