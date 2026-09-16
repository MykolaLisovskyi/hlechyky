using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Підроблене джерело: треки зі списку, уривок — кілька кілобайт нулів (або null для «зламаних»).</summary>
sealed class FakeMelodySource(IReadOnlyList<MelodyTrack> tracks, ISet<string>? broken = null) : IMelodySource
{
    public int Clips;

    public Task<IReadOnlyList<MelodyTrack>> PickAsync(int count, Random rng, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MelodyTrack>>([.. tracks.Take(count)]);

    public Task<byte[]?> ClipAsync(MelodyTrack track, double startSec, int seconds, CancellationToken ct)
    {
        Interlocked.Increment(ref Clips);
        return Task.FromResult(broken?.Contains(track.Id) == true ? null : new byte[5000]);
    }
}

/// <summary>«Вгадай мелодію» (specs/melody.md).</summary>
public class MelodyTests
{
    static MelodyTrack T(string id, string artist, string title, int dur = 200) => new(id, title, artist, dur, null, "/dev/null");

    static readonly MelodyTrack[] Songs =
    [
        T("a", "Океан Ельзи", "Обійми"),
        T("b", "Скрябін", "Старі фотографії"),
        T("c", "DakhaBrakha", "Vesna"),
        T("d", "KALUSH", "Stefania (feat. Skofka)"),
        T("e", "The Hardkiss", "Journey"),
    ];

    static RoomHarness Table(IMelodySource source, object? options = null, params string[] nicks)
    {
        var h = new RoomHarness("melody", options: options, seed: 3, services: RoomHarness.WithService(source));
        foreach (var n in nicks.Length == 0 ? ["Оля", "Петро"] : nicks) h.Join(n);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    /// <summary>Тикати, поки фон не наріже уривок і фаза не стане потрібною (фон — справжня задача).</summary>
    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < 400 && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++)
        {
            h.Tick();
            if (Phase(h) != phase) Thread.Sleep(2);
        }
        Assert.Equal(phase, Phase(h));
    }

    static ActResult Guess(RoomHarness h, int seat, string text)
    {
        h.Clock.AdvanceMs(Melody.GuessEveryMs);
        return h.Act(seat, "guess", new { text });
    }

    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    static RoomHarness Playing(object? options = null)
    {
        var h = Table(new FakeMelodySource([Songs[0], Songs[1], Songs[2]]), options ?? new { rounds = "5" });
        Until(h, "play");
        return h;
    }

    // ---------------------------------------------------------------- раунд

    [Fact]
    public void A_round_starts_with_a_clip_link_and_no_answer()
    {
        var h = Playing();
        var v = h.View(1);
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Matches(@"^/api/games/melody/[0-9a-f]{24}\.mp3$", v.GetProperty("clip").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("answer").ValueKind);
        Assert.DoesNotContain("Обійми", v.GetRawText());
        Assert.DoesNotContain("Океан", v.GetRawText());
    }

    [Fact]
    public void The_clip_is_served_by_its_token()
    {
        var h = Playing();
        var url = h.View(0).GetProperty("clip").GetString()!;
        var token = url[(url.LastIndexOf('/') + 1)..^4];
        Assert.Equal(5000, MelodyClips.Get(token)!.Length);
        Assert.Null(MelodyClips.Get("0123456789abcdef01234567"));
    }

    [Fact]
    public void Artist_and_title_score_separately_and_first_gets_a_bonus()
    {
        var h = Playing();

        var r = Guess(h, 0, "океан ельзи");
        Assert.True(r.Ok);
        Assert.Contains("виконавець", r.Message);
        Assert.Equal(Melody.ArtistPoints + Melody.ArtistFirst, Score(h, 0));

        Assert.True(Guess(h, 1, "Okean Elzy").Ok);
        Assert.Equal(Melody.ArtistPoints, Score(h, 1));

        Assert.True(Guess(h, 1, "обійми").Ok);
        Assert.Equal(Melody.ArtistPoints + Melody.TitlePoints + Melody.TitleFirst, Score(h, 1));
    }

    [Fact]
    public void Both_at_once_count_twice()
    {
        var h = Playing();
        var r = Guess(h, 0, "Океан Ельзи — Обійми");
        Assert.Contains("виконавець", r.Message);
        Assert.Contains("назва", r.Message);
        Assert.True(h.View(0).GetProperty("me").GetProperty("title").GetBoolean());
    }

    [Fact]
    public void A_miss_is_a_miss_and_repeats_score_nothing()
    {
        var h = Playing();
        Assert.Equal("Мимо", Guess(h, 0, "Бумбокс").Message);
        Guess(h, 0, "океан ельзи");
        var before = Score(h, 0);
        Assert.False(Guess(h, 0, "океан ельзи").Ok);
        Assert.Equal(before, Score(h, 0));
    }

    [Fact]
    public void Guesses_are_rate_limited()
    {
        var h = Playing();
        Guess(h, 0, "щось");
        Assert.Equal("Не так швидко", h.Act(0, "guess", new { text = "обійми" }).Message);
    }

    [Fact]
    public void When_everyone_has_both_the_round_ends_and_the_answer_opens()
    {
        var h = Playing();
        Guess(h, 0, "океан ельзи обійми");
        Guess(h, 1, "океан ельзи обійми");
        h.Tick();

        Assert.Equal("reveal", Phase(h));
        var answer = h.View(null).GetProperty("answer");
        Assert.Equal("Обійми", answer.GetProperty("title").GetString());
        Assert.False(Guess(h, 0, "скрябін").Ok);
    }

    [Fact]
    public void Time_runs_out_and_the_next_track_follows()
    {
        var h = Playing(new { rounds = "5", clip = "10" });
        h.Tick((10_000 + Melody.ExtraMs) / Melody.TickMs + 1);
        Assert.Equal("reveal", Phase(h));
        h.Tick(Melody.RevealMs / Melody.TickMs + 1);
        Until(h, "play");
        Assert.Equal(2, h.View(null).GetProperty("round").GetInt32());
    }

    [Fact]
    public void Fewer_tracks_than_rounds_means_a_shorter_game()
    {
        var h = Table(new FakeMelodySource([Songs[0], Songs[1]]), new { rounds = "5", clip = "10" });
        for (var i = 0; i < 10 && h.Room.Status == RoomStatus.Playing; i++)
        {
            Until(h, "play");
            Guess(h, 0, h.View(0).GetProperty("round").GetInt32() == 1 ? "обійми" : "старі фотографії");
            h.Tick((10_000 + Melody.ExtraMs) / Melody.TickMs + 1);
            h.Tick(Melody.RevealMs / Melody.TickMs + 2);
            for (var k = 0; k < 50 && h.Room.Status == RoomStatus.Playing && Phase(h) == "loading"; k++) { h.Tick(); Thread.Sleep(2); }
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("найкраще вухо в Оля", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_clip_that_fails_is_skipped()
    {
        var src = new FakeMelodySource([Songs[0], Songs[1]], new HashSet<string> { "a" });
        var h = Table(src, new { rounds = "5" });
        Until(h, "play");
        Assert.Contains("виконавець", Guess(h, 0, "скрябін").Message);
    }

    [Fact]
    public void No_tracks_at_all_closes_the_table_with_a_reason()
    {
        var h = Table(new FakeMelodySource([]));
        for (var i = 0; i < 200 && h.Room.Status == RoomStatus.Playing; i++) { h.Tick(); Thread.Sleep(2); }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Contains("нема скачаних треків", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Solo_works_and_leaving_everyone_ends_it()
    {
        var h = Table(new FakeMelodySource(Songs), null, "Оля");
        Until(h, "play");
        h.Leave("Оля");
        Assert.False(h.Rooms.Find(h.RoomId) is { Status: RoomStatus.Playing });
    }

    [Fact]
    public void Rematch_starts_over_with_zero_scores()
    {
        var h = Table(new FakeMelodySource([Songs[0]]), new { rounds = "5", clip = "10" });
        Until(h, "play");
        Guess(h, 0, "обійми");
        h.Tick((10_000 + Melody.ExtraMs) / Melody.TickMs + 1);
        h.Tick(Melody.RevealMs / Melody.TickMs + 2);
        for (var k = 0; k < 200 && h.Room.Status == RoomStatus.Playing; k++) { h.Tick(); Thread.Sleep(2); }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch();
        Until(h, "play");
        Assert.Equal(1, h.View(null).GetProperty("round").GetInt32());
        Assert.All(h.View(null).GetProperty("scores").EnumerateArray(), e => Assert.Equal(0, e.GetInt32()));
    }

    // ---------------------------------------------------------------- відповіді

    [Theory]
    [InlineData("Океан Ельзи", "Як ніколи", "океан ельзи", true)]
    [InlineData("Океан Ельзи", "Як ніколи", "Okean Elzy", true)]
    [InlineData("Скрябін", "Сам собі країна", "Skryabin", true)]
    [InlineData("Скрябін", "Сам собі країна", "скрябин", true)]
    [InlineData("KALUSH", "Stefania", "калуш", true)]
    [InlineData("DNK", "А Може Ти Мене Полюбиш (feat. TEMRA)", "temra", false)]
    [InlineData("Jerry Heil & alyona alyona", "Teresa & Maria", "alyona alyona", true)]
    [InlineData("Somber Sounds", "Скрябін - Спи собі сама", "скрябін", true)]
    [InlineData("Океан Ельзи - Topic", "Обійми", "океан ельзи", true)]
    [InlineData("TESLENKO", "Кожен раз", "teslenko", true)]
    [InlineData("TESLENKO", "Кожен раз", "tesla", false)]
    [InlineData("The Hardkiss", "Journey", "hardkis", false)]
    [InlineData("The Hardkiss", "Journey", "the hardkis", true)]
    public void Artist_matching(string artist, string title, string guess, bool hit) =>
        Assert.Equal(hit, MelodyAnswer.Hits(guess, MelodyAnswer.Artists(T("x", artist, title))));

    [Theory]
    [InlineData("DNK", "А Може Ти Мене Полюбиш (feat. TEMRA)", "а може ти мене полюбиш", true)]
    [InlineData("Somber Sounds", "Скрябін - Спи собі сама", "спи собі сама", true)]
    [InlineData("Пиріг і Батіг", "Гаї шумлять (1913)", "гаї шумлять", true)]
    [InlineData("Пиріг і Батіг", "Гаї шумлять (1913)", "гаи шумлять", true)]
    [InlineData("Океан Ельзи", "Обійми", "обійми мене", true)]
    [InlineData("Океан Ельзи", "Обійми", "обі", false)]
    [InlineData("KALUSH", "Stefania", "Стефанія", true)]
    public void Title_matching(string artist, string title, string guess, bool hit) =>
        Assert.Equal(hit, MelodyAnswer.Hits(guess, MelodyAnswer.Titles(T("x", artist, title))));

    [Fact]
    public void Picking_avoids_the_same_song_and_prefers_other_artists()
    {
        var list = new List<MelodyTrack>
        {
            T("1", "Скрябін", "Старі фотографії"),
            T("2", "Скрябін", "Старі фотографії (Official Video)"),
            T("3", "Скрябін", "Люди як кораблі"),
            T("4", "Океан Ельзи", "Обійми"),
        };
        var picked = MelodyLibrary.Choose(list, 2, new Random(1));
        Assert.Equal(2, picked.Count);
        Assert.NotEqual(picked[0].Artist, picked[1].Artist);

        var all = MelodyLibrary.Choose([.. list], 4, new Random(1));
        Assert.Equal(3, all.Count);   // дві копії «Старих фотографій» — одна пісня
    }
}
