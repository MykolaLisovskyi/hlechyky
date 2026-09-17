using System.Globalization;
using System.Text.Json;
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
            kind TEXT NOT NULL DEFAULT 'chat', created_at TEXT NOT NULL, room_id TEXT);
        CREATE TABLE IF NOT EXISTS cache(key TEXT PRIMARY KEY, json TEXT NOT NULL, fetched_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS queue_items(
            position INTEGER NOT NULL, item_id TEXT PRIMARY KEY, track_id TEXT NOT NULL,
            requested_by TEXT NOT NULL, kind TEXT NOT NULL, reason TEXT, via TEXT, added_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS playlists(
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS playlist_tracks(
            playlist_id INTEGER NOT NULL, track_id TEXT NOT NULL, added_by TEXT NOT NULL, added_at TEXT NOT NULL,
            PRIMARY KEY(playlist_id, track_id));
        CREATE TABLE IF NOT EXISTS play_listeners(
            play_id INTEGER NOT NULL, nick TEXT NOT NULL, PRIMARY KEY(play_id, nick)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_plays_track ON plays(track_id);
        CREATE TABLE IF NOT EXISTS dj_feedback(
            id INTEGER PRIMARY KEY AUTOINCREMENT, artist_key TEXT NOT NULL, seed_id TEXT, kind TEXT NOT NULL,
            nick TEXT, created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_dj_feedback_created ON dj_feedback(created_at);
        """;

    /// <summary>
    /// Таблиці ігрової платформи (черепки, результати, рейтинги, ачівки, щоденне, збережені стани).
    /// Тут — лише DDL: усі запити до них живуть у Games/Economy/Store.cs, щоб цей файл лишався тонким
    /// і не збирав на собі конфлікти від кожної нової гри.
    /// </summary>
    const string GamesSchema = """
        CREATE TABLE IF NOT EXISTS wallets(
            nick_key TEXT PRIMARY KEY, nick TEXT NOT NULL, balance INTEGER NOT NULL DEFAULT 0,
            earned INTEGER NOT NULL DEFAULT 0, spent INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ledger(
            id INTEGER PRIMARY KEY AUTOINCREMENT, nick_key TEXT NOT NULL, delta INTEGER NOT NULL,
            reason TEXT NOT NULL, ref TEXT, created_at TEXT NOT NULL);
        -- ref — ключ ідемпотентності. У SQLite NULL-и в унікальному індексі вважаються різними, але
        -- часткового індексу тут ще й дешевше: рядки без ref у нього просто не потрапляють.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_ledger_ref ON ledger(ref) WHERE ref IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_ledger_nick_created ON ledger(nick_key, created_at);
        CREATE INDEX IF NOT EXISTS ix_ledger_created ON ledger(created_at);
        CREATE TABLE IF NOT EXISTS economy_counters(
            nick_key TEXT NOT NULL, key TEXT NOT NULL, day TEXT NOT NULL, n INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(nick_key, key, day));
        CREATE TABLE IF NOT EXISTS game_results(
            id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL, game TEXT NOT NULL,
            round INTEGER NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL, outcome TEXT NOT NULL,
            score INTEGER, opponents TEXT, stake INTEGER NOT NULL DEFAULT 0, tries INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_results_room ON game_results(room_id, round, nick_key);
        CREATE INDEX IF NOT EXISTS ix_results_game_created ON game_results(game, created_at);
        CREATE INDEX IF NOT EXISTS ix_results_nick ON game_results(nick_key, id);
        CREATE TABLE IF NOT EXISTS ratings(
            nick_key TEXT NOT NULL, game TEXT NOT NULL, nick TEXT NOT NULL,
            elo INTEGER NOT NULL DEFAULT 1000, games INTEGER NOT NULL DEFAULT 0,
            wins INTEGER NOT NULL DEFAULT 0, losses INTEGER NOT NULL DEFAULT 0,
            draws INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL, PRIMARY KEY(nick_key, game));
        CREATE INDEX IF NOT EXISTS ix_ratings_game_elo ON ratings(game, elo DESC);
        CREATE TABLE IF NOT EXISTS achievements(
            nick_key TEXT NOT NULL, key TEXT NOT NULL, nick TEXT NOT NULL, unlocked_at TEXT NOT NULL,
            PRIMARY KEY(nick_key, key));
        CREATE TABLE IF NOT EXISTS daily_results(
            day TEXT NOT NULL, game TEXT NOT NULL, nick_key TEXT NOT NULL, nick TEXT NOT NULL,
            solved INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
            ms INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
            PRIMARY KEY(day, game, nick_key));
        CREATE INDEX IF NOT EXISTS ix_daily_game_day ON daily_results(game, day);
        CREATE TABLE IF NOT EXISTS game_state(key TEXT PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL);
        """;

    const string TrackCols = "t.id, t.title, t.artist, t.duration_sec, t.thumb_url, t.source_url, t.album";

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var c = Open();
        Exec(c, Schema);
        Exec(c, GamesSchema);
        // migrations for DBs created before these columns existed
        try { Exec(c, "ALTER TABLE plays ADD COLUMN via TEXT"); } catch (SqliteException) { /* exists */ }
        // справжня довжина файлу (каталог YouTube бреше на секунду-дві) і пік підключень до потоку за трек
        try { Exec(c, "ALTER TABLE plays ADD COLUMN duration_sec INTEGER"); } catch (SqliteException) { /* exists */ }
        try { Exec(c, "ALTER TABLE plays ADD COLUMN stream_peak INTEGER"); } catch (SqliteException) { /* exists */ }
        try { Exec(c, "ALTER TABLE tracks ADD COLUMN song_key TEXT"); } catch (SqliteException) { /* exists */ }
        // скільки черепків віддали за бан; 0 — забанив адмін
        try { Exec(c, "ALTER TABLE bans ADD COLUMN price INTEGER NOT NULL DEFAULT 0"); } catch (SqliteException) { /* exists */ }
        // id столу, про який цей рядок Журналу: фронт малює біля нього кнопку «Сісти»/«Дивитись»
        try { Exec(c, "ALTER TABLE chat ADD COLUMN room_id TEXT"); } catch (SqliteException) { /* exists */ }
        // на яке повідомлення це відповідь, і хто яке лайкнув
        try { Exec(c, "ALTER TABLE chat ADD COLUMN reply_to INTEGER"); } catch (SqliteException) { /* exists */ }
        // «👎 більше не давати» у «Вгадай мелодію»: такі треки (і та сама пісня з інших завантажень) гра не бере
        Exec(c, "CREATE TABLE IF NOT EXISTS melody_dislikes(track_id TEXT NOT NULL, nick TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(track_id, nick))");
        Exec(c, "CREATE TABLE IF NOT EXISTS chat_likes(chat_id INTEGER NOT NULL, nick TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(chat_id, nick))");
        // Акаунти: нік займають один раз разом із паролем. nick_key — той самий trim+lower, що й у гаманців,
        // тож усе, що вже лежить під цим ніком (глеки, ачівки, статистика), стає добром акаунта без переносу.
        // pass_hash порожній — акаунт лише через Google (сіль усе одно є: вона живе в сесійній куці).
        Exec(c, """
            CREATE TABLE IF NOT EXISTS accounts(
                nick_key TEXT PRIMARY KEY, nick TEXT NOT NULL, pass_hash TEXT NOT NULL, pass_salt TEXT NOT NULL,
                role TEXT NOT NULL DEFAULT 'member', created_at TEXT NOT NULL, seen_at TEXT NOT NULL,
                google_sub TEXT, email TEXT)
            """);
        try { Exec(c, "ALTER TABLE accounts ADD COLUMN google_sub TEXT"); } catch (SqliteException) { /* exists */ }
        try { Exec(c, "ALTER TABLE accounts ADD COLUMN email TEXT"); } catch (SqliteException) { /* exists */ }
        Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS ix_accounts_google ON accounts(google_sub) WHERE google_sub IS NOT NULL");
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_tracks_song_key ON tracks(song_key)");
        BackfillSongKeys(c);
    }

    /// <summary>Ключ пісні рахується в C#: SQLite-івський lower() не знає кирилиці.</summary>
    static void BackfillSongKeys(SqliteConnection c)
    {
        var rows = new List<(string Id, string Key)>();
        using (var cmd = Cmd(c, "SELECT id, artist, title FROM tracks WHERE song_key IS NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) rows.Add((r.GetString(0), SongKey.Of(r.GetString(1), r.GetString(2))));
        if (rows.Count == 0) return;
        using var tx = c.BeginTransaction();
        foreach (var (id, key) in rows) Exec(c, "UPDATE tracks SET song_key=$k WHERE id=$id", ("$k", key), ("$id", id));
        tx.Commit();
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    // ---- гачки для Games/Economy ----
    // Економіка тримає свій SQL у себе (Store.cs), а сюди виносить тільки те, без чого не обійтись:
    // з'єднання на час однієї короткої операції.

    /// <summary>Виконати щось на власному з'єднанні (транзакції економіки — всередині f).</summary>
    public T With<T>(Func<SqliteConnection, T> f)
    {
        using var c = Open();
        return f(c);
    }

    /// <summary>Те саме без результату.</summary>
    public void With(Action<SqliteConnection> a)
    {
        using var c = Open();
        a(c);
    }

    /// <summary>Разовий запит без результату — щоб не писати With(c => ...) заради одного рядка.</summary>
    public void Exec(string sql, params (string Name, object? Value)[] ps)
    {
        using var c = Open();
        Exec(c, sql, ps);
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
            INSERT INTO tracks(id, title, artist, album, duration_sec, thumb_url, source_url, created_at, song_key)
            VALUES($id, $title, $artist, $album, $dur, $thumb, $src, $now, $key)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title, artist=excluded.artist, album=excluded.album,
                duration_sec=excluded.duration_sec, thumb_url=excluded.thumb_url, source_url=excluded.source_url, song_key=excluded.song_key
            """,
            ("$id", t.Id), ("$title", t.Title), ("$artist", t.Artist), ("$album", t.Album),
            ("$dur", t.DurationSec), ("$thumb", t.ThumbUrl), ("$src", t.SourceUrl), ("$now", Now()), ("$key", SongKey.Of(t.Artist, t.Title)));
    }

    public void SetTrackFile(string id, string path)
    {
        using var c = Open();
        Exec(c, "UPDATE tracks SET file_path=$p WHERE id=$id", ("$p", path), ("$id", id));
    }

    // ---- кеш файлів ----

    public string? TrackFile(string id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT file_path FROM tracks WHERE id=$id", ("$id", id));
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Інші id тієї самої пісні: їхня тривалість і записаний файл (може вже не існувати).</summary>
    public List<(string Id, int DurationSec, string? FilePath)> SameSongFiles(string songKey, string exceptId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, duration_sec, file_path FROM tracks WHERE song_key=$k AND id<>$id AND id NOT LIKE 'voice-%'",
            ("$k", songKey), ("$id", exceptId));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int, string?)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), Str(r, 2)));
        return list;
    }

    /// <summary>Файл видалили з кешу: стираємо його в усіх треків, що на нього посилались.</summary>
    public void ForgetTrackFile(string path)
    {
        var name = Path.GetFileName(path);
        using var c = Open();
        var ids = new List<string>();
        using (var cmd = Cmd(c, "SELECT id, file_path FROM tracks WHERE file_path IS NOT NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (Path.GetFileName(r.GetString(1)).Equals(name, StringComparison.OrdinalIgnoreCase)) ids.Add(r.GetString(0));
        foreach (var id in ids) Exec(c, "UPDATE tracks SET file_path=NULL WHERE id=$id", ("$id", id));
    }

    public sealed record CacheStat(string TrackId, string? FilePath, int Plays, DateTimeOffset? LastPlayed, int Likes, bool InPlaylist);

    /// <summary>Усе, що TrackCache зважує перед видаленням: повтори, останнє програвання, лайки, плейлисти.</summary>
    public List<CacheStat> CacheStats()
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT t.id, t.file_path,
                   (SELECT COUNT(*) FROM plays p WHERE p.track_id = t.id),
                   (SELECT MAX(p.started_at) FROM plays p WHERE p.track_id = t.id),
                   (SELECT COUNT(*) FROM likes l WHERE l.track_id = t.id),
                   EXISTS (SELECT 1 FROM playlist_tracks x WHERE x.track_id = t.id)
            FROM tracks t WHERE t.id NOT LIKE 'voice-%'
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<CacheStat>();
        while (r.Read())
            list.Add(new CacheStat(r.GetString(0), Str(r, 1), r.GetInt32(2), r.IsDBNull(3) ? null : Ts(r.GetString(3)), r.GetInt32(4), r.GetInt64(5) != 0));
        return list;
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

    /// <summary>Справжня довжина треку, щойно liquidsoap її знає: від неї рахується, скільки дослухали.</summary>
    public void SetPlayDuration(long id, int sec)
    {
        using var c = Open();
        Exec(c, "UPDATE plays SET duration_sec=$d WHERE id=$id", ("$d", sec), ("$id", id));
    }

    /// <summary>Хто слухав трек (ніки з увімкненим плеєром на сайті) і пік підключень до потоку, разом з ETS2 та VLC.</summary>
    public void NotePlayListeners(long id, int streamListeners, IEnumerable<string> nicks)
    {
        using var c = Open();
        Exec(c, "UPDATE plays SET stream_peak=MAX(COALESCE(stream_peak, 0), $n) WHERE id=$id", ("$n", streamListeners), ("$id", id));
        foreach (var nick in nicks)
            Exec(c, "INSERT OR IGNORE INTO play_listeners(play_id, nick) VALUES($id, $n)", ("$id", id), ("$n", nick));
    }

    public sealed record TrackRating(TrackInfo Track, int Plays, int Skips, int? Completion, int Listeners, List<string> ListenerNicks,
        int StreamPeak, int Likes, DateTimeOffset LastPlayed);

    /// <summary>
    /// Рейтинг треків за період. Дослуховування — середнє по програваннях: скільки секунд прозвучало з довжини
    /// файлу (без обрізання «на секунду раніше»: хто дограв без 10 секунд, дограв). Голосові не музика.
    /// </summary>
    public List<TrackRating> TrackRatings(int days, string sort, int n)
    {
        var order = sort switch
        {
            "completion" => "completion DESC, plays DESC",
            "listeners" => "listeners DESC, plays DESC",
            "likes" => "likes DESC, plays DESC",
            _ => "plays DESC, completion DESC",
        };
        using var c = Open();
        using var cmd = Cmd(c, $"""
            WITH p AS (
                SELECT p.id, p.track_id, p.skipped, p.started_at, p.stream_peak,
                       (julianday(p.ended_at) - julianday(p.started_at)) * 86400 AS played,
                       COALESCE(NULLIF(p.duration_sec, 0), NULLIF(t.duration_sec, 0)) AS dur
                FROM plays p JOIN tracks t ON t.id = p.track_id
                WHERE p.started_at >= $since AND p.ended_at IS NOT NULL AND p.track_id NOT LIKE 'voice-%'
            ), agg AS (
                SELECT track_id, COUNT(*) AS plays, SUM(skipped) AS skips, MAX(started_at) AS last_played,
                       COALESCE(MAX(stream_peak), 0) AS stream_peak,
                       ROUND(AVG(CASE WHEN dur IS NULL THEN NULL WHEN played >= dur - 10 THEN 1.0 ELSE MAX(played, 0) / dur END) * 100) AS completion
                FROM p GROUP BY track_id
            ), who AS (
                SELECT track_id, COUNT(*) AS listeners, GROUP_CONCAT(nick, char(10)) AS nicks
                FROM (SELECT DISTINCT p.track_id, l.nick FROM p JOIN play_listeners l ON l.play_id = p.id) GROUP BY track_id
            )
            SELECT {TrackCols}, a.plays, a.skips, a.completion, COALESCE(w.listeners, 0) AS listeners, w.nicks, a.stream_peak,
                   (SELECT COUNT(*) FROM likes l WHERE l.track_id = t.id) AS likes, a.last_played
            FROM agg a JOIN tracks t ON t.id = a.track_id LEFT JOIN who w ON w.track_id = a.track_id
            ORDER BY {order}, a.last_played DESC LIMIT $n
            """, ("$since", DateTimeOffset.UtcNow.AddDays(-days).ToString("o")), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<TrackRating>();
        while (r.Read())
            list.Add(new TrackRating(ReadTrack(r), r.GetInt32(7), r.GetInt32(8), r.IsDBNull(9) ? null : (int)r.GetDouble(9), r.GetInt32(10),
                r.IsDBNull(11) ? [] : r.GetString(11).Split('\n').ToList(), r.GetInt32(12), r.GetInt32(13), Ts(r.GetString(14))));
        return list;
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

    /// <summary>
    /// Треки, які ставили люди, найсвіжіші перші. Це і є смак кімнати: те, що Дядько Глек накрутив
    /// собі сам, сюди не потрапляє, інакше він би вчився на власному дрейфі. Голосові не музика.
    /// </summary>
    public List<TrackInfo> RecentUserTracks(int n)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT {TrackCols}, MAX(p.id) AS m FROM plays p JOIN tracks t ON t.id = p.track_id
            WHERE p.source = 'user' AND t.id NOT LIKE 'voice-%'
            GROUP BY t.id ORDER BY m DESC LIMIT $n
            """, ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<TrackInfo>();
        while (r.Read()) list.Add(ReadTrack(r));
        return list;
    }

    /// <summary>Артисти, яких кімната ставила сама або лайкала — «свої» для авто-DJ.</summary>
    public List<string> TasteArtists(int n)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT t.artist FROM plays p JOIN tracks t ON t.id = p.track_id
            WHERE p.source = 'user' AND t.id NOT LIKE 'voice-%'
            UNION
            SELECT t.artist FROM likes l JOIN tracks t ON t.id = l.track_id
            LIMIT $n
            """, ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Скільки треків Глек поставив сам після останнього людського замовлення. Переживає рестарт.</summary>
    public int AutoPlaysSinceUser()
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT COUNT(*) FROM plays
            WHERE source = 'autodj' AND id > COALESCE((SELECT MAX(id) FROM plays WHERE source = 'user'), 0)
            """);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Своє, давно забуте: треки з власних замовлень чи лайків, яких не було в ефірі від $since.</summary>
    public List<TrackInfo> ArchiveTracks(DateTimeOffset since, int n)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT {TrackCols} FROM tracks t JOIN plays p ON p.track_id = t.id
            WHERE t.id NOT LIKE 'voice-%'
            GROUP BY t.id
            HAVING MAX(p.started_at) < $since
               AND (SUM(CASE WHEN p.source = 'user' THEN 1 ELSE 0 END) > 0
                    OR EXISTS (SELECT 1 FROM likes l WHERE l.track_id = t.id))
            ORDER BY RANDOM() LIMIT $n
            """, ("$since", since.ToUniversalTime().ToString("o")), ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<TrackInfo>();
        while (r.Read()) list.Add(ReadTrack(r));
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

    /// <summary>False — трек уже був у бані.</summary>
    public bool Ban(string trackId, string by, int price = 0)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT OR IGNORE INTO bans(track_id, by_nick, price, created_at) VALUES($t, $b, $p, $now)",
            ("$t", trackId), ("$b", by), ("$p", price), ("$now", Now()));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>False — такого бану й не було.</summary>
    public bool Unban(string trackId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM bans WHERE track_id=$t", ("$t", trackId));
        return cmd.ExecuteNonQuery() > 0;
    }

    public sealed record BanRow(TrackInfo Track, string? By, int Price, DateTimeOffset CreatedAt);

    /// <summary>Бан-лист, свіжі зверху. Трек, якого чомусь нема в tracks, показуємо хоч за id.</summary>
    public List<BanRow> Bans()
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT b.track_id, b.by_nick, b.price, b.created_at, {TrackCols}
            FROM bans b LEFT JOIN tracks t ON t.id = b.track_id ORDER BY b.created_at DESC
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<BanRow>();
        while (r.Read())
        {
            var track = r.IsDBNull(4) ? new TrackInfo(r.GetString(0), r.GetString(0), "", 0, null, "", null) : ReadTrack(r, 4);
            list.Add(new BanRow(track, Str(r, 1), r.GetInt32(2), Ts(r.GetString(3))));
        }
        return list;
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

    /// <summary>
    /// Рядок у балачки. <paramref name="roomId"/> — жива кімната, про яку цей рядок («Влад поставив стіл»):
    /// фронт малює біля нього кнопку до столу. Кімнати живуть у пам'яті й помирають із сервером, але id
    /// лежить у базі разом із рядком — інакше після F5 кнопка зникала б із історії ще за життя столу.
    /// </summary>
    public ChatMessage AddChat(string nick, string text, string kind, string? roomId = null, long? replyTo = null)
    {
        var now = Now();
        using var c = Open();
        // Відповідають лише на живе повідомлення людини чи Глека; на рядок Журналу чи неіснуюче — ні, тоді це просто репліка.
        (string Nick, string Text)? parent = null;
        if (replyTo is { } pid)
        {
            using var pc = Cmd(c, "SELECT nick, text FROM chat WHERE id=$id AND kind <> 'system'", ("$id", pid));
            using var pr = pc.ExecuteReader();
            if (pr.Read()) parent = (pr.GetString(0), pr.GetString(1));
            else replyTo = null;
        }
        using var cmd = Cmd(c, "INSERT INTO chat(nick, text, kind, room_id, reply_to, created_at) VALUES($n, $t, $k, $r, $p, $now); SELECT last_insert_rowid();",
            ("$n", nick), ("$t", text), ("$k", kind), ("$r", roomId), ("$p", replyTo), ("$now", now));
        var id = (long)cmd.ExecuteScalar()!;
        return new ChatMessage(id, nick, text, Ts(now), kind, roomId, replyTo, parent?.Nick, Quote(parent?.Text), []);
    }

    /// <summary>Уривок повідомлення для цитати над відповіддю: весь текст не потрібен, лише щоб упізнати.</summary>
    static string? Quote(string? text)
    {
        if (text is null) return null;
        const int max = 120;
        if (text.Length <= max) return text;
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>Last <paramref name="nChat"/> people/DJ messages plus last <paramref name="nLog"/> log lines, oldest first,
    /// so a busy event log cannot push real conversation out of the history.</summary>
    public List<ChatMessage> RecentChat(int nChat, int nLog = 120)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT m.id, m.nick, m.text, m.kind, m.created_at, m.room_id, m.reply_to, p.nick, p.text FROM (
                SELECT id, nick, text, kind, created_at, room_id, reply_to FROM (
                    SELECT * FROM chat WHERE kind <> 'system' ORDER BY id DESC LIMIT $nc)
                UNION ALL
                SELECT id, nick, text, kind, created_at, room_id, reply_to FROM (
                    SELECT * FROM chat WHERE kind = 'system' ORDER BY id DESC LIMIT $nl)
            ) m
            LEFT JOIN chat p ON p.id = m.reply_to
            ORDER BY m.id
            """, ("$nc", nChat), ("$nl", nLog));
        var rows = new List<(long Id, string Nick, string Text, string Kind, string At, string? Room, long? ReplyTo, string? PNick, string? PText)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6),
                    r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        }
        var likes = LikesFor(c, rows.Where(x => x.Kind != "system").Select(x => x.Id).ToList());
        return [.. rows.Select(x => new ChatMessage(x.Id, x.Nick, x.Text, Ts(x.At), x.Kind, x.Room, x.ReplyTo, x.PNick, Quote(x.PText),
            likes.TryGetValue(x.Id, out var l) ? [.. l] : []))];
    }

    /// <summary>Хто лайкнув кожне з повідомлень — у порядку лайків.</summary>
    static Dictionary<long, List<string>> LikesFor(SqliteConnection c, List<long> ids)
    {
        var map = new Dictionary<long, List<string>>();
        if (ids.Count == 0) return map;
        using var cmd = Cmd(c, "SELECT chat_id, nick FROM chat_likes WHERE chat_id >= $lo AND chat_id <= $hi ORDER BY created_at",
            ("$lo", ids.Min()), ("$hi", ids.Max()));
        using var r = cmd.ExecuteReader();
        var wanted = ids.ToHashSet();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (!wanted.Contains(id)) continue;
            if (!map.TryGetValue(id, out var list)) map[id] = list = [];
            list.Add(r.GetString(1));
        }
        return map;
    }

    /// <summary>
    /// Поставити або зняти ❤ з повідомлення. null — такого повідомлення нема або це рядок Журналу (їх не лайкають).
    /// Інакше — хто лайкнув після зміни.
    /// </summary>
    public List<string>? ToggleChatLike(long chatId, string nick)
    {
        using var c = Open();
        using (var k = Cmd(c, "SELECT kind FROM chat WHERE id=$id", ("$id", chatId)))
        {
            if (k.ExecuteScalar() is not string kind || kind == "system") return null;
        }
        // Лайк прив'язаний до ніка без регістру: «Оля» і «оля» — одна людина, як і скрізь на сайті. Порівнюємо в C#:
        // COLLATE NOCASE у SQLite знає лише латиницю.
        using var tx = c.BeginTransaction();
        var existing = LikesFor(c, [chatId]).GetValueOrDefault(chatId)?.FirstOrDefault(n => string.Equals(n, nick, StringComparison.OrdinalIgnoreCase));
        using (var change = existing is not null
            ? Cmd(c, "DELETE FROM chat_likes WHERE chat_id=$id AND nick=$n", ("$id", chatId), ("$n", existing))
            : Cmd(c, "INSERT INTO chat_likes(chat_id, nick, created_at) VALUES($id, $n, $now)", ("$id", chatId), ("$n", nick), ("$now", Now())))
        {
            change.Transaction = tx;
            change.ExecuteNonQuery();
        }
        tx.Commit();
        return LikesFor(c, [chatId]).TryGetValue(chatId, out var l) ? l : [];
    }

    // ---- «Вгадай мелодію»: що більше не давати ----

    /// <summary>
    /// Поставити або зняти 👎 треку в «Вгадай мелодію». null — такого треку нема. Інакше — чи стоїть тепер дизлайк
    /// від цього ніка і скільки їх у треку всього. Нік без регістру: «Оля» і «оля» — одна людина.
    /// </summary>
    public (bool Mine, int Total)? ToggleMelodyDislike(string trackId, string nick)
    {
        using var c = Open();
        using (var t = Cmd(c, "SELECT 1 FROM tracks WHERE id=$id", ("$id", trackId)))
        {
            if (t.ExecuteScalar() is null) return null;
        }
        var nicks = new List<string>();
        using (var q = Cmd(c, "SELECT nick FROM melody_dislikes WHERE track_id=$id", ("$id", trackId)))
        using (var r = q.ExecuteReader())
            while (r.Read()) nicks.Add(r.GetString(0));
        var existing = nicks.FirstOrDefault(n => string.Equals(n, nick, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Exec(c, "DELETE FROM melody_dislikes WHERE track_id=$id AND nick=$n", ("$id", trackId), ("$n", existing));
            return (false, nicks.Count - 1);
        }
        Exec(c, "INSERT INTO melody_dislikes(track_id, nick, created_at) VALUES($id, $n, $now)", ("$id", trackId), ("$n", nick), ("$now", Now()));
        return (true, nicks.Count + 1);
    }

    // ---- що кімнаті не зайшло з порад Глека ----

    /// <summary>«Не те» чи швидкий скіп авто-треку: артист і сід, від якого порада прийшла.</summary>
    public void AddDjFeedback(string artistKey, string? seedId, string kind, string? nick)
    {
        using var c = Open();
        Exec(c, "INSERT INTO dj_feedback(artist_key, seed_id, kind, nick, created_at) VALUES($a, $s, $k, $n, $now)",
            ("$a", artistKey), ("$s", seedId), ("$k", kind), ("$n", nick), ("$now", Now()));
    }

    public List<(string ArtistKey, string? SeedId)> DjFeedbackSince(DateTimeOffset since)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT artist_key, seed_id FROM dj_feedback WHERE created_at >= $s",
            ("$s", since.ToUniversalTime().ToString("o")));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string?)>();
        while (r.Read()) list.Add((r.GetString(0), Str(r, 1)));
        return list;
    }

    // ---- акаунти ----

    const string AccountCols = "nick, pass_hash, pass_salt, role, google_sub, email";
    static Account ReadAccount(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), Str(r, 5));

    /// <summary>Нік уже чийсь? Порівняння без регістру — через nick_key, бо lower() у SQLite кирилиці не знає.</summary>
    public Account? FindAccount(string nick)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {AccountCols} FROM accounts WHERE nick_key=$k", ("$k", Auth.NickKey(nick)));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAccount(r) : null;
    }

    /// <summary>Акаунт, до якого прив'язаний цей Google (його стале <c>sub</c>, не пошта: пошту можна змінити).</summary>
    public Account? FindAccountByGoogle(string sub)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT {AccountCols} FROM accounts WHERE google_sub=$g", ("$g", sub));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAccount(r) : null;
    }

    /// <summary>
    /// Зайняти нік. false — уже зайнятий або цей Google уже чийсь (гонка двох реєстрацій теж сюди:
    /// PRIMARY KEY і унікальний індекс не дадуть двох). Порожній <paramref name="passHash"/> — вхід лише через Google.
    /// </summary>
    public bool AddAccount(string nick, string passHash, string passSalt, string? googleSub = null, string? email = null)
    {
        using var c = Open();
        try
        {
            Exec(c, "INSERT INTO accounts(nick_key, nick, pass_hash, pass_salt, created_at, seen_at, google_sub, email) VALUES($k, $n, $h, $s, $now, $now, $g, $e)",
                ("$k", Auth.NickKey(nick)), ("$n", nick), ("$h", passHash), ("$s", passSalt), ("$now", Now()), ("$g", googleSub), ("$e", email));
            return true;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return false; } // constraint: нік або Google уже є
    }

    /// <summary>Прив'язати Google до акаунта. false — цей Google уже прив'язаний до іншого.</summary>
    public bool SetAccountGoogle(string nick, string sub, string? email)
    {
        using var c = Open();
        try
        {
            Exec(c, "UPDATE accounts SET google_sub=$g, email=COALESCE($e, email) WHERE nick_key=$k", ("$g", sub), ("$e", email), ("$k", Auth.NickKey(nick)));
            return true;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return false; }
    }

    public void SetAccountPassword(string nick, string passHash, string passSalt)
    {
        using var c = Open();
        Exec(c, "UPDATE accounts SET pass_hash=$h, pass_salt=$s WHERE nick_key=$k", ("$h", passHash), ("$s", passSalt), ("$k", Auth.NickKey(nick)));
    }

    public void SetAccountRole(string nick, string role)
    {
        using var c = Open();
        Exec(c, "UPDATE accounts SET role=$r WHERE nick_key=$k", ("$r", role), ("$k", Auth.NickKey(nick)));
    }

    /// <summary>Коли востаннє заходив — щоб колись можна було відрізнити живі акаунти від покинутих.</summary>
    public void TouchAccount(string nick)
    {
        using var c = Open();
        Exec(c, "UPDATE accounts SET seen_at=$now WHERE nick_key=$k", ("$now", Now()), ("$k", Auth.NickKey(nick)));
    }

    /// <summary>
    /// Гість зареєструвався чи зайшов із того ж браузера: усе, що він нафармив як «гість Вася», переїжджає
    /// на акаунт. Ігрові таблиці — за nick_key, радійні — за самим ніком. Де в акаунта вже щось є (грав під
    /// цим ніком ще до акаунтів), зливаємо: гаманець і лічильники — сумою, рейтинг — більшим ело й сумою
    /// партій, ачівки та лайки — об'єднанням, збереження гончарні — тим, де більше зроблено глеків.
    /// Балачки не чіпаємо: історія — як було сказано. Виняток із «тут лише DDL для ігрових таблиць»:
    /// злиття мусить бути однією транзакцією через обидві половини бази.
    /// </summary>
    public void MergeNick(string from, string to)
    {
        var (fk, tk) = (Auth.NickKey(from), Auth.NickKey(to));
        if (fk.Length == 0 || fk == tk) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        (string, object?)[] p = [("$f", fk), ("$t", tk), ("$fn", from), ("$tn", to), ("$now", Now())];

        Exec(c, """
            INSERT INTO wallets(nick_key, nick, balance, earned, spent, updated_at)
            SELECT $t, $tn, balance, earned, spent, $now FROM wallets WHERE nick_key=$f
            ON CONFLICT(nick_key) DO UPDATE SET balance=balance+excluded.balance, earned=earned+excluded.earned,
                spent=spent+excluded.spent, updated_at=excluded.updated_at
            """, p);
        Exec(c, "DELETE FROM wallets WHERE nick_key=$f", p);
        Exec(c, "UPDATE ledger SET nick_key=$t WHERE nick_key=$f", p);
        Exec(c, """
            INSERT INTO economy_counters(nick_key, key, day, n) SELECT $t, key, day, n FROM economy_counters WHERE nick_key=$f
            ON CONFLICT(nick_key, key, day) DO UPDATE SET n=n+excluded.n
            """, p);
        Exec(c, "DELETE FROM economy_counters WHERE nick_key=$f", p);
        Exec(c, """
            INSERT INTO ratings(nick_key, game, nick, elo, games, wins, losses, draws, updated_at)
            SELECT $t, game, $tn, elo, games, wins, losses, draws, $now FROM ratings WHERE nick_key=$f
            ON CONFLICT(nick_key, game) DO UPDATE SET elo=max(elo, excluded.elo), games=games+excluded.games,
                wins=wins+excluded.wins, losses=losses+excluded.losses, draws=draws+excluded.draws, updated_at=excluded.updated_at
            """, p);
        Exec(c, "DELETE FROM ratings WHERE nick_key=$f", p);
        // Там, де ключ складений, конфлікт означає «в акаунта вже є» — його й лишаємо, гостьовий дублікат прибираємо.
        foreach (var table in new[] { "game_results", "achievements", "daily_results" })
        {
            Exec(c, $"UPDATE OR IGNORE {table} SET nick_key=$t, nick=$tn WHERE nick_key=$f", p);
            Exec(c, $"DELETE FROM {table} WHERE nick_key=$f", p);
        }
        foreach (var table in new[] { "likes", "chat_likes", "melody_dislikes", "play_listeners" })
        {
            Exec(c, $"UPDATE OR IGNORE {table} SET nick=$tn WHERE nick=$fn", p);
            Exec(c, $"DELETE FROM {table} WHERE nick=$fn", p);
        }
        Exec(c, "UPDATE plays SET requested_by=$tn WHERE requested_by=$fn", p);
        Exec(c, "UPDATE playlists SET created_by=$tn WHERE created_by=$fn", p);
        Exec(c, "UPDATE playlist_tracks SET added_by=$tn WHERE added_by=$fn", p);
        Exec(c, "UPDATE bans SET by_nick=$tn WHERE by_nick=$fn", p);
        Exec(c, "UPDATE dj_feedback SET nick=$tn WHERE nick=$fn", p);
        MergeStates(c, fk, tk);
        tx.Commit();
    }

    /// <summary>
    /// Збереження під ніком: <c>clicker:&lt;нік&gt;</c> (гончарня — покращення, глеки, все) і <c>daily:&lt;гра&gt;:&lt;день&gt;:&lt;нік&gt;</c>.
    /// Гончарню, якщо збереження є з обох боків, беремо ту, де більше зроблено глеків (Total у знімку);
    /// щоденну гру — ту, що вже в акаунта.
    /// </summary>
    static void MergeStates(SqliteConnection c, string fk, string tk)
    {
        var rows = new List<(string Key, string Json)>();
        using (var cmd = Cmd(c, "SELECT key, json FROM game_state WHERE key=$ck OR key LIKE 'daily:%:' || $f", ("$ck", "clicker:" + fk), ("$f", fk)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        foreach (var (key, json) in rows)
        {
            if (!key.EndsWith(fk, StringComparison.Ordinal)) continue;
            var target = key[..^fk.Length] + tk;
            string? existing;
            using (var q = Cmd(c, "SELECT json FROM game_state WHERE key=$k", ("$k", target))) existing = q.ExecuteScalar() as string;
            var take = existing is null || (key.StartsWith("clicker:", StringComparison.Ordinal) && Progress(json) > Progress(existing));
            if (take)
                Exec(c, "INSERT OR REPLACE INTO game_state(key, json, updated_at) VALUES($k, $j, $now)", ("$k", target), ("$j", json), ("$now", Now()));
            Exec(c, "DELETE FROM game_state WHERE key=$k", ("$k", key));
        }
    }

    /// <summary>Скільки глеків зроблено за весь час — поле Total у знімку гончарні; чужий чи битий JSON — 0.</summary>
    public static long Progress(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            foreach (var prop in d.RootElement.EnumerateObject())
                if (prop.Name.Equals("total", StringComparison.OrdinalIgnoreCase) && prop.Value.TryGetInt64(out var n)) return n;
        }
        catch (JsonException) { /* не знімок гончарні */ }
        return 0;
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
