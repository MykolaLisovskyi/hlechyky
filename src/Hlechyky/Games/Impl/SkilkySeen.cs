using System.Globalization;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Пам'ять «Скільки?»: хто яке запитання вже бачив і коли. Живе в базі, тож переживає рестарти й деплої, і
/// своя в кожного ніка — хто пропустив партію, свої запитання ще побачить. Таблиця й SQL тут, як у реклами:
/// від <see cref="Db"/> беремо лише з'єднання.
/// <para>
/// Ніколи не кидає. Нема бази (тести з порожнім провайдером), зайнята чи покалічена — пам'ять мовчить, і
/// запитання просто тасуються, як раніше: повтор кращий за зламану партію.
/// </para>
/// </summary>
public sealed class SkilkySeen(Db? db)
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS skilky_seen(
            nick_key TEXT NOT NULL, q_key TEXT NOT NULL, seen_at TEXT NOT NULL, times INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY(nick_key, q_key)) WITHOUT ROWID;
        """;

    bool _ready;

    /// <summary>Ключ ніка — той самий, що в економіці: без пробілів по краях і без регістру.</summary>
    public static string NickKey(string nick) => EconomyStore.Key(nick);

    /// <summary>
    /// Для кожного запитання, яке бачив хоч хтось із цих гравців, — коли його бачили востаннє (найсвіжіший
    /// раз серед усіх). Запитань, яких не бачив ніхто, у словнику нема.
    /// </summary>
    public Dictionary<string, DateTimeOffset> LastSeen(IReadOnlyCollection<string> nickKeys)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (db is null || nickKeys.Count == 0) return result;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var cmd = c.CreateCommand();
                var names = nickKeys.Select((key, i) =>
                {
                    cmd.Parameters.AddWithValue("$n" + i, key);
                    return "$n" + i;
                }).ToList();
                cmd.CommandText = $"SELECT q_key, MAX(seen_at) FROM skilky_seen WHERE nick_key IN ({string.Join(",", names)}) GROUP BY q_key";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result[r.GetString(0)] = DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                return 0;
            });
        }
        catch (Exception)
        {
            result.Clear();
        }
        return result;
    }

    /// <summary>Ці гравці щойно побачили це запитання.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, SkilkyQuestion question, DateTimeOffset now) =>
        Mark(nickKeys, [question], now);

    /// <summary>Ці гравці бачили ці запитання — одним записом у базу.</summary>
    public void Mark(IReadOnlyCollection<string> nickKeys, IEnumerable<SkilkyQuestion> questions, DateTimeOffset now)
    {
        if (db is null || nickKeys.Count == 0) return;
        try
        {
            db.With(c =>
            {
                Ensure(c);
                using var tx = c.BeginTransaction();
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO skilky_seen(nick_key, q_key, seen_at) VALUES($n, $q, $at)
                    ON CONFLICT(nick_key, q_key) DO UPDATE SET seen_at = excluded.seen_at, times = times + 1
                    """;
                var n = cmd.Parameters.Add("$n", SqliteType.Text);
                var q = cmd.Parameters.Add("$q", SqliteType.Text);
                // UTC і «O»: однакова довжина рядка, тож MAX(seen_at) у SQL — це справді найсвіжіший час.
                cmd.Parameters.AddWithValue("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                foreach (var question in questions)
                    foreach (var key in nickKeys)
                    {
                        n.Value = key;
                        q.Value = question.Key;
                        cmd.ExecuteNonQuery();
                    }
                tx.Commit();
                return 0;
            });
        }
        catch (Exception)
        {
            // Не запам'ятали — запитання колись повториться раніше, ніж могло б. Партія від цього не страждає.
        }
    }

    void Ensure(SqliteConnection c)
    {
        if (_ready) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _ready = true;
    }

    /// <summary>
    /// Найсвіжіші для цього столу: спершу ті, яких не бачив ніхто, далі — бачені найдавніше. Порядок
    /// усередині однаково свіжих бере з <paramref name="shuffled"/> (сортування стабільне), тож тасувати треба
    /// до виклику. Без жодного повтору, щонайбільше <paramref name="take"/>.
    /// </summary>
    public static List<T> Freshest<T>(IReadOnlyList<T> shuffled, Func<T, string> key,
        IReadOnlyDictionary<string, DateTimeOffset> lastSeen, int take) =>
        [.. shuffled
            .OrderBy(x => lastSeen.TryGetValue(key(x), out var at) ? at : DateTimeOffset.MinValue)
            .Take(take)];
}
