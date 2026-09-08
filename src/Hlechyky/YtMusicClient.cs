using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hlechyky;

/// <summary>
/// YouTube Music via its internal (InnerTube) API: song search, per-track radio ("Mix"),
/// and resolving an "artist + title" pair to a video id. Anonymous, no key needed.
/// Falls back are handled by callers (yt-dlp).
/// </summary>
public sealed partial class YtMusicClient(ILogger<YtMusicClient> log)
{
    static readonly HttpClient Http = CreateHttp();
    const string SongsFilter = "EgWKAQIIAWoKEAoQAxAEEAkQBQ%3D%3D";

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.Add("Origin", "https://music.youtube.com");
        h.DefaultRequestHeaders.Add("X-Goog-Api-Format-Version", "1");
        return h;
    }

    static JsonObject Body() => new()
    {
        ["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = "WEB_REMIX",
                ["clientVersion"] = "1.20250901.01.00",
                ["hl"] = "uk",
                ["gl"] = "UA",
            },
        },
    };

    async Task<JsonNode?> PostAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false", content, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(s, cancellationToken: ct);
    }

    public async Task<List<SearchResult>> SearchSongsAsync(string query, int limit, CancellationToken ct)
    {
        var body = Body();
        body["query"] = query;
        body["params"] = SongsFilter;
        var root = await PostAsync("search", body, ct);
        var list = new List<SearchResult>();
        var tabs = root?["contents"]?["tabbedSearchResultsRenderer"]?["tabs"]?.AsArray();
        var sections = tabs?.FirstOrDefault()?["tabRenderer"]?["content"]?["sectionListRenderer"]?["contents"]?.AsArray();
        if (sections is null) return list;
        foreach (var sec in sections)
        {
            var items = sec?["musicShelfRenderer"]?["contents"]?.AsArray();
            if (items is null) continue;
            foreach (var it in items)
            {
                var r = it?["musicResponsiveListItemRenderer"];
                if (r is null) continue;
                var id = r["playlistItemData"]?["videoId"]?.GetValue<string>();
                if (id is null) continue;
                var cols = r["flexColumns"]?.AsArray();
                if (cols is null || cols.Count < 2) continue;
                var title = RunsText(cols[0]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
                var byline = RunsText(cols[1]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
                var (artist, album, dur) = ParseByline(byline);
                var thumb = LastThumb(r["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"]);
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new SearchResult(id, title, artist, album, dur, thumb));
                if (list.Count >= limit) return list;
            }
        }
        return list;
    }

    /// <summary>YouTube Music "radio" for a track. The first entry is the seed itself.</summary>
    public async Task<List<SearchResult>> RadioAsync(string videoId, int limit, CancellationToken ct)
    {
        var body = Body();
        body["videoId"] = videoId;
        body["playlistId"] = "RDAMVM" + videoId;
        body["isAudioOnly"] = true;
        body["enablePersistentPlaylistPanel"] = true;
        body["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL";
        body["watchEndpointMusicSupportedConfigs"] = new JsonObject
        {
            ["watchEndpointMusicConfig"] = new JsonObject
            {
                ["hasPersistentPlaylistPanel"] = true,
                ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV",
            },
        };
        var root = await PostAsync("next", body, ct);
        var tabs = root?["contents"]?["singleColumnMusicWatchNextResultsRenderer"]?["tabbedRenderer"]?["watchNextTabbedResultsRenderer"]?["tabs"]?.AsArray();
        var contents = tabs?.FirstOrDefault()?["tabRenderer"]?["content"]?["musicQueueRenderer"]?["content"]?["playlistPanelRenderer"]?["contents"]?.AsArray();
        var list = new List<SearchResult>();
        if (contents is null) return list;
        foreach (var it in contents)
        {
            var r = it?["playlistPanelVideoRenderer"] ?? it?["playlistPanelVideoWrapperRenderer"]?["primaryRenderer"]?["playlistPanelVideoRenderer"];
            if (r is null) continue;
            var id = r["videoId"]?.GetValue<string>();
            if (id is null) continue;
            var title = RunsText(r["title"]);
            var (artist, album, _) = ParseByline(RunsText(r["longBylineText"]));
            var dur = ParseDuration(RunsText(r["lengthText"]));
            var thumb = LastThumb(r["thumbnail"]?["thumbnails"]);
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new SearchResult(id, title, artist, album, dur, thumb));
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>Canonical metadata for a known video id (radio seed entry).</summary>
    public async Task<SearchResult?> LookupAsync(string videoId, CancellationToken ct)
    {
        var r = await RadioAsync(videoId, 2, ct);
        return r.FirstOrDefault(x => x.Id == videoId);
    }

    /// <summary>Find the YouTube Music song for a Last.fm-style artist + title pair.</summary>
    public async Task<SearchResult?> ResolveAsync(string artist, string title, CancellationToken ct)
    {
        var results = await SearchSongsAsync($"{artist} {title}", 8, ct);
        var nt = Norm(title);
        var na = Norm(FirstArtist(artist));
        if (nt.Length == 0) return null;
        var best = results.FirstOrDefault(r => Norm(r.Title).Contains(nt) && Norm(r.Artist).Contains(na))
                   ?? results.FirstOrDefault(r => Norm(r.Title) == nt)
                   ?? results.FirstOrDefault(r => Norm(r.Artist).Contains(na) && (Norm(r.Title).Contains(nt) || nt.Contains(Norm(r.Title))));
        if (best is null) log.LogDebug("YTM resolve miss: {Artist} - {Title}", artist, title);
        return best;
    }

    static string RunsText(JsonNode? textNode)
    {
        var runs = textNode?["runs"]?.AsArray();
        if (runs is null) return textNode?["simpleText"]?.GetValue<string>() ?? "";
        return string.Concat(runs.Select(r => r?["text"]?.GetValue<string>() ?? ""));
    }

    /// <summary>Byline looks like "Artist і Artist2 • Album • 3:21" (search) or "Artist • Album • 2024" (radio).</summary>
    static (string Artist, string? Album, int Duration) ParseByline(string byline)
    {
        var segs = byline.Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var dur = 0;
        if (segs.Count > 0 && DurationRx().IsMatch(segs[^1]))
        {
            dur = ParseDuration(segs[^1]);
            segs.RemoveAt(segs.Count - 1);
        }
        if (segs.Count > 0 && YearRx().IsMatch(segs[^1])) segs.RemoveAt(segs.Count - 1);
        if (segs.Count > 1 && segs[0] is "Пісня" or "Song" or "Відео" or "Video") segs.RemoveAt(0);
        var artist = segs.Count > 0 ? segs[0] : "";
        var album = segs.Count > 1 && !ViewsRx().IsMatch(segs[1]) ? segs[1] : null;
        return (artist, album, dur);
    }

    public static int ParseDuration(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Trim().Split(':');
        var total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var v)) return 0;
            total = total * 60 + v;
        }
        return total;
    }

    static string? LastThumb(JsonNode? thumbs)
    {
        var url = thumbs?.AsArray().LastOrDefault()?["url"]?.GetValue<string>();
        return url is null ? null : ThumbSizeRx().Replace(url, "=w300-h300");
    }

    public static string Norm(string s)
    {
        var cleaned = NonWordRx().Replace(s.ToLowerInvariant(), " ");
        return SpacesRx().Replace(cleaned, " ").Trim();
    }

    public static string FirstArtist(string artist) =>
        artist.Split([" і ", " & ", ", ", " and ", " feat. ", " ft. ", " x "], StringSplitOptions.None)[0].Trim();

    [GeneratedRegex(@"^\d+:\d\d(:\d\d)?$")] private static partial Regex DurationRx();
    [GeneratedRegex(@"^\d{4}$")] private static partial Regex YearRx();
    [GeneratedRegex(@"\d.*(перегляд|views|тис\.|млн|K$|M$)", RegexOptions.IgnoreCase)] private static partial Regex ViewsRx();
    [GeneratedRegex(@"=w\d+-h\d+")] private static partial Regex ThumbSizeRx();
    [GeneratedRegex(@"[^\p{L}\p{Nd} ]")] private static partial Regex NonWordRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRx();
}
