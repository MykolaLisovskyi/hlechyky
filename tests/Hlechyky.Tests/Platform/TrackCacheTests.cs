using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Кеш треків і рейтинг. Кеш за тиждень ефіру з'їдав диск, а одна й та сама пісня з різних каналів YouTube
/// качалась по кілька разів («Stand By Me» — вчетверо).
/// </summary>
public class TrackCacheTests
{
    static TrackInfo T(string id, string artist, string title, int dur = 200) =>
        new(id, title, artist, dur, null, "https://music.youtube.com/watch?v=" + id, null);

    [Theory]
    [InlineData("Ben E. King", "Stand By Me", "Ben E King", "Stand by me (Official Video)")]
    [InlineData("Океан Ельзи", "Обійми", "ОКЕАН ЕЛЬЗИ", "Обійми [Official Audio]")]
    [InlineData("Roy Orbison", "Oh, Pretty Woman", "Roy Orbison", "Oh Pretty Woman (Remastered 2015)")]
    public void Same_song_from_different_uploads_has_one_key(string a1, string t1, string a2, string t2) =>
        Assert.Equal(SongKey.Of(a1, t1), SongKey.Of(a2, t2));

    [Theory]
    [InlineData("Matheus & Kauan", "Então Toma", "Matheus & Kauan", "Então Toma (Ao Vivo)")]
    [InlineData("Imagine Dragons", "Believer", "Imagine Dragons", "Believer (Kaskade Remix)")]
    [InlineData("SadSvit", "Касета", "SadSvit", "Касета (acoustic)")]
    public void Live_remix_and_acoustic_are_other_recordings(string a1, string t1, string a2, string t2) =>
        Assert.NotEqual(SongKey.Of(a1, t1), SongKey.Of(a2, t2));

    [Theory]
    [InlineData(200, 204, true)]
    [InlineData(200, 206, false)]
    [InlineData(0, 200, false)]   // невідома тривалість — ризикувати не будемо
    public void Duration_must_match_within_five_seconds(int a, int b, bool same) =>
        Assert.Equal(same, SongKey.SameDuration(a, b));

    // ---- що видаляти ----

    static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static CachePlan.Entry E(string name, int daysAgo, int plays = 1, int likes = 0, bool playlist = false, long mb = 100) =>
        new(name, mb * 1024 * 1024, Now.AddDays(-daysAgo), plays, likes, playlist);

    const long Mb = 1024 * 1024;

    [Fact]
    public void Under_the_limit_nothing_goes()
    {
        var files = new[] { E("a", 30), E("b", 20) };
        Assert.Empty(CachePlan.Victims(files, new HashSet<string>(), 300 * Mb));
    }

    [Fact]
    public void Oldest_single_plays_go_first_down_to_ninety_percent()
    {
        var files = new[] { E("new", 0), E("old", 10), E("older", 20), E("oldest", 30) };
        var victims = CachePlan.Victims(files, new HashSet<string>(), 300 * Mb).Select(v => v.Path).ToList();
        // 400 МБ при ліміті 300: до 270 треба зняти два файли, а не один
        Assert.Equal(["oldest", "older"], victims);
    }

    [Fact]
    public void Repeats_likes_and_playlists_buy_time()
    {
        var files = new[]
        {
            E("hit", 12, plays: 6),          // 12 днів тому, але грав 6 разів: +10 днів
            E("liked", 9, likes: 1),         // +3 дні
            E("listed", 8, playlist: true),  // +3 дні
            E("once", 7),
        };
        var victims = CachePlan.Victims(files, new HashSet<string>(), 250 * Mb).Select(v => v.Path).ToList();
        Assert.Equal(["once", "liked"], victims);
    }

    [Fact]
    public void Busy_files_are_never_deleted_even_when_oldest()
    {
        var files = new[] { E("on-air", 100), E("queued", 90), E("x", 1) };
        var victims = CachePlan.Victims(files, new HashSet<string> { "on-air", "queued" }, 150 * Mb).Select(v => v.Path).ToList();
        Assert.Equal(["x"], victims);
    }

    // ---- та сама пісня вже лежить у кеші ----

    [Fact]
    public void Another_upload_of_a_cached_song_reuses_its_file()
    {
        using var db = new TempDb();
        var dir = Directory.CreateTempSubdirectory("hlechyky-cache-").FullName;
        try
        {
            var yt = new YtDlpService(new FixedOptions<YtDlpOptions>(new YtDlpOptions { CacheDir = dir }), db.Db, NullLogger<YtDlpService>.Instance);
            var topic = T("topic000001", "Ben E. King", "Stand By Me", 180);
            var clip = T("clip0000002", "Ben E King", "Stand by Me (Official Video)", 183);
            var live = T("live0000003", "Ben E. King", "Stand By Me (Live)", 240);
            foreach (var t in new[] { topic, clip, live }) db.Db.UpsertTrack(t);

            var file = Path.Combine(dir, topic.Id + ".m4a");
            File.WriteAllBytes(file, [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(dir, topic.Id + ".webp"), [1]);   // обкладинка — не трек
            db.Db.SetTrackFile(topic.Id, file);

            Assert.Equal(file, yt.FindCached(topic.Id));
            Assert.Equal(file, yt.FindCached(clip.Id));
            Assert.Null(yt.FindCached(live.Id));

            db.Db.ForgetTrackFile(file);
            File.Delete(file);
            Assert.Null(yt.FindCached(clip.Id));
            Assert.Null(db.Db.TrackFile(topic.Id));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- рейтинг ----

    [Fact]
    public void Rating_counts_plays_completion_and_listeners()
    {
        using var db = new TempDb();
        var a = T("aaaaaaaaaaa", "SadSvit", "Касета", 200);
        var b = T("bbbbbbbbbbb", "Kalush", "Стефанія", 180);
        db.Db.UpsertTrack(a);
        db.Db.UpsertTrack(b);

        // a: двічі, раз дограв (довжину файлу знає liquidsoap), раз скіп на половині
        var p1 = db.Db.StartPlay(a.Id, "user", "микола", null, null);
        db.Db.SetPlayDuration(p1, 200);
        db.Db.NotePlayListeners(p1, 3, ["микола", "оля"]);
        db.Db.EndPlay(p1, skipped: false);
        var p2 = db.Db.StartPlay(a.Id, "autodj", null, null, null);
        db.Db.NotePlayListeners(p2, 1, ["оля"]);
        db.Db.EndPlay(p2, skipped: true);
        db.Db.Exec("UPDATE plays SET started_at=$s, ended_at=$e WHERE id=$id",
            ("$s", Now.ToString("o")), ("$e", Now.AddSeconds(198).ToString("o")), ("$id", p1));
        db.Db.Exec("UPDATE plays SET started_at=$s, ended_at=$e WHERE id=$id",
            ("$s", Now.AddMinutes(5).ToString("o")), ("$e", Now.AddMinutes(5).AddSeconds(100).ToString("o")), ("$id", p2));

        var pb = db.Db.StartPlay(b.Id, "autodj", null, null, null);
        db.Db.EndPlay(pb, skipped: false);

        var byPlays = db.Db.TrackRatings(3650, "plays", 10);
        Assert.Equal([a.Id, b.Id], byPlays.Select(r => r.Track.Id));
        var ra = byPlays[0];
        Assert.Equal(2, ra.Plays);
        Assert.Equal(1, ra.Skips);
        Assert.Equal(75, ra.Completion);   // (100% + 50%) / 2
        Assert.Equal(2, ra.Listeners);
        Assert.Equal(["микола", "оля"], ra.ListenerNicks.Order());
        Assert.Equal(3, ra.StreamPeak);
    }
}
