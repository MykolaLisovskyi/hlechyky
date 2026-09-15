using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Виплати за партії, стелі на день, соло-результати й позастандартні нагороди.</summary>
public class RewardsTests
{
    static readonly GameInfo Chess = EconomyRig.Info("chess", "Шахи", "шахи", rated: true);
    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");

    [Fact]
    public void Winner_gets_five_loser_one()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));

        Assert.Equal(5, rig.Paid("Оля", "win:ttt"));
        Assert.Equal(1, rig.Paid("Петро", "play:ttt"));
    }

    [Fact]
    public void Draw_pays_two_to_each()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [], draw: true));

        Assert.Equal(2, rig.Paid("Оля", "draw:ttt"));
        Assert.Equal(2, rig.Paid("Петро", "draw:ttt"));
    }

    [Fact]
    public void Short_and_quick_game_pays_nothing_but_is_still_recorded()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0], moves: 3, seconds: 5));

        Assert.Equal(0, rig.Paid("Оля", "win:ttt"));
        Assert.Equal(1, rig.Store.CountResults("оля", "ttt", "win", DateTimeOffset.MinValue));
    }

    [Fact]
    public void Long_enough_game_pays_even_with_few_moves()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0], moves: 3, seconds: 25));
        Assert.Equal(5, rig.Paid("Оля", "win:ttt"));
    }

    [Fact]
    public void Enough_moves_pays_even_when_the_game_was_fast()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0], moves: 6, seconds: 4));
        Assert.Equal(5, rig.Paid("Оля", "win:ttt"));
    }

    [Fact]
    public void Eleventh_game_of_the_day_pays_nothing()
    {
        using var rig = new EconomyRig();
        for (var i = 0; i < 11; i++)
            rig.Events.Raise(rig.Finished($"r{i}", Ttt, ["Оля", "Петро"], [0]));

        Assert.Equal(50, rig.Paid("Оля", "win:ttt"));       // десять перемог по п'ять
        Assert.Equal(11, rig.Store.CountResults("оля", "ttt", "win", DateTimeOffset.MinValue));
    }

    [Fact]
    public void The_cap_is_per_game_not_per_person()
    {
        using var rig = new EconomyRig();
        for (var i = 0; i < 11; i++)
            rig.Events.Raise(rig.Finished($"a{i}", Ttt, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("b1", Chess, ["Оля", "Петро"], [0]));

        Assert.Equal(50, rig.Paid("Оля", "win:ttt"));
        Assert.Equal(5, rig.Paid("Оля", "win:chess"));   // стеля рахується на кожну гру окремо
    }

    [Fact]
    public void Repeated_event_pays_and_records_once()
    {
        using var rig = new EconomyRig();
        var e = rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]);
        rig.Events.Raise(e);
        rig.Events.Raise(e);

        Assert.Equal(5, rig.Paid("Оля", "win:ttt"));
        Assert.Equal(1, rig.Store.CountResults("оля", "ttt", null, DateTimeOffset.MinValue));
    }

    [Fact]
    public void Game_against_yourself_pays_nothing()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "оля"], [0]));
        Assert.Equal(0, rig.Economy.Balance("Оля"));
        Assert.Equal(0, rig.Store.CountResults("оля", "ttt", null, DateTimeOffset.MinValue));
    }

    [Fact]
    public void Empty_seat_does_not_become_a_player()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", null], [0]));
        Assert.Equal(0, rig.Economy.Balance("Оля"));
        Assert.Empty(rig.Outbox.Of<WalletChanged>());
    }

    [Fact]
    public void Solo_score_lands_in_results_and_keeps_the_best()
    {
        using var rig = new EconomyRig();
        var now = rig.Clock.UtcNow;
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 100, ScoreOrder.HigherIsBetter, "clicker:оля", now));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 40, ScoreOrder.HigherIsBetter, "clicker:оля", now));
        rig.Events.Raise(new SoloScoreEvent("clicker", "Оля", 250, ScoreOrder.HigherIsBetter, "clicker:оля", now));

        Assert.Equal(250, rig.Store.BestSolo("оля", "clicker", higherIsBetter: true));
        Assert.Equal(1, rig.Store.CountResults("оля", "clicker", "solo", DateTimeOffset.MinValue));
    }

    [Fact]
    public void Daily_solo_score_writes_a_daily_result()
    {
        using var rig = new EconomyRig();
        var day = rig.Daily.Today();
        rig.Events.Raise(new SoloScoreEvent("wordle", "Оля", 3, ScoreOrder.LowerIsBetter,
            $"daily:wordle:{day}:оля", rig.Clock.UtcNow));

        var row = rig.Daily.MyResult("Оля", "wordle");
        Assert.NotNull(row);
        Assert.True(row!.Solved);
        Assert.Equal(3, row.Attempts);
    }

    [Fact]
    public void Big_daily_score_is_read_as_milliseconds()
    {
        using var rig = new EconomyRig();
        var day = rig.Daily.Today();
        rig.Events.Raise(new SoloScoreEvent("mines-daily", "Оля", 42_000, ScoreOrder.LowerIsBetter,
            $"daily:mines-daily:{day}:оля", rig.Clock.UtcNow));

        var row = rig.Daily.MyResult("Оля", "mines-daily");
        Assert.NotNull(row);
        Assert.Equal(42_000, row!.Ms);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public void Daily_award_pays_once_a_day_and_again_tomorrow()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("wordle", "room1", "Оля", 5, "daily:wordle"));
        rig.Events.Raise(new AwardEvent("wordle", "room2", "Оля", 5, "daily:wordle"));
        Assert.Equal(5, rig.Economy.Balance("Оля"));

        rig.Clock.Advance(TimeSpan.FromHours(9));   // новий київський день
        rig.Events.Raise(new AwardEvent("wordle", "room3", "Оля", 5, "daily:wordle"));
        Assert.Equal(10, rig.Economy.Balance("Оля"));
    }

    [Fact]
    public void Clicker_exchange_stops_at_the_daily_cap()
    {
        using var rig = new EconomyRig();
        for (var i = 0; i < 5; i++)
            rig.Events.Raise(new AwardEvent("clicker", "room1", "Оля", 5, "clicker"));

        Assert.Equal(20, rig.Paid("Оля", "clicker"));    // ClickerDailyCap = 20
    }

    [Fact]
    public void Awards_from_games_share_one_daily_cap()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("skilky", "room1", "Оля", 25, "skilky"));
        rig.Events.Raise(new AwardEvent("skilky", "room2", "Оля", 25, "skilky"));
        Assert.Equal(25, rig.Paid("Оля", "award:skilky"));    // AwardDailyCap = 30, друга нагорода не влізла
    }

    [Fact]
    public void Award_of_the_same_reason_in_the_same_room_pays_once()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("skilky", "room1", "Оля", 3, "skilky"));
        rig.Events.Raise(new AwardEvent("skilky", "room1", "Оля", 3, "skilky"));
        Assert.Equal(3, rig.Paid("Оля", "award:skilky"));
    }

    [Fact]
    public void Points_turned_into_shards_pay_without_a_daily_cap()
    {
        using var rig = new EconomyRig();
        for (var round = 1; round <= 10; round++)
            rig.Events.Raise(new AwardEvent("skilky", "room1", "Оля", 7, $"points:{round}"));
        Assert.Equal(70, rig.Paid("Оля", "points:skilky"));    // понад AwardDailyCap = 30
        Assert.Equal(0, rig.Paid("Оля", "award:skilky"));      // і не з'їдає спільну стелю нагород
    }

    [Fact]
    public void The_same_match_pays_its_points_once()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("skilky", "room1", "Оля", 4, "points:2"));
        rig.Events.Raise(new AwardEvent("skilky", "room1", "Оля", 4, "points:2"));
        rig.Events.Raise(new AwardEvent("skilky", "room2", "Оля", 4, "points:2"));   // інший стіл — інша партія
        Assert.Equal(8, rig.Paid("Оля", "points:skilky"));
    }

    [Fact]
    public void Ad_contest_pays_the_winner_outside_the_award_cap()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("ad", "room1", "Оля", 25, "ad:winner"));
        rig.Events.Raise(new AwardEvent("ad", "room2", "Оля", 25, "ad:winner"));
        Assert.Equal(50, rig.Paid("Оля", "ad:winner"));
        Assert.True(rig.Achievements.Has("Оля", "ad-winner"));
    }

    [Fact]
    public void Award_with_ach_prefix_unlocks_an_achievement_and_pays_its_own_reward()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new AwardEvent("scrabble", "room1", "Оля", 0, "ach:scrabble-30"));

        Assert.True(rig.Achievements.Has("Оля", "scrabble-30"));
        Assert.Equal(20, rig.Paid("Оля", "ach:scrabble-30"));
    }

    [Fact]
    public void Stake_is_not_the_rewards_business()
    {
        using var rig = new EconomyRig();
        rig.Economy.Grant("Оля", 25, "listen");
        Assert.True(rig.Economy.TrySpend("Оля", 25, "stake", "stake:r1:1:оля"));
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0], stake: 25));

        // виплата банку — справа каркаса кімнат; сервіси лише нарахували стандартні п'ять
        Assert.Equal(5, rig.Paid("Оля", "win:ttt"));
        Assert.Equal(-25, rig.Paid("Оля", "stake"));
    }

    [Fact]
    public void A_daily_award_without_a_sum_pays_the_configured_reward()
    {
        using var rig = new EconomyRig();
        rig.Options.DailyReward = 7;
        // головоломка не назвала суму — платимо типову з налаштувань, а не нуль
        rig.Events.Raise(new AwardEvent("wordle", "daily:wordle:2026-09-10:оля", "Оля", 0, "daily:wordle"));
        Assert.Equal(7, rig.Paid("Оля", "daily:wordle"));
    }

    [Fact]
    public void A_broken_daily_key_is_kept_as_a_plain_solo_result()
    {
        using var rig = new EconomyRig();
        rig.Events.Raise(new SoloScoreEvent("wordle", "Оля", 3, ScoreOrder.LowerIsBetter,
            "daily:wordle:позавчора:оля", rig.Clock.UtcNow));

        // «день», якого не буває, не має осідати в таблиці щоденного
        Assert.Null(rig.Daily.MyResult("Оля", "wordle", "позавчора"));
        Assert.Null(rig.Daily.MyResult("Оля", "wordle"));
        Assert.Equal(1, rig.Store.CountResults("оля", "wordle", "solo", DateTimeOffset.MinValue));
    }

    [Fact]
    public void A_broken_wallet_subscriber_does_not_rob_the_second_player()
    {
        using var rig = new EconomyRig();
        rig.Economy.Changed += (nick, _) =>
        {
            if (Economy.Key(nick) == "оля") throw new InvalidOperationException("ачівка спіткнулась");
        };
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));

        Assert.Equal(1, rig.Paid("Петро", "play:ttt"));
        Assert.Equal(1, rig.Store.CountResults("петро", "ttt", null, DateTimeOffset.MinValue));
        Assert.Equal(1, rig.Store.CountResults("оля", "ttt", null, DateTimeOffset.MinValue));
    }

    [Fact]
    public void A_repeated_event_does_not_move_elo_a_second_time()
    {
        using var rig = new EconomyRig();
        var e = rig.Finished("r1", Chess, ["Оля", "Петро"], [0]);
        rig.Events.Raise(e);
        var after = rig.Ratings.Of("Оля", "chess").Elo;
        rig.Events.Raise(e);

        Assert.Equal(after, rig.Ratings.Of("Оля", "chess").Elo);
        Assert.Equal(1, rig.Ratings.Of("Оля", "chess").Games);
    }
}
