using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>Wrapper around the yt-dlp binary: metadata, audio download into the cache, flat playlists.</summary>
public sealed class YtDlpService(IOptionsMonitor<YtDlpOptions> options, ILogger<YtDlpService> log)
{
    YtDlpOptions O => options.CurrentValue;
    string Bin => Paths.Resolve(O.BinaryPath);
    public string CacheDir => Paths.Resolve(O.CacheDir);

    List<string> BaseArgs() => ["--no-warnings", "--no-playlist", "--encoding", "utf-8", "--ffmpeg-location", Paths.Resolve(O.FfmpegDir)];

    /// <summary>Cookie arguments for the "YouTube wants a login" retry; empty when nothing is configured.</summary>
    List<string> CookieArgs()
    {
        if (!string.IsNullOrWhiteSpace(O.CookiesFile))
        {
            var f = Paths.Resolve(O.CookiesFile);
            if (File.Exists(f)) return ["--cookies", f];
            log.LogWarning("YtDlp:CookiesFile {File} does not exist", f);
        }
        if (!string.IsNullOrWhiteSpace(O.CookiesFromBrowser)) return ["--cookies-from-browser", O.CookiesFromBrowser];
        return [];
    }

    public bool HasCookies => CookieArgs().Count > 0;

    /// <summary>Age-gated (18+) videos: YouTube only serves them to a logged-in account.</summary>
    public static bool NeedsLogin(string err) =>
        err.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
        || err.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
        || err.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs yt-dlp anonymously; when YouTube demands a login and cookies are configured, once more with them.</summary>
    async Task<(int Code, string Out, string Err)> RunWithCookieFallbackAsync(List<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var r = await RunAsync(args, timeout, ct);
        if (r.Code == 0 || !NeedsLogin(r.Err)) return r;
        var cookies = CookieArgs();
        if (cookies.Count == 0) return r;
        log.LogInformation("YouTube wants a login for {Url}; retrying with cookies ({Mode})", args[^1], cookies[0]);
        return await RunAsync([.. cookies, .. args], timeout, ct);
    }

    async Task<(int Code, string Out, string Err)> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Bin)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Paths.Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp не запустився");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("yt-dlp не вклався в час");
        }
        return (p.ExitCode, await outTask, await errTask);
    }

    public async Task<TrackInfo> FetchInfoAsync(string url, CancellationToken ct)
    {
        var args = BaseArgs();
        args.AddRange(["-J", url]);
        var (code, o, e) = await RunWithCookieFallbackAsync(args, TimeSpan.FromSeconds(60), ct);
        if (code != 0 || string.IsNullOrWhiteSpace(o)) throw new InvalidOperationException(Short(e));
        var n = JsonNode.Parse(o) ?? throw new InvalidOperationException("порожня відповідь yt-dlp");
        if (n["entries"] is JsonArray entries) n = entries.FirstOrDefault() ?? throw new InvalidOperationException("порожній плейлист");

        var extractor = n["extractor_key"]?.GetValue<string>() ?? "generic";
        var rawId = n["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var id = extractor.Equals("Youtube", StringComparison.OrdinalIgnoreCase) ? rawId : $"{extractor.ToLowerInvariant()}-{Sanitize(rawId)}";
        var title = Str(n["track"]) ?? Str(n["title"]) ?? "Без назви";
        var uploader = StripTopic(Str(n["channel"]) ?? Str(n["uploader"]) ?? "");
        var artist = Str(n["artist"]);
        if (artist is null && n["artists"] is JsonArray ar && ar.Count > 0)
            artist = string.Join(", ", ar.Select(x => Str(x)).Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = uploader;
            var dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (n["track"] is null && dash > 0)
            {
                artist = title[..dash].Trim();
                title = title[(dash + 3)..].Trim();
            }
        }
        var dur = (int)Math.Round(Num(n["duration"]));
        return new TrackInfo(id, title, artist, dur, Str(n["thumbnail"]), Str(n["webpage_url"]) ?? url, Str(n["album"]));
    }

    public string? FindCached(string id)
    {
        Directory.CreateDirectory(CacheDir);
        return Directory.EnumerateFiles(CacheDir, id + ".*")
            .FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") && !f.EndsWith(".webp") && !f.EndsWith(".jpg") && !f.EndsWith(".json"));
    }

    /// <summary>Downloads best audio for the track into the cache, returns the file path.</summary>
    public async Task<string> DownloadAsync(TrackInfo t, CancellationToken ct)
    {
        var cached = FindCached(t.Id);
        if (cached is not null) return cached;
        var args = BaseArgs();
        args.AddRange([
            "-f", "bestaudio[ext=m4a]/bestaudio/best",
            "-x", "--audio-format", "best",
            "--no-progress",
            "-o", Path.Combine(CacheDir, t.Id + ".%(ext)s"),
            "--print", "after_move:filepath",
            t.SourceUrl,
        ]);
        var (code, o, e) = await RunWithCookieFallbackAsync(args, TimeSpan.FromSeconds(O.TimeoutSeconds), ct);
        var path = o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (path is not null && File.Exists(path)) return path;
        cached = FindCached(t.Id);
        if (cached is not null) return cached;
        log.LogWarning("yt-dlp download failed ({Code}) for {Url}: {Err}", code, t.SourceUrl, e);
        throw new InvalidOperationException(code != 0 ? Short(e) : "yt-dlp не повернув файл");
    }

    public async Task<List<SearchResult>> FlatPlaylistAsync(string url, int max, CancellationToken ct)
    {
        var args = BaseArgs();
        args.Remove("--no-playlist");
        args.AddRange(["--flat-playlist", "--playlist-end", max.ToString(), "-J", url]);
        var (code, o, e) = await RunAsync(args, TimeSpan.FromSeconds(60), ct);
        if (code != 0 || string.IsNullOrWhiteSpace(o)) throw new InvalidOperationException(Short(e));
        var n = JsonNode.Parse(o);
        var list = new List<SearchResult>();
        var entries = n?["entries"]?.AsArray();
        if (entries is null) return list;
        foreach (var en in entries)
        {
            if (en is null) continue;
            var id = Str(en["id"]);
            var title = Str(en["title"]);
            if (id is null || title is null) continue;
            var artist = StripTopic(Str(en["channel"]) ?? Str(en["uploader"]) ?? "");
            var thumb = en["thumbnails"]?.AsArray().LastOrDefault()?["url"]?.GetValue<string>();
            list.Add(new SearchResult(id, title, artist, null, (int)Math.Round(Num(en["duration"])), thumb));
        }
        return list;
    }

    static string? Str(JsonNode? n) => n is null ? null : n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToString();

    static double Num(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<double>(); }
        catch { return double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0; }
    }

    static string StripTopic(string s) => s.EndsWith(" - Topic", StringComparison.Ordinal) ? s[..^8] : s;

    static string Sanitize(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());

    string Short(string err)
    {
        var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? "невідома помилка yt-dlp";
        if (last.StartsWith("ERROR:", StringComparison.Ordinal)) last = last[6..].Trim();
        if (NeedsLogin(last))
            return HasCookies
                ? "YouTube не пустив до відео 18+ навіть з куками (акаунт у куках не залогінений або без підтвердженого віку)"
                : "відео 18+, YouTube віддає його тільки залогіненому акаунту; потрібні куки (YtDlp:CookiesFile, дивись README)";
        return last.Length > 220 ? last[..220] : last;
    }
}
