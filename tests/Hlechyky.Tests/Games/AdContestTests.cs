using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Конкурс реклами. Кімнати тут нема, тож і RoomHarness ні до чого: сервіс збирається на тимчасовій базі
/// разом зі справжньою економікою (EconomyRig), а ffmpeg, Дядько Глек і ефір підмінені заглушками —
/// перевіряти треба правила й виплати, а не те, як браузер пише webm.
/// </summary>
public class AdContestTests
{
    // =============================================================================================
    // Заглушки
    // =============================================================================================

    /// <summary>Голосове, яке «вже готове»: id видається по порядку, тривалість задає тест.</summary>
    sealed class FakeVoice : IVoiceSaver
    {
        int _n;
        public bool Enabled { get; set; } = true;
        public long MaxUploadBytes => 10 * 1024 * 1024;
        /// <summary>Скільки секунд «записав» браузер наступного разу.</summary>
        public int Seconds { get; set; } = 18;
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = new();
        /// <summary>Що встигає статись, поки ffmpeg жує запис (наприклад, тікер закриває конкурс).</summary>
        public Action? OnSave { get; set; }

        public Task<(TrackInfo Track, string FilePath)> SaveAsync(Stream body, string nick, CancellationToken ct)
        {
            OnSave?.Invoke();
            var id = "voice-" + (++_n).ToString("D6");
            Files.Add(id);
            return Task.FromResult((new TrackInfo(id, "Голосове", nick, Seconds, null, $"/api/voice/{id}.mp3", null), id + ".mp3"));
        }

        public string? FilePath(string id) => Files.Contains(id) ? id + ".mp3" : null;

        public void Delete(string id)
        {
            Deleted.Add(id);
            Files.Remove(id);
        }
    }

    /// <summary>Дядько Глек, який або мовчить (null), або каже рівно те, що поклав тест.</summary>
    sealed class FakeWriter : IAdScriptWriter
    {
        public string? Text { get; set; }
        public int Calls { get; private set; }
        /// <summary>Затримка «поки модель думає»: тест сам вирішує, коли сценарій буде готовий.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<string?> WriteAsync(string instruction, int maxChars, CancellationToken ct)
        {
            Calls++;
            if (Gate is not null) await Gate.Task;
            return Text;
        }
    }

    /// <summary>Ефір, який лише запам'ятовує, що йому дали.</summary>
    sealed class FakeAir : IAdAir
    {
        public List<TrackInfo> Played { get; } = new();
        /// <summary>Ефір відмовив («уже в черзі») — джингл має спробувати ще раз пізніше.</summary>
        public bool Refuse { get; set; }

        public (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick)
        {
            if (Refuse) return (false, "Уже в черзі");
            Played.Add(track);
            return (true, "Голосове в черзі");
        }
    }

    /// <summary>Що «зараз грає» для нагороди за рекламу.</summary>
    public sealed class FakeOnAir : IAdOnAir
    {
        public string? TrackId { get; set; }
        public bool SkipPending { get; set; }
        public (string? TrackId, bool SkipPending) Now() => (TrackId, SkipPending);
    }

    sealed class AdRig : IDisposable
    {
        public EconomyRig Eco { get; } = new();
        public FakeVoice Voice { get; } = new();
        public FakeWriter Writer { get; } = new();
        public FakeAir Air { get; } = new();
        public AdOptions Options { get; } = new();
        public AdContestStore Store { get; }
        public AdContest Ads { get; }
        public AdJingle Jingle { get; }
        public AdLibrary Library { get; }
        public AdListenRewards Rewards { get; }
        public FakeOnAir OnAir { get; } = new();

        public FakeClock Clock => Eco.Clock;
        public Presence Presence => Eco.Presence;

        public AdRig()
        {
            Store = new AdContestStore(Eco.Db);
            Ads = Cold();
            var opts = new FixedOptions<AdOptions>(Options);
            Library = new AdLibrary(new AdLibraryStore(Eco.Db), Voice, Store, Clock, NullLogger<AdLibrary>.Instance);
            Rewards = new AdListenRewards(Eco.Economy, Presence, OnAir, Clock, opts, NullLogger<AdListenRewards>.Instance)
            {
                Delay = _ => new TaskCompletionSource().Task,     // перевірку під кінець реклами тести кличуть самі
            };
            Jingle = new AdJingle(Ads, Library, Rewards, Air, Voice, Presence, Clock, opts, NullLogger<AdJingle>.Instance);
            Presence.Set("c1", "Оля");     // типово хтось на сайті є; тест, якому треба порожньо, чистить сам
        }

        /// <summary>Той самий конкурс на тій самій базі, але без нічого в пам'яті — як після перезапуску сервера.</summary>
        public AdContest Cold() => new(Store, Clock, Eco.Events, Eco.Outbox, Writer, Voice,
            new FixedOptions<AdOptions>(Options), NullLogger<AdContest>.Instance);

        /// <summary>Відкрити конкурс і віддати його id.</summary>
        public long Open()
        {
            var (ok, message) = Ads.OpenAsync().GetAwaiter().GetResult();
            Assert.True(ok, message);
            return Store.Active()!.Id;
        }

        /// <summary>Записати рекламу від імені ніка.</summary>
        public (bool Ok, string Message) Enter(long id, string nick, int seconds = 18)
        {
            Voice.Seconds = seconds;
            return Ads.EnterAsync(id, nick, new MemoryStream(Encoding.UTF8.GetBytes("webm")), default).GetAwaiter().GetResult();
        }

        public long EntryOf(long contest, string nick) => Store.Entry(contest, EconomyStore.Key(nick))!.Id;

        /// <summary>Трек, який заграв в ефірі, — джингл рахує саме такі події.</summary>
        public void TrackOnAir(string id = "yt-1") =>
            Jingle.OnTrackStarted(new TrackInfo(id, "Пісня", "Хтось", 180, null, "https://x", null));

        public JsonElement Snapshot(string nick) => Views.Json(Ads.Snapshot(nick));

        public void Dispose() => Eco.Dispose();
    }

    /// <summary>Понеділок, 12:30 за Києвом (у вересні Київ — UTC+3).</summary>
    static readonly DateTimeOffset MondayNoon = new(2026, 9, 14, 9, 30, 0, TimeSpan.Zero);

    // =============================================================================================
    // Сценарій
    // =============================================================================================

    [Fact]
    public void A_silent_dj_leaves_the_contest_with_a_built_in_script()
    {
        using var r = new AdRig();
        r.Writer.Text = null;

        r.Open();

        Assert.Equal(1, r.Writer.Calls);
        Assert.Contains(r.Store.Active()!.Script, AdContest.Templates);
    }

    [Fact]
    public void The_built_in_script_is_the_same_for_the_same_day_and_changes_with_the_day()
    {
        using var a = new AdRig();
        using var b = new AdRig();
        using var c = new AdRig();
        c.Clock.Advance(TimeSpan.FromDays(1));

        a.Open();
        b.Open();
        c.Open();

        Assert.Equal(a.Store.Active()!.Script, b.Store.Active()!.Script);
        Assert.NotEqual(a.Store.Active()!.Script, c.Store.Active()!.Script);
    }

    [Fact]
    public void A_script_written_by_the_dj_is_taken_as_it_is()
    {
        using var r = new AdRig();
        r.Writer.Text = "  Глек гуде, вода співає, а ти стоїш і слухаєш, як минає літо.  ";

        r.Open();

        Assert.Equal("Глек гуде, вода співає, а ти стоїш і слухаєш, як минає літо.", r.Store.Active()!.Script);
    }

    [Fact]
    public async Task A_second_contest_cannot_start_while_the_first_one_is_running()
    {
        using var r = new AdRig();
        r.Open();

        var (ok, message) = await r.Ads.OpenAsync();

        Assert.False(ok);
        Assert.Equal("Конкурс уже триває — спершу закрий той", message);
    }

    // =============================================================================================
    // Участь
    // =============================================================================================

    [Fact]
    public void A_new_recording_replaces_the_old_one_and_the_old_file_goes_away()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        var first = r.Store.Entry(id, "оля")!.TrackId;

        var (ok, message) = r.Enter(id, "Оля");

        Assert.True(ok);
        Assert.Equal("Перезаписав — стара версія пішла в небуття разом із голосами за неї", message);
        Assert.Single(r.Store.Entries(id));
        Assert.Equal([first], r.Voice.Deleted);
        Assert.NotEqual(first, r.Store.Entries(id)[0].TrackId);
    }

    [Fact]
    public void A_recording_longer_than_the_limit_is_refused_and_its_file_is_deleted()
    {
        using var r = new AdRig();
        var id = r.Open();

        var (ok, message) = r.Enter(id, "Оля", seconds: 41);

        Assert.False(ok);
        Assert.Equal("Задовга реклама: 41 с, а треба до 30", message);
        Assert.Empty(r.Store.Entries(id));
        Assert.Single(r.Voice.Deleted);
    }

    [Fact]
    public void A_recording_a_little_over_the_limit_still_gets_in()
    {
        using var r = new AdRig();
        var id = r.Open();

        // браузер спиняє мікрофон не миттєво, тож 33 с — це ще «тридцять», а не порушення
        Assert.True(r.Enter(id, "Оля", seconds: 33).Ok);
        Assert.Single(r.Store.Entries(id));
    }

    [Fact]
    public void Taking_your_recording_back_removes_it_with_its_votes_and_its_file()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        var olya = r.EntryOf(id, "Оля");
        r.Ads.Vote(id, "Петро", olya);
        var track = r.Store.Entry(id, "оля")!.TrackId;

        var (ok, _) = r.Ads.DropEntry(id, "Оля");

        Assert.True(ok);
        Assert.Single(r.Store.Entries(id));
        Assert.Equal(0, r.Store.Entries(id)[0].Votes);
        Assert.Contains(track, r.Voice.Deleted);
        Assert.Equal("Ти ще нічого не записував", r.Ads.DropEntry(id, "Оля").Message);
    }

    [Fact]
    public void A_guest_without_a_nick_neither_records_nor_votes()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        var olya = r.EntryOf(id, "Оля");

        Assert.Equal("Спершу скажи, як тебе кликати", r.Enter(id, "гість").Message);
        Assert.Equal("Спершу скажи, як тебе кликати", r.Ads.Vote(id, "гість", olya).Message);
        Assert.Single(r.Store.Entries(id));
    }

    // =============================================================================================
    // Голоси
    // =============================================================================================

    [Fact]
    public void One_vote_per_nick_and_changing_it_moves_the_vote_instead_of_adding_one()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Enter(id, "Маруся");
        var olya = r.EntryOf(id, "Оля");
        var petro = r.EntryOf(id, "Петро");

        Assert.True(r.Ads.Vote(id, "Маруся", olya).Ok);
        Assert.True(r.Ads.Vote(id, "Маруся", petro).Ok);

        var entries = r.Store.Entries(id);
        Assert.Equal(0, entries.First(e => e.Id == olya).Votes);
        Assert.Equal(1, entries.First(e => e.Id == petro).Votes);
        Assert.Equal(petro, r.Store.VoteOf(id, "маруся"));
    }

    [Fact]
    public void You_cannot_vote_for_yourself()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");

        var (ok, message) = r.Ads.Vote(id, "оля", r.EntryOf(id, "Оля"));

        Assert.False(ok);
        Assert.Equal("За себе голосувати не можна", message);
        Assert.Equal(0, r.Store.Entries(id)[0].Votes);
    }

    [Fact]
    public void Voting_in_a_closed_contest_and_for_a_stranger_entry_is_refused()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        var olya = r.EntryOf(id, "Оля");

        Assert.Equal("Такого запису нема", r.Ads.Vote(id, "Петро", olya + 999).Message);
        r.Ads.Vote(id, "Петро", olya);
        r.Ads.Close(id);
        Assert.Equal("Цей конкурс уже закрито", r.Ads.Vote(id, "Маруся", olya).Message);
        Assert.Equal("Цей конкурс уже закрито", r.Enter(id, "Маруся").Message);
    }

    // =============================================================================================
    // Закриття і виплати
    // =============================================================================================

    [Fact]
    public void The_entry_with_the_most_votes_wins()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Маруся", r.EntryOf(id, "Петро"));
        r.Ads.Vote(id, "Іван", r.EntryOf(id, "Петро"));
        r.Ads.Vote(id, "Ганна", r.EntryOf(id, "Оля"));

        var (ok, message) = r.Ads.Close(id);

        Assert.True(ok);
        Assert.Equal("Переміг Петро", message);
        Assert.Equal("петро", r.Store.Get(id)!.WinnerNickKey);
        Assert.Contains(r.Eco.Outbox.Of<Journal>(), j => j.Text.Contains("переміг Петро — 2 голоси"));
    }

    [Fact]
    public void On_a_tie_the_earlier_recording_wins()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Маруся", r.EntryOf(id, "Оля"));
        r.Ads.Vote(id, "Іван", r.EntryOf(id, "Петро"));

        r.Ads.Close(id);

        Assert.Equal("оля", r.Store.Get(id)!.WinnerNickKey);
    }

    [Fact]
    public void Nobody_voted_means_nobody_won_but_the_recordings_are_still_paid()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");

        var (ok, _) = r.Ads.Close(id);

        Assert.True(ok);
        Assert.Null(r.Store.Get(id)!.WinnerNickKey);
        Assert.Null(r.Ads.Winner());
        Assert.Equal(3, r.Eco.Paid("Оля", "ad:entry"));
        Assert.Contains(r.Eco.Outbox.Of<Journal>(), j => j.Text.Contains("ніхто не проголосував"));
    }

    [Fact]
    public void Closing_pays_the_winner_the_entrants_and_the_voters()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Маруся", r.EntryOf(id, "Петро"));

        r.Ads.Close(id);

        Assert.Equal(25, r.Eco.Paid("Петро", "ad:winner"));
        Assert.Equal(3, r.Eco.Paid("Петро", "ad:entry"));
        Assert.Equal(3, r.Eco.Paid("Оля", "ad:entry"));
        Assert.Equal(1, r.Eco.Paid("Маруся", "ad:vote"));
        Assert.Equal(0, r.Eco.Paid("Оля", "ad:winner"));
        // ачівку «Голос села» платформа вішає сама, побачивши причину ad:winner
        Assert.Contains(r.Eco.Outbox.Of<AchievementUnlocked>(), a => a.Key == "ad-winner" && a.Nick == "Петро");
    }

    [Fact]
    public void Payouts_carry_the_refs_the_economy_expects_so_a_second_close_pays_nothing()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));
        r.Ads.Close(id);

        // Ref будує сама економіка з причини ad:*; повтор із тим самим ключем — Duplicate, а не друга виплата.
        Assert.Equal(GrantResult.Duplicate, r.Eco.Economy.Grant("Оля", 1, "ad:winner", $"ad:{id}:winner:оля"));
        Assert.Equal(GrantResult.Duplicate, r.Eco.Economy.Grant("Оля", 1, "ad:entry", $"ad:{id}:entry:оля"));
        Assert.Equal(GrantResult.Duplicate, r.Eco.Economy.Grant("Петро", 1, "ad:vote", $"ad:{id}:vote:петро"));

        var (ok, message) = r.Ads.Close(id);

        Assert.False(ok);
        Assert.Equal("Конкурс уже закрито", message);
        Assert.Equal(25, r.Eco.Paid("Оля", "ad:winner"));
        Assert.Equal(1, r.Eco.Paid("Петро", "ad:vote"));
    }

    // =============================================================================================
    // Тікер: понеділок і дотерміновані конкурси
    // =============================================================================================

    [Fact]
    public async Task A_contest_opens_by_itself_on_monday_noon_and_not_twice_the_same_day()
    {
        using var r = new AdRig();
        r.Clock.UtcNow = MondayNoon.AddHours(-1);            // понеділок, 11:30 за Києвом — ще рано

        await r.Ads.TickAsync();
        Assert.Null(r.Store.Active());

        r.Clock.UtcNow = MondayNoon;
        await r.Ads.TickAsync();
        var id = r.Store.Active()!.Id;

        // адмін закрив його по обіді — другий за той самий день тікер не відкриває
        r.Ads.Close(id);
        r.Clock.Advance(TimeSpan.FromHours(3));
        await r.Ads.TickAsync();
        Assert.Null(r.Store.Active());

        // а наступного понеділка — знову
        r.Clock.UtcNow = MondayNoon.AddDays(7);
        await r.Ads.TickAsync();
        Assert.NotNull(r.Store.Active());
        Assert.NotEqual(id, r.Store.Active()!.Id);
    }

    [Fact]
    public async Task On_any_other_day_the_ticker_opens_nothing()
    {
        using var r = new AdRig();
        r.Clock.UtcNow = MondayNoon.AddDays(1);

        await r.Ads.TickAsync();

        Assert.Null(r.Store.Active());
    }

    [Fact]
    public async Task When_the_time_is_up_the_ticker_closes_the_contest_itself()
    {
        using var r = new AdRig();
        r.Clock.UtcNow = MondayNoon;
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));

        r.Clock.Advance(TimeSpan.FromDays(2));
        await r.Ads.TickAsync();
        Assert.False(r.Store.Get(id)!.Closed);

        r.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await r.Ads.TickAsync();

        Assert.True(r.Store.Get(id)!.Closed);
        Assert.Equal(25, r.Eco.Paid("Оля", "ad:winner"));
    }

    // =============================================================================================
    // Ефір
    // =============================================================================================

    /// <summary>Конкурс із переможцем «Оля»: далі перевіряємо тільки те, коли її реклама йде в ефір.</summary>
    static long WinnerReady(AdRig r)
    {
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));
        r.Ads.Close(id);
        return id;
    }

    [Fact]
    public void The_winning_ad_goes_on_air_after_six_tracks_and_not_before()
    {
        using var r = new AdRig();
        WinnerReady(r);

        for (var i = 0; i < 5; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.TrackOnAir();

        var ad = Assert.Single(r.Air.Played);
        Assert.Equal("Реклама глека", ad.Title);
        Assert.Equal("Оля", ad.Artist);
    }

    [Fact]
    public void After_the_ad_it_waits_both_six_tracks_and_twenty_five_minutes()
    {
        using var r = new AdRig();
        WinnerReady(r);
        for (var i = 0; i < 6; i++) r.TrackOnAir();
        var ad = r.Air.Played[0];
        r.Jingle.OnTrackStarted(ad);                       // реклама заграла — відлік почався звідси

        for (var i = 0; i < 6; i++) r.TrackOnAir();        // треків уже досить…
        Assert.Single(r.Air.Played);                       // …а хвилин ще ні

        r.Clock.Advance(TimeSpan.FromMinutes(26));
        r.TrackOnAir();

        Assert.Equal(2, r.Air.Played.Count);
    }

    [Fact]
    public void The_ad_itself_does_not_count_as_one_of_the_six_tracks()
    {
        using var r = new AdRig();
        WinnerReady(r);
        var winner = r.Ads.Winner()!;

        for (var i = 0; i < 3; i++) r.TrackOnAir();
        r.Jingle.OnTrackStarted(new TrackInfo(winner.TrackId, "Реклама глека", "Оля", 18, null, "/x", null));
        for (var i = 0; i < 3; i++) r.TrackOnAir();

        Assert.Equal(3, r.Jingle.Since);
        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void With_nobody_on_the_site_the_ad_waits_for_the_first_listener()
    {
        using var r = new AdRig();
        WinnerReady(r);
        r.Presence.Remove("c1");

        for (var i = 0; i < 10; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.Presence.Set("c2", "Петро");
        r.TrackOnAir();

        Assert.Single(r.Air.Played);
    }

    [Fact]
    public void Ad_Jingle_false_leaves_the_air_alone()
    {
        using var r = new AdRig();
        r.Options.Jingle = false;
        WinnerReady(r);

        for (var i = 0; i < 20; i++) r.TrackOnAir();

        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void A_winner_whose_file_vanished_from_the_cache_is_simply_not_played()
    {
        using var r = new AdRig();
        WinnerReady(r);
        r.Voice.Files.Clear();                             // кеш почистили — mp3 більше нема

        for (var i = 0; i < 10; i++) r.TrackOnAir();

        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void A_refused_queue_is_retried_on_the_next_track()
    {
        using var r = new AdRig();
        WinnerReady(r);
        r.Air.Refuse = true;

        for (var i = 0; i < 6; i++) r.TrackOnAir();
        Assert.Empty(r.Air.Played);

        r.Air.Refuse = false;
        r.TrackOnAir();

        Assert.Single(r.Air.Played);
    }

    // =============================================================================================
    // Ефір господаря
    // =============================================================================================

    (bool Ok, string Message) HouseRecord(AdRig r, string nick = "Адмін", int seconds = 12)
    {
        r.Voice.Seconds = seconds;
        return r.Ads.AirRecordAsync(nick, new MemoryStream(Encoding.UTF8.GetBytes("webm")), default).GetAwaiter().GetResult();
    }

    [Fact]
    public void An_entry_from_a_running_contest_goes_on_air_without_waiting_for_the_close()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Владік", seconds: 7);

        var (ok, _) = r.Ads.AirEntry(r.EntryOf(id, "Владік"));
        Assert.True(ok);
        for (var i = 0; i < 6; i++) r.TrackOnAir();

        var ad = Assert.Single(r.Air.Played);
        Assert.Equal("Владік", ad.Artist);
        Assert.Equal(7, ad.DurationSec);
    }

    [Fact]
    public void The_house_ad_beats_the_contest_winner_and_clearing_it_brings_the_winner_back()
    {
        using var r = new AdRig();
        WinnerReady(r);
        Assert.True(HouseRecord(r).Ok);

        Assert.True(r.Ads.OnAir()!.House);
        Assert.Equal("Адмін", r.Ads.OnAir()!.Nick);

        Assert.True(r.Ads.AirClear().Ok);

        Assert.False(r.Ads.OnAir()!.House);
        Assert.Equal("Оля", r.Ads.OnAir()!.Nick);
    }

    [Fact]
    public void Frequency_from_the_admin_overrides_the_config_and_survives_a_restart()
    {
        using var r = new AdRig();
        HouseRecord(r);

        Assert.True(r.Ads.AirEvery(3, 0).Ok);
        Assert.Equal((3, 0), r.Cold().Frequency());

        for (var i = 0; i < 3; i++) r.TrackOnAir();
        Assert.Single(r.Air.Played);
        r.Jingle.OnTrackStarted(r.Air.Played[0]);
        for (var i = 0; i < 3; i++) r.TrackOnAir();       // жодних 25 хвилин: господар поставив нуль
        Assert.Equal(2, r.Air.Played.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(101, 0)]
    [InlineData(3, -1)]
    [InlineData(3, 601)]
    public void Nonsense_frequency_is_refused(int every, int minutes)
    {
        using var r = new AdRig();
        Assert.False(r.Ads.AirEvery(every, minutes).Ok);
        Assert.Equal((6, 25), r.Ads.Frequency());
    }

    [Fact]
    public void Play_now_queues_the_ad_at_once_and_restarts_the_count()
    {
        using var r = new AdRig();
        HouseRecord(r);
        r.TrackOnAir();

        Assert.True(r.Jingle.PlayNow().Ok);

        Assert.Single(r.Air.Played);
        Assert.Equal(0, r.Jingle.Since);
    }

    [Fact]
    public void Play_now_with_nothing_to_play_says_so()
    {
        using var r = new AdRig();
        Assert.False(r.Jingle.PlayNow().Ok);
    }

    [Fact]
    public void Re_recording_an_entry_that_is_on_air_keeps_the_aired_file()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Владік");
        var aired = r.Store.Entry(id, EconomyStore.Key("Владік"))!.TrackId;
        r.Ads.AirEntry(r.EntryOf(id, "Владік"));

        r.Enter(id, "Владік");

        Assert.DoesNotContain(aired, r.Voice.Deleted);
        Assert.Equal(aired, r.Ads.OnAir()!.TrackId);
    }

    [Fact]
    public void Replacing_the_house_recording_deletes_the_old_one_but_never_a_contest_entry()
    {
        using var r = new AdRig();
        HouseRecord(r);
        var own = r.Ads.OnAir()!.TrackId;
        var id = r.Open();
        r.Enter(id, "Владік");
        var entry = r.Store.Entry(id, EconomyStore.Key("Владік"))!.TrackId;

        r.Ads.AirEntry(r.EntryOf(id, "Владік"));
        Assert.Contains(own, r.Voice.Deleted);

        HouseRecord(r);
        Assert.DoesNotContain(entry, r.Voice.Deleted);
    }

    [Fact]
    public void When_the_ad_changes_the_old_one_still_playing_does_not_count_as_a_track()
    {
        using var r = new AdRig();
        WinnerReady(r);
        HouseRecord(r);

        r.TrackOnAir();
        r.TrackOnAir();
        r.Jingle.OnTrackStarted(new TrackInfo("voice-000001", "Реклама глека", "Оля", 18, null, "/x", null));

        Assert.Equal(0, r.Jingle.Since);
    }

    // =============================================================================================
    // Що бачить браузер
    // =============================================================================================

    [Fact]
    public void The_snapshot_has_the_shape_the_panel_draws()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Enter(id, "Петро");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));

        var v = r.Snapshot("Петро");
        var active = v.GetProperty("active");
        var entries = active.GetProperty("entries").EnumerateArray().ToList();
        var mine = entries.Single(e => e.GetProperty("mine").GetBoolean());

        Assert.Equal(id, active.GetProperty("id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(active.GetProperty("script").GetString()));
        Assert.Equal(30, active.GetProperty("maxSeconds").GetInt32());
        Assert.Equal(mine.GetProperty("id").GetInt64(), active.GetProperty("myEntry").GetInt64());
        Assert.Equal("Петро", mine.GetProperty("nick").GetString());
        Assert.Equal(18, mine.GetProperty("seconds").GetInt32());
        Assert.Equal(1, entries.Single(e => e.GetProperty("nick").GetString() == "Оля").GetProperty("votes").GetInt32());
        Assert.Equal(entries.Single(e => e.GetProperty("nick").GetString() == "Оля").GetProperty("id").GetInt64(),
            active.GetProperty("myVote").GetInt64());
        Assert.Empty(v.GetProperty("past").EnumerateArray());
    }

    [Fact]
    public void A_closed_contest_leaves_no_active_one_but_shows_up_among_the_past_winners()
    {
        using var r = new AdRig();
        var id = WinnerReady(r);

        var v = r.Snapshot("Петро");
        var past = Assert.Single(v.GetProperty("past").EnumerateArray().ToList());

        Assert.Equal(JsonValueKind.Null, v.GetProperty("active").ValueKind);
        Assert.Equal(id, past.GetProperty("id").GetInt64());
        Assert.Equal("Оля", past.GetProperty("winner").GetString());
        Assert.Equal(1, past.GetProperty("votes").GetInt32());
        Assert.StartsWith("voice-", past.GetProperty("trackId").GetString());
    }

    [Fact]
    public async Task A_disabled_contest_neither_opens_nor_ticks()
    {
        using var r = new AdRig();
        r.Options.Enabled = false;
        r.Clock.UtcNow = MondayNoon;

        var (ok, message) = await r.Ads.OpenAsync();
        await r.Ads.TickAsync();

        Assert.False(ok);
        Assert.Equal("Конкурс реклами вимкнено", message);
        Assert.Null(r.Store.Active());
    }

    // =============================================================================================
    // Плитка, якою модуль потрапляє в браузер
    // =============================================================================================

    [Fact]
    public void The_contest_tile_sits_in_the_catalog_with_the_module_that_draws_it()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "ad-contest");

        Assert.Equal("party", game.Group);
        Assert.Equal("immediate", game.Start);
        Assert.True(game.Private);
        Assert.False(game.Rated);
        Assert.Equal(1, game.MinPlayers);
        Assert.Equal(1, game.MaxPlayers);
        Assert.Equal(0, game.TickMs);          // не реалтайм: кадрів у конкурсу нема
        Assert.NotEmpty(game.Hint);
        // саме заради цього рядка плитка й існує: браузер вантажить модулі рівно з каталогу
        Assert.Equal("ad-contest", game.Module);
        Assert.True(game.HasCss);
        Assert.True(File.Exists(Paths.Resolve($"web/games/{game.Module}.js")));
    }

    [Fact]
    public void The_contest_tile_has_no_state_and_no_view_of_its_own()
    {
        var tile = new AdContestTile();

        tile.Start();

        Assert.Equal("{}", Views.Text(tile.View(0)));
        Assert.Equal("{}", Views.Text(tile.View(null)));
    }

    // =============================================================================================
    // Межі конкурсу: дзвінок, вимикач, гонки
    // =============================================================================================

    [Fact]
    public async Task Two_contests_cannot_open_at_once_even_while_the_dj_is_still_writing()
    {
        using var r = new AdRig();
        var gate = new TaskCompletionSource();
        r.Writer.Gate = gate;

        // Обидва виклики встигли побачити «активного нема» до того, як хтось написав сценарій.
        var first = r.Ads.OpenAsync();
        var second = r.Ads.OpenAsync();
        gate.SetResult();
        var done = await Task.WhenAll(first, second);

        Assert.Single(done, x => x.Ok);
        Assert.Contains(done, x => !x.Ok && x.Message == "Конкурс уже триває — спершу закрий той");
        // другого відкритого рядка не лишилось: закрили той, що видно, — і активних більше нема
        r.Ads.Close(r.Store.Active()!.Id);
        Assert.Null(r.Store.Active());
    }

    [Fact]
    public void After_the_bell_nothing_is_accepted_even_before_the_ticker_wakes_up()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        var olya = r.EntryOf(id, "Оля");

        r.Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        Assert.False(r.Store.Get(id)!.Closed);         // хвилинний тікер ще спить

        Assert.Equal("Конкурс уже скінчився", r.Enter(id, "Петро").Message);
        Assert.Equal("Конкурс уже скінчився", r.Ads.Vote(id, "Петро", olya).Message);
        Assert.Equal("Конкурс уже скінчився", r.Ads.DropEntry(id, "Оля").Message);
        Assert.Single(r.Store.Entries(id));
        Assert.Equal(0, r.Store.Entries(id)[0].Votes);
    }

    [Fact]
    public async Task Turning_the_contest_off_shuts_the_doors_but_still_finishes_the_running_one()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));
        r.Options.Enabled = false;

        Assert.Equal("Конкурс реклами вимкнено", r.Enter(id, "Маруся").Message);
        Assert.Equal("Конкурс реклами вимкнено", r.Ads.Vote(id, "Іван", r.EntryOf(id, "Оля")).Message);
        Assert.Equal("Конкурс реклами вимкнено", r.Ads.DropEntry(id, "Оля").Message);

        r.Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await r.Ads.TickAsync();

        // вимикач не має лишати людей без черепків за те, що вони вже зробили
        Assert.True(r.Store.Get(id)!.Closed);
        Assert.Equal(25, r.Eco.Paid("Оля", "ad:winner"));
        Assert.Equal(1, r.Eco.Paid("Петро", "ad:vote"));
    }

    [Fact]
    public void A_recording_that_came_back_from_ffmpeg_after_the_close_is_thrown_away()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Voice.OnSave = () => r.Ads.Close(id);        // поки конвеєр жував запис, конкурс закрився

        var (ok, message) = r.Enter(id, "Оля");

        Assert.False(ok);
        Assert.Equal("Не встиг: конкурс щойно закрився", message);
        Assert.Empty(r.Store.Entries(id));
        Assert.Single(r.Voice.Deleted);                // mp3 не лишився в кеші сиротою
        Assert.Equal(0, r.Eco.Paid("Оля", "ad:entry"));
    }

    [Fact]
    public void Re_recording_takes_the_votes_of_the_old_take_with_it()
    {
        using var r = new AdRig();
        var id = r.Open();
        r.Enter(id, "Оля");
        r.Ads.Vote(id, "Петро", r.EntryOf(id, "Оля"));
        Assert.Equal(1, r.Store.Entries(id)[0].Votes);

        r.Enter(id, "Оля");                            // зібрав голоси — і підмінив запис

        Assert.Equal(0, r.Store.Entries(id)[0].Votes);
        Assert.Null(r.Store.VoteOf(id, "петро"));
    }

    [Fact]
    public void The_template_index_survives_a_seed_at_the_very_edge_of_int()
    {
        // Days.Seed віддає майже int.MaxValue; у int сума з номером конкурсу перевернулась би в мінус,
        // і конкурс не відкрився б узагалі — ні руками, ні тікером.
        Assert.Contains(AdContest.Template(int.MaxValue, 7), AdContest.Templates);
        Assert.Contains(AdContest.Template(int.MaxValue - 1, 1_000_000), AdContest.Templates);
        Assert.Equal(AdContest.Templates[5], AdContest.Template(5, 0));
    }

    [Fact]
    public void A_contest_nobody_voted_in_does_not_take_the_old_ad_off_the_air()
    {
        using var r = new AdRig();
        WinnerReady(r);                                // переможець — Оля
        var second = r.Open();
        r.Enter(second, "Петро");
        r.Ads.Close(second);                           // а тут не проголосував ніхто

        var kept = r.Ads.Winner();

        Assert.Equal("Оля", kept!.Nick);
        // і після перезапуску сервера відповідь та сама: інакше реклама зникала б і поверталась сама собою
        Assert.Equal(kept.TrackId, r.Cold().Winner()!.TrackId);
    }
    // =============================================================================================
    // Бібліотека реклам, ротація і черепки за прослуховування
    // =============================================================================================

    static (bool Ok, string Message) AddClip(AdRig r, string title, int seconds = 20)
    {
        r.Voice.Seconds = seconds;
        return r.Library.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("wav")), title, "владік", default).GetAwaiter().GetResult();
    }

    [Fact]
    public void Rotation_plays_every_clip_once_before_any_repeats_and_never_twice_in_a_row()
    {
        using var r = new AdRig();
        foreach (var t in new[] { "А", "Б", "В", "Г", "Ґ" }) Assert.True(AddClip(r, t).Ok);

        var played = new List<string>();
        for (var i = 0; i < 50; i++) played.Add(r.Library.Take()!.Title);

        for (var deck = 0; deck < 10; deck++)
            Assert.Equal(5, played.Skip(deck * 5).Take(5).Distinct().Count());
        for (var i = 1; i < played.Count; i++) Assert.NotEqual(played[i - 1], played[i]);
    }

    [Fact]
    public void Rotation_skips_switched_off_clips_and_clips_whose_file_is_gone()
    {
        using var r = new AdRig();
        AddClip(r, "Живий");
        AddClip(r, "Вимкнений");
        AddClip(r, "Без файлу");
        var all = r.Library.All();
        r.Library.SetEnabled(all[1].Id, false);
        r.Voice.Files.Remove(all[2].TrackId);

        for (var i = 0; i < 10; i++) Assert.Equal("Живий", r.Library.Take()!.Title);

        r.Library.SetAll(false);
        Assert.Null(r.Library.Take());
        Assert.False(r.Library.HasLive());
    }

    [Fact]
    public void The_jingle_prefers_the_library_over_the_house_ad_and_falls_back_when_rotation_is_empty()
    {
        using var r = new AdRig();
        r.Options.EveryTracks = 1;
        r.Options.MinMinutes = 0;
        Assert.True(r.Ads.AirRecordAsync("владік", new MemoryStream(Encoding.UTF8.GetBytes("webm")), default).GetAwaiter().GetResult().Ok);
        AddClip(r, "Глекминатор", 22);

        r.TrackOnAir("yt-1");
        var queued = Assert.Single(r.Air.Played);
        Assert.Equal("Глекминатор", queued.Artist);
        Assert.Equal(AdJingle.AdTitle, queued.Title);

        r.Library.SetAll(false);
        r.TrackOnAir("yt-2");
        Assert.Equal(2, r.Air.Played.Count);
        Assert.Equal("владік", r.Air.Played[1].Artist);           // реклама господаря підхопила
    }

    [Fact]
    public void A_clip_playing_counts_as_a_play_and_resets_the_track_counter()
    {
        using var r = new AdRig();
        r.Options.EveryTracks = 3;
        r.Options.MinMinutes = 0;
        AddClip(r, "Прожарка");
        var clip = r.Library.All()[0];

        r.TrackOnAir("yt-1");
        r.TrackOnAir("yt-2");
        r.Jingle.OnTrackStarted(new TrackInfo(clip.TrackId, AdJingle.AdTitle, clip.Title, 20, null, "x", null));

        Assert.Equal(0, r.Jingle.Since);
        Assert.Equal(1, r.Library.All()[0].Plays);
        Assert.Empty(r.Air.Played);
    }

    [Fact]
    public void Listeners_who_heard_the_ad_to_the_end_get_shards_up_to_the_daily_cap()
    {
        using var r = new AdRig();
        r.Options.ListenReward = 2;
        r.Options.ListenDailyCap = 4;
        AddClip(r, "Каламбур");
        var clip = r.Library.All()[0];
        var track = new TrackInfo(clip.TrackId, AdJingle.AdTitle, clip.Title, 20, null, "x", null);
        r.Presence.Set("c2", "Петро");
        r.Presence.SetListening("c2", true);
        r.OnAir.TrackId = clip.TrackId;

        for (var i = 0; i < 3; i++)
        {
            var p = r.Rewards.Start(track)!;
            r.Clock.Advance(TimeSpan.FromSeconds(30));
            r.Rewards.Settle(p);
        }

        Assert.Equal(4, r.Eco.Paid("Петро", "ad:listen"));       // стеля 4 = дві реклами по 2
        Assert.Equal(0, r.Eco.Paid("Оля", "ad:listen"));          // Оля на сайті, але плеєр вимкнений
    }

    [Fact]
    public void No_shards_for_an_ad_that_was_skipped_or_for_someone_who_switched_the_player_off()
    {
        using var r = new AdRig();
        AddClip(r, "Каламбур");
        var clip = r.Library.All()[0];
        var track = new TrackInfo(clip.TrackId, AdJingle.AdTitle, clip.Title, 20, null, "x", null);
        r.Presence.Set("c2", "Петро");
        r.Presence.SetListening("c2", true);
        r.Presence.Set("c3", "Марта");
        r.Presence.SetListening("c3", true);

        r.OnAir.TrackId = clip.TrackId;
        r.OnAir.SkipPending = true;
        Assert.Equal(0, r.Rewards.Settle(r.Rewards.Start(track)!));

        r.OnAir.SkipPending = false;
        var p = r.Rewards.Start(track)!;
        r.Presence.SetListening("c3", false);
        Assert.Equal(1, r.Rewards.Settle(p));
        Assert.Equal(0, r.Eco.Paid("Марта", "ad:listen"));
        Assert.Equal(r.Options.ListenReward, r.Eco.Paid("Петро", "ad:listen"));

        // реклама вже скінчилась і грає пісня — запізніла перевірка нічого не платить
        var late = r.Rewards.Start(track)!;
        r.OnAir.TrackId = "yt-9";
        Assert.Equal(0, r.Rewards.Settle(late));
    }

    [Fact]
    public void Deleting_a_clip_removes_its_file_rename_validates_the_title()
    {
        using var r = new AdRig();
        AddClip(r, "Стара назва");
        var clip = r.Library.All()[0];

        Assert.False(r.Library.Rename(clip.Id, "   ").Ok);
        Assert.True(r.Library.Rename(clip.Id, "Нова назва").Ok);
        Assert.Equal("Нова назва", r.Library.Get(clip.Id)!.Title);

        Assert.True(r.Library.Delete(clip.Id).Ok);
        Assert.Contains(clip.TrackId, r.Voice.Deleted);
        Assert.Empty(r.Library.All());
        Assert.False(r.Library.IsAd(clip.TrackId));
    }
}
