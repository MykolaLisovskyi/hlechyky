using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hlechyky;

/// <summary>
/// Turns a Spotify track link into "artist + title (+ length)" without an API key. The public
/// track page no longer carries Open Graph tags for bots, but the embed page still ships the
/// track as JSON (__NEXT_DATA__); the oEmbed endpoint is the last resort (title only).
/// Accepts open.spotify.com/track/…, the intl-xx/ variants, spotify:track:… URIs and spotify.link short links.
/// </summary>
public static partial class SpotifyResolver
{
    public sealed record Track(string Id, string Title, string Artist, int DurationSec, string? ThumbUrl);

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("uk,en;q=0.8");
        return h;
    }

    public static bool IsSpotify(string input)
    {
        input = input.Trim();
        if (input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(input, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
               && (u.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("spotify.link", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<Track> ResolveAsync(string input, CancellationToken ct)
    {
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) && u.Host.Equals("spotify.link", StringComparison.OrdinalIgnoreCase))
        {
            // mobile share links redirect to the real page
            using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
            input = resp.RequestMessage?.RequestUri?.ToString() ?? input;
        }
        var m = TrackIdRx().Match(input);
        if (!m.Success)
        {
            var what = input.Contains("/album/") ? "альбом" : input.Contains("/playlist/") ? "плейлист" : input.Contains("/artist/") ? "артист" : null;
            throw new InvalidOperationException(what is null ? "зі Spotify підтримуються тільки посилання на трек" : $"це {what}, а зі Spotify підтримуються тільки посилання на окремий трек");
        }
        var id = m.Groups[1].Value;

        try
        {
            var t = await FromEmbedAsync(id, ct);
            if (t is not null) return t;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fall through to oEmbed
            _ = ex;
        }
        var title = await FromOEmbedAsync(id, ct);
        if (title is null) throw new InvalidOperationException("Spotify не віддав назву треку");
        return title;
    }

    /// <summary>open.spotify.com/embed/track/{id}: Next.js page with props.pageProps.state.data.entity {name, artists[], duration}.</summary>
    static async Task<Track?> FromEmbedAsync(string id, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/track/{id}", ct);
        var m = NextDataRx().Match(html);
        if (!m.Success) return null;
        var root = JsonNode.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
        var entity = root?["props"]?["pageProps"]?["state"]?["data"]?["entity"] ?? FindEntity(root, "spotify:track:" + id);
        if (entity is null) return null;
        var name = entity["name"]?.GetValue<string>() ?? entity["title"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var artists = entity["artists"]?.AsArray().Select(a => a?["name"]?.GetValue<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? new();
        var artist = string.Join(", ", artists);
        if (string.IsNullOrWhiteSpace(artist)) artist = entity["subtitle"]?.GetValue<string>() ?? "";
        var durMs = 0d;
        try { durMs = entity["duration"]?.GetValue<double>() ?? 0; } catch { /* not a number */ }
        var thumb = entity["visualIdentity"]?["image"]?.AsArray().LastOrDefault()?["url"]?.GetValue<string>()
                    ?? entity["coverArt"]?["sources"]?.AsArray().FirstOrDefault()?["url"]?.GetValue<string>();
        return new Track(id, name.Trim(), artist.Trim(), (int)Math.Round(durMs / 1000), thumb);
    }

    static JsonNode? FindEntity(JsonNode? n, string uri)
    {
        switch (n)
        {
            case JsonObject o:
                if (o["uri"]?.ToString() == uri && o["name"] is not null) return o;
                foreach (var kv in o)
                    if (FindEntity(kv.Value, uri) is { } hit) return hit;
                break;
            case JsonArray a:
                foreach (var x in a)
                    if (FindEntity(x, uri) is { } hit) return hit;
                break;
        }
        return null;
    }

    static async Task<Track?> FromOEmbedAsync(string id, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"https://open.spotify.com/oembed?url=https://open.spotify.com/track/{id}", ct);
        var n = JsonNode.Parse(json);
        var title = n?["title"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(title) ? null : new Track(id, title.Trim(), "", 0, n?["thumbnail_url"]?.GetValue<string>());
    }

    [GeneratedRegex(@"(?:/track/|spotify:track:)([A-Za-z0-9]{22})")] private static partial Regex TrackIdRx();
    [GeneratedRegex(@"<script[^>]*id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline)] private static partial Regex NextDataRx();
}
