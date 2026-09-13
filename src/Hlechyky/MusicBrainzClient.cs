using System.Text.Json.Nodes;

namespace Hlechyky;

/// <summary>
/// MusicBrainz: базу ведуть люди руками, тож AI-контент-ферм у ній практично нема, а нішеве своє є.
/// Нам звідти потрібне одне — чи такий артист узагалі існує. Без ключа, але не частіше запиту на
/// секунду (їхнє правило) і з нормальним User-Agent. Відповіді кешуються в SQLite на 30 днів.
/// </summary>
public sealed class MusicBrainzClient(Db db, ILogger<MusicBrainzClient> log)
{
    static readonly HttpClient Http = CreateHttp();
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _lastCall = DateTime.MinValue;

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Hlechyky/0.1 ( https://github.com/kartatyi/hlechyky )");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return h;
    }

    /// <summary>Чи є артист із таким іменем (або таким псевдонімом): true / false, null — MusicBrainz не відповів.</summary>
    public async Task<bool?> ArtistExistsAsync(string artist, CancellationToken ct)
    {
        var name = YtMusicClient.FirstArtist(artist);
        var want = YtMusicClient.Norm(name);
        if (want.Length == 0) return false;
        var cacheKey = "mb:artist:" + want;
        if (db.CacheGet(cacheKey, TimeSpan.FromDays(30)) is { } cached) return cached == "1";

        await _gate.WaitAsync(ct);
        try
        {
            // 503 у них буває просто від навантаження: одна повторна спроба, далі — «не знаю»
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var wait = TimeSpan.FromMilliseconds(attempt == 0 ? 1100 : 2500) - (DateTime.UtcNow - _lastCall);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastCall = DateTime.UtcNow;
                var q = "artist:\"" + name.Replace("\\", "").Replace("\"", "") + "\" OR alias:\"" + name.Replace("\\", "").Replace("\"", "") + "\"";
                using var resp = await Http.GetAsync("https://musicbrainz.org/ws/2/artist?fmt=json&limit=5&query=" + Uri.EscapeDataString(q), ct);
                if ((int)resp.StatusCode is 503 or 429) continue;
                resp.EnsureSuccessStatusCode();
                var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                var found = (root?["artists"]?.AsArray() ?? []).Any(a =>
                    YtMusicClient.Norm(a?["name"]?.GetValue<string>() ?? "") == want
                    || (a?["aliases"]?.AsArray() ?? []).Any(al => YtMusicClient.Norm(al?["name"]?.GetValue<string>() ?? "") == want));
                db.CacheSet(cacheKey, found ? "1" : "0");
                return found;
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "MusicBrainz lookup failed for {Artist}", artist);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
