using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Трек для «Вгадай мелодію»: те, що треба вгадати, і звідки різати уривок.</summary>
public sealed record MelodyTrack(string Id, string Title, string Artist, int DurationSec, string? Thumb, string FilePath);

/// <summary>
/// Звідки гра бере треки й уривки. У проді — <see cref="MelodyLibrary"/> (кеш радіо + ffmpeg), у тестах — підробка
/// через <c>Ctx.Services</c>. Обидва методи кличуться поза замком кімнати, у фоновій задачі.
/// </summary>
public interface IMelodySource
{
    /// <summary>До <paramref name="count"/> різних треків, для яких є файл. Порожньо — грати нема в що.</summary>
    Task<IReadOnlyList<MelodyTrack>> PickAsync(int count, Random rng, CancellationToken ct);

    /// <summary>Уривок у mp3 без жодних метаданих. null — не вийшло (файл зник, ffmpeg упав).</summary>
    Task<byte[]?> ClipAsync(MelodyTrack track, double startSec, int seconds, CancellationToken ct);
}

/// <summary>
/// Треки з історії радіо, які лежать у кеші (<c>tracks.file_path</c>), і уривки з них через ffmpeg. Беремо те, що
/// село слухало найчастіше (<see cref="Popular"/>). Голосові, забанені й коротші за 45 секунд не беремо; одного
/// виконавця в партії намагаємось не повторювати.
/// </summary>
public sealed class MelodyLibrary(Db? db, IOptionsMonitor<YtDlpOptions>? options) : IMelodySource
{
    const int MinDuration = 45;

    public Task<IReadOnlyList<MelodyTrack>> PickAsync(int count, Random rng, CancellationToken ct) => Task.Run(() =>
    {
        if (db is null) return (IReadOnlyList<MelodyTrack>)[];
        var all = db.With(c =>
        {
            using var cmd = c.CreateCommand();
            // Скільки разів трек дограв до кінця (скіпнуте не рахується) і скільки в нього лайків.
            cmd.CommandText = """
                SELECT t.id, t.title, t.artist, t.duration_sec, t.thumb_url, t.file_path,
                       (SELECT COUNT(*) FROM plays p WHERE p.track_id = t.id AND p.skipped = 0) AS plays,
                       (SELECT COUNT(*) FROM likes l WHERE l.track_id = t.id) AS likes
                FROM tracks t
                WHERE t.file_path IS NOT NULL AND t.id NOT LIKE $voice AND t.duration_sec >= $min
                  AND t.id NOT IN (SELECT track_id FROM bans)
                """;
            cmd.Parameters.AddWithValue("$voice", VoiceService.Prefix + "%");
            cmd.Parameters.AddWithValue("$min", MinDuration);
            using var r = cmd.ExecuteReader();
            var list = new List<(MelodyTrack Track, int Plays, int Likes)>();
            while (r.Read())
                list.Add((new MelodyTrack(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)), r.GetInt32(6), r.GetInt32(7)));
            return list;
        });
        return Choose(Popular(all.Where(x => File.Exists(x.Track.FilePath)).ToList(), count), count, rng);
    }, ct);

    /// <summary>
    /// Найулюбленіше село: рахунок треку — скільки разів дограв плюс два за кожен лайк. Беремо верхівку, утричі
    /// більшу за партію (але не менше 30), щоб у кожній партії були свої пісні, а не щоразу та сама десятка.
    /// Треки, яких ніхто не дослухав і не лайкнув, ідуть лише тоді, коли популярних замало.
    /// </summary>
    public static List<MelodyTrack> Popular(List<(MelodyTrack Track, int Plays, int Likes)> all, int count)
    {
        var pool = Math.Max(count * 3, 30);
        var ranked = all.OrderByDescending(x => x.Plays + 2 * x.Likes).ThenBy(x => x.Track.Id, StringComparer.Ordinal).ToList();
        var loved = ranked.Where(x => x.Plays + x.Likes > 0).Take(pool).Select(x => x.Track).ToList();
        if (loved.Count >= count + 4) return loved;
        return [.. ranked.Take(Math.Max(pool, count + 4)).Select(x => x.Track)];
    }

    /// <summary>
    /// Перемішати й узяти <paramref name="count"/>: спершу різні пісні різних виконавців, потім — якщо треків
    /// замало — повтори виконавців. Однакову пісню (різні завантаження) двічі не беремо ніколи.
    /// </summary>
    public static IReadOnlyList<MelodyTrack> Choose(List<MelodyTrack> all, int count, Random rng)
    {
        for (var i = all.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }
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
