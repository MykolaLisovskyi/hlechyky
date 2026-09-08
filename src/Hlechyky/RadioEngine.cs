using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Hlechyky;

public sealed class Presence
{
    readonly ConcurrentDictionary<string, string> _conns = new();
    public void Set(string connId, string nick) => _conns[connId] = nick;
    public void Remove(string connId) => _conns.TryRemove(connId, out _);
    public string? Get(string connId) => _conns.TryGetValue(connId, out var n) ? n : null;
    public List<string> Online => _conns.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    public int Count => Online.Count;
}

/// <summary>
/// The room: one queue, one now-playing, one auto-DJ. Keeps liquidsoap fed with exactly one
/// pending request per queue so transitions are gapless, and learns what is on air from
/// liquidsoap's track callbacks (plus a poll as a safety net). The queue is persisted, and on
/// start-up the engine adopts whatever liquidsoap still holds, so a server restart loses nothing.
/// </summary>
public sealed class RadioEngine : BackgroundService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly Db _db;
    readonly YtDlpService _ytdlp;
    readonly YtMusicClient _ytm;
    readonly LiquidsoapClient _liq;
    readonly AutoDj _autoDj;
    readonly LastFmClient _lastFm;
    readonly Presence _presence;
    readonly IHubContext<RadioHub> _hub;
    readonly ILogger<RadioEngine> _log;
    readonly IOptionsMonitor<SiteOptions> _site;
    readonly IOptionsMonitor<YtDlpOptions> _yt;
    readonly IOptionsMonitor<AutoDjOptions> _adj;
    readonly IOptionsMonitor<IcecastOptions> _ice;

    readonly object _lock = new();
    readonly List<QueueItem> _queue = new();
    QueueItem? _autoNext;
    Task? _autoPrepare;
    DateTime _autoRetryAt = DateTime.MinValue;
    readonly HashSet<string> _autoFailed = new();
    /// <summary>Items removed while liquidsoap already held them: liquidsoap will not drop a prefetched request, so we skip it the moment it starts.</summary>
    readonly HashSet<string> _skipOnStart = new();
    readonly Dictionary<string, int> _redispatches = new();
    NowPlaying _now = new();
    bool _liqOk;
    int _listeners;
    bool _spotifyLive;
    string? _spotifyTitle;
    readonly SemaphoreSlim _dlGate = new(2, 2);
    readonly SemaphoreSlim _tickGate = new(1, 1);
    DateTime _lastIcecast = DateTime.MinValue;
    DateTime _lastReconcile = DateTime.MinValue;
    long _liqUptime = -1;
    long _persistVer, _persistedVer;
    readonly object _persistLock = new();
    readonly Random _rng = new();

    // suggestions: built from what is on air right now; a new track on air means a new set. The
    // first one is what the auto-DJ takes when the queue runs dry.
    /// <summary>The panel has four fixed slots: the auto-DJ's next track (when there is one) plus suggestions.</summary>
    const int Slots = 4;
    int SuggestionTarget => Slots - (_autoNext is null ? 0 : 1);
    readonly List<QueueItem> _suggestions = new();
    readonly Dictionary<string, DateTime> _dismissed = new();
    Task? _suggestTask;
    DateTime _suggestRetryAt = DateTime.MinValue;
    /// <summary>Identity of the seed the current suggestions were built from: "t:{trackId}", "s:{spotify title}" or "none".</summary>
    string? _suggestSeedKey;
    TrackInfo? _suggestSeed;
    (string Title, TrackInfo? Seed)? _spotifySeed;

    public RadioEngine(Db db, YtDlpService ytdlp, YtMusicClient ytm, LiquidsoapClient liq, AutoDj autoDj, LastFmClient lastFm,
        Presence presence, IHubContext<RadioHub> hub, ILogger<RadioEngine> log,
        IOptionsMonitor<SiteOptions> site, IOptionsMonitor<YtDlpOptions> yt, IOptionsMonitor<AutoDjOptions> adj,
        IOptionsMonitor<IcecastOptions> ice)
    {
        _db = db; _ytdlp = ytdlp; _ytm = ytm; _liq = liq; _autoDj = autoDj; _lastFm = lastFm; _presence = presence; _hub = hub; _log = log;
        _site = site; _yt = yt; _adj = adj; _ice = ice;
    }

    string Dj => _site.CurrentValue.DjName;
    string DjGen => _site.CurrentValue.DjNameGen;

    // ---------- snapshot / broadcast ----------

    public StateSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = new NowPlaying
            {
                Track = _now.Track, Source = _now.Source, ItemId = _now.ItemId, RequestedBy = _now.RequestedBy, Reason = _now.Reason, Via = _now.Via,
                StartedAt = _now.StartedAt, DurationSec = _now.DurationSec, PlayId = _now.PlayId, SkipPending = _now.SkipPending,
                Likers = _now.Track is null ? new() : _db.Likers(_now.Track.Id),
                SpotifyLive = _spotifyLive, SpotifyTitle = _spotifyTitle,
            };
            return new StateSnapshot
            {
                Now = now,
                Queue = _queue.Select(ToDto).ToList(),
                AutoNext = _autoNext is { } a ? ToDto(a) : null,
                Suggestions = _suggestions.Select(ToDto).ToList(),
                SuggestSeed = _suggestSeed,
                Online = _presence.Online,
                LiquidsoapOk = _liqOk,
                Listeners = _listeners,
                StreamDelaySeconds = _site.CurrentValue.StreamDelaySeconds,
                SiteName = _site.CurrentValue.Name,
                DjName = Dj,
                DjNameGen = DjGen,
                StreamUrl = _site.CurrentValue.PublicStreamUrl,
                LastFmEnabled = _lastFm.Enabled,
            };
        }
    }

    static QueueItemDto ToDto(QueueItem i) => new(i.ItemId, i.Track, i.RequestedBy, i.Status.ToString().ToLowerInvariant(), i.Error, i.Kind, i.Reason, i.Via, i.AddedAt);

    async Task BroadcastAsync()
    {
        try { await _hub.Clients.All.SendAsync("state", Snapshot()); }
        catch (Exception ex) { _log.LogWarning(ex, "broadcast failed"); }
    }

    void Broadcast() => _ = BroadcastAsync();

    async Task ChatAsync(string nick, string text, string kind)
    {
        try
        {
            var m = _db.AddChat(nick, text, kind);
            await _hub.Clients.All.SendAsync("chat", m);
        }
        catch (Exception ex) { _log.LogWarning(ex, "system chat failed"); }
    }

    /// <summary>Event log line ("владік додає …"): shown in the log tab, not among people's messages.</summary>
    void SystemChat(string text) => _ = ChatAsync(_site.CurrentValue.Name, text, "system");

    /// <summary>The DJ persona speaking in the chat.</summary>
    void DjChat(string text) => _ = ChatAsync(Dj, text, "dj");

    /// <summary>Writes the queue (order + who/why) to SQLite off the hot path; the newest snapshot always wins.</summary>
    void PersistQueue()
    {
        List<QueueItem> copy;
        long ver;
        lock (_lock)
        {
            copy = _queue.ToList();
            ver = ++_persistVer;
        }
        _ = Task.Run(() =>
        {
            lock (_persistLock)
            {
                if (ver <= _persistedVer) return;
                try { _db.SaveQueue(copy); _persistedVer = ver; }
                catch (Exception ex) { _log.LogWarning(ex, "persist queue failed"); }
            }
        });
    }

    // ---------- user actions ----------

    public async Task<(bool Ok, string Message)> AddAsync(string nick, bool isAdmin, string? input, SearchResult? pick, CancellationToken ct,
        string? via = null, string? reason = null, bool quiet = false)
    {
        TrackInfo track;
        try
        {
            track = pick is not null ? AutoDj.ToTrack(pick) : await ResolveInputAsync(input ?? "", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "resolve failed for {Input}", input);
            return (false, "Не вийшло розібрати: " + ex.Message);
        }
        if (_db.IsBanned(track.Id)) return (false, "Цей трек у бан-листі");
        var max = _yt.CurrentValue.MaxDurationSeconds;
        if (!isAdmin && track.DurationSec > max) return (false, $"Задовгий трек ({track.DurationSec / 60} хв), ліміт {max / 60} хв");
        lock (_lock)
        {
            if (_queue.Any(q => q.Track.Id == track.Id)) return (false, "Уже в черзі");
            if (_now.Track?.Id == track.Id && _now.Source is "user" or "autodj") return (false, "Уже грає");
            _queue.Add(new QueueItem { Track = track, RequestedBy = nick, Via = via, Reason = reason });
        }
        _db.UpsertTrack(track);
        PersistQueue();
        if (!quiet) SystemChat(via == "suggestion" ? $"{nick} бере пораду {DjGen}: {track.Label}" : $"{nick} додає {track.Label}");
        Broadcast();
        _ = TickSafeAsync();
        return (true, "Закинуто: " + track.Label);
    }

    /// <summary>Queue a track we already know (from likes, history, playlists) by id.</summary>
    public Task<(bool Ok, string Message)> AddKnownAsync(string trackId, string nick, bool isAdmin, CancellationToken ct, bool quiet = false)
    {
        var t = _db.GetTrack(trackId);
        if (t is null) return Task.FromResult((false, "Не знаю такого треку"));
        return AddAsync(nick, isAdmin, null, new SearchResult(t.Id, t.Title, t.Artist, t.Album, t.DurationSec, t.ThumbUrl), ct, quiet: quiet);
    }

    /// <summary>Queue several known tracks (a playlist), optionally shuffled. Returns how many actually got in.</summary>
    public async Task<int> AddManyAsync(IEnumerable<string> trackIds, string nick, bool isAdmin, bool shuffle, CancellationToken ct)
    {
        var ids = trackIds.ToList();
        if (shuffle) ids = ids.OrderBy(_ => _rng.Next()).ToList();
        var n = 0;
        foreach (var id in ids)
            if ((await AddKnownAsync(id, nick, isAdmin, ct, quiet: true)).Ok) n++;
        return n;
    }

    async Task<TrackInfo> ResolveInputAsync(string input, CancellationToken ct)
    {
        input = input.Trim();
        if (input.Length == 0) throw new InvalidOperationException("порожній запит");
        if (SpotifyResolver.IsSpotify(input))
        {
            var sp = await SpotifyResolver.ResolveAsync(input, ct);
            var label = string.IsNullOrWhiteSpace(sp.Artist) ? sp.Title : $"{sp.Artist} — {sp.Title}";
            var r = await _ytm.ResolveAsync(sp.Artist, sp.Title, ct);
            if (r is null)
            {
                // loose match: the first search hit of about the same length
                var hits = await _ytm.SearchSongsAsync(string.IsNullOrWhiteSpace(sp.Artist) ? sp.Title : $"{sp.Artist} {sp.Title}", 5, ct);
                r = hits.FirstOrDefault(f => sp.DurationSec == 0 || f.DurationSec == 0 || Math.Abs(f.DurationSec - sp.DurationSec) <= 20) ?? hits.FirstOrDefault();
            }
            if (r is null) throw new InvalidOperationException($"не знайшов «{label}» на YouTube Music");
            _log.LogInformation("spotify {Id} = {Label} -> YTM {Yt} ({YtLabel})", sp.Id, label, r.Id, $"{r.Artist} — {r.Title}");
            return AutoDj.ToTrack(r);
        }
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var vid = YouTubeId(uri);
            if (vid is not null)
            {
                try
                {
                    var r = await _ytm.LookupAsync(vid, ct);
                    if (r is not null && r.DurationSec > 0) return AutoDj.ToTrack(r);
                }
                catch (Exception ex) { _log.LogDebug(ex, "YTM lookup failed for {Id}", vid); }
            }
            return await _ytdlp.FetchInfoAsync(input, ct);
        }
        var found = await _ytm.SearchSongsAsync(input, 1, ct);
        return found.Count > 0 ? AutoDj.ToTrack(found[0]) : throw new InvalidOperationException("нічого не знайшов");
    }

    static string? YouTubeId(Uri u)
    {
        var host = u.Host.ToLowerInvariant();
        if (host.EndsWith("youtu.be", StringComparison.Ordinal))
        {
            var seg = u.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
            return seg is { Length: 11 } ? seg : null;
        }
        if (!host.EndsWith("youtube.com", StringComparison.Ordinal)) return null;
        var q = QueryHelpers.ParseQuery(u.Query);
        if (q.TryGetValue("v", out var v) && v.ToString() is { Length: 11 } vid) return vid;
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length >= 2 && segs[0] is "shorts" or "embed" or "live" && segs[1].Length == 11) return segs[1];
        return null;
    }

    public (bool Ok, string Message) Remove(string itemId, string nick, bool isAdmin)
    {
        QueueItem? item;
        lock (_lock)
        {
            item = _queue.FirstOrDefault(q => q.ItemId == itemId);
            if (item is null) return (false, "Нема такого в черзі");
            if (!isAdmin && !item.RequestedBy.Equals(nick, StringComparison.OrdinalIgnoreCase)) return (false, "Можна прибирати тільки свої треки");
            _queue.Remove(item);
        }
        DropFromLiquidsoap("userq", item);
        PersistQueue();
        SystemChat($"{nick} прибирає {item.Track.Label} з черги");
        Broadcast();
        _ = TickSafeAsync();
        return (true, "Прибрано");
    }

    /// <summary>
    /// Take a request away from liquidsoap. "remove" only works while the request still sits in the
    /// queue; once prefetched it stays alive, so in that case we remember to skip it as it starts.
    /// </summary>
    void DropFromLiquidsoap(string queue, QueueItem item)
    {
        if (item.Status != ItemStatus.Dispatched || item.Rid is null) return;
        var rid = item.Rid;
        _ = Task.Run(async () =>
        {
            var alive = true;
            try
            {
                await _liq.RemoveAsync(queue, rid);
                alive = (await _liq.AllRequestsAsync()).Contains(rid);
            }
            catch (Exception ex) { _log.LogDebug(ex, "remove {Rid} failed", rid); }
            if (alive)
            {
                lock (_lock) _skipOnStart.Add(item.ItemId);
                _log.LogInformation("liquidsoap keeps rid {Rid} ({Label}); will skip it on start", rid, item.Track.Label);
            }
        });
    }

    public (bool Ok, string Message) Move(string itemId, int toIndex, string nick, bool isAdmin)
    {
        lock (_lock)
        {
            var idx = _queue.FindIndex(q => q.ItemId == itemId);
            if (idx < 0) return (false, "Нема такого в черзі");
            var item = _queue[idx];
            if (item.Status == ItemStatus.Dispatched) return (false, "Цей трек уже наступний, його не посунеш");
            if (!isAdmin && !item.RequestedBy.Equals(nick, StringComparison.OrdinalIgnoreCase)) return (false, "Можна рухати тільки свої треки");
            var min = _queue.Count > 0 && _queue[0].Status == ItemStatus.Dispatched ? 1 : 0;
            toIndex = Math.Clamp(toIndex, min, _queue.Count - 1);
            _queue.RemoveAt(idx);
            _queue.Insert(toIndex, item);
        }
        PersistQueue();
        Broadcast();
        return (true, "Ок");
    }

    /// <summary>One click by anyone switches to the next track at once; there is no voting.</summary>
    public async Task<(bool Ok, string Message)> SkipAsync(string nick)
    {
        string? label;
        string queue;
        lock (_lock)
        {
            if (_now.Source is not ("user" or "autodj")) return (false, "Зараз нема що скіпати");
            if (_now.SkipPending) return (true, "Уже перемикаю");
            queue = _now.Source == "user" ? "userq" : "autoq";
            label = _now.Track?.Label;
        }
        try { await _liq.SkipAsync(queue); }
        catch (Exception ex) { return (false, "liquidsoap не відповідає: " + ex.Message); }
        lock (_lock)
        {
            _now.SkipPending = true;
            if (_now.PlayId > 0) _db.EndPlay(_now.PlayId, skipped: true);
        }
        SystemChat($"{nick} скіпає {label}");
        Broadcast();
        return (true, "Скіп, перемикаю");
    }

    public (bool Liked, int Count, List<string> Likers) ToggleLike(string trackId, string nick)
    {
        var liked = _db.ToggleLike(trackId, nick);
        var likers = _db.Likers(trackId);
        if (liked)
        {
            var t = _db.GetTrack(trackId);
            if (t is not null) SystemChat($"{nick} ❤ {t.Label}");
        }
        Broadcast();
        return (liked, likers.Count, likers);
    }

    public async Task<(bool Ok, string Message)> BanAsync(string trackId, string nick)
    {
        _db.Ban(trackId, nick);
        List<QueueItem> removed;
        bool skipNow;
        QueueItem? auto = null;
        lock (_lock)
        {
            removed = _queue.Where(q => q.Track.Id == trackId).ToList();
            foreach (var r in removed) _queue.Remove(r);
            skipNow = _now.Track?.Id == trackId && _now.Source is "user" or "autodj";
            if (_autoNext?.Track.Id == trackId) { auto = _autoNext; _autoNext = null; }
        }
        foreach (var r in removed) DropFromLiquidsoap("userq", r);
        if (auto is not null) DropFromLiquidsoap("autoq", auto);
        if (skipNow) { try { await _liq.SkipAsync(_now.Source == "user" ? "userq" : "autoq"); } catch { /* best effort */ } }
        PersistQueue();
        var t = _db.GetTrack(trackId);
        SystemChat($"{nick} банить {t?.Label ?? trackId}");
        Broadcast();
        _ = TickSafeAsync();
        return (true, "Забанено");
    }

    // ---------- suggestion bar ----------

    /// <summary>"Не те": drops a suggestion, or the auto-DJ's already chosen next track (then the next suggestion takes its place).</summary>
    public (bool Ok, string Message) DismissSuggestion(string itemId, string nick)
    {
        QueueItem? s;
        QueueItem? auto = null;
        lock (_lock)
        {
            s = _suggestions.FirstOrDefault(x => x.ItemId == itemId);
            if (s is not null) _suggestions.Remove(s);
            else if (_autoNext?.ItemId == itemId) { s = auto = _autoNext; _autoNext = null; }
            if (s is null) return (false, "Ця пропозиція вже зникла");
            _dismissed[s.Track.Id] = DateTime.UtcNow;
        }
        if (auto is not null) DropFromLiquidsoap("autoq", auto);
        _log.LogInformation("{Nick} dismissed {What} {Label}", nick, auto is null ? "suggestion" : "auto-next", s.Track.Label);
        if (auto is not null) SystemChat($"{nick} відхиляє {s.Track.Label}, {Dj} шукає інше");
        EnsureSuggestions();
        Broadcast();
        _ = TickSafeAsync();
        return (true, "Добре, пошукаю інше");
    }

    public async Task<(bool Ok, string Message)> AddSuggestionAsync(string itemId, string nick, bool isAdmin, CancellationToken ct)
    {
        QueueItem? s;
        lock (_lock) s = _suggestions.FirstOrDefault(x => x.ItemId == itemId);
        if (s is null) return (false, "Ця пропозиція вже зникла");
        var t = s.Track;
        var r = await AddAsync(nick, isAdmin, null, new SearchResult(t.Id, t.Title, t.Artist, t.Album, t.DurationSec, t.ThumbUrl), ct, via: "suggestion", reason: s.Reason);
        if (r.Ok)
        {
            lock (_lock) _suggestions.Remove(s);
            EnsureSuggestions();
            Broadcast();
        }
        return r;
    }

    /// <summary>What the suggestions follow: the track on air, else the Spotify fallback's title, else "none" (last played is used).</summary>
    string CurrentSeedKey() =>
        _now.Source is "user" or "autodj" && _now.Track is not null ? "t:" + _now.Track.Id
        : _spotifyLive && !string.IsNullOrWhiteSpace(_spotifyTitle) ? "s:" + _spotifyTitle
        : "none";

    /// <summary>Keeps <see cref="SuggestionTarget"/> suggestions for the current seed; a new track on air throws the old set away.</summary>
    void EnsureSuggestions()
    {
        lock (_lock)
        {
            if (!_adj.CurrentValue.Enabled) return;
            var key = CurrentSeedKey();
            if (key != _suggestSeedKey)
            {
                _suggestions.Clear();
                _suggestSeed = null;
                _suggestSeedKey = key;
                _suggestRetryAt = DateTime.MinValue;
            }
            if (_suggestions.Count >= SuggestionTarget || DateTime.UtcNow < _suggestRetryAt) return;
            if (_suggestTask is { IsCompleted: false }) return;
            _suggestTask = FillSuggestionsAsync(key);
        }
    }

    /// <param name="key">The seed these suggestions are for; if it changed while we were looking, the result is thrown away.</param>
    async Task FillSuggestionsAsync(string key)
    {
        try
        {
            HashSet<string> exclude;
            int need;
            TrackInfo? seed;
            string? spotifyTitle;
            lock (_lock)
            {
                exclude = _queue.Select(q => q.Track.Id).ToHashSet();
                exclude.UnionWith(_suggestions.Select(s => s.Track.Id));
                exclude.UnionWith(_autoFailed);
                var ttl = TimeSpan.FromHours(Math.Max(1, _adj.CurrentValue.NoRepeatHours));
                foreach (var (id, at) in _dismissed.ToList())
                {
                    if (DateTime.UtcNow - at > ttl) _dismissed.Remove(id);
                    else exclude.Add(id);
                }
                if (_now.Track is not null) exclude.Add(_now.Track.Id);
                if (_autoNext is not null) exclude.Add(_autoNext.Track.Id);
                need = SuggestionTarget - _suggestions.Count;
                seed = _now.Source is "user" or "autodj" ? _now.Track : null;
                spotifyTitle = seed is null && _spotifyLive ? _spotifyTitle : null;
            }
            if (need <= 0) return;
            seed ??= await SpotifySeedAsync(spotifyTitle) ?? _autoDj.FallbackSeed();
            var picks = await _autoDj.PickManyAsync(seed, exclude, need, CancellationToken.None);
            lock (_lock)
            {
                if (key != _suggestSeedKey) return; // the track changed meanwhile; these were built for the old one
                _suggestSeed = seed;
                if (picks.Count == 0) _suggestRetryAt = DateTime.UtcNow.AddSeconds(45);
                foreach (var p in picks)
                    if (_suggestions.Count < SuggestionTarget && _suggestions.All(s => s.Track.Id != p.Track.Id))
                        _suggestions.Add(new QueueItem { Track = p.Track, RequestedBy = "auto-DJ", Kind = "suggestion", Reason = p.Reason, Status = ItemStatus.Ready });
            }
            Broadcast();
            await TickSafeAsync(); // a queue that ran dry takes the first one straight away
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "suggestions failed");
            _suggestRetryAt = DateTime.UtcNow.AddSeconds(45);
        }
    }

    /// <summary>While the Spotify fallback is on air, its current "Artist - Title" seeds the suggestions.</summary>
    async Task<TrackInfo?> SpotifySeedAsync(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0) return null;
        if (_spotifySeed is { } cached && cached.Title == title) return cached.Seed;
        TrackInfo? seed = null;
        try
        {
            var r = await _ytm.ResolveAsync(title[..dash].Trim(), title[(dash + 3)..].Trim(), CancellationToken.None);
            if (r is not null) seed = AutoDj.ToTrack(r);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "spotify seed resolve failed for {Title}", title);
        }
        _spotifySeed = (title, seed);
        return seed;
    }

    // ---------- liquidsoap events ----------

    public async Task OnLiquidsoapTrackAsync(Dictionary<string, string> m)
    {
        m.TryGetValue("rt_kind", out var kind);
        m.TryGetValue("rt_item", out var itemId);
        _log.LogInformation("liquidsoap track: kind={Kind} item={Item} title={Title}", kind, itemId, m.GetValueOrDefault("title"));
        if (kind is not ("user" or "autodj")) return; // fallback / silence are detected by polling
        var queue = kind == "user" ? "userq" : "autoq";

        QueueItem? item = null;
        long prevPlay;
        bool skipIt;
        lock (_lock)
        {
            if (_now.ItemId == itemId && itemId is not null)
            {
                // Callback and poll racing on a fresh track is normal; the same item starting again
                // after it has been playing for a while is liquidsoap replaying a request we pushed twice.
                if ((DateTimeOffset.UtcNow - _now.StartedAt).TotalSeconds < 15) return;
                _log.LogWarning("duplicate play of {Item} ({Title}); skipping it", itemId, m.GetValueOrDefault("title"));
                skipIt = true;
            }
            else skipIt = itemId is not null && _skipOnStart.Remove(itemId);
        }
        if (skipIt)
        {
            try { await _liq.SkipAsync(queue); } catch (Exception ex) { _log.LogWarning(ex, "skip of unwanted request failed"); }
            return;
        }

        lock (_lock)
        {
            prevPlay = _now.PlayId;
            if (kind == "user")
            {
                item = _queue.FirstOrDefault(q => q.ItemId == itemId);
                if (item is not null) _queue.Remove(item);
            }
            else if (_autoNext?.ItemId == itemId)
            {
                item = _autoNext;
                _autoNext = null;
            }
            var track = item?.Track ?? (m.TryGetValue("track_id", out var tid) ? _db.GetTrack(tid) : null);
            _now = new NowPlaying
            {
                Track = track, Source = kind, ItemId = itemId, StartedAt = DateTimeOffset.UtcNow,
                RequestedBy = item?.Kind == "user" ? item.RequestedBy : null,
                Reason = item?.Reason, Via = item?.Via, DurationSec = track?.DurationSec ?? 0,
            };
        }
        if (item?.Kind == "user") PersistQueue();
        EnsureSuggestions(); // a new track on air: the old suggestions go, new ones are built from this one
        if (prevPlay > 0) _db.EndPlay(prevPlay, skipped: false);
        if (_now.Track is not null)
        {
            var pid = _db.StartPlay(_now.Track.Id, kind, _now.RequestedBy, _now.Reason, _now.Via);
            lock (_lock) _now.PlayId = pid;
        }
        if (kind == "autodj" && _now.Track is not null) DjChat(DjLine(_now.Track.Label, _now.Reason));
        try
        {
            // the file's real length beats the catalogue's; liquidsoap knows it once the track is on air
            var remaining = await _liq.RemainingAsync();
            if (remaining is > 1) lock (_lock) { if (_now.ItemId == itemId) _now.DurationSec = (int)Math.Ceiling(remaining.Value); }
        }
        catch { /* cosmetic */ }
        await BroadcastAsync();
        await TickSafeAsync();
    }

    string DjLine(string label, string? reason)
    {
        var r = string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}";
        string[] lines =
        [
            $"Черга порожня, тож ставлю {label}{r}.",
            $"Тримайте: {label}{r}.",
            $"Ніхто нічого не кинув, тому {label}{r}.",
            $"Витягнув з полиці {label}{r}.",
            $"Моя черга. {label}{r}.",
        ];
        return lines[_rng.Next(lines.Length)];
    }

    // ---------- background loop ----------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(500, ct);
        try { await AdoptAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "adopt failed"); _db.EndOpenPlays(); }
        while (!ct.IsCancellationRequested)
        {
            await TickSafeAsync(ct);
            try { await PollAsync(ct); }
            catch (Exception ex) { _log.LogDebug(ex, "poll failed"); }
            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Start-up: restore the persisted queue and adopt what liquidsoap still holds (its pending
    /// requests and the track on air), so a server restart is invisible to listeners.
    /// </summary>
    async Task AdoptAsync(CancellationToken ct)
    {
        var persisted = _db.LoadQueue();
        var alive = new Dictionary<string, (string Rid, string Kind, Dictionary<string, string> Meta)>();
        Dictionary<string, string> onAir = new();
        double? remaining = null;
        try
        {
            _liqUptime = await _liq.UptimeSecondsAsync(ct);
            onAir = await _liq.CurrentMetadataAsync(ct);
            remaining = await _liq.RemainingAsync(ct);
            foreach (var rid in await _liq.AllRequestsAsync(ct))
            {
                var meta = await _liq.MetadataAsync(rid, ct);
                if (meta.GetValueOrDefault("rt_kind") is not ("user" or "autodj") || !meta.TryGetValue("rt_item", out var it)) continue;
                if (!alive.ContainsKey(it)) alive[it] = (rid, meta["rt_kind"], meta); // a second rid for the same item is a duplicate; the on-start guard skips it
            }
            _liqOk = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "liquidsoap not reachable at start-up; restoring the queue only");
        }

        var onAirItem = onAir.GetValueOrDefault("rt_kind") is "user" or "autodj" && remaining is not null ? onAir.GetValueOrDefault("rt_item") : null;
        var restored = 0;
        lock (_lock)
        {
            foreach (var p in persisted)
            {
                if (p.ItemId == onAirItem) continue;
                var item = new QueueItem { ItemId = p.ItemId, Track = p.Track, RequestedBy = p.RequestedBy, Kind = p.Kind, Reason = p.Reason, Via = p.Via, AddedAt = p.AddedAt };
                if (alive.TryGetValue(p.ItemId, out var a))
                {
                    item.Status = ItemStatus.Dispatched;
                    item.Rid = a.Rid;
                    item.FilePath = _ytdlp.FindCached(p.Track.Id);
                }
                if (p.Kind == "autodj") { if (item.Status == ItemStatus.Dispatched) _autoNext = item; }
                else { _queue.Add(item); restored++; }
            }
            foreach (var (it, a) in alive)
            {
                if (it == onAirItem || _queue.Any(q => q.ItemId == it) || _autoNext?.ItemId == it) continue;
                var tid = a.Meta.GetValueOrDefault("track_id") ?? "";
                var track = _db.GetTrack(tid) ?? new TrackInfo(tid, a.Meta.GetValueOrDefault("title") ?? "?", a.Meta.GetValueOrDefault("artist") ?? "", 0, null, $"https://music.youtube.com/watch?v={tid}", null);
                var item = new QueueItem
                {
                    ItemId = it, Track = track, RequestedBy = a.Kind == "user" ? "хтось" : "auto-DJ", Kind = a.Kind,
                    Status = ItemStatus.Dispatched, Rid = a.Rid, FilePath = _ytdlp.FindCached(tid),
                };
                if (a.Kind == "user") { _queue.Insert(0, item); restored++; }
                else _autoNext ??= item;
            }
        }

        if (onAirItem is not null)
        {
            var kind = onAir["rt_kind"];
            var tid = onAir.GetValueOrDefault("track_id") ?? "";
            var track = _db.GetTrack(tid);
            var p = persisted.FirstOrDefault(x => x.ItemId == onAirItem);
            var dur = track?.DurationSec ?? 0;
            var elapsed = dur > 0 && remaining is { } rem && rem <= dur ? dur - rem : 0;
            var pid = _db.OpenPlayId(tid);
            _db.EndOpenPlaysExcept(pid);
            // the on-air item has already left the queue; the previous process recorded who asked for it in the open play
            var play = pid > 0 ? _db.GetPlay(pid) : null;
            var by = p?.RequestedBy ?? play?.RequestedBy;
            var reason = p?.Reason ?? play?.Reason;
            var via = p?.Via ?? play?.Via;
            if (pid == 0 && track is not null) pid = _db.StartPlay(tid, kind, by, reason, via);
            lock (_lock)
            {
                _now = new NowPlaying
                {
                    Track = track, Source = kind, ItemId = onAirItem, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-elapsed),
                    RequestedBy = kind == "user" ? by : null, Reason = reason, Via = via,
                    DurationSec = dur, PlayId = pid,
                };
            }
        }
        else _db.EndOpenPlays();

        _log.LogInformation("adopted: {Restored} queued, {Alive} alive in liquidsoap, on air: {OnAir}", restored, alive.Count, _now.Track?.Label ?? _now.Source);
        PersistQueue();
        Broadcast();
    }

    async Task TickSafeAsync(CancellationToken ct = default)
    {
        try { await TickAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "tick failed"); }
    }

    async Task TickAsync(CancellationToken ct)
    {
        if (!await _tickGate.WaitAsync(0, ct)) return;
        try
        {
            List<QueueItem> toDownload;
            lock (_lock) toDownload = _queue.Where(q => q.Status == ItemStatus.Queued).Take(2).ToList();
            foreach (var item in toDownload) StartDownload(item);

            QueueItem? head;
            bool anyDispatched;
            lock (_lock)
            {
                anyDispatched = _queue.Any(q => q.Status == ItemStatus.Dispatched);
                head = _queue.FirstOrDefault();
            }
            if (!anyDispatched && head is not null)
            {
                if (head.Status == ItemStatus.Failed)
                {
                    lock (_lock) _queue.Remove(head);
                    PersistQueue();
                    SystemChat($"Не вийшло завантажити {head.Track.Label}: {head.Error}");
                    Broadcast();
                }
                else if (head.Status == ItemStatus.Ready)
                {
                    await DispatchAsync("userq", head, ct);
                }
            }

            QueueItem? first = null;
            lock (_lock)
            {
                var needAuto = _adj.CurrentValue.Enabled && _autoNext is null && DateTime.UtcNow >= _autoRetryAt
                               && _queue.All(q => q.Status == ItemStatus.Dispatched)
                               && (_autoPrepare is null || _autoPrepare.IsCompleted);
                // the queue is about to run dry: the first suggestion becomes the auto-DJ's next track
                if (needAuto && _suggestions.Count > 0) { first = _suggestions[0]; _suggestions.RemoveAt(0); }
            }
            if (first is not null) _autoPrepare = PrepareAutoAsync(first);

            QueueItem? auto;
            lock (_lock) auto = _autoNext;
            if (auto is { Status: ItemStatus.Ready }) await DispatchAsync("autoq", auto, ct);
            else if (auto is { Status: ItemStatus.Failed })
            {
                lock (_lock) { _autoFailed.Add(auto.Track.Id); _autoNext = null; }
                SystemChat($"{Dj} не зміг скачати {auto.Track.Label}: {auto.Error}");
                Broadcast();
            }

            EnsureSuggestions();
        }
        finally
        {
            _tickGate.Release();
        }
    }

    async Task DispatchAsync(string queue, QueueItem item, CancellationToken ct)
    {
        if (item.FilePath is null) return;
        var uri = LiquidsoapClient.Annotate(
            [("rt_kind", item.Kind), ("rt_item", item.ItemId), ("track_id", item.Track.Id), ("title", item.Track.Title), ("artist", item.Track.Artist)],
            _liq.ContainerPath(item.FilePath));
        string? rid;
        try { rid = await _liq.PushAsync(queue, uri, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "push to {Queue} failed", queue);
            _liqOk = false;
            return;
        }
        if (rid is null) return;
        lock (_lock)
        {
            item.Status = ItemStatus.Dispatched;
            item.Rid = rid;
        }
        _liqOk = true;
        _log.LogInformation("dispatched {Label} to {Queue} as rid {Rid}", item.Track.Label, queue, rid);
        Broadcast();
    }

    void StartDownload(QueueItem item)
    {
        lock (_lock)
        {
            if (item.Status != ItemStatus.Queued) return;
            item.Status = ItemStatus.Downloading;
        }
        _ = Task.Run(async () =>
        {
            await _dlGate.WaitAsync();
            try
            {
                var path = await _ytdlp.DownloadAsync(item.Track, CancellationToken.None);
                _db.SetTrackFile(item.Track.Id, path);
                lock (_lock) { item.FilePath = path; item.Status = ItemStatus.Ready; }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "download failed for {Id}", item.Track.Id);
                lock (_lock) { item.Status = ItemStatus.Failed; item.Error = ex.Message; }
            }
            finally
            {
                _dlGate.Release();
            }
            Broadcast();
            await TickSafeAsync();
        });
    }

    /// <summary>Downloads the suggestion that became the auto-DJ's next track; the tick dispatches it once it is ready.</summary>
    async Task PrepareAutoAsync(QueueItem pick)
    {
        try
        {
            _db.UpsertTrack(pick.Track);
            // same ItemId as the suggestion card, so in the UI it just turns into "next" instead of being redrawn
            var item = new QueueItem { ItemId = pick.ItemId, Track = pick.Track, RequestedBy = "auto-DJ", Kind = "autodj", Reason = pick.Reason, Status = ItemStatus.Downloading };
            lock (_lock) _autoNext = item;
            Broadcast();
            await _dlGate.WaitAsync();
            try
            {
                var path = await _ytdlp.DownloadAsync(item.Track, CancellationToken.None);
                _db.SetTrackFile(item.Track.Id, path);
                lock (_lock) { item.FilePath = path; item.Status = ItemStatus.Ready; }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "auto-DJ download failed for {Id}", item.Track.Id);
                lock (_lock) { item.Status = ItemStatus.Failed; item.Error = ex.Message; }
            }
            finally
            {
                _dlGate.Release();
            }
            Broadcast();
            await TickSafeAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "auto-DJ prepare failed");
            _autoRetryAt = DateTime.UtcNow.AddSeconds(30);
        }
    }

    async Task PollAsync(CancellationToken ct)
    {
        double? remaining;
        Dictionary<string, string> meta;
        try
        {
            var uptime = await _liq.UptimeSecondsAsync(ct);
            if (_liqUptime >= 0 && uptime < _liqUptime) OnLiquidsoapRestarted();
            _liqUptime = uptime;
            remaining = await _liq.RemainingAsync(ct);
            meta = await _liq.CurrentMetadataAsync(ct);
            if (!_liqOk) { _liqOk = true; Broadcast(); }
        }
        catch
        {
            if (_liqOk) { _liqOk = false; Broadcast(); }
            return;
        }

        var kind = meta.GetValueOrDefault("rt_kind");
        if (remaining is not null && kind is "user" or "autodj")
        {
            string? current;
            lock (_lock) current = _now.ItemId;
            if (meta.TryGetValue("rt_item", out var it) && it != current) await OnLiquidsoapTrackAsync(meta);
        }
        else if (remaining is null)
        {
            var changed = false;
            lock (_lock)
            {
                if (_now.Source is "user" or "autodj")
                {
                    _now = new NowPlaying { Source = "fallback", StartedAt = DateTimeOffset.UtcNow };
                    changed = true;
                }
            }
            if (changed)
            {
                _db.EndOpenPlays();
                await PollIcecastAsync(ct);
                Broadcast();
                await TickSafeAsync(ct);
            }
        }

        if (DateTime.UtcNow - _lastIcecast > TimeSpan.FromSeconds(10))
        {
            _lastIcecast = DateTime.UtcNow;
            await PollIcecastAsync(ct);
        }
        if (DateTime.UtcNow - _lastReconcile > TimeSpan.FromSeconds(15))
        {
            _lastReconcile = DateTime.UtcNow;
            await ReconcileAsync(ct);
        }
    }

    /// <summary>liquidsoap restarted: every request it held is gone. Re-queue what was playing, forget the rids.</summary>
    void OnLiquidsoapRestarted()
    {
        lock (_lock)
        {
            foreach (var q in _queue.Where(q => q.Status == ItemStatus.Dispatched)) { q.Status = ItemStatus.Ready; q.Rid = null; }
            if (_autoNext is { Status: ItemStatus.Dispatched }) { _autoNext.Status = ItemStatus.Ready; _autoNext.Rid = null; }
            _skipOnStart.Clear();
            if (_now.Source is not ("user" or "autodj") || _now.Track is null) return;
            var played = (DateTimeOffset.UtcNow - _now.StartedAt).TotalSeconds;
            if (_now.DurationSec > 0 && _now.DurationSec - played < 15) return; // was about to end anyway
            var file = _ytdlp.FindCached(_now.Track.Id);
            var item = new QueueItem
            {
                Track = _now.Track, RequestedBy = _now.RequestedBy ?? "auto-DJ", Kind = _now.Source, Reason = _now.Reason, Via = _now.Via,
                Status = file is null ? ItemStatus.Queued : ItemStatus.Ready, FilePath = file,
            };
            if (_now.Source == "user") _queue.Insert(0, item);
            else if (_autoNext is null) _autoNext = item;
            _log.LogWarning("liquidsoap restarted while playing {Label}; re-queued", _now.Track.Label);
            _now = new NowPlaying { Source = "fallback", StartedAt = DateTimeOffset.UtcNow };
        }
        _db.EndOpenPlays();
        PersistQueue();
    }

    /// <summary>
    /// A request we pushed is "lost" only when liquidsoap no longer knows it at all ("request.all").
    /// "&lt;queue&gt;.queue" is not enough: a prefetched request leaves that list while still alive,
    /// and re-pushing it is exactly how the same song used to play twice in a row.
    /// </summary>
    async Task ReconcileAsync(CancellationToken ct)
    {
        var alive = (await _liq.AllRequestsAsync(ct)).ToHashSet();
        var changed = false;
        var failed = new List<QueueItem>();
        lock (_lock)
        {
            foreach (var item in _queue.Where(q => q.Status == ItemStatus.Dispatched && q.Rid is not null).ToList())
            {
                if (alive.Contains(item.Rid!)) continue;
                var n = _redispatches.GetValueOrDefault(item.ItemId) + 1;
                _redispatches[item.ItemId] = n;
                if (n > 2)
                {
                    _queue.Remove(item);
                    failed.Add(item);
                    continue;
                }
                item.Status = ItemStatus.Ready;
                item.Rid = null;
                changed = true;
            }
            if (_autoNext is { Status: ItemStatus.Dispatched, Rid: { } arid } && !alive.Contains(arid))
            {
                _autoNext.Status = ItemStatus.Ready;
                _autoNext.Rid = null;
                changed = true;
            }
        }
        foreach (var f in failed) SystemChat($"liquidsoap не взяв {f.Track.Label}, прибираю з черги");
        if (failed.Count > 0) { PersistQueue(); Broadcast(); }
        if (changed)
        {
            _log.LogWarning("liquidsoap lost pending requests; re-dispatching");
            await TickSafeAsync(ct);
        }
    }

    async Task PollIcecastAsync(CancellationToken ct)
    {
        try
        {
            var o = _ice.CurrentValue;
            var json = await Http.GetStringAsync(o.StatusUrl, ct);
            var root = JsonNode.Parse(json);
            var sources = root?["icestats"]?["source"] switch
            {
                JsonArray a => a.Where(x => x is not null).ToList(),
                JsonObject s => [s],
                _ => new List<JsonNode?>(),
            };
            var listeners = 0;
            var spotifyLive = false;
            string? spotifyTitle = null;
            foreach (var s in sources)
            {
                var url = s?["listenurl"]?.GetValue<string>() ?? "";
                if (url.EndsWith(o.RadioMount, StringComparison.Ordinal)) listeners = (int)(s?["listeners"]?.GetValue<double>() ?? 0);
                if (url.EndsWith(o.SpotifyMount, StringComparison.Ordinal))
                {
                    spotifyLive = true;
                    spotifyTitle = s?["title"]?.ToString();
                }
            }
            var changed = listeners != _listeners || spotifyLive != _spotifyLive || spotifyTitle != _spotifyTitle;
            _listeners = listeners;
            _spotifyLive = spotifyLive;
            _spotifyTitle = spotifyTitle;
            if (changed) Broadcast();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "icecast status failed");
        }
    }
}
