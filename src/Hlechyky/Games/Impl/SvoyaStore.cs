using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Рядок таблиці пакетів. Сам пакет — цілим JSON у <see cref="Json"/>; решта колонок — те, що потрібно списку
/// без розбору JSON (назва, чий, чи публічний, чи грається).
/// </summary>
public sealed record SvoyaRow(
    string Id, string OwnerKey, string OwnerNick, string Title, bool Public, bool Hidden, bool Ready,
    string Source, string Json, long MediaBytes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Розібраний пакет — щоразу новий (без кешу: <c>row with { Json = … }</c> тягнув би за собою старий розбір).
    /// Кривий JSON у базі — порожній пакет, а не падіння списку.
    /// </summary>
    public SvoyaPack Pack() => SvoyaPack.Parse(Json) ?? new SvoyaPack { Id = Id, Title = Title };
}

/// <summary>
/// Пакети «Своєї гри» в SQLite (specs/svoya.md §6). Як <see cref="AdContestStore"/>: свій DDL і весь SQL тут,
/// від <see cref="Db"/> — лише з'єднання на одну коротку операцію. Вбудовані пакети сюди не пишуться: вони
/// живуть файлами (<see cref="SvoyaBuiltin"/>); лічильник партій — спільний для всіх (<c>svoya_plays</c>).
/// </summary>
public sealed class SvoyaStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS svoya_packs(
            id TEXT PRIMARY KEY, owner_key TEXT NOT NULL, owner_nick TEXT NOT NULL, title TEXT NOT NULL,
            public INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0, ready INTEGER NOT NULL DEFAULT 0,
            source TEXT NOT NULL DEFAULT 'user', json TEXT NOT NULL, media_bytes INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_svoya_packs_owner ON svoya_packs(owner_key);
        -- скільки разів пакет зіграли; і для вбудованих, яких у svoya_packs нема
        CREATE TABLE IF NOT EXISTS svoya_plays(pack_id TEXT PRIMARY KEY, n INTEGER NOT NULL DEFAULT 0);
        """;

    const string Columns = "id, owner_key, owner_nick, title, public, hidden, ready, source, json, media_bytes, created_at, updated_at";

    readonly Db _db;

    public SvoyaStore(Db db)
    {
        _db = db;
        _db.With(c => Exec(c, Schema));
    }

    public SvoyaRow? Get(string id) => _db.With(c => Many(c, "id = $id", ("$id", id)).FirstOrDefault());

    /// <summary>Пакети одного ніка, свіжі першими.</summary>
    public List<SvoyaRow> Mine(string ownerKey) => _db.With(c => Many(c, "owner_key = $k", ("$k", ownerKey)));

    /// <summary>Публічні, у які можна грати. Сховані адміном — лише з <paramref name="withHidden"/>.</summary>
    public List<SvoyaRow> Public(bool withHidden = false) =>
        _db.With(c => Many(c, withHidden ? "public = 1 AND ready = 1" : "public = 1 AND ready = 1 AND hidden = 0"));

    public int CountOf(string ownerKey) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM svoya_packs WHERE owner_key = $k", ("$k", ownerKey));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Скільки медіа в усіх пакетах ніка, крім <paramref name="exceptId"/> (для квоти при збереженні саме його).</summary>
    public long MediaBytesOf(string ownerKey, string? exceptId = null) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT COALESCE(SUM(media_bytes), 0) FROM svoya_packs WHERE owner_key = $k AND id <> $x",
            ("$k", ownerKey), ("$x", exceptId ?? ""));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    public void Insert(SvoyaRow row) => _db.With(c => Exec(c, $"""
        INSERT INTO svoya_packs({Columns})
        VALUES($id, $ok, $on, $t, $p, $h, $r, $s, $j, $m, $ca, $ua)
        """, Params(row)));

    /// <summary>Переписати пакет цілком. Власника й дату створення не чіпає — їх не переписує ніхто.</summary>
    public bool Update(SvoyaRow row) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            UPDATE svoya_packs SET title = $t, public = $p, ready = $r, source = $s, json = $j, media_bytes = $m, updated_at = $ua
            WHERE id = $id
            """, Params(row));
        return cmd.ExecuteNonQuery() > 0;
    });

    public bool SetHidden(string id, bool hidden) => _db.With(c =>
    {
        using var cmd = Cmd(c, "UPDATE svoya_packs SET hidden = $h WHERE id = $id", ("$h", hidden ? 1 : 0), ("$id", id));
        return cmd.ExecuteNonQuery() > 0;
    });

    public bool Delete(string id) => _db.With(c =>
    {
        Exec(c, "DELETE FROM svoya_plays WHERE pack_id = $id", ("$id", id));
        using var cmd = Cmd(c, "DELETE FROM svoya_packs WHERE id = $id", ("$id", id));
        return cmd.ExecuteNonQuery() > 0;
    });

    // ---------- партії ----------

    public void AddPlay(string packId) => _db.With(c => Exec(c, """
        INSERT INTO svoya_plays(pack_id, n) VALUES($id, 1)
        ON CONFLICT(pack_id) DO UPDATE SET n = n + 1
        """, ("$id", packId)));

    /// <summary>Лічильники партій для списку — одним запитом.</summary>
    public Dictionary<string, int> Plays() => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT pack_id, n FROM svoya_plays");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    });

    // ---------- дрібне ----------

    static (string, object?)[] Params(SvoyaRow row) =>
    [
        ("$id", row.Id), ("$ok", row.OwnerKey), ("$on", row.OwnerNick), ("$t", row.Title),
        ("$p", row.Public ? 1 : 0), ("$h", row.Hidden ? 1 : 0), ("$r", row.Ready ? 1 : 0), ("$s", row.Source),
        ("$j", row.Json), ("$m", row.MediaBytes), ("$ca", Iso(row.CreatedAt)), ("$ua", Iso(row.UpdatedAt)),
    ];

    static List<SvoyaRow> Many(SqliteConnection c, string where, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, $"SELECT {Columns} FROM svoya_packs WHERE {where} ORDER BY updated_at DESC, id", ps);
        using var r = cmd.ExecuteReader();
        var list = new List<SvoyaRow>();
        while (r.Read())
            list.Add(new SvoyaRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt32(4) == 1, r.GetInt32(5) == 1, r.GetInt32(6) == 1, r.GetString(7), r.GetString(8),
                r.GetInt64(9), Ts(r.GetString(10)), Ts(r.GetString(11))));
        return list;
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o");
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
}
