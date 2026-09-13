using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Ключ пісні незалежно від того, під яким id YouTube її віддав: «Ben E. King — Stand By Me» приходить
/// і з каналу виконавця, і з «- Topic», і з кліпу, а качати її вчетверо нема чого. Регістр, розділові
/// знаки й приписки на кшталт «(Official Video)» не рахуються; «(ao vivo)», «remix», «acoustic» — рахуються,
/// бо це вже інший запис.
/// </summary>
public static partial class SongKey
{
    [GeneratedRegex(@"[\(\[][^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|hd|hq|4k|mv|clip|кліп|remaster(ed)?)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    public static string Of(string? artist, string? title)
    {
        var a = Squash(artist);
        var t = Squash(Noise().Replace(title ?? "", " "));
        return t.Length == 0 ? "" : a + "|" + t;
    }

    static string Squash(string? s)
    {
        var sb = new StringBuilder();
        var gap = false;
        foreach (var ch in (s ?? "").Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(ch)) { if (gap && sb.Length > 0) sb.Append(' '); sb.Append(ch); gap = false; }
            else gap = true;
        }
        return sb.ToString();
    }

    /// <summary>Та сама пісня: ключ збігся і тривалість відрізняється не більше ніж на 5 секунд (невідома тривалість — не та сама).</summary>
    public static bool SameDuration(int a, int b) => a > 0 && b > 0 && Math.Abs(a - b) <= 5;
}

/// <summary>
/// Що видаляти, коли кеш переріс ліміт. Чиста функція, без диска й бази, щоб її можна було перевірити тестом.
/// Кожен файл отримує «вагу» — коли ним користувались востаннє плюс пільга за повтори, лайки й плейлисти;
/// першими йдуть найлегші. Видаляємо з запасом до 90% ліміту, щоб не смикати диск після кожного скачування.
/// </summary>
public static class CachePlan
{
    public sealed record Entry(string Path, long Bytes, DateTimeOffset LastUsed, int Plays, int Likes, bool InPlaylist);

    public static readonly TimeSpan PerRepeat = TimeSpan.FromDays(2);
    public static readonly TimeSpan PerLike = TimeSpan.FromDays(3);
    public static readonly TimeSpan ForPlaylist = TimeSpan.FromDays(3);

    public static DateTimeOffset Weight(Entry e) =>
        e.LastUsed + PerRepeat * Math.Min(Math.Max(e.Plays - 1, 0), 10) + PerLike * Math.Min(e.Likes, 5) + (e.InPlaylist ? ForPlaylist : TimeSpan.Zero);

    public static List<Entry> Victims(IEnumerable<Entry> files, ISet<string> keep, long limitBytes)
    {
        var all = files.ToList();
        var total = all.Sum(f => f.Bytes);
        if (limitBytes <= 0 || total <= limitBytes) return [];
        var target = (long)(limitBytes * 0.9);
        var victims = new List<Entry>();
        foreach (var f in all.Where(f => !keep.Contains(f.Path)).OrderBy(Weight))
        {
            if (total <= target) break;
            victims.Add(f);
            total -= f.Bytes;
        }
        return victims;
    }
}

/// <summary>
/// Тримає теку cache у межах YtDlp:CacheMaxGb. Перевіряє при старті, раз на 10 хвилин і після кожного
/// скачування. Ніколи не чіпає: те, що в ефірі, у черзі й наступне в Глека; голосові та рекламу (voice-*);
/// недокачане; файли, якими користувались останні дві години (їх могли щойно взяти в чергу).
/// </summary>
public sealed class TrackCache(Db db, YtDlpService ytdlp, RadioEngine engine, IOptionsMonitor<YtDlpOptions> options, ILogger<TrackCache> log)
    : BackgroundService
{
    static readonly TimeSpan Fresh = TimeSpan.FromHours(2);
    readonly SemaphoreSlim _poke = new(0, 1);

    public long LimitBytes => (long)(Math.Max(0, options.CurrentValue.CacheMaxGb) * 1024 * 1024 * 1024);

    public (long Bytes, int Files) Usage()
    {
        var files = TrackFiles().ToList();
        return (files.Sum(f => f.Length), files.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        ytdlp.Downloaded += Poke;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);   // хай рушій спершу підхопить чергу й ефір
            while (!ct.IsCancellationRequested)
            {
                try { Enforce(); }
                catch (Exception ex) { log.LogWarning(ex, "прибирання кешу спіткнулось"); }
                try { await _poke.WaitAsync(TimeSpan.FromMinutes(10), ct); }
                catch (OperationCanceledException) { }
            }
        }
        finally { ytdlp.Downloaded -= Poke; }
    }

    void Poke()
    {
        try { _poke.Release(); } catch (SemaphoreFullException) { /* уже чекає перевірки */ }
    }

    IEnumerable<FileInfo> TrackFiles()
    {
        var dir = new DirectoryInfo(ytdlp.CacheDir);
        if (!dir.Exists) return [];
        return dir.EnumerateFiles().Where(f => !VoiceService.IsVoice(f.Name) && !f.Name.EndsWith(".part") && !f.Name.EndsWith(".ytdl"));
    }

    public int Enforce()
    {
        var now = DateTimeOffset.UtcNow;
        var files = TrackFiles().ToList();

        // сміття від yt-dlp (обкладинки, .unknown_video тощо) нікому не потрібне, якщо не свіже
        var junk = files.Where(f => !YtDlpService.IsAudio(f.Name) && now - f.LastWriteTimeUtc > Fresh).ToList();
        foreach (var f in junk) TryDelete(f.FullName);
        files = files.Except(junk).Where(f => YtDlpService.IsAudio(f.Name)).ToList();

        var limit = LimitBytes;
        if (limit <= 0 || files.Sum(f => f.Length) <= limit) return junk.Count;

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in engine.BusyTrackIds())
            if (ytdlp.FindCached(id) is { } p) keep.Add(Path.GetFullPath(p));
        foreach (var f in files.Where(f => now - f.LastWriteTimeUtc < Fresh)) keep.Add(f.FullName);

        // один файл може слугувати кільком id (та сама пісня з різних каналів): їхні повтори й лайки складаються
        var byFile = new Dictionary<string, (int Plays, DateTimeOffset? LastPlayed, int Likes, bool InPlaylist)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in db.CacheStats())
        {
            var stem = s.FilePath is { Length: > 0 } fp ? Path.GetFileNameWithoutExtension(fp) : s.TrackId;
            var a = byFile.GetValueOrDefault(stem);
            byFile[stem] = (a.Plays + s.Plays, Max(a.LastPlayed, s.LastPlayed), a.Likes + s.Likes, a.InPlaylist || s.InPlaylist);
        }
        var entries = files.Select(f =>
        {
            var s = byFile.GetValueOrDefault(Path.GetFileNameWithoutExtension(f.Name));
            var touched = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero);
            return new CachePlan.Entry(f.FullName, f.Length, Max(s.LastPlayed, touched)!.Value, s.Plays, s.Likes, s.InPlaylist);
        });

        var victims = CachePlan.Victims(entries, keep, limit);
        long freed = 0;
        foreach (var v in victims)
        {
            if (!TryDelete(v.Path)) continue;
            db.ForgetTrackFile(v.Path);
            freed += v.Bytes;
        }
        if (victims.Count > 0)
            log.LogInformation("кеш: видалено {Count} файлів, звільнено {Mb} МБ (ліміт {Gb} ГБ)", victims.Count, freed / (1024 * 1024), options.CurrentValue.CacheMaxGb);
        return victims.Count + junk.Count;
    }

    static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "не вдалося видалити {Path}, спробую наступного разу", path);
            return false;
        }
    }
}
