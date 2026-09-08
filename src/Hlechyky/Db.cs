using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky;

/// <summary>Thin SQLite layer. One connection per call, WAL mode; the DB is tiny and low-traffic.</summary>
public sealed class Db
{
    readonly string _cs;

    const string Schema = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS tracks(
            id TEXT PRIMARY KEY, title TEXT NOT NULL, artist TEXT NOT NULL, album TEXT,
            duration_sec INTEGER NOT NULL DEFAULT 0, thumb_url TEXT, source_url TEXT NOT NULL,
            file_path TEXT, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS plays(
            id INTEGER PRIMARY KEY AUTOINCREMENT, track_id TEXT, source TEXT NOT NULL,
            requested_by TEXT, reason TEXT, started_at TEXT NOT NULL, ended_at TEXT,
            skipped INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_plays_started ON plays(started_at);
        CREATE TABLE IF NOT EXISTS likes(
            track_id TEXT NOT NULL, nick TEXT NOT NULL, created_at TEXT NOT NULL,
            PRIMARY KEY(track_id, nick));
        CREATE TABLE IF NOT EXISTS bans(track_id TEXT PRIMARY KEY, by_nick TEXT, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS chat(
            id INTEGER PRIMARY KEY AUTOINCREMENT, nick TEXT NOT NULL, text TEXT NOT NULL,
            kind TEXT NOT NULL DEFAULT 'chat', created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS cache(key TEXT PRIMARY KEY, json TEXT NOT NULL, fetched_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS queue_items(
            position INTEGER NOT NULL, item_id TEXT PRIMARY KEY, track_id TEXT NOT NULL,
            requested_by TEXT NOT NULL, kind TEXT NOT NULL, reason TEXT, via TEXT, added_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS playlists(
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS playlist_tracks(
            playlist_id INTEGER NOT NULL, track_id TEXT NOT NULL, added_by TEXT NOT NULL, added_at TEXT NOT NULL,
            PRIMARY KEY(playlist_id, track_id));
        """;

    const string TrackCols = "t.id, t.title, t.artist, t.duration_sec, t.thumb_url, t.source_url, t.album";

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var c = Open();
        Exec(c, Schema);
        // migrations for DBs created before these columns existed
        try { Exec(c, "ALTER TABLE plays ADD COLUMN via TEXT"); } catch (SqliteException) { /* exists */ }
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        cmd.ExecuteNonQuery();
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("o");
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static TrackInfo ReadTrack(SqliteDataReader r, int o = 0) => new(
        r.GetString(o), r.GetString(o + 1), r.GetString(o + 2), r.GetInt32(o + 3),
        r.IsDBNull(o + 4) ? null : r.GetString(o + 4), r.GetString(o + 5),
        r.IsDBNull(o + 6) ? null : r.GetString(o + 6));

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---- tracks ----

    public void UpsertTrack(TrackInfo t)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO tracks(id, title, artist, album, duration_sec, thumb_url, source_url, created_at)
            VALUES($id, $title, $artist, $album, $dur, $thumb, $src, $now)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title, artist=excluded.artist, album=excluded.album,
                duration_sec=excluded.duration_sec, thumb_url=excluded.thumb_url, source_url=excluded.source_url
            """,
            ("$id", t.Id), ("$title", t.Title), ("$artist", t.Artist), ("$album", t.Album),
            ("$dur", t.DurationSec), ("$thumb", t.ThumbUrl), ("$src", t.SourceUrl), ("$now", Now()));
    }

    public void SetTrackFile(string id, string path)
    {
        using var c = Open();
        Exec(c, "UPDATE tracks SET file_path=$p WHERE id=$id", ("$p", path), ("$id", id));
    }

    public TrackInfo? GetTrack(string id)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {TrackCols} FROM tracks t WHERE t.id=$id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadTrack(r) : null;
    }

    // ---- plays ----

    public long StartPlay(string? trackId, string source, string? requestedBy, string? reason, string? via)
    {
        using var c = Open();
        Exec(c, "UPDATE plays SET ended_at=$now WHERE ended_at IS NULL", ("$now", Now()));
        using var cmd = Cmd(c, """
            INSERT INTO plays(track_id, source, requested_by, reason, via, started_at) VALUES($t, $s, $b, $r, $v, $now);
            SELECT last_insert_rowid();
            """, ("$t", trackId), ("$s", source), ("$b", requestedBy), ("$r", reason), ("$v", via), ("$now", Now()));
        return (long)cmd.ExecuteScalar()!;
    }

    public void EndPlay(long id, bool skipped)
    {
        using var c = Open();
        Exec(c, "UPDATE plays SET ended_at=$now, skipped=$s WHERE id=$id AND ended_at IS NULL",
            ("$now", Now()), ("$s", skipped ? 1 : 0), ("$id", id));
    }

    public void EndOpenPlays()
    {
        using var c = Open();
        Exec(c, "UPDATE plays SET ended_at=$now WHERE ended_at IS NULL", ("$now", Now()));
    }

    /// <summary>Id of the still-open play of this track (the previous server process started it), or 0.</summary>
    public long OpenPlayId(string trackId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id FROM plays WHERE ended_at IS NULL AND track_id=$t ORDER BY id DESC LIMIT 1", ("$t", trackId));
        return cmd.ExecuteScalar() is long id ? id : 0;
    }

    public (string? RequestedBy, string? Reason, string? Via)? GetPlay(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT requested_by, reason, via FROM plays WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (Str(r, 0), Str(r, 1), Str(r, 2)) : null;
    }

    public void EndOpenPlaysExcept(long keepId)
    {
        using var c = Open();
        Exec(c, "UPDATE plays SET ended_at=$now WHERE ended_at IS NULL AND id<>$k", ("$now", Now()), ("$k", keepId));
    }

    public List<HistoryEntry> History(int n)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT p.id, {TrackCols}, p.source, p.requested_by, p.started_at,
                   (SELECT COUNT(*) FROM likes l WHERE l.track_id = t.id), p.via, p.skipped
            FROM plays p JOIN tracks t ON t.id = p.track_id
            ORDER BY p.id DESC LIMIT $n
            """, ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (r.Read())
            list.Add(new HistoryEntry(r.GetInt64(0), ReadTrack(r, 1), r.GetString(8), Str(r, 9), Ts(r.GetString(10)), r.GetInt32(11), Str(r, 12), r.GetInt32(13) == 1));
        return list;
    }

    /// <summary>Most recent distinct tracks that were listened to the end (skipped ones are not a taste signal).</summary>
    public List<TrackInfo> RecentDistinctTracks(int n, bool excludeSkipped = true)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT {TrackCols}, MAX(p.id) AS m FROM plays p JOIN tracks t ON t.id = p.track_id
            WHERE p.source IN ('user', 'autodj') {(excludeSkipped ? "AND p.skipped = 0" : "")}
            GROUP BY t.id ORDER BY m DESC LIMIT $n
            """, ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<TrackInfo>();
        while (r.Read()) list.Add(ReadTrack(r));
        return list;
    }

    public HashSet<string> PlayedTrackIdsSince(DateTimeOffset since)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT DISTINCT track_id FROM plays WHERE track_id IS NOT NULL AND started_at >= $s",
            ("$s", since.ToUniversalTime().ToString("o")));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public List<string> RecentArtists(int n)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT t.artist FROM plays p JOIN tracks t ON t.id = p.track_id ORDER BY p.id DESC LIMIT $n", ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<NickCount> TopRequesters(int days)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT requested_by, COUNT(*) FROM plays
            WHERE source = 'user' AND requested_by IS NOT NULL AND started_at >= $s
            GROUP BY requested_by ORDER BY 2 DESC LIMIT 20
            """, ("$s", DateTimeOffset.UtcNow.AddDays(-days).ToString("o")));
        using var r = cmd.ExecuteReader();
        var list = new List<NickCount>();
        while (r.Read()) list.Add(new NickCount(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    // ---- queue persistence (survives server restarts) ----

    public void SaveQueue(IEnumerable<QueueItem> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM queue_items");
        var pos = 0;
        foreach (var i in items)
            Exec(c, """
                INSERT INTO queue_items(position, item_id, track_id, requested_by, kind, reason, via, added_at)
                VALUES($p, $i, $t, $b, $k, $r, $v, $a)
                """, ("$p", pos++), ("$i", i.ItemId), ("$t", i.Track.Id), ("$b", i.RequestedBy), ("$k", i.Kind),
                ("$r", i.Reason), ("$v", i.Via), ("$a", i.AddedAt.ToString("o")));
        tx.Commit();
    }

    public List<PersistedQueueItem> LoadQueue()
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT q.item_id, {TrackCols}, q.requested_by, q.kind, q.reason, q.via, q.added_at
            FROM queue_items q JOIN tracks t ON t.id = q.track_id ORDER BY q.position
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<PersistedQueueItem>();
        while (r.Read())
            list.Add(new PersistedQueueItem(r.GetString(0), ReadTrack(r, 1), r.GetString(8), r.GetString(9), Str(r, 10), Str(r, 11), Ts(r.GetString(12))));
        return list;
    }

    // ---- likes / bans ----

    public bool ToggleLike(string trackId, string nick)
    {
        using var c = Open();
        using var check = Cmd(c, "SELECT 1 FROM likes WHERE track_id=$t AND nick=$n", ("$t", trackId), ("$n", nick));
        if (check.ExecuteScalar() is not null)
        {
            Exec(c, "DELETE FROM likes WHERE track_id=$t AND nick=$n", ("$t", trackId), ("$n", nick));
            return false;
        }
        Exec(c, "INSERT INTO likes(track_id, nick, created_at) VALUES($t, $n, $now)", ("$t", trackId), ("$n", nick), ("$now", Now()));
        return true;
    }

    public List<string> Likers(string trackId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT nick FROM likes WHERE track_id=$t ORDER BY created_at", ("$t", trackId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<TrackInfo> LikedTracks(int n)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT {TrackCols}, COUNT(*) AS cnt FROM likes l JOIN tracks t ON t.id = l.track_id
            GROUP BY t.id ORDER BY cnt DESC LIMIT $n
            """, ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<TrackInfo>();
        while (r.Read()) list.Add(ReadTrack(r));
        return list;
    }

    /// <summary>Liked tracks with who liked them, newest like first.</summary>
    public List<(TrackInfo Track, List<string> Likers, DateTimeOffset LastLike)> LikedTracksDetailed(int n)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT {TrackCols}, GROUP_CONCAT(l.nick, '\n'), MAX(l.created_at)
            FROM likes l JOIN tracks t ON t.id = l.track_id
            GROUP BY t.id ORDER BY MAX(l.created_at) DESC LIMIT $n
            """, ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<(TrackInfo, List<string>, DateTimeOffset)>();
        while (r.Read()) list.Add((ReadTrack(r), r.GetString(7).Split('\n').ToList(), Ts(r.GetString(8))));
        return list;
    }

    public bool IsBanned(string trackId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT 1 FROM bans WHERE track_id=$t", ("$t", trackId));
        return cmd.ExecuteScalar() is not null;
    }

    public HashSet<string> BannedIds()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT track_id FROM bans");
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void Ban(string trackId, string by)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO bans(track_id, by_nick, created_at) VALUES($t, $b, $now)", ("$t", trackId), ("$b", by), ("$now", Now()));
    }

    // ---- playlists ----

    public sealed record Playlist(long Id, string Name, string CreatedBy, int Count, string? ThumbUrl);

    public List<Playlist> Playlists()
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT p.id, p.name, p.created_by,
                   (SELECT COUNT(*) FROM playlist_tracks x WHERE x.playlist_id = p.id),
                   (SELECT t.thumb_url FROM playlist_tracks x JOIN tracks t ON t.id = x.track_id WHERE x.playlist_id = p.id ORDER BY x.added_at DESC LIMIT 1)
            FROM playlists p ORDER BY p.id
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<Playlist>();
        while (r.Read()) list.Add(new Playlist(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Str(r, 4)));
        return list;
    }

    public Playlist? GetPlaylist(long id) => Playlists().FirstOrDefault(p => p.Id == id);

    public long CreatePlaylist(string name, string by)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT INTO playlists(name, created_by, created_at) VALUES($n, $b, $now); SELECT last_insert_rowid();",
            ("$n", name), ("$b", by), ("$now", Now()));
        return (long)cmd.ExecuteScalar()!;
    }

    public void DeletePlaylist(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM playlist_tracks WHERE playlist_id=$id; DELETE FROM playlists WHERE id=$id", ("$id", id));
    }

    public bool AddToPlaylist(long id, string trackId, string by)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT OR IGNORE INTO playlist_tracks(playlist_id, track_id, added_by, added_at) VALUES($p, $t, $b, $now)",
            ("$p", id), ("$t", trackId), ("$b", by), ("$now", Now()));
        return cmd.ExecuteNonQuery() > 0;
    }

    public void RemoveFromPlaylist(long id, string trackId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM playlist_tracks WHERE playlist_id=$p AND track_id=$t", ("$p", id), ("$t", trackId));
    }

    public List<(TrackInfo Track, string AddedBy)> PlaylistTracks(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {TrackCols}, x.added_by FROM playlist_tracks x JOIN tracks t ON t.id = x.track_id WHERE x.playlist_id=$p ORDER BY x.added_at", ("$p", id));
        using var r = cmd.ExecuteReader();
        var list = new List<(TrackInfo, string)>();
        while (r.Read()) list.Add((ReadTrack(r), r.GetString(7)));
        return list;
    }

    // ---- chat ----

    public ChatMessage AddChat(string nick, string text, string kind)
    {
        var now = Now();
        using var c = Open();
        using var cmd = Cmd(c, "INSERT INTO chat(nick, text, kind, created_at) VALUES($n, $t, $k, $now); SELECT last_insert_rowid();",
            ("$n", nick), ("$t", text), ("$k", kind), ("$now", now));
        var id = (long)cmd.ExecuteScalar()!;
        return new ChatMessage(id, nick, text, Ts(now), kind);
    }

    /// <summary>Last <paramref name="nChat"/> people/DJ messages plus last <paramref name="nLog"/> log lines, oldest first,
    /// so a busy event log cannot push real conversation out of the history.</summary>
    public List<ChatMessage> RecentChat(int nChat, int nLog = 120)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT id, nick, text, kind, created_at FROM (
                SELECT * FROM chat WHERE kind <> 'system' ORDER BY id DESC LIMIT $nc)
            UNION ALL
            SELECT id, nick, text, kind, created_at FROM (
                SELECT * FROM chat WHERE kind = 'system' ORDER BY id DESC LIMIT $nl)
            ORDER BY id
            """, ("$nc", nChat), ("$nl", nLog));
        using var r = cmd.ExecuteReader();
        var list = new List<ChatMessage>();
        while (r.Read()) list.Add(new ChatMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), Ts(r.GetString(4)), r.GetString(3)));
        return list;
    }

    // ---- generic cache ----

    public string? CacheGet(string key, TimeSpan maxAge)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json, fetched_at FROM cache WHERE key=$k", ("$k", key));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return DateTimeOffset.UtcNow - Ts(r.GetString(1)) <= maxAge ? r.GetString(0) : null;
    }

    public void CacheSet(string key, string json)
    {
        using var c = Open();
        Exec(c, "INSERT INTO cache(key, json, fetched_at) VALUES($k, $j, $now) ON CONFLICT(key) DO UPDATE SET json=excluded.json, fetched_at=excluded.fetched_at",
            ("$k", key), ("$j", json), ("$now", Now()));
    }
}
