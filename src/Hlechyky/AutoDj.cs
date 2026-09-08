using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Builds suggestions from ONE seed: the track on air right now. YouTube Music "radio" of that
/// track is the backbone (relevance-ordered, every entry already has a video id); Last.fm
/// "similar tracks" boosts candidates both agree on and adds a few of its own. Recently played,
/// queued, banned and dismissed tracks are dropped, artists that just played are damped, then a
/// weighted random from the top keeps consecutive picks from being identical.
/// When nothing is on air the seed falls back to the last played track, a liked one, or SeedQuery.
/// </summary>
public sealed class AutoDj(Db db, LastFmClient lastFm, YtMusicClient ytm, YtDlpService ytdlp, IOptionsMonitor<AutoDjOptions> options, ILogger<AutoDj> log)
{
    public sealed record Pick(TrackInfo Track, string Reason);

    sealed class Cand
    {
        public required string Key { get; init; }
        public required string Artist { get; init; }
        public required string Title { get; init; }
        public double Score;
        public SearchResult? Yt;
        public HashSet<string> Sources { get; } = new();
    }

    AutoDjOptions O => options.CurrentValue;
    readonly Random _rng = new();

    /// <summary>What to build on when nothing is on air: the last track that played, else a random liked one.</summary>
    public TrackInfo? FallbackSeed()
    {
        var recent = db.RecentDistinctTracks(1, excludeSkipped: false);
        if (recent.Count > 0) return recent[0];
        var liked = db.LikedTracks(50);
        return liked.Count > 0 ? liked[_rng.Next(liked.Count)] : null;
    }

    /// <param name="seed">The track the picks should be similar to; null means "no history at all" (SeedQuery is used).</param>
    public async Task<List<Pick>> PickManyAsync(TrackInfo? seed, IReadOnlySet<string> excludeIds, int count, CancellationToken ct)
    {
        var picks = new List<Pick>();
        if (count <= 0) return picks;

        var played = db.PlayedTrackIdsSince(DateTimeOffset.UtcNow.AddHours(-O.NoRepeatHours));
        var banned = db.BannedIds();
        var recentArtists = db.RecentArtists(3).Select(ArtistKey).ToHashSet();
        bool Blocked(string id) => excludeIds.Contains(id) || played.Contains(id) || banned.Contains(id);

        if (seed is null)
        {
            if (string.IsNullOrWhiteSpace(O.SeedQuery)) return picks;
            var found = await ytm.SearchSongsAsync(O.SeedQuery, 10, ct);
            foreach (var r in found.Where(r => !Blocked(r.Id) && Fits(r)).OrderBy(_ => _rng.Next()).Take(count))
                picks.Add(new Pick(ToTrack(r), $"стартовий сід «{O.SeedQuery}»"));
            return picks;
        }

        var seedKey = KeyOf(seed.Artist, seed.Title);
        var seedArtist = ArtistKey(seed.Artist);
        var cands = new Dictionary<string, Cand>();
        Cand Get(string key, string artist, string title)
        {
            if (!cands.TryGetValue(key, out var c)) cands[key] = c = new Cand { Key = key, Artist = artist, Title = title };
            return c;
        }

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
            if (key == seedKey) continue;
            var c = Get(key, r.Artist, r.Title);
            c.Score += Math.Pow(0.97, pos++);
            c.Yt ??= r;
            c.Sources.Add("YT Music");
        }

        // 2) Last.fm similar tracks: agreement with the radio is a strong signal, its own finds come with a lower weight
        if (lastFm.Enabled)
        {
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
            foreach (var s in sim)
            {
                var key = KeyOf(s.Artist, s.Title);
                if (key == seedKey) continue;
                var c = Get(key, s.Artist, s.Title);
                c.Score += (c.Sources.Count > 0 ? 0.8 : 0.45) * s.Match;
                c.Sources.Add("Last.fm");
            }
        }

        var pool = cands.Values.Where(c => c.Yt is null || (!Blocked(c.Yt.Id) && Fits(c.Yt))).ToList();
        foreach (var c in pool)
        {
            var a = ArtistKey(c.Artist);
            if (a == seedArtist) c.Score *= 0.5;            // same artist again is fine, just not all the time
            else if (recentArtists.Contains(a)) c.Score *= 0.35;
        }
        // YouTube's order is the point of a "radio"; a little jitter only keeps two fills from being identical
        var top = pool.OrderByDescending(c => c.Score * (0.88 + 0.12 * _rng.NextDouble())).Take(10 + count * 4).ToList();
        log.LogInformation("auto-DJ: seed {Seed}, {Cands} candidates, top: {Top}", seed.Label, cands.Count,
            string.Join(" | ", top.Take(5).Select(c => $"{c.Artist} - {c.Title} ({c.Score:F2})")));

        var chosen = new HashSet<string>();
        var chosenArtists = new HashSet<string>();
        var reason = $"схоже на {seed.Label}";
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
            log.LogInformation("auto-DJ pick: {Label} ({Sources}; {Reason})", c.Artist + " - " + c.Title, string.Join("+", c.Sources), reason);
            picks.Add(new Pick(ToTrack(yt), reason));
        }
        return picks;
    }

    bool Fits(SearchResult r) => r.DurationSec == 0 || r.DurationSec <= O.MaxDurationSeconds;

    static string ArtistKey(string artist) => YtMusicClient.Norm(YtMusicClient.FirstArtist(artist));
    static string KeyOf(string artist, string title) => ArtistKey(artist) + "|" + YtMusicClient.Norm(title);

    public static TrackInfo ToTrack(SearchResult r) =>
        new(r.Id, r.Title, r.Artist, r.DurationSec, r.ThumbUrl, $"https://music.youtube.com/watch?v={r.Id}", r.Album);
}
