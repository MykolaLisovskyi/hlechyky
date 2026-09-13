using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Смак кімнати: артисти, яких тут уже ставили або лайкали, і яким письмом підписане те, що тут
/// слухають. Рахується тільки з людських замовлень — на власних виборах Дядько Глек учився б на
/// своєму ж дрейфі. Звідси ж беруться якорі: свіжі замовлення, від яких можна відштовхнутись,
/// коли він уже задовго крутить своє.
/// </summary>
public sealed class RoomTaste(Db db, IOptionsMonitor<AutoDjOptions> options)
{
    /// <param name="Artists">Нормалізовані ключі артистів, яких кімната знає.</param>
    /// <param name="Script">Переважне письмо — <c>cyr</c>, <c>lat</c> або порожньо, якщо ще нема з чого судити.</param>
    /// <param name="Lean">0 — кімната мішана, 1 — слухає виключно одне. Сила всіх множників нижче.</param>
    public sealed record Profile(HashSet<string> Artists, string Script, double Lean)
    {
        public static readonly Profile Empty = new([], "", 0);
    }

    /// <summary>Менше замовлень — і будь-який нахил це просто випадковість, а не смак.</summary>
    const int MinTracksForLean = 12;

    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    readonly object _lock = new();
    readonly Random _rng = new();
    readonly Queue<string> _lastAnchors = new();
    Profile _cached = Profile.Empty;
    DateTime _builtAt = DateTime.MinValue;

    public Profile Current
    {
        get
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _builtAt < Ttl) return _cached;
                _builtAt = DateTime.UtcNow;
                return _cached = Build();
            }
        }
    }

    Profile Build()
    {
        var artists = db.TasteArtists(500).Select(AutoDj.ArtistKey).Where(a => a.Length > 0).ToHashSet();
        var recent = db.RecentUserTracks(200);
        var counts = recent.Select(t => ScriptOf(t.Artist, t.Title)).Where(s => s.Length > 0)
            .GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var forced = options.CurrentValue.PreferScript?.Trim().ToLowerInvariant() ?? "";
        if (forced is "cyr" or "lat") return new Profile(artists, forced, 1);
        var total = counts.Values.Sum();
        if (total < MinTracksForLean) return new Profile(artists, "", 0);
        var top = counts.MaxBy(kv => kv.Value);
        // рівно навпіл — нахилу нема; що далі від половини, то впевненіше
        return new Profile(artists, top.Key, Math.Clamp((top.Value / (double)total - 0.5) * 2, 0, 1));
    }

    /// <summary>
    /// Яким письмом підписаний трек. Дивимось на слова від двох літер і лише на першого виконавця:
    /// YouTube Music з українською локаллю склеює їх через кириличне «і», і без цього будь-який
    /// «true viking, fijorddd і moonlighttt» рахувався б своїм.
    /// </summary>
    public static string ScriptOf(string artist, string title)
    {
        bool cyr = false, lat = false;
        foreach (var word in (YtMusicClient.FirstArtist(artist) + " " + title).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = word.Where(char.IsLetter).ToArray();
            if (letters.Length < 2) continue;
            if (letters.Any(c => c is >= 'Ѐ' and <= 'ӿ')) cyr = true;
            else if (letters.Any(char.IsAsciiLetter)) lat = true;
        }
        return cyr ? "cyr" : lat ? "lat" : "";
    }

    /// <summary>Артист уже звучав у кімнаті з людського замовлення або є в лайках.</summary>
    public bool IsKnown(string artist) => Current.Artists.Contains(AutoDj.ArtistKey(artist));

    /// <summary>
    /// Наскільки підняти чи притиснути кандидата під смак кімнати.
    /// </summary>
    /// <param name="bias">
    /// Сила тиску: 1 — коли в ефірі вибір самого Глека (тоді й тягнути назад найпотрібніше),
    /// менше — коли трек поставила людина: її вибір сам по собі теж заява про смак.
    /// </param>
    public double Multiplier(string artist, string title, double bias)
    {
        var k = Math.Clamp(bias, 0, 1) * Math.Clamp(options.CurrentValue.TasteBias, 0, 2);
        if (k <= 0) return 1;
        var p = Current;
        var m = 1.0;
        if (p.Artists.Contains(AutoDj.ArtistKey(artist))) m *= 1 + 0.7 * k;
        if (p.Script.Length > 0 && p.Lean > 0)
        {
            var script = ScriptOf(artist, title);
            if (script == p.Script) m *= 1 + 0.35 * p.Lean * k;
            else if (script.Length > 0) m *= 1 - 0.6 * p.Lean * k;
        }
        return m;
    }

    /// <summary>
    /// Якір проти дрейфу: свіже людське замовлення (а як таких нема — щось лайкнуте), від якого
    /// Глек будує поради, коли в ефірі вже його власний вибір. Свіжіше важить більше, але й старіше
    /// має шанс, і кілька останніх якорів поспіль не повторюються.
    /// </summary>
    public TrackInfo? Anchor(IReadOnlySet<string> exclude)
    {
        bool Ok(TrackInfo t) => !exclude.Contains(t.Id) && !VoiceService.IsVoice(t.Id);
        var pool = db.RecentUserTracks(15).Where(Ok).ToList();
        if (pool.Count == 0) pool = db.LikedTracks(20).Where(Ok).ToList();
        if (pool.Count == 0) return null;
        lock (_lock)
        {
            var fresh = pool.Where(t => !_lastAnchors.Contains(t.Id)).ToList();
            if (fresh.Count > 0) pool = fresh;
            var weights = pool.Select((_, i) => 1.0 / (i + 2)).ToList();
            var roll = _rng.NextDouble() * weights.Sum();
            var pick = pool[^1];
            for (var i = 0; i < pool.Count; i++)
            {
                roll -= weights[i];
                if (roll > 0) continue;
                pick = pool[i];
                break;
            }
            _lastAnchors.Enqueue(pick.Id);
            while (_lastAnchors.Count > 4) _lastAnchors.Dequeue();
            return pick;
        }
    }
}
