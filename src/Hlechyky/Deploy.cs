using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Автодеплой. GitHub шле сюди подію workflow_run; коли збірка в Actions закінчилась зеленою
/// на main, запускаємо deploy.ps1 окремим процесом — він переживе перезапуск сервера.
/// Тіло підписане HMAC-SHA256 (той самий секрет, що в налаштуваннях вебхука на GitHub);
/// без підпису або без секрета в конфізі сюди ніхто не достукається.
/// Подію, яка до нас не дійшла, підбирає <see cref="DeployWatch"/>.
/// </summary>
public static class Deploy
{
    /// <summary>З якого коміту зібрано build\ — пише start.ps1 після кожної збірки.</summary>
    public const string BuiltFile = "data/built.sha";
    /// <summary>Коміт, для якого сервер востаннє запускав deploy.ps1 — сам, за вебхуком чи опитувачем.</summary>
    public const string TriedFile = "data/deploy.tried";
    /// <summary>Замок deploy.ps1: поки він є, деплой іде.</summary>
    public const string LockFile = "data/deploy.lock";

    public static async Task<IResult> HandleAsync(HttpContext ctx, DeployOptions opt, ILogger log)
    {
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.WebhookSecret)) return Results.NotFound();
        if (ctx.Request.ContentLength > 1_000_000) return Results.StatusCode(413);

        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        var body = buffer.ToArray();

        if (!SignatureOk(body, ctx.Request.Headers["X-Hub-Signature-256"].ToString(), opt.WebhookSecret))
        {
            log.LogWarning("Деплой: запит із поганим підписом від {Ip}", ctx.Connection.RemoteIpAddress);
            return Results.StatusCode(401);
        }

        var evt = ctx.Request.Headers["X-GitHub-Event"].ToString();
        if (evt == "ping") return Ignored("Глечики слухають");
        if (evt != "workflow_run") return Ignored($"Подія {evt} мене не обходить");

        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { return Results.BadRequest(new { ok = false, message = "Не JSON" }); }

        if (Str(root, "action") != "completed") return Ignored("Збірка ще біжить");
        if (!root.TryGetProperty("workflow_run", out var run)) return Ignored("Нема workflow_run");

        var name = Str(run, "name");
        var branch = Str(run, "head_branch");
        var conclusion = Str(run, "conclusion");
        var sha = Str(run, "head_sha");

        if (!string.IsNullOrEmpty(opt.Workflow) && !name.Equals(opt.Workflow, StringComparison.OrdinalIgnoreCase))
            return Ignored($"Чекаю на «{opt.Workflow}», а це «{name}»");
        if (!branch.Equals(opt.Branch, StringComparison.Ordinal) || Str(run, "event") != "push")
            return Ignored($"Не push у {opt.Branch} (гілка {branch})");
        if (conclusion != "success")
        {
            log.LogWarning("Деплой: збірка {Sha} червона ({Conclusion}), нічого не чіпаю", Short(sha), conclusion);
            return Ignored($"Збірка {conclusion}, деплою не буде");
        }

        log.LogInformation("Деплой: зелена збірка {Sha}, запускаю {Script}", Short(sha), opt.Script);
        try { Run(Paths.Root, opt.Script, sha, "webhook"); }
        catch (Exception ex)
        {
            log.LogError(ex, "Деплой: не вийшло запустити {Script}", opt.Script);
            return Results.StatusCode(500);
        }
        return Results.Accepted(value: new { ok = true, message = $"Деплою {Short(sha)}" });
    }

    static IResult Ignored(string message) => Results.Ok(new { ok = true, message });

    internal static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    static bool SignatureOk(byte[] body, string header, string secret)
    {
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        byte[] got;
        try { got = Convert.FromHexString(header[prefix.Length..]); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, got);
    }

    /// <summary>Перший рядок файла-позначки (built.sha, deploy.tried) без пробілів; нема файла — null.</summary>
    public static string? ReadMark(string root, string file)
    {
        try
        {
            var path = Path.Combine(root, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Запам'ятати коміт у <see cref="TriedFile"/> і запустити deploy.ps1. Позначка — до запуску: сервер, який
    /// деплой зараз приб'є, мусить устигнути її лишити, інакше опитувач нового сервера смикнув би той самий коміт ще раз.
    /// </summary>
    public static void Run(string root, string script, string sha, string via)
    {
        MarkTried(root, sha);
        Launch(root, script, sha, via);
    }

    /// <summary>Записати <see cref="TriedFile"/>. Не вийшло — не біда: гірше буде лише зайва спроба.</summary>
    public static void MarkTried(string root, string sha)
    {
        if (string.IsNullOrEmpty(sha)) return;
        try { File.WriteAllText(Path.Combine(root, TriedFile), sha); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// UseShellExecute — щоб дочірній процес не успадкував хендли сервера: за мить сервер
    /// приб'ють, а deploy.ps1 має спокійно доробити своє.
    /// </summary>
    static void Launch(string root, string script, string sha, string via)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = root,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Path.Combine(root, script));
        if (!string.IsNullOrEmpty(sha))
        {
            psi.ArgumentList.Add("-Sha");
            psi.ArgumentList.Add(sha);
        }
        psi.ArgumentList.Add("-Via");
        psi.ArgumentList.Add(via);
        Process.Start(psi);
    }
}

/// <summary>
/// Підстраховка вебхука. GitHub чекає відповіді 10 с, а подію, що не вклалась, більше не шле; до домашнього
/// сайту він часом достукується 6–9 с навіть там, де сервер відповідає миттєво. 15.09.2026 так двічі поспіль
/// загубилась «збірка завершена» — CI зелений, а деплою нема.
/// <para>
/// Тож раз на <see cref="DeployOptions.PollMinutes"/> сервер сам питає GitHub API про найсвіжішу зелену збірку
/// гілки і, якщо build\ зібрано не з неї і саме її ще не пробували (<see cref="Deploy.TriedFile"/>), запускає
/// deploy.ps1 так само, як вебхук. Одна спроба на коміт: деплой, що впав і відкотився, не смикає сайт по колу
/// перезапусками; повторити — новим комітом, Redeliver на GitHub або deploy.ps1 руками.
/// </para>
/// <para>
/// Працює лише на справжній прод-копії: деплой увімкнено, є секрет вебхука, <see cref="DeployOptions.Repo"/> і
/// позначка build\ від start.ps1. Без токена: публічне API дає 60 запитів на годину з адреси, опитувач бере 20.
/// </para>
/// </summary>
public sealed class DeployWatch : BackgroundService
{
    static readonly HttpClient SharedHttp = CreateHttp();

    readonly IOptionsMonitor<DeployOptions> _opt;
    readonly ILogger _log;
    readonly HttpClient _http;
    readonly string _root;
    readonly Action<DeployOptions, string> _launch;
    DateTimeOffset _pauseUntil;
    bool _failing;

    public DeployWatch(IOptionsMonitor<DeployOptions> opt, ILogger<DeployWatch> log)
        : this(opt, log, SharedHttp, Paths.Root, (o, sha) => Deploy.Run(Paths.Root, o.Script, sha, "poll")) { }

    /// <param name="launch">Що робити з комітом, який пора викотити; тести кладуть сюди запис замість deploy.ps1.</param>
    public DeployWatch(IOptionsMonitor<DeployOptions> opt, ILogger log, HttpClient http, string root, Action<DeployOptions, string> launch)
    {
        _opt = opt;
        _log = log;
        _http = http;
        _root = root;
        _launch = launch;
    }

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("hlechyky-deploy");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        h.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return h;
    }

    static bool Active(DeployOptions o) =>
        o.Enabled && o.PollMinutes > 0 && !string.IsNullOrWhiteSpace(o.WebhookSecret) && !string.IsNullOrWhiteSpace(o.Repo);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Щойно після старту нема чого поспішати: цей сервер міг підняти сам деплой, і хай він спершу доробить.
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(ct);
                if (_failing) _log.LogInformation("Деплой: GitHub API знову відповідає");
                _failing = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Інтернет ліг — не засмічуємо лог щоразу: попередження одне, далі мовчки до відновлення.
                if (!_failing) _log.LogWarning("Деплой: не вийшло спитати GitHub про збірки ({Error})", ex.Message);
                _failing = true;
            }
            var wait = TimeSpan.FromMinutes(Math.Clamp(_opt.CurrentValue.PollMinutes, 1, 60));
            var paused = _pauseUntil - DateTimeOffset.UtcNow;
            try { await Task.Delay(paused > wait ? paused : wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Одна перевірка. Повертає коміт, для якого запустили деплой, або null, якщо робити нічого.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        var o = _opt.CurrentValue;
        if (!Active(o)) return null;
        // build\ не від start.ps1 — це не прод-копія (або її ще жодного разу не збирали), чіпати не будемо.
        if (Deploy.ReadMark(_root, Deploy.BuiltFile) is not { Length: > 0 } built) return null;
        // Деплой уже йде: він тягне верхівку origin, тож хай доробить, а глянемо наступного разу.
        if (File.Exists(Path.Combine(_root, Deploy.LockFile))) return null;

        var url = $"https://api.github.com/repos/{o.Repo.Trim()}/actions/runs?branch={Uri.EscapeDataString(o.Branch)}&event=push&status=success&per_page=10";
        using var res = await _http.GetAsync(url, ct);
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _pauseUntil = ResetOf(res) ?? DateTimeOffset.UtcNow.AddMinutes(15);
            throw new HttpRequestException($"GitHub API {(int)res.StatusCode}: вичерпано ліміт, чекаю до {_pauseUntil:HH:mm} UTC");
        }
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var sha = Pending(doc.RootElement, o, built, Deploy.ReadMark(_root, Deploy.TriedFile) ?? "");
        if (sha is null) return null;
        _log.LogWarning("Деплой: вебхук про зелену збірку {Sha} не дійшов, build\\ на {Built} — запускаю {Script} сам",
            Deploy.Short(sha), Deploy.Short(built), o.Script);
        Deploy.MarkTried(_root, sha);
        _launch(o, sha);
        return sha;
    }

    /// <summary>
    /// Коміт, який пора викотити: найсвіжіша зелена збірка потрібного workflow на push у гілку (GitHub віддає
    /// збірки від нових до старих) — якщо build\ зібрано не з неї і саме її ще не пробували. Старіші зелені не
    /// рахуються: deploy.ps1 однаково тягне верхівку.
    /// </summary>
    public static string? Pending(JsonElement response, DeployOptions o, string built, string tried)
    {
        if (!response.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return null;
        foreach (var run in runs.EnumerateArray())
        {
            if (!string.IsNullOrEmpty(o.Workflow) && !Deploy.Str(run, "name").Equals(o.Workflow, StringComparison.OrdinalIgnoreCase)) continue;
            if (Deploy.Str(run, "head_branch") != o.Branch || Deploy.Str(run, "event") != "push") continue;
            if (Deploy.Str(run, "conclusion") != "success") continue;
            var sha = Deploy.Str(run, "head_sha");
            if (sha.Length == 0 || Same(sha, built) || Same(sha, tried)) return null;
            return sha;
        }
        return null;
    }

    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Коли GitHub знову пустить: <c>Retry-After</c> (секунди) або <c>X-RateLimit-Reset</c> (unix-час).</summary>
    static DateTimeOffset? ResetOf(HttpResponseMessage res)
    {
        if (res.Headers.RetryAfter?.Delta is { } delta) return DateTimeOffset.UtcNow + delta;
        if (res.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }
}
