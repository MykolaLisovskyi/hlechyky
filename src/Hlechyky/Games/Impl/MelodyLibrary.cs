using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Трек для «Вгадай мелодію»: те, що треба вгадати, і звідки різати уривок. Пісня з добірки
/// (<see cref="MelodyClassics"/>), якої ще нема на диску, — <see cref="Pending"/>: без id і без файла, доки
/// <see cref="IMelodySource.ResolveAsync"/> не знайде її на YouTube Music і не скачає.
/// </summary>
public sealed record MelodyTrack(string Id, string Title, string Artist, int DurationSec, string? Thumb, string FilePath)
{
    public bool Pending => FilePath.Length == 0;
}

/// <summary>
/// Звідки гра бере треки й уривки. У проді — <see cref="MelodyLibrary"/> (кеш радіо + добірки + ffmpeg), у тестах —
/// підробка через <c>Ctx.Services</c>. Усі методи кличуться поза замком кімнати, у фоновій задачі.
/// </summary>
public interface IMelodySource
{
    /// <summary>
    /// До <paramref name="count"/> різних треків із <paramref name="categories"/> (<see cref="MelodyCategories"/>),
    /// порівну з кожної. Перший — обов'язково з файлом, решта можуть бути <see cref="MelodyTrack.Pending"/>.
    /// Порожньо — грати нема в що.
    /// </summary>
    Task<IReadOnlyList<MelodyTrack>> PickAsync(int count, IReadOnlyList<string> categories, Random rng, CancellationToken ct);

    /// <summary>
    /// Трек із файлом на диску: той самий, якщо файл є; для <see cref="MelodyTrack.Pending"/> — знайти й скачати.
    /// null — не вийшло (файл зник, на YouTube не знайшлось, yt-dlp упав): трек пропускається.
    /// </summary>
    Task<MelodyTrack?> ResolveAsync(MelodyTrack track, CancellationToken ct);

    /// <summary>Уривок у mp3 без жодних метаданих. null — не вийшло (файл зник, ffmpeg упав).</summary>
    Task<byte[]?> ClipAsync(MelodyTrack track, double startSec, int seconds, CancellationToken ct);
}

/// <summary>
/// Треки для гри: з історії радіо, які лежать у кеші (<c>tracks.file_path</c>), і з добірок
/// (<see cref="MelodyClassics"/>), які за потреби знаходяться на YouTube Music і качаються в <c>cache/melody</c>.
/// Уривки — через ffmpeg. Треки з 👎 (<c>melody_dislikes</c>) — і всі завантаження тієї самої пісні — не беремо.
/// З радіо беремо все, що хоч раз звучало (<see cref="Heard"/>). Голосові, забанені й коротші за 45 секунд не
/// беремо; одного виконавця в партії намагаємось не повторювати.
/// </summary>
public sealed class MelodyLibrary(
    Db? db, IOptionsMonitor<YtDlpOptions>? options, YtMusicClient? ytm = null, YtDlpService? ytdlp = null,
    MelodyClassics? classics = null, IOptionsMonitor<MelodyOptions>? melody = null, ILogger<MelodyLibrary>? log = null) : IMelodySource
{
    const int MinDuration = 45;
    /// <summary>Скільки чекати на пошук і скачування одного треку з добірки.</summary>
    static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(4);

    MelodyClassics Classics => classics ?? MelodyClassics.Default;

    public sealed record Row(MelodyTrack Track, int Plays, string SongKey);

    /// <summary>Усі придатні треки з бази (файл може вже не існувати) і ключі пісень із 👎.</summary>
    (List<Row> Rows, HashSet<string> Disliked) Load() => db!.With(c =>
    {
        var rows = new List<Row>();
        using (var cmd = c.CreateCommand())
        {
            // Скільки разів трек звучав на радіо — хай навіть його скіпнули: його чули.
            cmd.CommandText = """
                SELECT t.id, t.title, t.artist, t.duration_sec, t.thumb_url, t.file_path,
                       (SELECT COUNT(*) FROM plays p WHERE p.track_id = t.id) AS plays, t.song_key
                FROM tracks t
                WHERE t.file_path IS NOT NULL AND t.id NOT LIKE $voice AND t.duration_sec >= $min
                  AND t.id NOT IN (SELECT track_id FROM bans)
                  AND t.id NOT IN (SELECT track_id FROM melody_dislikes)
                  -- та сама пісня з іншого завантаження теж не годиться
                  AND (t.song_key IS NULL OR t.song_key = '' OR t.song_key NOT IN (
                      SELECT x.song_key FROM tracks x JOIN melody_dislikes d ON d.track_id = x.id
                      WHERE x.song_key IS NOT NULL AND x.song_key <> ''))
                """;
            cmd.Parameters.AddWithValue("$voice", VoiceService.Prefix + "%");
            cmd.Parameters.AddWithValue("$min", MinDuration);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new Row(new MelodyTrack(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)), r.GetInt32(6), r.IsDBNull(7) ? "" : r.GetString(7)));
        }
        var disliked = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT x.song_key FROM tracks x JOIN melody_dislikes d ON d.track_id = x.id WHERE x.song_key IS NOT NULL AND x.song_key <> ''";
            using var r = cmd.ExecuteReader();
            while (r.Read()) disliked.Add(r.GetString(0));
        }
        return (rows, disliked);
    });

    public Task<IReadOnlyList<MelodyTrack>> PickAsync(int count, IReadOnlyList<string> categories, Random rng, CancellationToken ct) => Task.Run(() =>
    {
        if (db is null) return (IReadOnlyList<MelodyTrack>)[];
        var (rows, disliked) = Load();
        var cached = rows.Where(x => File.Exists(x.Track.FilePath)).ToList();
        var pools = Pools(cached, disliked, categories, Classics, rng);
        return FirstReady(Choose(Interleave(pools), count, rng, shuffle: false));
    }, ct);

    /// <summary>
    /// По списку кандидатів на кожну обрану категорію, кожен перемішаний. Радіо: українське / решта
    /// (<see cref="MelodyLanguage"/>), лише те, що звучало. Добірка: пісня з кешу, якщо та сама вже є (під
    /// назвою з добірки — вона чистіша за ютубівську), інакше <see cref="MelodyTrack.Pending"/>; з 👎 — ні.
    /// </summary>
    public static List<List<MelodyTrack>> Pools(List<Row> cached, ISet<string> disliked, IReadOnlyList<string> categories, MelodyClassics classics, Random rng)
    {
        HashSet<MelodyTrack>? ua = null;
        HashSet<MelodyTrack> Ua() => ua ??= MelodyLanguage.Default.Ukrainian(cached.Select(x => x.Track)).ToHashSet();
        ILookup<string, Row>? byKey = null;
        var pools = new List<List<MelodyTrack>>();
        foreach (var cat in categories.Distinct(StringComparer.Ordinal))
        {
            List<MelodyTrack> pool;
            if (cat == MelodyCategories.Ua) pool = Heard(cached.Where(x => Ua().Contains(x.Track)));
            else if (cat == MelodyCategories.World) pool = Heard(cached.Where(x => !Ua().Contains(x.Track)));
            else
            {
                byKey ??= cached.Where(x => x.SongKey.Length > 0).ToLookup(x => x.SongKey, StringComparer.Ordinal);
                pool = [];
                foreach (var e in classics.In(cat))
                {
                    if (byKey[e.Key].OrderByDescending(x => x.Plays).FirstOrDefault() is { } hit)
                        pool.Add(hit.Track with { Title = e.Title, Artist = e.Artist });
                    else if (!disliked.Contains(e.Key))
                        pool.Add(new MelodyTrack("", e.Title, e.Artist, 0, null, ""));
                }
            }
            Shuffle(pool, rng);
            if (pool.Count > 0) pools.Add(pool);
        }
        return pools;
    }

    /// <summary>По одному з кожного списку по колу: щоб 10 раундів на дві категорії дали 5 + 5, а не 9 + 1.</summary>
    public static List<MelodyTrack> Interleave(List<List<MelodyTrack>> pools)
    {
        var all = new List<MelodyTrack>();
        for (var i = 0; pools.Any(p => i < p.Count); i++)
            foreach (var p in pools) if (i < p.Count) all.Add(p[i]);
        return all;
    }

    /// <summary>Першим — трек, який уже на диску: партія починається одразу, а качається решта вже під час гри.</summary>
    public static IReadOnlyList<MelodyTrack> FirstReady(IReadOnlyList<MelodyTrack> picked)
    {
        var i = picked.ToList().FindIndex(t => !t.Pending);
        if (i <= 0) return picked;
        return [picked[i], .. picked.Where((_, j) => j != i)];
    }

    /// <summary>
    /// Усе, що хоч раз звучало на радіо, — рівноправно, без переваги частим чи лайкнутим: інакше партії
    /// крутились би довкола тієї самої десятки. Лише коли таких замало, докидаємо решту кешу.
    /// </summary>
    public static List<MelodyTrack> Heard(IEnumerable<Row> all, int count = 10)
    {
        var list = all.ToList();
        var heard = list.Where(x => x.Plays > 0).Select(x => x.Track).ToList();
        if (heard.Count >= count + 4) return heard;
        return [.. heard, .. list.Where(x => x.Plays == 0).Select(x => x.Track)];
    }

    static void Shuffle<T>(List<T> all, Random rng)
    {
        for (var i = all.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }
    }

    /// <summary>
    /// Узяти <paramref name="count"/>: спершу різні пісні різних виконавців, потім — якщо треків
    /// замало — повтори виконавців. Однакову пісню (різні завантаження) двічі не беремо ніколи.
    /// </summary>
    public static IReadOnlyList<MelodyTrack> Choose(List<MelodyTrack> all, int count, Random rng, bool shuffle = true)
    {
        if (shuffle) Shuffle(all, rng);
        var songs = new HashSet<string>(StringComparer.Ordinal);
        var artists = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<MelodyTrack>();
        foreach (var pass in new[] { true, false })
        {
            foreach (var t in all)
            {
                if (picked.Count >= count) break;
                var song = SongKey.Of(t.Artist, t.Title);
                if (songs.Contains(song) || picked.Contains(t)) continue;
                var artist = MelodyAnswer.Key(t.Artist);
                if (pass && artists.Contains(artist)) continue;
                picked.Add(t);
                songs.Add(song);
                artists.Add(artist);
            }
        }
        return picked;
    }

    // ---------- добірки: знайти й скачати ----------

    /// <summary>Куди кладемо треки з добірок: підтека кешу радіо, яку <see cref="TrackCache"/> не чіпає (він дивиться лише верхній рівень).</summary>
    public string ClassicsDir => Path.Combine(ytdlp?.CacheDir ?? Paths.Resolve(options?.CurrentValue.CacheDir ?? "cache"), "melody");

    /// <summary>Одну пісню кількома столами одночасно не качаємо: хто другий — чекає на ту саму задачу.</summary>
    readonly ConcurrentDictionary<string, Task<MelodyTrack?>> _fetching = new(StringComparer.Ordinal);

    public async Task<MelodyTrack?> ResolveAsync(MelodyTrack track, CancellationToken ct)
    {
        if (!track.Pending)
        {
            if (!File.Exists(track.FilePath)) return null;
            Touch(track.FilePath);
            return track;
        }
        if (db is null || ytm is null || ytdlp is null) return null;
        var key = SongKey.Of(track.Artist, track.Title);
        var task = _fetching.GetOrAdd(key, k =>
        {
            var t = FetchAsync(track, k);
            _ = t.ContinueWith(done => _fetching.TryRemove(new KeyValuePair<string, Task<MelodyTrack?>>(k, done)), TaskScheduler.Default);
            return t;
        });
        return await task.WaitAsync(ct);
    }

    /// <summary>Пісня з добірки: спершу — чи не з'явилась тим часом у кеші, далі YouTube Music → yt-dlp → база.</summary>
    async Task<MelodyTrack?> FetchAsync(MelodyTrack want, string key)
    {
        try
        {
            foreach (var (id, dur, path) in db!.SameSongFiles(key, ""))
            {
                if (path is null || !File.Exists(path) || dur < MinDuration) continue;
                var known = db.GetTrack(id);
                return new MelodyTrack(id, want.Title, want.Artist, dur, known?.ThumbUrl, path);
            }
            using var cts = new CancellationTokenSource(FetchTimeout);
            var hit = Pick(await ytm!.SearchSongsAsync($"{want.Artist} {want.Title}", 10, cts.Token), want);
            if (hit is null)
            {
                log?.LogInformation("мелодія: «{Artist} — {Title}» на YouTube Music не знайшлась", want.Artist, want.Title);
                return null;
            }
            // у базі — під назвою з добірки: вона і є правильна відповідь, а ютубівська буває «(Remastered 2011)»
            var info = new TrackInfo(hit.Id, want.Title, want.Artist, hit.DurationSec, hit.ThumbUrl, "https://music.youtube.com/watch?v=" + hit.Id, hit.Album);
            db.UpsertTrack(info);
            Directory.CreateDirectory(ClassicsDir);
            var file = await ytdlp!.DownloadAsync(info, cts.Token, ClassicsDir);
            db.SetTrackFile(hit.Id, file);
            log?.LogInformation("мелодія: скачано «{Artist} — {Title}» ({Id})", want.Artist, want.Title, hit.Id);
            Trim();
            return new MelodyTrack(hit.Id, want.Title, want.Artist, hit.DurationSec, hit.ThumbUrl, file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.LogWarning("мелодія: «{Artist} — {Title}» не скачалась: {Err}", want.Artist, want.Title, ex.Message);
            return null;
        }
    }

    /// <summary>Не пісня, а її підміна: інструментал, караоке, кавер, «сповільнена», мінусовка — таке не беремо ніколи.</summary>
    static readonly System.Text.RegularExpressions.Regex Fake = new(
        @"instrumental|karaoke|караоке|cover|кавер|nightcore|sped.?up|slowed|reverb|8d|tribute|minus|мінус|backing|lullaby|kids|piano version|orchestra",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Та сама пісня, але не «та»: наживо, акустика, подовжена, ремікс, демо — беремо лише коли іншого нема.</summary>
    static readonly System.Text.RegularExpressions.Regex Variant = new(
        @"\blive\b|наживо|acoustic|акустич|unplugged|extended|remix|\bmix\b|demo|rehearsal|medley|reprise|edit\b.*\b(club|dance)|version",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Який із результатів пошуку YouTube Music — саме та пісня з добірки. Виконавець І назва мають збігтись за
    /// тими ж правилами, що й здогадка гравця (<see cref="MelodyAnswer.Hits"/>: без регістру, з транслітом, дужки
    /// не рахуються) — інакше «Тартак — Наше літо» стає «наше літо» іншого гурту, а «гормони» — першим-ліпшим треком
    /// тієї ж співачки. Підміни (<see cref="Fake"/>) — геть; наживо/подовжені (<see cref="Variant"/>) і довші за
    /// 8 хвилин — у кінець черги. Серед рівних — перший, як його поставив YouTube.
    /// </summary>
    public static SearchResult? Pick(IEnumerable<SearchResult> results, MelodyTrack want)
    {
        var artists = MelodyAnswer.Artists(want);
        var titles = MelodyAnswer.Titles(want);
        SearchResult? best = null;
        var bestRank = int.MaxValue;
        foreach (var r in results)
        {
            if (r.DurationSec > 0 && r.DurationSec < MinDuration) continue;
            if (Fake.IsMatch(r.Title)) continue;
            var found = new MelodyTrack(r.Id, r.Title, r.Artist, r.DurationSec, r.ThumbUrl, "");
            var artistOk = MelodyAnswer.Hits(r.Artist, artists) || MelodyAnswer.Hits(want.Artist, MelodyAnswer.Artists(found));
            var titleOk = MelodyAnswer.Hits(r.Title, titles) || MelodyAnswer.Hits(want.Title, MelodyAnswer.Titles(found));
            if (!artistOk || !titleOk) continue;
            var rank = (Variant.IsMatch(r.Title) ? 1 : 0) + (r.DurationSec > 8 * 60 ? 1 : 0);
            if (rank < bestRank) { best = r; bestRank = rank; }
            if (rank == 0) break;
        }
        return best;
    }

    /// <summary>Свіжа позначка на файлі, який щойно брали: <see cref="Trim"/> видаляє найдавніше взяте.</summary>
    void Touch(string path)
    {
        if (!path.StartsWith(ClassicsDir, StringComparison.OrdinalIgnoreCase)) return;
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { /* не біда */ }
    }

    /// <summary>Тримати <c>cache/melody</c> у межах <see cref="MelodyOptions.ClassicsMaxMb"/>: зайве — те, чого найдовше не брали.</summary>
    public int Trim()
    {
        var limit = (long)Math.Max(0, melody?.CurrentValue.ClassicsMaxMb ?? new MelodyOptions().ClassicsMaxMb) * 1024 * 1024;
        var dir = new DirectoryInfo(ClassicsDir);
        if (limit <= 0 || !dir.Exists) return 0;
        var files = dir.EnumerateFiles().Where(f => YtDlpService.IsAudio(f.Name)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        var removed = 0;
        foreach (var f in files)
        {
            if (total <= limit) break;
            try { f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            db?.ForgetTrackFile(f.FullName);
            total -= f.Length;
            removed++;
        }
        if (removed > 0) log?.LogInformation("мелодія: з cache/melody прибрано {N} файлів (ліміт {Mb} МБ)", removed, limit / (1024 * 1024));
        return removed;
    }

    public async Task<byte[]?> ClipAsync(MelodyTrack track, double startSec, int seconds, CancellationToken ct)
    {
        if (!File.Exists(track.FilePath)) return null;
        var fadeOut = Math.Max(0, seconds - 1.2).ToString("0.0", CultureInfo.InvariantCulture);
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", startSec.ToString("0.0", CultureInfo.InvariantCulture), "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-i", track.FilePath,
            // -map_metadata -1: ні назви, ні виконавця, ні обкладинки в уривку — підглянути в «Властивостях» нічого
            "-vn", "-sn", "-map_metadata", "-1", "-ac", "2", "-ar", "44100", "-b:a", "128k",
            // loudnorm: одні треки в кеші гучні, інші тихі — без вирівнювання кожен уривок довелось би крутити повзунком
            "-af", $"loudnorm=I=-18:TP=-2:LRA=11,afade=t=in:d=0.4,afade=t=out:st={fadeOut}:d=1.2",
            "-f", "mp3", "pipe:1",
        ];
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var ms = new MemoryStream();
            var errTask = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token);
                await p.WaitForExitAsync(cts.Token);
                await errTask;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* уже вийшов */ }
                return null;
            }
            return p.ExitCode == 0 && ms.Length > 4096 ? ms.ToArray() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    string Ffmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dir = options?.CurrentValue.FfmpegDir;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var path = Path.Combine(Paths.Resolve(dir), name);
            if (File.Exists(path)) return path;
        }
        return name;   // нехай шукає в PATH
    }
}

/// <summary>
/// Готові уривки, які браузер тягне за випадковим токеном (<c>/api/games/melody/&lt;токен&gt;.mp3</c>). Токен нічого не
/// каже про трек, а в самому файлі метаданих нема — назву з адреси чи файла не вичитати. Живуть 20 хвилин.
/// </summary>
public static class MelodyClips
{
    static readonly TimeSpan Life = TimeSpan.FromMinutes(20);
    const int MaxClips = 400;
    static readonly ConcurrentDictionary<string, (byte[] Data, DateTimeOffset Expires)> Store = new(StringComparer.Ordinal);

    public static string Put(byte[] data)
    {
        Sweep();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        Store[token] = (data, DateTimeOffset.UtcNow + Life);
        return token;
    }

    public static byte[]? Get(string token) =>
        Store.TryGetValue(token, out var hit) && hit.Expires > DateTimeOffset.UtcNow ? hit.Data : null;

    static void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (k, v) in Store) if (v.Expires <= now) Store.TryRemove(k, out _);
        if (Store.Count < MaxClips) return;
        foreach (var k in Store.OrderBy(kv => kv.Value.Expires).Take(Store.Count - MaxClips + 1).Select(kv => kv.Key).ToList())
            Store.TryRemove(k, out _);
    }

    public static WebApplication Map(WebApplication app)
    {
        app.MapGet("/api/games/melody/{file}", (string file, HttpContext c) =>
        {
            var token = file.EndsWith(".mp3", StringComparison.Ordinal) ? file[..^4] : file;
            if (token.Length != 24 || Get(token) is not { } data) return Results.NotFound();
            c.Response.Headers.CacheControl = "no-store";
            return Results.File(data, "audio/mpeg", enableRangeProcessing: true);
        });
        return app;
    }
}

/// <summary>
/// Чи вгадано виконавця або назву. Назви з YouTube брудні: «Скрябін - Спи собі сама» від «Somber Sounds»,
/// «А Може Ти Мене Полюбиш (feat. TEMRA)», «Гаї шумлять (1913)». Тож варіантів правильної відповіді кілька:
/// виконавці через кому/&amp;/feat, назва без дужок, обидві половини «Виконавець - Назва». Порівняння — без
/// регістру й розділових знаків, кирилиця і латиниця зводяться одна до одної («Океан Ельзи» = «Okean Elzy»),
/// і прощається дрібна помилка (одна літера на кожні п'ять).
/// </summary>
public static class MelodyAnswer
{
    static readonly string[] ArtistSplit = [",", "&", " feat.", " feat ", " ft.", " ft ", " x ", " і ", " и ", " and ", " та "];

    public static List<string> Artists(MelodyTrack t)
    {
        var set = new List<string>();
        void Add(string s) { var k = Key(s); if (k.Length >= 2 && !set.Contains(k)) set.Add(k); }
        Add(t.Artist);
        foreach (var part in Split(t.Artist)) Add(part);
        // «Виконавець - Назва» у полі назви: ліва половина — теж виконавець
        if (Dash(t.Title) is { } halves) { Add(halves.Left); foreach (var part in Split(halves.Left)) Add(part); }
        // «Topic»-канали YouTube: «Океан Ельзи - Topic»
        foreach (var k in set.ToList()) if (k.EndsWith(" topic", StringComparison.Ordinal)) Add(k[..^6]);
        return set;
    }

    public static List<string> Titles(MelodyTrack t)
    {
        var set = new List<string>();
        void Add(string s) { var k = Key(s); if (k.Length >= 1 && !set.Contains(k)) set.Add(k); }
        var title = Dash(t.Title) is { } halves ? halves.Right : t.Title;
        Add(StripBrackets(title));
        Add(title);
        return set;
    }

    /// <summary>Чи здогадка влучає в одну з відповідей.</summary>
    public static bool Hits(string guess, IEnumerable<string> answers)
    {
        var g = Key(guess);
        if (g.Length == 0) return false;
        var gl = Latin(g);
        foreach (var a in answers)
        {
            if (Near(g, a) || Near(gl, Latin(a))) return true;
        }
        return false;
    }

    static bool Near(string g, string a)
    {
        if (g == a) return true;
        if (a.Length < 3) return false;
        // здогадка довша за відповідь і містить її цілими словами: «океан ельзи як ніколи» влучає в обидва
        if ((" " + g + " ").Contains(" " + a + " ", StringComparison.Ordinal)) return true;
        var max = a.Length / 5;
        return max > 0 && Math.Abs(g.Length - a.Length) <= max && Distance(g, a) <= max;
    }

    /// <summary>Нижній регістр, лише літери й цифри, один пробіл між словами, ё→е, апострофи геть.</summary>
    public static string Key(string? s)
    {
        var sb = new StringBuilder();
        var gap = false;
        foreach (var raw in (s ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            var ch = raw == 'ё' ? 'е' : raw;
            if (ch is '\'' or '’' or 'ʼ' or '`') continue;
            if (char.IsLetterOrDigit(ch)) { if (gap && sb.Length > 0) sb.Append(' '); sb.Append(ch); gap = false; }
            else gap = true;
        }
        return sb.ToString();
    }

    static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "h", ['ґ'] = "g", ['д'] = "d", ['е'] = "e", ['є'] = "ie", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "y", ['і'] = "i", ['ї'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n",
        ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ь'] = "", ['ю'] = "iu", ['я'] = "ia", ['ы'] = "y", ['э'] = "e", ['ъ'] = "",
    };

    /// <summary>
    /// Грубе зведення до латиниці, однакове для обох боків: кирилиця — за спрощеною таблицею, латиниця — зі
    /// злиттям схожих звуків (y/i, h/g, kh/h, w/v, j/i). Не для людей, а щоб «Скрябін» і «Skryabin» зустрілись.
    /// </summary>
    public static string Latin(string key)
    {
        var sb = new StringBuilder(key.Length + 8);
        foreach (var ch in key) sb.Append(Translit.TryGetValue(ch, out var l) ? l : ch.ToString());
        return sb.ToString()
            .Replace("kh", "h").Replace("zh", "j").Replace("ch", "c").Replace("sh", "s").Replace("ts", "c")
            .Replace('y', 'i').Replace('g', 'h').Replace('w', 'v').Replace('j', 'i').Replace("ie", "e")
            .Replace("ia", "a").Replace("iu", "u").Replace("ya", "a").Replace("yu", "u");
    }

    static IEnumerable<string> Split(string s)
    {
        var parts = new List<string> { " " + s + " " };
        foreach (var sep in ArtistSplit)
            parts = [.. parts.SelectMany(p => p.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
        return parts;
    }

    static (string Left, string Right)? Dash(string title)
    {
        foreach (var sep in new[] { " - ", " – ", " — " })
        {
            var i = title.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) return (title[..i], title[(i + sep.Length)..]);
        }
        return null;
    }

    static string StripBrackets(string s)
    {
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var ch in s)
        {
            if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0) sb.Append(ch);
        }
        return sb.ToString();
    }

    static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>
/// Чи пісня українська — без жодної бази мов, лише з того, що є в назві й імені виконавця:
/// <list type="bullet">
/// <item>ы, э, ъ, ё — не українська, і крапка (хай навіть виконавець зі списку);</item>
/// <item>і, ї, є, ґ, апостроф або суто українське слово («ти», «що», «шо», «як», «двох»…) у назві чи в імені —
/// українська (сполучник « і » між виконавцями не рахується: так база склеює кількох виконавців);</item>
/// <item>виконавець, у якого знайшовся хоч один такий трек, вважається українським — і його «Teresa &amp; Maria» теж;</item>
/// <item>виконавці зі списку <c>data/melody/ukrainian-artists.txt</c> — для тих, кого за літерами не впізнати
/// («KALUSH», «БЕЗ ОБМЕЖЕНЬ»).</item>
/// </list>
/// </summary>
public sealed partial class MelodyLanguage(IEnumerable<string> knownArtists)
{
    public const string FileName = "data/melody/ukrainian-artists.txt";

    static readonly Lazy<MelodyLanguage> Cached = new(() => Load(Paths.Resolve(FileName)));
    public static MelodyLanguage Default => Cached.Value;

    readonly HashSet<string> _known = [.. knownArtists.Select(Norm).Where(k => k.Length > 0)];

    public static MelodyLanguage Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new([]);
            return new(File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new([]); }
    }

    public IEnumerable<MelodyTrack> Ukrainian(IEnumerable<MelodyTrack> tracks)
    {
        var list = tracks.ToList();
        var learned = new HashSet<string>(_known, StringComparer.Ordinal);
        foreach (var t in list.Where(t => !Russian(t) && Marked(t)))
            foreach (var part in Parts(t.Artist)) learned.Add(part);
        return list.Where(t => !Russian(t) && (Marked(t) || Parts(t.Artist).Any(learned.Contains)));
    }

    static bool Russian(MelodyTrack t) => (t.Title + " " + t.Artist).Any(c => "ыэъёЫЭЪЁ".Contains(c));

    static bool Marked(MelodyTrack t) => HasUa(t.Title) || HasUa(Joiner().Replace(t.Artist, ", "));

    /// <summary>
    /// Слова, яких у російській нема (там «ты», «что», «как», «это»…): вони видають українську назву навіть без
    /// і/ї/є/ґ — «Кава на двох», «ШО ТИ, ШО ТИ».
    /// </summary>
    static readonly HashSet<string> UaWords = new(StringComparer.Ordinal)
    {
        "ти", "ми", "ви", "що", "шо", "як", "це", "чи", "вже", "щоб", "або", "дуже", "тобі", "мені", "він", "вона",
        "воно", "вони", "мій", "твій", "двох", "коли", "зараз", "тільки", "завжди", "кохаю", "кохання", "кохана",
        "серце", "дівчино", "мамо", "чорний", "чорна", "вибираю", "хлопці", "добре", "бачу", "бути", "треба",
    };

    static bool HasUa(string s)
    {
        foreach (var w in s.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            if (UaWords.Contains(w)) return true;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if ("іїєґІЇЄҐʼ".Contains(c)) return true;
            // апостроф усередині кириличного слова: «п'яніли», «зв'язок»
            if (c is '\'' or '’' && i > 0 && i + 1 < s.Length && Cyr(s[i - 1]) && Cyr(s[i + 1])) return true;
        }
        return false;
    }

    static readonly char[] Separators = [.. " ,.!?;:()[]{}\"«»—–-_/|#🇺🇦".ToCharArray()];

    static bool Cyr(char c) => c is >= 'А' and <= 'я';

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+і\s+")]
    private static partial System.Text.RegularExpressions.Regex Joiner();

    static IEnumerable<string> Parts(string artist) =>
        Joiner().Replace(artist, ", ")
            .Split([",", "&", " feat.", " feat ", " ft.", " x ", " X ", " and "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(artist)
            .Select(Norm)
            .Where(k => k.Length > 0);

    /// <summary>Ключ виконавця: без регістру, розділових знаків і пробілів, зведений до латиниці.</summary>
    static string Norm(string s) => MelodyAnswer.Latin(MelodyAnswer.Key(s)).Replace(" ", "");
}
