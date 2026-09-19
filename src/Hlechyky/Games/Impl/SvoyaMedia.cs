using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Що вийшло після перекодування: скільки звучить і чи довелось обрізати.</summary>
public sealed record SvoyaTranscoded(bool Ok, double Seconds, bool Trimmed, string? Error = null);

/// <summary>ffmpeg для звуку й відео. Окремо — щоб тести не залежали від ffmpeg на машині.</summary>
public interface ISvoyaTranscoder
{
    Task<SvoyaTranscoded> AudioAsync(string input, string output, int maxSeconds, CancellationToken ct);
    Task<SvoyaTranscoded> VideoAsync(string input, string output, int maxSeconds, CancellationToken ct);
}

/// <summary>Справжній ffmpeg/ffprobe з <c>YtDlp:FfmpegDir</c>: звук — mp3 128k, відео — h264/aac до 720p.</summary>
public sealed class FfmpegTranscoder(IOptionsMonitor<YtDlpOptions> yt, ILogger<FfmpegTranscoder> log) : ISvoyaTranscoder
{
    string Tool(string name) => Path.Combine(Paths.Resolve(yt.CurrentValue.FfmpegDir), OperatingSystem.IsWindows() ? name + ".exe" : name);

    public Task<SvoyaTranscoded> AudioAsync(string input, string output, int maxSeconds, CancellationToken ct) =>
        RunAsync(input, output, maxSeconds, ["-vn", "-ac", "2", "-ar", "44100", "-b:a", "128k"], TimeSpan.FromSeconds(60), ct);

    public Task<SvoyaTranscoded> VideoAsync(string input, string output, int maxSeconds, CancellationToken ct) =>
        RunAsync(input, output, maxSeconds, [
            "-vf", "scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart",
        ], TimeSpan.FromSeconds(180), ct);

    async Task<SvoyaTranscoded> RunAsync(string input, string output, int maxSeconds, string[] codec, TimeSpan timeout, CancellationToken ct)
    {
        var before = await DurationAsync(input, ct);
        var (code, err) = await Exec(Tool("ffmpeg"), ["-hide_banner", "-loglevel", "error", "-y", "-i", input, "-t", maxSeconds.ToString(CultureInfo.InvariantCulture), .. codec, output], timeout, ct);
        if (code != 0 || !File.Exists(output))
        {
            log.LogWarning("своя гра: ffmpeg не перекодував ({Code}): {Err}", code, err.Trim());
            return new SvoyaTranscoded(false, 0, false, "Файл не вдалось перекодувати — це точно звук чи відео?");
        }
        var after = await DurationAsync(output, ct);
        return new SvoyaTranscoded(after > 0, after, before > maxSeconds + 0.5, after > 0 ? null : "Порожній файл");
    }

    async Task<double> DurationAsync(string path, CancellationToken ct)
    {
        var (code, o) = await Exec(Tool("ffprobe"), ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path], TimeSpan.FromSeconds(15), ct, stdout: true);
        return code == 0 && double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    static async Task<(int Code, string Text)> Exec(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, bool stdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Paths.Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
        if (p is null) return (-1, "не запустився");
        using (p)
        {
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* уже вийшов */ }
                return (-1, "не вклався в час");
            }
            return (p.ExitCode, stdout ? await o : await e);
        }
    }
}

/// <summary>
/// Медіа пакетів (specs/svoya.md §5): завантаження з конструктора (і з імпорту), перевірка, перекодування, квоти,
/// роздача за точним іменем. Ім'я файла — sha1 уже готового вмісту: той самий файл двічі не лягає, а з імені не
/// вгадати, що в ньому.
/// </summary>
public sealed class SvoyaUploads(SvoyaStore store, SvoyaFiles files, ISvoyaTranscoder transcoder, IClock clock,
    IOptionsMonitor<SvoyaOptions>? options = null, ILogger<SvoyaUploads>? log = null)
{
    public const long ImageMax = 5L * 1024 * 1024, AudioMax = 15L * 1024 * 1024, VideoMax = 60L * 1024 * 1024;
    public const int MaxSeconds = 90;
    /// <summary>Файл, на який пакет не посилається, прибирається не одразу: його щойно завантажили, а пакет ще не зберегли.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(30);

    static readonly string[] ImageExt = ["jpg", "jpeg", "png", "webp", "gif"];
    static readonly string[] AudioExt = ["mp3", "ogg", "oga", "m4a", "wav", "opus", "aac", "flac"];
    static readonly string[] VideoExt = ["mp4", "webm", "mov", "m4v", "mkv"];

    SvoyaOptions O => options?.CurrentValue ?? new SvoyaOptions();

    /// <summary>Який це вид медіа за розширенням; null — не приймаємо.</summary>
    public static string? KindOf(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ImageExt.Contains(ext) ? SvoyaMedia.Image : AudioExt.Contains(ext) ? SvoyaMedia.Audio : VideoExt.Contains(ext) ? SvoyaMedia.Video : null;
    }

    public static long LimitOf(string kind) => kind switch { SvoyaMedia.Image => ImageMax, SvoyaMedia.Audio => AudioMax, _ => VideoMax };

    /// <summary>Картинка за першими байтами: розширення файла — лише за вмістом, не за тим, що написав браузер.</summary>
    public static string? ImageType(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8))) return "gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return "webp";
        return null;
    }

    /// <summary>
    /// Прийняти файл у теку пакета. Результат — <c>{ file, kind, seconds, bytes, warning }</c>; конструктор кладе
    /// це в <c>question.media</c>. Перевіряє права (<paramref name="user"/> null — імпорт, права перевірено вище).
    /// </summary>
    public async Task<SvoyaReply> UploadAsync(string packId, SvoyaUser? user, string fileName, Stream body, CancellationToken ct)
    {
        if (store.Get(packId) is not { } row) return SvoyaReply.Fail(SvoyaPacks.NoPack);
        if (user is not null && !SvoyaPacks.CanEdit(user, row)) return SvoyaReply.Fail(SvoyaPacks.NotYours);
        if (KindOf(fileName) is not { } kind) return SvoyaReply.Fail("Такий файл не візьму: картинка (jpg, png, webp, gif), звук (mp3, ogg, m4a, wav) або відео (mp4, webm)");
        var limit = LimitOf(kind);

        var dir = files.Dir(packId);
        Directory.CreateDirectory(dir);
        var raw = Path.Combine(dir, $".up-{Guid.NewGuid():N}.part");
        var done = Path.Combine(dir, $".up-{Guid.NewGuid():N}.{(kind == SvoyaMedia.Audio ? "mp3" : "mp4")}");
        try
        {
            long size;
            await using (var f = File.Create(raw))
            {
                var buf = new byte[81920];
                size = 0;
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > limit) return SvoyaReply.Fail($"Завеликий файл: для {Word(kind)} — до {limit / (1024 * 1024)} МБ");
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (size == 0) return SvoyaReply.Fail("Порожній файл");

            string ext, final;
            double seconds = 0;
            string? warning = null;
            if (kind == SvoyaMedia.Image)
            {
                var head = new byte[16];
                await using (var f = File.OpenRead(raw)) _ = await f.ReadAsync(head, ct);
                ext = ImageType(head) ?? "";
                if (ext.Length == 0) return SvoyaReply.Fail("Це не схоже на картинку");
                final = raw;
            }
            else
            {
                var r = kind == SvoyaMedia.Audio
                    ? await transcoder.AudioAsync(raw, done, MaxSeconds, ct)
                    : await transcoder.VideoAsync(raw, done, MaxSeconds, ct);
                if (!r.Ok) return SvoyaReply.Fail(r.Error ?? "Файл не вдалось перекодувати");
                seconds = r.Seconds;
                if (r.Trimmed) warning = $"Обрізано до {MaxSeconds} с";
                ext = kind == SvoyaMedia.Audio ? "mp3" : "mp4";
                final = done;
            }

            string hash;
            await using (var h = File.OpenRead(final)) hash = Convert.ToHexString(await SHA1.HashDataAsync(h, ct)).ToLowerInvariant();
            // ім'я — перші 24 знаки хеша: коротко, а збіг у межах одного пакета неможливий на практиці
            var name = hash[..24] + "." + ext;
            var target = Path.Combine(dir, name);
            var bytes = new FileInfo(final).Length;
            var fresh = !File.Exists(target);

            if (fresh && user is not { Admin: true })
            {
                var packBytes = DirBytes(dir) + bytes;
                if (packBytes > (long)O.PackMaxMb * 1024 * 1024) return SvoyaReply.Fail($"Медіа пакета більше за {O.PackMaxMb} МБ — прибери щось");
                // інші пакети ніка — за теками, а не за базою: там і те, що завантажили, але ще не зберегли
                var others = store.Mine(row.OwnerKey).Where(r => r.Id != row.Id).Sum(r => DirBytes(files.Dir(r.Id)));
                if (others + packBytes > (long)O.UserMaxMb * 1024 * 1024)
                    return SvoyaReply.Fail($"Усі твої пакети разом більші за {O.UserMaxMb} МБ медіа");
            }
            if (fresh) File.Move(final, target);
            else File.SetLastWriteTimeUtc(target, clock.UtcNow.UtcDateTime);   // знову потрібен — не прибирати
            log?.LogInformation("своя гра: у пакет {Pack} лягло {Kind} {Name} ({Bytes} Б)", packId, kind, name, bytes);
            return new SvoyaReply(true, warning ?? "", new { file = name, kind, seconds = (int)Math.Round(seconds), bytes, warning });
        }
        finally
        {
            foreach (var p in new[] { raw, done })
                try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
        }
    }

    static string Word(string kind) => kind switch { SvoyaMedia.Image => "картинки", SvoyaMedia.Audio => "звуку", _ => "відео" };

    static long DirBytes(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).StartsWith('.')).Sum(f => new FileInfo(f).Length) : 0;

    /// <summary>
    /// Прибрати з теки файли, на які пакет більше не посилається, — але лише старші за <see cref="OrphanGrace"/>:
    /// свіжий файл міг лягти за мить до збереження, яке про нього ще не знає.
    /// </summary>
    public int Sweep(SvoyaPack pack)
    {
        var dir = files.Dir(pack.Id);
        if (!Directory.Exists(dir)) return 0;
        var keep = pack.MediaFiles().ToHashSet(StringComparer.Ordinal);
        var now = clock.UtcNow.UtcDateTime;
        var removed = 0;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (keep.Contains(name)) continue;
            if (now - File.GetLastWriteTimeUtc(f) < OrphanGrace) continue;
            try { File.Delete(f); removed++; } catch (IOException) { }
        }
        return removed;
    }

    /// <summary>Шлях до медіа за точним іменем (без списків тек і «..»); null — нема.</summary>
    public string? FileOf(string packId, string name) =>
        name.Length is > 4 and < 80 && name.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '.') && !name.StartsWith('.')
            && files.Size(packId, name) is not null ? Path.Combine(files.Dir(packId), name) : null;

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };
}
