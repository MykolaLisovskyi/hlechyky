using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Одна реклама в бібліотеці господаря. <c>Enabled</c> — чи бере її ротація.</summary>
public sealed record AdClip(long Id, string TrackId, string Title, int Seconds, bool Enabled, int Plays,
    DateTimeOffset? LastPlayedAt, DateTimeOffset CreatedAt);

/// <summary>Таблиця бібліотеки реклам. Як і в конкурсу, DDL і SQL живуть тут, а від <see cref="Db"/> — лише з'єднання.</summary>
public sealed class AdLibraryStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS ad_library(
            id INTEGER PRIMARY KEY AUTOINCREMENT, track_id TEXT NOT NULL UNIQUE, title TEXT NOT NULL,
            dur_sec INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 1,
            plays INTEGER NOT NULL DEFAULT 0, last_played_at TEXT, created_at TEXT NOT NULL);
        """;
    const string Cols = "id, track_id, title, dur_sec, enabled, plays, last_played_at, created_at";

    readonly Db _db;

    public AdLibraryStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, Schema); return 0; });
    }

    public long Add(string trackId, string title, int seconds, bool enabled, DateTimeOffset now) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO ad_library(track_id, title, dur_sec, enabled, created_at) VALUES($t, $title, $d, $e, $now);
            SELECT last_insert_rowid();
            """, ("$t", trackId), ("$title", title), ("$d", seconds), ("$e", enabled ? 1 : 0), ("$now", Iso(now)));
        return (long)cmd.ExecuteScalar()!;
    });

    public List<AdClip> All() => _db.With(c => Read(c, $"SELECT {Cols} FROM ad_library ORDER BY id"));

    public AdClip? Get(long id) => _db.With(c => Read(c, $"SELECT {Cols} FROM ad_library WHERE id = $id", ("$id", id)).FirstOrDefault());

    public bool SetTitle(long id, string title) => _db.With(c => Exec(c, "UPDATE ad_library SET title = $t WHERE id = $id", ("$t", title), ("$id", id)) > 0);

    public bool SetEnabled(long id, bool on) => _db.With(c => Exec(c, "UPDATE ad_library SET enabled = $e WHERE id = $id", ("$e", on ? 1 : 0), ("$id", id)) > 0);

    public int SetAllEnabled(bool on) => _db.With(c => Exec(c, "UPDATE ad_library SET enabled = $e", ("$e", on ? 1 : 0)));

    public bool Delete(long id) => _db.With(c => Exec(c, "DELETE FROM ad_library WHERE id = $id", ("$id", id)) > 0);

    public void MarkPlayed(string trackId, DateTimeOffset now) => _db.With(c =>
        Exec(c, "UPDATE ad_library SET plays = plays + 1, last_played_at = $now WHERE track_id = $t", ("$now", Iso(now)), ("$t", trackId)));

    static List<AdClip> Read(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<AdClip>();
        while (r.Read())
            list.Add(new AdClip(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4) == 1, r.GetInt32(5),
                r.IsDBNull(6) ? null : Ts(r.GetString(6)), Ts(r.GetString(7))));
        return list;
    }

    static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Бібліотека реклам господаря і ротація «випадково без повторів»: колода тасується, кожна увімкнена
/// реклама з живим файлом грає по разу, потім колода тасується знову — і та, що грала останньою, першою
/// в новій колоді не стане. Колода живе в пам'яті: після рестарту просто тасується наново.
/// </summary>
public sealed class AdLibrary(AdLibraryStore store, IVoiceSaver voice, AdContestStore contest, IClock clock, ILogger<AdLibrary> log)
{
    public const int MaxTitle = 60;

    readonly object _lock = new();
    readonly List<long> _deck = new();
    long? _last;
    HashSet<string>? _trackIds;

    public List<AdClip> All() => store.All();

    public AdClip? Get(long id) => store.Get(id);

    /// <summary>Чи це трек із бібліотеки — джингл так упізнає рекламу в ефірі.</summary>
    public bool IsAd(string trackId)
    {
        lock (_lock) return (_trackIds ??= store.All().Select(a => a.TrackId).ToHashSet(StringComparer.Ordinal)).Contains(trackId);
    }

    bool Live(AdClip a) => a.Enabled && voice.FilePath(a.TrackId) is not null;

    /// <summary>Чи є що крутити з бібліотеки.</summary>
    public bool HasLive() => store.All().Any(Live);

    /// <summary>Наступна реклама з колоди; null — у ротації порожньо.</summary>
    public AdClip? Take()
    {
        var live = store.All().Where(Live).ToDictionary(a => a.Id);
        if (live.Count == 0) return null;
        lock (_lock)
        {
            _deck.RemoveAll(id => !live.ContainsKey(id));
            if (_deck.Count == 0)
            {
                _deck.AddRange(live.Keys.OrderBy(_ => Random.Shared.Next()));
                // межа колод: та сама реклама двічі підряд — саме те, чого «без повторів» обіцяє не робити
                if (_deck.Count > 1 && _deck[0] == _last) (_deck[0], _deck[^1]) = (_deck[^1], _deck[0]);
            }
            var id = _deck[0];
            _deck.RemoveAt(0);
            _last = id;
            return live[id];
        }
    }

    public void Played(string trackId) => store.MarkPlayed(trackId, clock.UtcNow);

    public async Task<(bool Ok, string Message)> AddAsync(Stream body, string? title, string nick, CancellationToken ct)
    {
        if (!voice.Enabled) return (false, "Голосові вимкнені");
        TrackInfo track;
        try { (track, _) = await voice.SaveAsync(body, nick, ct); }
        catch (Exception ex) { return (false, "Не вийшло взяти файл: " + ex.Message); }
        var name = Clean(title) ?? $"Реклама {clock.UtcNow.ToLocalTime():dd.MM HH:mm}";
        store.Add(track.Id, name, track.DurationSec, enabled: true, clock.UtcNow);
        Forget();
        log.LogInformation("у бібліотеку реклам лягла «{Title}» ({Track}, {Sec} с)", name, track.Id, track.DurationSec);
        return (true, $"«{name}» у бібліотеці й у ротації");
    }

    public (bool Ok, string Message) Rename(long id, string? title)
    {
        if (Clean(title) is not { } name) return (false, $"Назва від 1 до {MaxTitle} символів");
        return store.SetTitle(id, name) ? (true, "Перейменовано") : (false, "Такої реклами нема");
    }

    public (bool Ok, string Message) SetEnabled(long id, bool on)
    {
        if (!store.SetEnabled(id, on)) return (false, "Такої реклами нема");
        Forget();
        return (true, on ? "У ротації" : "Прибрано з ротації");
    }

    public (bool Ok, string Message) SetAll(bool on)
    {
        var n = store.SetAllEnabled(on);
        Forget();
        return (true, on ? $"У ротації всі ({n})" : "Ротацію вимкнено, усі реклами лежать у бібліотеці");
    }

    public (bool Ok, string Message) Delete(long id)
    {
        if (store.Get(id) is not { } clip) return (false, "Такої реклами нема");
        store.Delete(id);
        Forget();
        // файл, який ще потрібен конкурсу чи ефіру господаря, лишаємо — бібліотека його лише позичала
        if (!contest.IsEntryTrack(clip.TrackId) && contest.Air().TrackId != clip.TrackId) voice.Delete(clip.TrackId);
        return (true, $"«{clip.Title}» видалено");
    }

    void Forget()
    {
        lock (_lock) _trackIds = null;
    }

    static string? Clean(string? title)
    {
        var t = (title ?? "").Trim();
        return t.Length is 0 or > MaxTitle ? null : t;
    }
}

/// <summary>Що зараз в ефірі — щоб нагорода знала, чи реклама дограла, а не полетіла в скіп.</summary>
public interface IAdOnAir
{
    (string? TrackId, bool SkipPending) Now();
}

public sealed class RadioOnAir(RadioEngine engine) : IAdOnAir
{
    public (string? TrackId, bool SkipPending) Now()
    {
        var n = engine.Snapshot().Now;
        return (n.Track?.Id, n.SkipPending);
    }
}

/// <summary>
/// Черепки за прослухану рекламу. Отримує той, у кого плеєр увімкнений (🎧) і тоді, коли реклама
/// заграла, і тоді, коли вона вже майже скінчилась (<see cref="SettleShare"/> її довжини), а сама реклама
/// за цей час не полетіла в скіп. Стеля на день — <c>Ad:ListenDailyCap</c> черепків.
/// </summary>
public sealed class AdListenRewards(Economy.Economy economy, Presence presence, IAdOnAir onAir, IClock clock,
    IOptionsMonitor<AdOptions> opts, ILogger<AdListenRewards> log)
{
    public const double SettleShare = 0.85;
    public const string CapKey = "ad-listen";

    public sealed record Pending(string TrackId, DateTimeOffset StartedAt, IReadOnlyCollection<string> Listeners);

    readonly object _lock = new();
    Pending? _pending;

    /// <summary>Затримка до перевірки; тести підміняють, щоб перевіряти самим.</summary>
    public Func<TimeSpan, Task> Delay { get; set; } = t => Task.Delay(t);

    static bool Named(string nick) => !string.IsNullOrWhiteSpace(nick) && nick != "гість";

    /// <summary>Реклама заграла: запам'ятати, хто слухає, і перевірити під кінець.</summary>
    public Pending? Start(TrackInfo track)
    {
        var o = opts.CurrentValue;
        if (o.ListenReward <= 0) return null;
        var p = new Pending(track.Id, clock.UtcNow, presence.Listening.Where(Named).ToList());
        lock (_lock) _pending = p;
        if (p.Listeners.Count == 0) return p;
        var wait = TimeSpan.FromSeconds(Math.Max(3, track.DurationSec * SettleShare));
        _ = Task.Run(async () =>
        {
            try
            {
                await Delay(wait);
                Settle(p);
            }
            catch (Exception ex) { log.LogWarning(ex, "нагорода за рекламу {Track} спіткнулась", track.Id); }
        });
        return p;
    }

    /// <summary>Роздати черепки за цю рекламу. Повертає, скільком людям нараховано.</summary>
    public int Settle(Pending p)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_pending, p)) return 0;   // уже грає інша реклама — ця своє відграла не до кінця
            _pending = null;
        }
        var (now, skip) = onAir.Now();
        if (now != p.TrackId || skip) return 0;
        var o = opts.CurrentValue;
        var still = presence.Listening.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paid = 0;
        foreach (var nick in p.Listeners.Where(still.Contains))
        {
            var key = EconomyStore.Key(nick);
            var r = economy.GrantCapped(nick, o.ListenReward, "ad:listen",
                $"ad-listen:{p.TrackId}:{p.StartedAt.UtcTicks}:{key}", CapKey, Math.Max(o.ListenReward, o.ListenDailyCap), o.ListenReward);
            if (r == GrantResult.Applied) paid++;
        }
        if (paid > 0) log.LogInformation("за рекламу {Track} черепки отримали {N}", p.TrackId, paid);
        return paid;
    }
}
