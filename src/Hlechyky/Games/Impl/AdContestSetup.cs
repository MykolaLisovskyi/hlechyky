using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Єдина дірочка конкурсу в ефір: поставити готове голосове в кінець черги. Інтерфейс тут для тестів —
/// справжній <see cref="RadioEngine"/> тягне за собою пів сервера, а перевірити треба лише «коли саме».
/// </summary>
public interface IAdAir
{
    (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick);
}

/// <summary>Справжній ефір: той самий публічний метод, яким у чергу лягають звичайні голосові.</summary>
public sealed class RadioAir(RadioEngine engine) : IAdAir
{
    public (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick) =>
        engine.AddVoice(track, filePath, nick);
}

/// <summary>
/// Джингл: переможна реклама час від часу сама заходить в ефір. Умов три, і всі три мусять збігтись —
/// минуло <c>Ad:EveryTracks</c> треків, минуло <c>Ad:MinMinutes</c> хвилин і на сайті хтось є. Без
/// останньої умови реклама крутилась би о четвертій ранку сама собі.
/// </summary>
public sealed class AdJingle(AdContest contest, AdLibrary library, AdListenRewards rewards, IAdAir air, IVoiceSaver voice,
    Presence presence, IClock clock, IOptionsMonitor<AdOptions> opts, ILogger<AdJingle> log)
{
    readonly object _lock = new();
    int _since;
    DateTimeOffset? _lastAt;

    /// <summary>Скільки треків минуло від останньої реклами — видно в тестах і в лозі.</summary>
    public int Since { get { lock (_lock) return _since; } }

    /// <summary>Заграв новий трек. Кличе <see cref="AdContestTicker"/>, підписаний на RadioEngine.TrackStarted.</summary>
    public void OnTrackStarted(TrackInfo track)
    {
        try { Step(track); }
        catch (Exception ex) { log.LogWarning(ex, "джингл реклами спіткнувся"); }
    }

    /// <summary>
    /// Реклама в ефірі — будь-яка: з бібліотеки, господаря чи конкурсу. За назвою теж, бо господар міг щойно
    /// замінити рекламу, а в черзі ще стояла попередня.
    /// </summary>
    bool IsAd(TrackInfo track) =>
        library.IsAd(track.Id) || track.Id == contest.OnAir()?.TrackId
        || (track.Title == AdTitle && track.Id.StartsWith("voice-", StringComparison.Ordinal));

    /// <summary>Що ставити наступним: колода бібліотеки, а як у ротації порожньо — реклама господаря чи переможець.</summary>
    AdWinner? Next() => library.Take() is { } clip ? Clip(clip) : contest.OnAir();

    static AdWinner Clip(AdClip clip) => new(0, clip.TrackId, clip.Title, clip.Seconds, House: true);

    void Step(TrackInfo track)
    {
        var o = opts.CurrentValue;
        if (!o.Enabled) return;
        if (IsAd(track))
        {
            // Це грає сама реклама: не рахуємо її за трек, починаємо відлік від цієї миті і платимо слухачам
            if (library.IsAd(track.Id)) library.Played(track.Id);
            rewards.Start(track);
            lock (_lock)
            {
                _since = 0;
                _lastAt = clock.UtcNow;
            }
            return;
        }
        if (!o.Jingle) return;
        // Реклама в ефірі кожен трек може бути вже інша: господар увімкнув ротацію, поставив свою або прибрав її.
        if (!library.HasLive() && contest.OnAir() is null) return;
        var (every, minutes) = contest.Frequency();

        lock (_lock)
        {
            _since++;
            if (_since < every) return;
            // Лічильник далі не росте, але й не скидається: щойно з'явиться слухач — реклама піде.
            if (presence.Count == 0) return;
            if (_lastAt is { } last && clock.UtcNow - last < TimeSpan.FromMinutes(minutes)) return;
            if (Next() is { } ad && Queue(ad).Ok) _since = 0;     // відмова — уже в черзі або в ефірі, спробуємо наступного разу
        }
    }

    public const string AdTitle = "Реклама глека";

    /// <summary>Господар натиснув «В ефір зараз»: наступна реклама стає в чергу одразу, без лічильника й хвилин.</summary>
    public (bool Ok, string Message) PlayNow()
    {
        lock (_lock)
        {
            if (Next() is not { } ad) return (false, "Нема що крутити: ні ротації, ні своєї реклами, ні переможця");
            var r = Queue(ad);
            if (r.Ok) _since = 0;
            return r.Ok ? (true, $"«{ad.Nick}» стала в чергу") : r;
        }
    }

    /// <summary>Саме цю рекламу з бібліотеки — в чергу, хай навіть вона вимкнена в ротації.</summary>
    public (bool Ok, string Message) PlayClip(long id)
    {
        if (library.Get(id) is not { } clip) return (false, "Такої реклами нема");
        lock (_lock)
        {
            var r = Queue(Clip(clip));
            if (r.Ok) _since = 0;
            return r.Ok ? (true, $"«{clip.Title}» стала в чергу") : r;
        }
    }

    (bool Ok, string Message) Queue(AdWinner ad)
    {
        if (voice.FilePath(ad.TrackId) is not { } path) return (false, "Файлу реклами вже нема");   // кеш почистили — не біда
        var track = new TrackInfo(ad.TrackId, AdTitle, ad.Nick, ad.Seconds, null, $"/api/voice/{ad.TrackId}.mp3", null);
        var r = air.AddVoice(track, path, "Дядько Глек");
        if (r.Ok)
        {
            _lastAt = clock.UtcNow;
            log.LogInformation("реклама {Track} ({Nick}) стала в чергу", ad.TrackId, ad.Nick);
        }
        return r;
    }
}

/// <summary>
/// Хвилинний тікер конкурсу: закриває дотерміновані, відкриває понеділкові. Заразом підписує джингл на
/// ефір — підписка живе рівно стільки, скільки сервер, і знімається на зупинці.
/// </summary>
public sealed class AdContestTicker(AdContest contest, AdJingle jingle, RadioEngine engine, ILogger<AdContestTicker> log)
    : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    public override Task StartAsync(CancellationToken ct)
    {
        engine.TrackStarted += jingle.OnTrackStarted;
        return base.StartAsync(ct);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        engine.TrackStarted -= jingle.OnTrackStarted;
        await base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await contest.TickAsync(ct); }
            catch (Exception ex) { log.LogWarning(ex, "хвилинний крок конкурсу реклами спіткнувся"); }
        }
    }
}

/// <summary>
/// Підключення конкурсу. Кличеться одним рядком із <c>EconomySetup.AddHlechykyEconomy</c> і одним —
/// із <c>MapHlechykyEconomy</c>: це єдині два рядки, які конкурс додає до спільних файлів.
/// </summary>
public static class AdContestSetup
{
    public static IServiceCollection AddAdContest(this IServiceCollection services)
    {
        services.AddOptions<AdOptions>().BindConfiguration("Ad");

        // Годинник, шину подій і скриньку реєструє каркас, і робить це раніше; TryAdd — щоб конкурс
        // піднімався і без нього (ранні гілки, тести).
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<GameEvents>();
        services.TryAddSingleton<IOutbox, NullOutbox>();

        services.TryAddSingleton<IVoiceSaver, VoiceSaver>();
        services.TryAddSingleton<IAdScriptWriter, DjScriptWriter>();
        services.TryAddSingleton<IAdAir, RadioAir>();

        services.TryAddSingleton<IAdOnAir, RadioOnAir>();

        services.AddSingleton<AdContestStore>();
        services.AddSingleton<AdContest>();
        services.AddSingleton<AdLibraryStore>();
        services.AddSingleton<AdLibrary>();
        services.AddSingleton<AdListenRewards>();
        services.AddSingleton<AdJingle>();
        services.AddHostedService<AdContestTicker>();
        return services;
    }

    public sealed record AdVoteRequest(long EntryId);
    public sealed record AdEveryRequest(int EveryTracks, int MinMinutes);
    public sealed record AdClipPatch(string? Title, bool? Enabled);
    public sealed record AdAllRequest(bool Enabled);

    public static WebApplication MapAdContest(this WebApplication app)
    {
        // Відповіді тієї самої форми, що й у решти сайту: { ok, message } — фронт уже вміє її читати.
        static IResult Reply((bool Ok, string Message) r) =>
            r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { ok = false, message = r.Message });
        static IResult Deny() => Results.BadRequest(new { ok = false, message = "Це вміє тільки господар" });

        app.MapGet("/api/ads", (HttpContext c, AdContest ads) => Results.Ok(ads.Snapshot(Auth.Nick(c))));

        // ---- ефір реклами: тільки господар ----
        app.MapGet("/api/ads/air", (HttpContext c, AdContest ads, AdJingle jingle) =>
            Auth.IsAdmin(c) ? Results.Ok(ads.AirView(jingle.Since)) : Deny());

        app.MapPost("/api/ads/air/entry", (HttpContext c, AdVoteRequest req, AdContest ads) =>
            Auth.IsAdmin(c) ? Reply(ads.AirEntry(req.EntryId)) : Deny());

        app.MapPost("/api/ads/air/record", async (HttpContext c, AdContest ads, IVoiceSaver voice, CancellationToken ct) =>
        {
            if (!Auth.IsAdmin(c)) return Deny();
            if (c.Request.ContentLength > voice.MaxUploadBytes)
                return Reply((false, $"Задовгий запис, ліміт {voice.MaxUploadBytes / (1024 * 1024)} МБ"));
            return Reply(await ads.AirRecordAsync(Auth.Nick(c), c.Request.Body, ct));
        });

        app.MapDelete("/api/ads/air", (HttpContext c, AdContest ads) =>
            Auth.IsAdmin(c) ? Reply(ads.AirClear()) : Deny());

        app.MapPost("/api/ads/air/every", (HttpContext c, AdEveryRequest req, AdContest ads) =>
            Auth.IsAdmin(c) ? Reply(ads.AirEvery(req.EveryTracks, req.MinMinutes)) : Deny());

        app.MapPost("/api/ads/air/now", (HttpContext c, AdJingle jingle) =>
            Auth.IsAdmin(c) ? Reply(jingle.PlayNow()) : Deny());

        // ---- бібліотека реклам і ротація: тільки господар ----
        app.MapGet("/api/ads/library", (HttpContext c, AdLibrary library, AdContest ads, AdJingle jingle, IVoiceSaver voice, IOptionsMonitor<AdOptions> opts) =>
        {
            if (!Auth.IsAdmin(c)) return Deny();
            var (every, minutes) = ads.Frequency();
            var o = opts.CurrentValue;
            var fallback = ads.OnAir();
            return Results.Ok(new
            {
                items = library.All().Select(a => new
                {
                    id = a.Id, trackId = a.TrackId, title = a.Title, seconds = a.Seconds, enabled = a.Enabled,
                    plays = a.Plays, lastPlayedAt = a.LastPlayedAt, createdAt = a.CreatedAt,
                    missing = voice.FilePath(a.TrackId) is null,
                }),
                everyTracks = every, minMinutes = minutes, since = jingle.Since,
                jingle = o.Enabled && o.Jingle,
                reward = new { amount = Math.Max(0, o.ListenReward), dailyCap = o.ListenDailyCap },
                fallback = fallback is null ? null : new { nick = fallback.Nick, seconds = fallback.Seconds, house = fallback.House },
                maxMb = voice.MaxUploadBytes / (1024 * 1024),
            });
        });

        // Тіло — сам аудіофайл (будь-який формат, який з'їсть ffmpeg); назва — у ?title=
        app.MapPost("/api/ads/library", async (HttpContext c, string? title, AdLibrary library, IVoiceSaver voice, CancellationToken ct) =>
        {
            if (!Auth.IsAdmin(c)) return Deny();
            if (c.Request.ContentLength > voice.MaxUploadBytes)
                return Reply((false, $"Завеликий файл, ліміт {voice.MaxUploadBytes / (1024 * 1024)} МБ"));
            return Reply(await library.AddAsync(c.Request.Body, title, Auth.Nick(c), ct));
        });

        app.MapPatch("/api/ads/library/{id:long}", (HttpContext c, long id, AdClipPatch req, AdLibrary library) =>
        {
            if (!Auth.IsAdmin(c)) return Deny();
            (bool Ok, string Message) r = (false, "Нема що міняти");
            if (req.Title is not null) r = library.Rename(id, req.Title);
            if (req.Enabled is { } on && (req.Title is null || r.Ok)) r = library.SetEnabled(id, on);
            return Reply(r);
        });

        app.MapPost("/api/ads/library/all", (HttpContext c, AdAllRequest req, AdLibrary library) =>
            Auth.IsAdmin(c) ? Reply(library.SetAll(req.Enabled)) : Deny());

        app.MapDelete("/api/ads/library/{id:long}", (HttpContext c, long id, AdLibrary library) =>
            Auth.IsAdmin(c) ? Reply(library.Delete(id)) : Deny());

        app.MapPost("/api/ads/library/{id:long}/now", (HttpContext c, long id, AdJingle jingle) =>
            Auth.IsAdmin(c) ? Reply(jingle.PlayClip(id)) : Deny());

        app.MapPost("/api/ads/new", async (HttpContext c, AdContest ads, CancellationToken ct) =>
            Auth.IsAdmin(c) ? Reply(await ads.OpenAsync(ct)) : Deny());

        app.MapPost("/api/ads/{id:long}/close", (HttpContext c, long id, AdContest ads) =>
            Auth.IsAdmin(c) ? Reply(ads.Close(id)) : Deny());

        // Тіло — сирий запис із мікрофона, як у /api/voice: json тут ні до чого.
        app.MapPost("/api/ads/{id:long}/entry", async (HttpContext c, long id, AdContest ads, IVoiceSaver voice, CancellationToken ct) =>
        {
            if (c.Request.ContentLength > voice.MaxUploadBytes)
                return Reply((false, $"Задовгий запис, ліміт {voice.MaxUploadBytes / (1024 * 1024)} МБ"));
            return Reply(await ads.EnterAsync(id, Auth.Nick(c), c.Request.Body, ct));
        });

        app.MapDelete("/api/ads/{id:long}/entry", (HttpContext c, long id, AdContest ads) =>
            Reply(ads.DropEntry(id, Auth.Nick(c))));

        app.MapPost("/api/ads/{id:long}/vote", (HttpContext c, long id, AdVoteRequest req, AdContest ads) =>
            Reply(ads.Vote(id, Auth.Nick(c), req.EntryId)));

        return app;
    }
}
