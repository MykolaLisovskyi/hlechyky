using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Builds suggestions from what is on air plus an anchor — a track a person actually asked for.
/// YouTube Music "radio" of each seed is the backbone (relevance-ordered, every entry already has
/// a video id); Last.fm "similar tracks" boosts candidates both agree on and adds a few of its own.
/// Recently played, queued, banned and dismissed tracks are dropped, artists that just played are
/// damped, the room's own taste (known artists, the script it listens in) tilts the rest, then a
/// weighted random from the top keeps consecutive picks from being identical.
/// Без якоря це було б випадкове блукання: власний вибір Глека ставав би сідом для наступного, і за
/// ніч він доїжджав від Жадана до вікінг-метала. Якір раз у раз вертає його до того, що тут просили.
/// </summary>
public sealed class AutoDj(Db db, LastFmClient lastFm, YtMusicClient ytm, YtDlpService ytdlp, RoomTaste taste, IOptionsMonitor<AutoDjOptions> options, ILogger<AutoDj> log)
{
    public sealed record Pick(TrackInfo Track, string Reason);

    /// <param name="Seed">Трек в ефірі — під нього шукаємо схоже.</param>
    /// <param name="Anchor">Свіже людське замовлення; його радіо домішується, щоб Глек не втік у власний дрейф.</param>
    /// <param name="Bias">Наскільки тиснути смаком кімнати: 1 — в ефірі вибір самого Глека, менше — трек поставила людина.</param>
    public sealed record Seeds(TrackInfo? Seed, TrackInfo? Anchor = null, double Bias = 1);

    sealed class Cand
    {
        public required string Key { get; init; }
        public required string Artist { get; init; }
        public required string Title { get; init; }
        public double SeedScore, AnchorScore, Mult = 1;
        public double Score => (SeedScore + AnchorScore) * Mult;
        /// <summary>Кандидат прийшов радше від якоря, ніж від треку в ефірі — так і напишемо в поясненні.</summary>
        public bool FromAnchor => AnchorScore > SeedScore;
        public SearchResult? Yt;
        public HashSet<string> Sources { get; } = new();
    }

    AutoDjOptions O => options.CurrentValue;
    readonly Random _rng = new();

    /// <summary>Радіо якоря важить трохи менше за радіо того, що зараз в ефірі: потік не рветься, дрейф гальмується.</summary>
    const double AnchorWeight = 0.75;

    /// <summary>
    /// What to build on when nothing is on air: the last track that played, else a random liked one.
    /// Голосові пропускаємо — від чийогось «привіт усім» схожої музики не підбереш.
    /// </summary>
    public TrackInfo? FallbackSeed()
    {
        var recent = db.RecentDistinctTracks(5, excludeSkipped: false).FirstOrDefault(t => !VoiceService.IsVoice(t.Id));
        if (recent is not null) return recent;
        var liked = db.LikedTracks(50).Where(t => !VoiceService.IsVoice(t.Id)).ToList();
        return liked.Count > 0 ? liked[_rng.Next(liked.Count)] : null;
    }

    /// <summary>
    /// Своє, давно забуте: трек, який кімната вже ставила або лайкала, а в ефірі його не було вже
    /// добу. Нічого не коштує, зате гарантовано вертає Глека у свою колію.
    /// </summary>
    public Pick? ArchivePick(IReadOnlySet<string> excludeIds)
    {
        var banned = db.BannedIds();
        var pool = db.ArchiveTracks(DateTimeOffset.UtcNow.AddHours(-Math.Max(24, O.NoRepeatHours)), 40)
            .Where(t => !excludeIds.Contains(t.Id) && !banned.Contains(t.Id)
                        && (t.DurationSec == 0 || t.DurationSec <= O.MaxDurationSeconds))
            .ToList();
        if (pool.Count == 0) return null;
        var t = pool[_rng.Next(pool.Count)];
        log.LogInformation("auto-DJ archive pick: {Label}", t.Label);
        return new Pick(t, "з нашого архіву");
    }

    /// <param name="s">Сіди: що в ефірі, за що зачепитись і наскільки тиснути смаком кімнати.</param>
    public async Task<List<Pick>> PickManyAsync(Seeds s, IReadOnlySet<string> excludeIds, int count, CancellationToken ct)
    {
        var picks = new List<Pick>();
        if (count <= 0) return picks;

        var played = db.PlayedTrackIdsSince(DateTimeOffset.UtcNow.AddHours(-O.NoRepeatHours));
        var banned = db.BannedIds();
        var recentArtists = db.RecentArtists(3).Select(ArtistKey).ToHashSet();
        bool Blocked(string id) => excludeIds.Contains(id) || played.Contains(id) || banned.Contains(id);

        if (s.Seed is null && s.Anchor is null)
        {
            if (string.IsNullOrWhiteSpace(O.SeedQuery)) return picks;
            var found = await ytm.SearchSongsAsync(O.SeedQuery, 10, ct);
            foreach (var r in found.Where(r => !Blocked(r.Id) && Fits(r)).OrderBy(_ => _rng.Next()).Take(count))
                picks.Add(new Pick(ToTrack(r), $"стартовий сід «{O.SeedQuery}»"));
            return picks;
        }

        var seeds = new[] { s.Seed, s.Anchor }.OfType<TrackInfo>().ToList();
        var seedKeys = seeds.Select(t => KeyOf(t.Artist, t.Title)).ToHashSet();
        var seedArtists = seeds.Select(t => ArtistKey(t.Artist)).ToHashSet();
        var cands = new Dictionary<string, Cand>();
        Cand Get(string key, string artist, string title)
        {
            if (!cands.TryGetValue(key, out var c)) cands[key] = c = new Cand { Key = key, Artist = artist, Title = title };
            return c;
        }

        // Кожен сід дає своє радіо; від якоря воно важить менше, але саме воно й тримає Глека вдома.
        async Task CollectAsync(TrackInfo seed, double weight, bool isAnchor)
        {
            // 1) YouTube Music radio of the seed: the order is YouTube's own relevance
            var radio = new List<SearchResult>();
            if (seed.Id.Length == 11)
            {
                try { radio = await ytm.RadioAsync(seed.Id, 50, ct); }
                catch (Exception ex) { log.LogWarning(ex, "YTM radio failed for {Id}", seed.Id); }
                if (radio.Count == 0)
                {
                    try { radio = await ytdlp.FlatPlaylistAsync($"https://music.youtube.com/watch?v={seed.Id}&list=RDAMVM{seed.Id}", 50, ct); }
                    catch (Exception ex) { log.LogWarning(ex, "yt-dlp radio failed for {Id}", seed.Id); }
                }
            }
            var pos = 0;
            foreach (var r in radio)
            {
                if (r.Id == seed.Id) continue;
                var key = KeyOf(r.Artist, r.Title);
                if (seedKeys.Contains(key)) continue;
                var c = Get(key, r.Artist, r.Title);
                var add = weight * Math.Pow(0.97, pos++);
                if (isAnchor) c.AnchorScore += add; else c.SeedScore += add;
                c.Yt ??= r;
                c.Sources.Add(isAnchor ? "радіо якоря" : "YT Music");
            }

            // 2) Last.fm similar tracks: agreement with the radio is a strong signal, its own finds come with a lower weight
            if (!lastFm.Enabled) return;
            var sim = new List<SimilarTrack>();
            try
            {
                sim = await lastFm.SimilarTracksAsync(seed.Artist, seed.Title, 40, ct);
                if (sim.Count == 0)
                    foreach (var (ar, m) in await lastFm.SimilarArtistsAsync(YtMusicClient.FirstArtist(seed.Artist), 5, ct))
                        foreach (var t in await lastFm.ArtistTopTracksAsync(ar, 5, ct))
                            sim.Add(t with { Match = t.Match * m * 0.7 });
            }
            catch (Exception ex) { log.LogWarning(ex, "Last.fm similar failed for {Label}", seed.Label); }
            foreach (var si in sim)
            {
                var key = KeyOf(si.Artist, si.Title);
                if (seedKeys.Contains(key)) continue;
                var c = Get(key, si.Artist, si.Title);
                var add = weight * (c.Sources.Count > 0 ? 0.8 : 0.45) * si.Match;
                if (isAnchor) c.AnchorScore += add; else c.SeedScore += add;
                c.Sources.Add("Last.fm");
            }
        }

        if (s.Seed is not null) await CollectAsync(s.Seed, 1, isAnchor: false);
        if (s.Anchor is not null) await CollectAsync(s.Anchor, AnchorWeight, isAnchor: true);

        var pool = cands.Values.Where(c => c.Yt is null || (!Blocked(c.Yt.Id) && Fits(c.Yt))).ToList();
        foreach (var c in pool)
        {
            var a = ArtistKey(c.Artist);
            if (seedArtists.Contains(a)) c.Mult *= 0.5;      // same artist again is fine, just not all the time
            else if (recentArtists.Contains(a)) c.Mult *= 0.35;
            c.Mult *= taste.Multiplier(c.Artist, c.Title, s.Bias);
        }
        // YouTube's order is the point of a "radio"; a little jitter only keeps two fills from being identical
        var top = pool.OrderByDescending(c => c.Score * (0.88 + 0.12 * _rng.NextDouble())).Take(10 + count * 4).ToList();
        var profile = taste.Current;
        log.LogInformation("auto-DJ: seed {Seed}, anchor {Anchor}, room {Script}/{Lean:F2}, {Cands} candidates, top: {Top}",
            s.Seed?.Label ?? "—", s.Anchor?.Label ?? "—", profile.Script.Length > 0 ? profile.Script : "—", profile.Lean, cands.Count,
            string.Join(" | ", top.Take(5).Select(c => $"{c.Artist} - {c.Title} ({c.Score:F2})")));

        var chosen = new HashSet<string>();
        var chosenArtists = new HashSet<string>();
        for (var attempt = 0; attempt < 6 + count * 4 && top.Count > 0 && picks.Count < count; attempt++)
        {
            var c = top[0];
            top.RemoveAt(0);
            if (count > 1 && chosenArtists.Contains(ArtistKey(c.Artist))) continue; // vary artists across a batch
            var yt = c.Yt;
            if (yt is null)
            {
                try { yt = await ytm.ResolveAsync(c.Artist, c.Title, ct); }
                catch (Exception ex) { log.LogWarning(ex, "resolve failed for {A} - {T}", c.Artist, c.Title); }
            }
            if (yt is null || Blocked(yt.Id) || !Fits(yt) || !chosen.Add(yt.Id)) continue;
            chosenArtists.Add(ArtistKey(c.Artist));
            var from = c.FromAnchor && s.Anchor is not null ? s.Anchor : s.Seed ?? s.Anchor;
            log.LogInformation("auto-DJ pick: {Label} ({Sources}; ×{Mult:F2})", c.Artist + " - " + c.Title, string.Join("+", c.Sources), c.Mult);
            picks.Add(new Pick(ToTrack(yt), $"схоже на {from!.Label}"));
        }
        return picks;
    }

    bool Fits(SearchResult r) => r.DurationSec == 0 || r.DurationSec <= O.MaxDurationSeconds;

    public static string ArtistKey(string artist) => YtMusicClient.Norm(YtMusicClient.FirstArtist(artist));
    static string KeyOf(string artist, string title) => ArtistKey(artist) + "|" + YtMusicClient.Norm(title);

    public static TrackInfo ToTrack(SearchResult r) =>
        new(r.Id, r.Title, r.Artist, r.DurationSec, r.ThumbUrl, $"https://music.youtube.com/watch?v={r.Id}", r.Album);
}
