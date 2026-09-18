using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>👎 у «Вгадай мелодію»: трек із дизлайком (і та сама пісня з інших завантажень) гра більше не бере.</summary>
public class MelodyDislikeTests
{
    static string AddTrack(TempDb t, string id, string artist, string title, string file, int plays = 1)
    {
        t.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tracks(id, title, artist, duration_sec, source_url, file_path, created_at, song_key)
                VALUES($id, $t, $a, 200, 'x', $f, '2026-09-01T00:00:00Z', $k)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$a", artist);
            cmd.Parameters.AddWithValue("$f", file);
            cmd.Parameters.AddWithValue("$k", SongKey.Of(artist, title));
            cmd.ExecuteNonQuery();
            for (var i = 0; i < plays; i++)
            {
                using var p = c.CreateCommand();
                p.CommandText = "INSERT INTO plays(track_id, source, started_at) VALUES($id, 'user', '2026-09-01T00:00:00Z')";
                p.Parameters.AddWithValue("$id", id);
                p.ExecuteNonQuery();
            }
        });
        return id;
    }

    [Fact]
    public void Dislike_toggles_per_nick_without_case()
    {
        using var t = new TempDb();
        var file = Path.GetTempFileName();
        try
        {
            AddTrack(t, "a", "Океан Ельзи", "Обійми", file);
            Assert.Equal((true, 1), t.Db.ToggleMelodyDislike("a", "Оля"));
            Assert.Equal((true, 2), t.Db.ToggleMelodyDislike("a", "Петро"));
            Assert.Equal((false, 1), t.Db.ToggleMelodyDislike("a", "оля"));
            Assert.Null(t.Db.ToggleMelodyDislike("nope", "Оля"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_disliked_song_is_not_picked_even_from_another_upload()
    {
        using var t = new TempDb();
        var file = Path.GetTempFileName();
        try
        {
            AddTrack(t, "a1", "Скрябін", "Старі фотографії", file);
            AddTrack(t, "a2", "Скрябін", "Старі фотографії (Official Video)", file);   // інше завантаження тієї ж пісні
            AddTrack(t, "b", "Океан Ельзи", "Обійми", file);
            AddTrack(t, "c", "KALUSH", "Stefania", file);

            var library = new MelodyLibrary(t.Db, null, classics: MelodyClassics.Empty);
            var radio = MelodyCategories.Radio;
            Assert.Equal(3, (await library.PickAsync(10, radio, new Random(1), default)).Count);   // а1/а2 — одна пісня

            t.Db.ToggleMelodyDislike("a1", "Оля");
            var picked = await library.PickAsync(10, radio, new Random(1), default);
            Assert.DoesNotContain(picked, x => x.Id is "a1" or "a2");
            Assert.Equal(["b", "c"], picked.Select(x => x.Id).Order());

            t.Db.ToggleMelodyDislike("a1", "Оля");     // зняли — повернулась
            Assert.Contains(await library.PickAsync(10, radio, new Random(1), default), x => x.Id is "a1" or "a2");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_disliked_song_from_a_playlist_is_not_fetched_either()
    {
        using var t = new TempDb();
        var file = Path.GetTempFileName();
        try
        {
            AddTrack(t, "q", "Queen", "Bohemian Rhapsody (Official Video)", file);
            var classics = MelodyClassics.Parse(["[rock] Рок", "Queen — Bohemian Rhapsody", "Nirvana — Lithium"]);
            var library = new MelodyLibrary(t.Db, null, classics: classics);

            var picked = await library.PickAsync(10, ["rock"], new Random(1), default);
            Assert.Equal(2, picked.Count);
            Assert.Equal("q", picked[0].Id);                       // з кешу — і першим, бо вже на диску
            Assert.Equal("Bohemian Rhapsody", picked[0].Title);    // під назвою з добірки
            Assert.True(picked[1].Pending);

            t.Db.ToggleMelodyDislike("q", "Оля");
            picked = await library.PickAsync(10, ["rock"], new Random(1), default);
            var only = Assert.Single(picked);
            Assert.Equal("Lithium", only.Title);                   // Queen з 👎 — і не з кешу, і не качати
        }
        finally { File.Delete(file); }
    }
}
