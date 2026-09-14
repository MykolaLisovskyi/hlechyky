using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>Бан-лист: адмін безкоштовно, решта — трек в ефірі за черепки, викуп із бану теж за черепки.</summary>
public class TrackBansTests
{
    sealed class FakeAir : IOnAir
    {
        public string? TrackId { get; set; }
        public List<string> Evicted { get; } = new();
        public List<string> Lines { get; } = new();
        public Task EvictAsync(string trackId) { Evicted.Add(trackId); return Task.CompletedTask; }
        public void Journal(string text) => Lines.Add(text);
    }

    sealed class Rig : IDisposable
    {
        public EconomyRig Eco { get; } = new();
        public FakeAir Air { get; } = new();
        public TrackBans Bans { get; }

        public Rig()
        {
            Bans = new TrackBans(Eco.Db, Eco.Economy, Air, new FixedOptions<EconomyOptions>(Eco.Options), NullLogger<TrackBans>.Instance);
            Eco.Db.UpsertTrack(new TrackInfo("song1", "Червоний мак", "Kolibri", 180, null, "https://music.youtube.com/watch?v=song1", null));
            Eco.Db.UpsertTrack(new TrackInfo("song2", "Тато хоче", "Sound Wave 26", 200, null, "https://music.youtube.com/watch?v=song2", null));
        }

        /// <summary>Докинути черепків і сказати, скільки стало: ачівка «Сотня» зверху докидає свої.</summary>
        public int Give(string nick, int amount)
        {
            Eco.Economy.Grant(nick, amount, "listen");
            return Eco.Economy.Balance(nick);
        }

        public int Balance(string nick) => Eco.Economy.Balance(nick);

        public void Dispose() => Eco.Dispose();
    }

    [Fact]
    public async Task Member_bans_track_on_air_for_shards()
    {
        using var rig = new Rig();
        var start = rig.Give("Оля", 150);
        rig.Air.TrackId = "song1";

        var (ok, message) = await rig.Bans.BanAsync("song1", "Оля", isAdmin: false);

        Assert.True(ok, message);
        Assert.Equal(start - 100, rig.Balance("Оля"));
        Assert.True(rig.Eco.Db.IsBanned("song1"));
        var row = Assert.Single(rig.Bans.List());
        Assert.Equal(("Оля", 100, "Червоний мак"), (row.By, row.Price, row.Track.Title));
        Assert.Equal(["song1"], rig.Air.Evicted);
        Assert.Contains("за 100 черепків", Assert.Single(rig.Air.Lines));
        Assert.Equal("−100 черепків: бан треку", Assert.Single(rig.Eco.Outbox.Of<Hlechyky.Games.WalletChanged>(), w => w.Delta < 0).Text);
    }

    [Fact]
    public async Task Member_cannot_ban_what_is_not_on_air()
    {
        using var rig = new Rig();
        var start = rig.Give("Оля", 500);
        rig.Air.TrackId = "song1";

        var (ok, _) = await rig.Bans.BanAsync("song2", "Оля", isAdmin: false);

        Assert.False(ok);
        Assert.False(rig.Eco.Db.IsBanned("song2"));
        Assert.Equal(start, rig.Balance("Оля"));
        Assert.Empty(rig.Air.Evicted);
    }

    [Fact]
    public async Task Member_without_enough_shards_is_told_how_many_they_have()
    {
        using var rig = new Rig();
        rig.Give("Петро", 57);
        rig.Air.TrackId = "song1";

        var (ok, message) = await rig.Bans.BanAsync("song1", "Петро", isAdmin: false);

        Assert.False(ok);
        Assert.Equal("Треба 100 черепків, а в тебе 57", message);
        Assert.False(rig.Eco.Db.IsBanned("song1"));
        Assert.Equal(57, rig.Balance("Петро"));
    }

    [Fact]
    public async Task Banning_twice_does_not_charge_twice()
    {
        using var rig = new Rig();
        var start = rig.Give("Оля", 300);
        rig.Air.TrackId = "song1";

        Assert.True((await rig.Bans.BanAsync("song1", "Оля", isAdmin: false)).Ok);
        var (ok, message) = await rig.Bans.BanAsync("song1", "Оля", isAdmin: false);

        Assert.False(ok);
        Assert.Equal("Уже в бані", message);
        Assert.Equal(start - 100, rig.Balance("Оля"));
    }

    [Fact]
    public async Task Parallel_bans_charge_only_one_person()
    {
        using var rig = new Rig();
        var nicks = new[] { "Оля", "Петро", "Іван", "Марта" };
        var start = nicks.Sum(n => rig.Give(n, 100));
        rig.Air.TrackId = "song1";

        var results = await Task.WhenAll(nicks.Select(n => Task.Run(() => rig.Bans.BanAsync("song1", n, isAdmin: false))));

        Assert.Single(results, r => r.Ok);
        Assert.Equal(start - 100, nicks.Sum(rig.Balance));
    }

    [Fact]
    public async Task Admin_bans_anything_for_free()
    {
        using var rig = new Rig();
        rig.Air.TrackId = null;

        var (ok, message) = await rig.Bans.BanAsync("song2", "владік", isAdmin: true);

        Assert.True(ok, message);
        Assert.Equal(0, Assert.Single(rig.Bans.List()).Price);
        Assert.Empty(rig.Eco.Outbox.Of<Hlechyky.Games.WalletChanged>());
        Assert.Equal("владік банить Sound Wave 26 — Тато хоче", Assert.Single(rig.Air.Lines));
    }

    [Fact]
    public async Task Voice_messages_are_not_for_sale()
    {
        using var rig = new Rig();
        rig.Eco.Db.UpsertTrack(new TrackInfo("voice-abc", "Голосове", "Оля", 10, null, "/api/voice/voice-abc.mp3", null));
        var start = rig.Give("Петро", 500);
        rig.Air.TrackId = "voice-abc";

        Assert.False((await rig.Bans.BanAsync("voice-abc", "Петро", isAdmin: false)).Ok);
        Assert.Equal(start, rig.Balance("Петро"));
    }

    [Fact]
    public async Task Price_zero_turns_paid_bans_off()
    {
        using var rig = new Rig();
        rig.Eco.Options.BanPrice = 0;
        var start = rig.Give("Оля", 500);
        rig.Air.TrackId = "song1";

        Assert.False((await rig.Bans.BanAsync("song1", "Оля", isAdmin: false)).Ok);
        Assert.True((await rig.Bans.BanAsync("song1", "владік", isAdmin: true)).Ok);
        Assert.Equal(start, rig.Balance("Оля"));
    }

    [Fact]
    public async Task Member_buys_a_track_out_of_the_ban()
    {
        using var rig = new Rig();
        await rig.Bans.BanAsync("song2", "владік", isAdmin: true);
        var start = rig.Give("Оля", 120);

        var (ok, message) = rig.Bans.Unban("song2", "Оля", isAdmin: false);

        Assert.True(ok, message);
        Assert.False(rig.Eco.Db.IsBanned("song2"));
        Assert.Equal(start - 100, rig.Balance("Оля"));
        Assert.Equal("Оля викуповує Sound Wave 26 — Тато хоче з бану за 100 черепків", rig.Air.Lines[^1]);
    }

    [Fact]
    public void Unban_of_a_track_that_is_not_banned_costs_nothing()
    {
        using var rig = new Rig();
        var start = rig.Give("Оля", 120);

        Assert.False(rig.Bans.Unban("song1", "Оля", isAdmin: false).Ok);
        Assert.Equal(start, rig.Balance("Оля"));
    }

    [Fact]
    public async Task Unban_without_enough_shards_keeps_the_ban()
    {
        using var rig = new Rig();
        await rig.Bans.BanAsync("song1", "владік", isAdmin: true);
        rig.Give("Оля", 99);

        Assert.False(rig.Bans.Unban("song1", "Оля", isAdmin: false).Ok);
        Assert.True(rig.Eco.Db.IsBanned("song1"));
        Assert.Equal(99, rig.Balance("Оля"));
    }

    [Fact]
    public async Task Admin_unbans_a_paid_ban_for_free_and_nobody_gets_shards_back()
    {
        using var rig = new Rig();
        var start = rig.Give("Оля", 100);
        rig.Air.TrackId = "song1";
        await rig.Bans.BanAsync("song1", "Оля", isAdmin: false);

        var (ok, _) = rig.Bans.Unban("song1", "владік", isAdmin: true);

        Assert.True(ok);
        Assert.False(rig.Eco.Db.IsBanned("song1"));
        Assert.Equal(start - 100, rig.Balance("Оля"));
        Assert.Equal(0, rig.Balance("владік"));
    }
}
