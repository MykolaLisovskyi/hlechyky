using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Зіпсований телефон (specs/telephone.md): фраза → малюнок → опис → … по колу, потім показ і ❤.
/// </summary>
public class TelephoneTests
{
    static readonly TelephonePhrases Phrases = new(["кіт на даху"]);

    static RoomHarness Table(int players, object? options = null)
    {
        var h = new RoomHarness("telephone", options: options, seed: 5, services: RoomHarness.WithService(Phrases));
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван", "Марта" }.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static JsonElement Task(RoomHarness h, int seat) => h.View(seat).GetProperty("task");
    static string Kind(RoomHarness h, int seat) => Task(h, seat).GetProperty("kind").GetString()!;
    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static int Step(RoomHarness h) => h.View(null).GetProperty("step").GetInt32();

    static void Write(RoomHarness h, int seat, string text) => Assert.True(h.Act(seat, "done", new { text }).Ok);

    static void DrawAndSubmit(RoomHarness h, int seat)
    {
        h.Input(seat, "draw", new { s = 1, c = 1, w = 8, p = new[] { 10, 10, seat * 100, 300 } });
        Assert.True(h.Act(seat, "done", new { n = 1 }).Ok);
    }

    /// <summary>Усі за столом здають своє завдання, і тик переводить на наступний крок.</summary>
    static void EveryoneSubmits(RoomHarness h, int players)
    {
        for (var s = 0; s < players; s++)
        {
            if (Kind(h, s) == Telephone.Draw) DrawAndSubmit(h, s);
            else Write(h, s, $"{Kind(h, s)} від {s} крок {Step(h)}");
        }
        h.Tick();
    }

    static void Next(RoomHarness h, int seat = 0)
    {
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        Assert.True(h.Act(seat, "next").Ok);
        h.Tick();
    }

    // ---------------------------------------------------------------- кроки

    [Fact]
    public void Everyone_starts_by_writing_a_phrase_with_ideas_to_roll()
    {
        var h = Table(3);
        Assert.Equal("step", Phase(h));
        for (var s = 0; s < 3; s++)
        {
            Assert.Equal(Telephone.Phrase, Kind(h, s));
            Assert.Equal(JsonValueKind.Null, Task(h, s).GetProperty("prompt").ValueKind);
            Assert.Contains("кіт на даху", Task(h, s).GetProperty("ideas").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("task").ValueKind);
        Assert.Equal(3, h.View(null).GetProperty("steps").GetInt32());
    }

    [Fact]
    public void The_next_player_draws_your_phrase_and_nobody_else_sees_it()
    {
        var h = Table(3);
        Write(h, 0, "слон на роликах");
        Write(h, 1, "бабуся в космосі");
        Write(h, 2, "борщ з кавуном");
        h.Tick();

        Assert.Equal(2, Step(h));
        // Петро (місце 1) малює ланцюжок Олі (0), Ганна — Петрів, Оля — Ганнин
        Assert.Equal(Telephone.Draw, Kind(h, 1));
        Assert.Equal("слон на роликах", Task(h, 1).GetProperty("prompt").GetProperty("text").GetString());
        Assert.Equal("бабуся в космосі", Task(h, 2).GetProperty("prompt").GetProperty("text").GetString());
        Assert.Equal("борщ з кавуном", Task(h, 0).GetProperty("prompt").GetProperty("text").GetString());
        Assert.DoesNotContain("слон на роликах", h.View(2).GetRawText());
        Assert.DoesNotContain("слон на роликах", h.View(null).GetRawText());
    }

    [Fact]
    public void After_a_drawing_comes_a_description_of_that_drawing()
    {
        var h = Table(3);
        EveryoneSubmits(h, 3);
        EveryoneSubmits(h, 3);

        Assert.Equal(3, Step(h));
        Assert.Equal(Telephone.Describe, Kind(h, 2));
        var prompt = Task(h, 2).GetProperty("prompt");
        Assert.Equal("drawing", prompt.GetProperty("kind").GetString());
        // Ганна (2) описує ланцюжок Олі — його малював Петро (1)
        Assert.Equal(100, prompt.GetProperty("ops")[0][6].GetInt32());
    }

    [Fact]
    public void A_step_waits_until_everyone_submits()
    {
        var h = Table(3);
        Write(h, 0, "раз");
        Write(h, 1, "два");
        h.Tick(5);
        Assert.Equal(1, Step(h));
        Assert.Equal([2], h.View(null).GetProperty("waiting").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void Edit_takes_a_submission_back()
    {
        var h = Table(3);
        Write(h, 0, "раз");
        Assert.True(h.Act(0, "edit").Ok);
        Write(h, 1, "два");
        Write(h, 2, "три");
        h.Tick();
        Assert.Equal(1, Step(h));
        Assert.False(Task(h, 0).GetProperty("ready").GetBoolean());
    }

    [Fact]
    public void An_empty_phrase_cannot_be_submitted()
    {
        var h = Table(3);
        Assert.False(h.Act(0, "done", new { text = "   " }).Ok);
    }

    [Fact]
    public void Time_running_out_fills_in_what_was_missing()
    {
        var h = Table(3, new { tempo = "fast" });
        h.Input(0, "text", new { text = "чернетка Олі" });
        h.Tick(40_000 / Telephone.TickMs + 1);

        Assert.Equal(2, Step(h));
        Assert.Equal("чернетка Олі", Task(h, 1).GetProperty("prompt").GetProperty("text").GetString());
        Assert.Equal("кіт на даху", Task(h, 2).GetProperty("prompt").GetProperty("text").GetString());   // Петро нічого не написав

        // малюнок, який не здали, все одно йде далі таким, яким був
        h.Input(1, "draw", new { s = 1, c = 1, w = 8, p = new[] { 1, 1, 2, 2 } });
        h.Tick(60_000 / Telephone.TickMs + 1);
        Assert.Equal(1, Task(h, 2).GetProperty("prompt").GetProperty("ops").GetArrayLength());
    }

    [Fact]
    public void A_drawing_that_did_not_fully_arrive_is_not_accepted()
    {
        var h = Table(3);
        EveryoneSubmits(h, 3);
        h.Input(1, "draw", new { s = 1, c = 1, w = 8, p = new[] { 1, 1, 2, 2 } });
        var r = h.Act(1, "done", new { n = 2 });
        Assert.False(r.Ok);
        Assert.Contains("не цілком", r.Message);
    }

    [Fact]
    public void Only_the_drawer_of_this_step_can_draw()
    {
        var h = Table(3);
        h.Input(0, "draw", new { s = 1, c = 1, w = 8, p = new[] { 1, 1, 2, 2 } });   // зараз фраза, не малюнок
        EveryoneSubmits(h, 3);
        Assert.Equal(0, Task(h, 0).GetProperty("n").GetInt32());
    }

    [Fact]
    public void Steps_option_cuts_the_chain_short()
    {
        var h = Table(5, new { steps = "4" });
        Assert.Equal(4, h.View(null).GetProperty("steps").GetInt32());
    }

    // ---------------------------------------------------------------- показ

    static RoomHarness Revealing()
    {
        var h = Table(3);
        for (var i = 0; i < 3; i++) EveryoneSubmits(h, 3);
        return h;
    }

    [Fact]
    public void After_the_last_step_chains_are_revealed_one_entry_at_a_time()
    {
        var h = Revealing();
        Assert.Equal("reveal", Phase(h));
        var r = h.View(1).GetProperty("reveal");
        Assert.Equal(0, r.GetProperty("owner").GetInt32());
        Assert.Equal(3, r.GetProperty("total").GetInt32());
        Assert.Equal(1, r.GetProperty("entries").GetArrayLength());

        Next(h);
        Next(h);
        var entries = h.View(1).GetProperty("reveal").GetProperty("entries");
        Assert.Equal(["text", "drawing", "text"], entries.EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal([0, 1, 2], entries.EnumerateArray().Select(e => e.GetProperty("seat").GetInt32()));

        Next(h);
        Assert.Equal(1, h.View(1).GetProperty("reveal").GetProperty("owner").GetInt32());
    }

    [Fact]
    public void A_double_click_on_next_turns_only_one_page()
    {
        var h = Revealing();
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        h.Act(0, "next");
        h.Act(1, "next");
        Assert.Equal(2, h.View(0).GetProperty("reveal").GetProperty("shown").GetInt32());
    }

    [Fact]
    public void Likes_go_to_the_author_and_toggle()
    {
        var h = Revealing();
        Assert.True(h.Act(1, "like", new { chain = 0, index = 0 }).Ok);
        Assert.True(h.Act(2, "like", new { chain = 0, index = 0 }).Ok);
        Assert.Equal(2, h.View(null).GetProperty("likes")[0].GetInt32());
        Assert.True(h.View(1).GetProperty("reveal").GetProperty("entries")[0].GetProperty("liked").GetBoolean());

        Assert.True(h.Act(2, "like", new { chain = 0, index = 0 }).Ok);
        Assert.Equal(1, h.View(null).GetProperty("likes")[0].GetInt32());
    }

    [Fact]
    public void You_cannot_like_yourself_or_what_was_not_shown()
    {
        var h = Revealing();
        Assert.False(h.Act(0, "like", new { chain = 0, index = 0 }).Ok);
        Assert.False(h.Act(0, "like", new { chain = 0, index = 1 }).Ok);
        Assert.False(h.Act(0, "like", new { chain = 1, index = 0 }).Ok);
    }

    [Fact]
    public void The_most_liked_wins_when_the_show_ends()
    {
        var h = Revealing();
        Next(h);
        h.Act(0, "like", new { chain = 0, index = 1 });   // Петрів малюнок
        h.Act(2, "like", new { chain = 0, index = 1 });
        for (var i = 0; i < 8 && h.Room.Status == RoomStatus.Playing; i++) Next(h);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Contains("найбільше ❤ у Петро", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(3, h.Scores.Count);
    }

    [Fact]
    public void No_likes_at_all_is_a_draw()
    {
        var h = Revealing();
        for (var i = 0; i < 9 && h.Room.Status == RoomStatus.Playing; i++) Next(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
    }

    // ---------------------------------------------------------------- вихід

    [Fact]
    public void Someone_leaving_mid_step_does_not_hold_the_others()
    {
        var h = Table(4);
        Write(h, 0, "раз");
        Write(h, 1, "два");
        Write(h, 2, "три");
        h.Leave("Іван");
        h.Tick();

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, Step(h));
    }

    [Fact]
    public void A_chain_that_lost_its_author_starts_later_with_a_phrase()
    {
        var h = Table(4);
        h.Leave("Іван");      // ланцюжок Івана (3) так і не почався
        Write(h, 0, "раз");
        Write(h, 1, "два");
        Write(h, 2, "три");
        h.Tick();
        // на другому кроці ланцюжок 3 дістається Олі (0): там порожньо, тож пише фразу
        Assert.Equal(Telephone.Phrase, Kind(h, 0));
        Assert.Equal(Telephone.Draw, Kind(h, 1));
    }

    [Fact]
    public void Down_to_one_player_the_game_ends()
    {
        var h = Table(3);
        h.Leave("Петро");
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Rematch_starts_from_phrases_again()
    {
        var h = Revealing();
        for (var i = 0; i < 9 && h.Room.Status == RoomStatus.Playing; i++) Next(h);
        h.Rematch();
        Assert.Equal("step", Phase(h));
        Assert.Equal(1, Step(h));
        Assert.All(h.View(null).GetProperty("likes").EnumerateArray(), e => Assert.Equal(0, e.GetInt32()));
    }

    [Fact]
    public void The_real_phrase_list_is_there()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) dir = dir.Parent;
        var phrases = TelephonePhrases.Load(Path.Combine(dir!.FullName, TelephonePhrases.FileName));
        Assert.True(phrases.All.Count >= 150, $"фраз лише {phrases.All.Count}");
        Assert.All(phrases.All, p => Assert.InRange(p.Length, 5, Telephone.MaxText));
    }
}
