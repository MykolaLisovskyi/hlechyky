using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky;

public sealed class TtsOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Чим запускати <c>python -m edge_tts</c>.</summary>
    public string Python { get; set; } = "python";
    public string CacheDir { get; set; } = "cache/tts";
    /// <summary>Швидкість edge-tts («+0%», «-4%»). Входить у ключ кешу: змінив — репліки озвучаться наново.</summary>
    public string Rate { get; set; } = "+0%";
    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>Більше черга не росте: зайве мовчки відкидається (гра тоді читає без голосу).</summary>
    public int MaxQueue { get; set; } = 3000;
}

/// <summary>Готова репліка: хеш (він же ім'я файла), шлях і скільки звучить.</summary>
public sealed record TtsClip(string Hash, string FilePath, double Seconds);

/// <summary>Той, хто справді озвучує. Окремо від черги — щоб черга й кеш тестувались без edge-tts і мережі.</summary>
public interface ITtsEngine
{
    /// <summary>Озвучити в <paramref name="outPath"/> (mp3). Невдача — false, не виняток.</summary>
    Task<bool> SynthesizeAsync(string voice, string text, string rate, string outPath, CancellationToken ct);
    /// <summary>Тривалість mp3 у секундах; 0 — не вийшло поміряти.</summary>
    Task<double> DurationAsync(string path, CancellationToken ct);
}

/// <summary>edge-tts через <c>python -m edge_tts</c>, тривалість — ffprobe з <c>YtDlp:FfmpegDir</c>.</summary>
public sealed class EdgeTtsEngine(IOptionsMonitor<TtsOptions> options, IOptionsMonitor<YtDlpOptions> yt, ILogger<EdgeTtsEngine> log) : ITtsEngine
{
    public async Task<bool> SynthesizeAsync(string voice, string text, string rate, string outPath, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var (code, err) = await RunAsync(o.Python, ["-m", "edge_tts", "--voice", voice, "--rate=" + rate, "--text", text, "--write-media", outPath],
            TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 120)), ct);
        if (code == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0) return true;
        log.LogWarning("edge-tts не озвучив ({Code}): {Err}", code, err.Trim().Split('\n').LastOrDefault());
        return false;
    }

    public async Task<double> DurationAsync(string path, CancellationToken ct)
    {
        var ffprobe = Path.Combine(Paths.Resolve(yt.CurrentValue.FfmpegDir), OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var (code, o) = await RunAsync(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path],
            TimeSpan.FromSeconds(15), ct, wantStdout: true);
        return code == 0 && double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    static async Task<(int Code, string Text)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, bool wantStdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Paths.Root,
        };
        // edge-tts пише в консоль; без UTF-8 python на Windows падає на кирилиці в тексті помилки
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
        if (p is null) return (-1, "не запустився");
        using (p)
        {
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* уже вийшов */ }
                return (-1, "не вклався в час");
            }
            return (p.ExitCode, wantStdout ? await outTask : await errTask);
        }
    }
}

/// <summary>
/// Голос ведучого (specs/svoya.md §4): репліки озвучуються edge-tts у фоні, по одній, і лягають у кеш
/// <c>cache/tts/&lt;sha1&gt;.mp3</c> (поруч <c>.sec</c> — тривалість, щоб після рестарту не міряти наново).
/// Є файл — більше не озвучується ніколи. Хто кличе з-під замка (гра), той лише питає <see cref="TryGet"/> і
/// ставить у чергу <see cref="Enqueue"/> — обидва не чекають нічого, крім словника в пам'яті й одного
/// маленького файла.
/// </summary>
public sealed class TtsService(ITtsEngine engine, IOptionsMonitor<TtsOptions> options, ILogger<TtsService> log) : BackgroundService
{
    readonly object _lock = new();
    readonly LinkedList<Job> _queue = new();
    readonly Dictionary<string, LinkedListNode<Job>> _queued = [];
    readonly Dictionary<string, double> _ready = [];
    readonly HashSet<string> _failed = [];
    readonly SemaphoreSlim _signal = new(0);

    sealed record Job(string Hash, string Voice, string Text, string Rate);

    TtsOptions O => options.CurrentValue;
    public bool Enabled => O.Enabled;
    public string CacheDir => Paths.Resolve(O.CacheDir);

    public static string Hash(string voice, string rate, string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{voice}|{rate}|{text.Trim()}"))).ToLowerInvariant();

    public string HashOf(string voice, string text) => Hash(voice, O.Rate, text);

    /// <summary>Готова репліка або null. Файл із попереднього запуску підхоплюється за його <c>.sec</c>.</summary>
    public TtsClip? TryGet(string voice, string text)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return null;
        var hash = HashOf(voice, text);
        var mp3 = Path.Combine(CacheDir, hash + ".mp3");
        lock (_lock)
        {
            if (_ready.TryGetValue(hash, out var sec)) return new TtsClip(hash, mp3, sec);
        }
        var known = ReadSeconds(hash);
        if (known is not { } s) return null;
        lock (_lock) _ready[hash] = s;
        return new TtsClip(hash, mp3, s);
    }

    double? ReadSeconds(string hash)
    {
        try
        {
            var sec = Path.Combine(CacheDir, hash + ".sec");
            if (!File.Exists(sec) || !File.Exists(Path.Combine(CacheDir, hash + ".mp3"))) return null;
            return double.TryParse(File.ReadAllText(sec).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Поставити репліки в чергу. <paramref name="urgent"/> — на самий початок (гра чекає саме на неї зараз),
    /// інакше в кінець. Уже готові, уже в черзі й ті, що вже не вдались, — пропускаються.
    /// </summary>
    public void Enqueue(string voice, IEnumerable<string> texts, bool urgent = false)
    {
        if (!Enabled) return;
        var rate = O.Rate;
        var added = false;
        lock (_lock)
        {
            foreach (var raw in urgent ? texts.Reverse() : texts)
            {
                var text = raw?.Trim() ?? "";
                if (text.Length == 0) continue;
                var hash = Hash(voice, rate, text);
                if (_ready.ContainsKey(hash) || _failed.Contains(hash)) continue;
                if (_queued.TryGetValue(hash, out var node))
                {
                    // node.List == null — воркер уже озвучує цю репліку, пересувати нема чого
                    if (urgent && node.List == _queue && node != _queue.First) { _queue.Remove(node); _queue.AddFirst(node); }
                    continue;
                }
                if (_queue.Count >= O.MaxQueue) continue;
                var job = new Job(hash, voice, text, rate);
                _queued[hash] = urgent ? _queue.AddFirst(job) : _queue.AddLast(job);
                added = true;
            }
        }
        if (added) _signal.Release();
    }

    /// <summary>Скільки з цих реплік уже озвучено — для «Озвучити» в конструкторі.</summary>
    public (int Ready, int Total) Progress(string voice, IEnumerable<string> texts)
    {
        var list = texts.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        return (list.Count(t => TryGet(voice, t) is not null), list.Count);
    }

    public int Queued { get { lock (_lock) return _queue.Count; } }

    /// <summary>Шлях до готового файла за ім'ям «хеш.mp3» — лише так, щоб з адреси не вийти за теку кешу.</summary>
    public string? FileOf(string name)
    {
        if (name.Length != 44 || !name.EndsWith(".mp3", StringComparison.Ordinal) || !name[..40].All(char.IsAsciiHexDigitLower)) return null;
        var path = Path.Combine(CacheDir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Узяти наступну репліку й озвучити. Публічне — щоб тести крутили чергу без фонового потоку.</summary>
    public async Task<bool> StepAsync(CancellationToken ct)
    {
        Job? job;
        lock (_lock)
        {
            job = _queue.First?.Value;
            if (job is not null) _queue.RemoveFirst();
        }
        if (job is null) return false;
        try { await ProduceAsync(job, ct); }
        finally { lock (_lock) _queued.Remove(job.Hash); }
        return true;
    }

    async Task ProduceAsync(Job job, CancellationToken ct)
    {
        if (ReadSeconds(job.Hash) is { } known) { lock (_lock) _ready[job.Hash] = known; return; }
        Directory.CreateDirectory(CacheDir);
        var mp3 = Path.Combine(CacheDir, job.Hash + ".mp3");
        var part = Path.Combine(CacheDir, job.Hash + ".part.mp3");
        try
        {
            if (!await engine.SynthesizeAsync(Voice(job.Voice), job.Text, job.Rate, part, ct)) { Fail(job); return; }
            var seconds = await engine.DurationAsync(part, ct);
            if (seconds <= 0) { Fail(job); return; }
            File.Move(part, mp3, overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(CacheDir, job.Hash + ".sec"), seconds.ToString("0.###", CultureInfo.InvariantCulture), ct);
            lock (_lock) _ready[job.Hash] = seconds;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "озвучка «{Text}» спіткнулась", Short(job.Text));
            Fail(job);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
        }
    }

    /// <summary>Не вийшло — більше не пробуємо (до рестарту): гра прочитає цю репліку без голосу.</summary>
    void Fail(Job job)
    {
        lock (_lock) _failed.Add(job.Hash);
        log.LogInformation("озвучка не вдалась: «{Text}»", Short(job.Text));
    }

    static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>Короткі імена голосів («ostap») → імена edge-tts. Невідоме — як є.</summary>
    public static string Voice(string name) => name switch
    {
        "ostap" => "uk-UA-OstapNeural",
        "polina" => "uk-UA-PolinaNeural",
        _ => name,
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct);
                while (await StepAsync(ct)) { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "черга озвучки спіткнулась"); }
        }
    }
}
