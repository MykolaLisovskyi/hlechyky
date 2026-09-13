using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Суддя якості: чи справжній це артист, а не контент-ферма. Популярність у самому YouTube тут не
/// допомагає — він такі ферми сам і розкручує (у «DannyHO» 4 млн слухачів на місяць, більше, ніж у SadSvit).
/// Тому питаємо тих, де рахують живих людей: Last.fm (слухачі) і MusicBrainz (базу ведуть руками).
/// Артист, якого кімната вже ставила чи лайкала, проходить без питань.
/// </summary>
public sealed class ArtistQuality(LastFmClient lastFm, MusicBrainzClient mb, RoomTaste taste, IOptionsMonitor<AutoDjOptions> options, ILogger<ArtistQuality> log)
{
    public enum Verdict
    {
        /// <summary>Уже звучав у кімнаті або в лайках.</summary>
        Known,
        /// <summary>Досить слухачів на Last.fm.</summary>
        Popular,
        /// <summary>Слухачів мало, але артист є в MusicBrainz — нішевий, проте справжній.</summary>
        Catalogued,
        /// <summary>Обидва джерела мовчать (мережа) — не караємо за чужі збої.</summary>
        Unknown,
        Rejected,
    }

    static readonly TimeSpan MemoTtl = TimeSpan.FromHours(6);
    readonly ConcurrentDictionary<string, (Verdict V, DateTime At)> _memo = new();

    public static bool Passes(Verdict v) => v != Verdict.Rejected;

    /// <param name="listeners">Слухачі на Last.fm: 0 — там такого нема, null — не вдалося спитати.</param>
    /// <param name="catalogued">Чи є в MusicBrainz; null — не питали або він не відповів.</param>
    public static Verdict Judge(bool known, int? listeners, bool? catalogued, int threshold)
    {
        if (known) return Verdict.Known;
        if (listeners >= threshold) return Verdict.Popular;
        if (catalogued == true) return Verdict.Catalogued;
        if (listeners is null && catalogued is null) return Verdict.Unknown;
        return Verdict.Rejected;
    }

    public async Task<Verdict> JudgeAsync(string artist, string title, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (taste.IsKnown(artist)) return Verdict.Known;
        var threshold = RoomTaste.ScriptOf(artist, title) == "cyr" ? o.MinListenersCyr : o.MinListeners;
        var memoKey = AutoDj.ArtistKey(artist) + "|" + threshold;
        if (_memo.TryGetValue(memoKey, out var m) && DateTime.UtcNow - m.At < MemoTtl) return m.V;

        var name = YtMusicClient.FirstArtist(artist);
        int? listeners = lastFm.Enabled ? await lastFm.ArtistListenersAsync(name, ct) : null;
        bool? catalogued = listeners >= threshold ? null : await mb.ArtistExistsAsync(name, ct);
        var v = Judge(false, listeners, catalogued, threshold);
        // «не знаю» від MusicBrainz пам'ятаємо недовго: за чверть години він, може, й відповість
        var final = listeners >= threshold || catalogued is not null;
        _memo[memoKey] = (v, final ? DateTime.UtcNow : DateTime.UtcNow - MemoTtl + TimeSpan.FromMinutes(15));
        log.LogInformation("quality {Artist}: {Verdict} (last.fm {Listeners}, musicbrainz {Mb})",
            name, v, listeners?.ToString() ?? "?", catalogued?.ToString() ?? "—");
        return v;
    }
}
