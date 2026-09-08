using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Hlechyky;

public sealed record SimilarTrack(string Artist, string Title, double Match);

/// <summary>Last.fm read API (similar tracks / artists). Responses are cached in SQLite for a week.</summary>
public sealed class LastFmClient(IOptionsMonitor<LastFmOptions> options, Db db, ILogger<LastFmClient> log)
{
    static readonly HttpClient Http = CreateHttp();
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _lastCall = DateTime.MinValue;

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Hlechyky/0.1 (private web radio for friends)");
        return h;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(options.CurrentValue.ApiKey);

    async Task<JsonNode?> CallAsync(string method, Dictionary<string, string> p, CancellationToken ct)
    {
        if (!Enabled) return null;
        var cacheKey = "lastfm:" + method + ":" + string.Join("|", p.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.ToLowerInvariant()));
        var cached = db.CacheGet(cacheKey, TimeSpan.FromDays(7));
        if (cached is not null) return JsonNode.Parse(cached);

        await _gate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromMilliseconds(300) - (DateTime.UtcNow - _lastCall);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var q = new Dictionary<string, string>(p) { ["method"] = method, ["api_key"] = options.CurrentValue.ApiKey, ["format"] = "json" };
            var url = "https://ws.audioscrobbler.com/2.0/?" + string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            _lastCall = DateTime.UtcNow;
            var text = await Http.GetStringAsync(url, ct);
            var node = JsonNode.Parse(text);
            if (node?["error"] is not null)
            {
                log.LogWarning("Last.fm {Method} error: {Msg}", method, node["message"]);
                return null;
            }
            db.CacheSet(cacheKey, text);
            return node;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Last.fm {Method} failed", method);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Last.fm returns an object instead of a one-element array; normalise.</summary>
    static IEnumerable<JsonNode> AsList(JsonNode? n) => n switch
    {
        JsonArray a => a.Where(x => x is not null).Select(x => x!),
        JsonObject o => [o],
        _ => [],
    };

    public async Task<List<SimilarTrack>> SimilarTracksAsync(string artist, string title, int limit, CancellationToken ct)
    {
        var n = await CallAsync("track.getSimilar", new() { ["artist"] = artist, ["track"] = title, ["autocorrect"] = "1", ["limit"] = limit.ToString() }, ct);
        var list = new List<SimilarTrack>();
        foreach (var t in AsList(n?["similartracks"]?["track"]))
        {
            var name = t["name"]?.GetValue<string>();
            var ar = t["artist"]?["name"]?.GetValue<string>();
            if (name is null || ar is null) continue;
            list.Add(new SimilarTrack(ar, name, ToDouble(t["match"])));
        }
        return list;
    }

    public async Task<List<(string Artist, double Match)>> SimilarArtistsAsync(string artist, int limit, CancellationToken ct)
    {
        var n = await CallAsync("artist.getSimilar", new() { ["artist"] = artist, ["autocorrect"] = "1", ["limit"] = limit.ToString() }, ct);
        var list = new List<(string, double)>();
        foreach (var a in AsList(n?["similarartists"]?["artist"]))
        {
            var name = a["name"]?.GetValue<string>();
            if (name is not null) list.Add((name, ToDouble(a["match"])));
        }
        return list;
    }

    public async Task<List<SimilarTrack>> ArtistTopTracksAsync(string artist, int limit, CancellationToken ct)
    {
        var n = await CallAsync("artist.getTopTracks", new() { ["artist"] = artist, ["autocorrect"] = "1", ["limit"] = limit.ToString() }, ct);
        var list = new List<SimilarTrack>();
        var i = 0;
        foreach (var t in AsList(n?["toptracks"]?["track"]))
        {
            var name = t["name"]?.GetValue<string>();
            var ar = t["artist"]?["name"]?.GetValue<string>() ?? artist;
            if (name is null) continue;
            list.Add(new SimilarTrack(ar, name, 1.0 - 0.5 * i++ / Math.Max(limit, 1)));
        }
        return list;
    }

    static double ToDouble(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<double>(); }
        catch { return double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0; }
    }
}
