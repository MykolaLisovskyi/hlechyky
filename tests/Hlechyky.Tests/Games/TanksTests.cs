using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Танчики: мапу, рух і снаряди перевіряємо на голому <see cref="TanksCore"/> (там танк можна поставити
/// рівно туди, куди треба), а партію, фраги й кінець — через кімнату.
/// </summary>
public class TanksTests
{
    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("tanks", options, seed: seed);
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван" }.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString() ?? "";

    static void Ready(RoomHarness h)
    {
        for (var i = 0; i < 200 && Phase(h) != "go"; i++) h.Tick(1);
        Assert.Equal("go", Phase(h));
    }

    /// <summary>Порожня арена (лише рамка), без живих: тест сам ставить те, що перевіряє.</summary>
    static TanksCore Empty(int seed = 1)
    {
        var core = new TanksCore(new Random(seed));
        core.Layout();
        return core;
    }

    static Tank Put(TanksCore core, int seat, int x, int y, int dir = 0)
    {
        var t = core.Tanks[seat];
        t.Cell = TanksCore.Cell(x, y);
        t.Move = -1;
        t.Step = 0;
        t.Want = -1;
        t.Dir = dir;
        t.Plays = true;
        t.Alive = true;
        return t;
    }

    static void Steps(TanksCore core, int n)
    {
        for (var i = 0; i < n; i++) core.Step();
    }

    static TankTile At(TanksCore core, int x, int y) => core.Tiles[TanksCore.Cell(x, y)];

    // ---------- мапа ----------

    [Fact]
    public void The_border_is_steel_and_the_corners_are_clear()
    {
        var core = Empty();
        core.Reset([true, true, true, true]);
        for (var x = 0; x < TanksCore.W; x++) { Assert.Equal(TankTile.Steel, At(core, x, 0)); Assert.Equal(TankTile.Steel, At(core, x, TanksCore.H - 1)); }
        for (var y = 0; y < TanksCore.H; y++) { Assert.Equal(TankTile.Steel, At(core, 0, y)); Assert.Equal(TankTile.Steel, At(core, TanksCore.W - 1, y)); }
        foreach (var c in TanksCore.Corners)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (x, y) = (TanksCore.X(c) + dx, TanksCore.Y(c) + dy);
                    if (x == 0 || y == 0 || x == TanksCore.W - 1 || y == TanksCore.H - 1) continue;   // рамка
                    Assert.Equal(TankTile.Free, At(core, x, y));
                }
    }

    [Fact]
    public void The_map_is_mirrored_on_both_axes()
    {
        var core = Empty(7);
        core.Reset([true, true]);
        for (var y = 0; y < TanksCore.H; y++)
            for (var x = 0; x < TanksCore.W; x++)
            {
                Assert.Equal(At(core, x, y), At(core, TanksCore.W - 1 - x, y));
                Assert.Equal(At(core, x, y), At(core, x, TanksCore.H - 1 - y));
            }
    }

    [Fact]
    public void Brick_and_steel_shares_are_in_range()
    {
        var bricks = 0.0; var steel = 0.0; var inner = 0.0;
        for (var seed = 1; seed <= 20; seed++)
        {
            var core = Empty(seed);
            core.Reset([true, true]);
            for (var y = 1; y < TanksCore.H - 1; y++)
                for (var x = 1; x < TanksCore.W - 1; x++)
                {
                    inner++;
                    if (At(core, x, y) == TankTile.Brick) bricks++;
                    if (At(core, x, y) == TankTile.Steel) steel++;
                }
        }
        Assert.InRange(bricks / inner, 0.18, 0.32);   // 28 % мінус кути
        Assert.InRange(steel / inner, 0.02, 0.09);
    }

    [Fact]
    public void Two_players_start_on_opposite_corners()
    {
        var core = Empty();
        core.Reset([true, true]);
        Assert.Equal(TanksCore.Cell(1, 1), core.Tanks[0].Cell);
        Assert.Equal(TanksCore.Cell(TanksCore.W - 2, TanksCore.H - 2), core.Tanks[1].Cell);
        Assert.False(core.Tanks[2].Alive);
    }

    // ---------- рух ----------

    [Fact]
    public void A_cell_takes_four_ticks_and_the_barrel_turns()
    {
        var core = Empty();
        var t = Put(core, 0, 5, 5, dir: 3);
        core.Turn(0, 0);
        Assert.Equal(0, t.Dir);                 // повернувся одразу, ще до тика
        Steps(core, 3);
        Assert.Equal(TanksCore.Cell(5, 5), t.Cell);
        Assert.Equal(9, TanksCore.PosX(t) - 5 * TanksCore.Sub);
        Steps(core, 1);
        Assert.Equal(TanksCore.Cell(6, 5), t.Cell);
        Assert.Equal(-1, t.Move);
    }

    [Fact]
    public void Steel_bricks_and_other_tanks_block_but_the_barrel_still_turns()
    {
        var core = Empty();
        var t = Put(core, 0, 5, 5);
        core.Tiles[TanksCore.Cell(6, 5)] = TankTile.Steel;
        core.Tiles[TanksCore.Cell(5, 6)] = TankTile.Brick;
        Put(core, 1, 4, 5);
        foreach (var dir in new[] { 0, 1, 2 })
        {
            core.Turn(0, dir);
            Steps(core, 8);
            Assert.Equal(TanksCore.Cell(5, 5), t.Cell);
            Assert.Equal(dir, t.Dir);
        }
        core.Turn(0, 3);
        Steps(core, 4);
        Assert.Equal(TanksCore.Cell(5, 4), t.Cell);
    }

    [Fact]
    public void Direction_changes_only_at_cell_borders()
    {
        var core = Empty();
        var t = Put(core, 0, 5, 5);
        core.Turn(0, 0);
        Steps(core, 1);
        core.Turn(0, 1);
        Assert.Equal(0, t.Dir);                 // посеред кроку дуло ще туди, куди їде
        Steps(core, 3);
        Assert.Equal(TanksCore.Cell(6, 5), t.Cell);
        Steps(core, 4);
        Assert.Equal(TanksCore.Cell(6, 6), t.Cell);
        Assert.Equal(1, t.Dir);
    }

    [Fact]
    public void Releasing_the_key_stops_at_the_next_cell()
    {
        var core = Empty();
        var t = Put(core, 0, 5, 5);
        core.Turn(0, 0);
        Steps(core, 1);
        core.Turn(0, -1);
        Steps(core, 10);
        Assert.Equal(TanksCore.Cell(6, 5), t.Cell);
        Assert.Equal(-1, t.Move);
    }

    // ---------- снаряди ----------

    [Fact]
    public void A_shell_flies_straight_from_the_barrel_and_only_one_at_a_time()
    {
        var core = Empty();
        var t = Put(core, 0, 5, 5, dir: 0);
        Assert.True(core.Fire(0));
        Assert.False(core.Fire(0));
        var s = Assert.Single(core.Shells);
        Assert.Equal(TanksCore.CenterX(t) + TanksCore.ShellNose, s.X);
        Assert.Equal(TanksCore.CenterY(t), s.Y);
        Steps(core, 1);
        Assert.Equal(TanksCore.CenterX(t) + TanksCore.ShellNose + TanksCore.ShellSpeed, s.X);
        Assert.Equal(TanksCore.CenterY(t), s.Y);
    }

    [Fact]
    public void A_shell_dies_on_steel_and_the_edge_and_reload_holds_the_next_shot()
    {
        var core = Empty();
        Put(core, 0, 5, 5, dir: 2);
        core.Tiles[TanksCore.Cell(3, 5)] = TankTile.Steel;
        core.Fire(0);
        Steps(core, 3);
        Assert.Empty(core.Shells);
        Assert.False(core.Fire(0));             // перезарядка ще йде
        Steps(core, TanksCore.ReloadTicks);
        Assert.True(core.Fire(0));
        Steps(core, 3);
        core.Tiles[TanksCore.Cell(3, 5)] = TankTile.Free;
        Steps(core, TanksCore.ReloadTicks);
        core.Fire(0);
        Steps(core, 30);                        // до рамки й за неї
        Assert.Empty(core.Shells);
    }

    [Fact]
    public void A_shell_breaks_a_brick_and_stops_there()
    {
        var core = Empty();
        Put(core, 0, 5, 5, dir: 1);
        core.Tiles[TanksCore.Cell(5, 7)] = TankTile.Brick;
        core.Tiles[TanksCore.Cell(5, 8)] = TankTile.Brick;
        core.Fire(0);
        Steps(core, 4);
        Assert.Equal(TankTile.Free, At(core, 5, 7));
        Assert.Equal(TankTile.Brick, At(core, 5, 8));
        Assert.Empty(core.Shells);
    }

    [Fact]
    public void A_hit_tank_loses_the_field_and_the_shooter_gains_a_frag()
    {
        var core = Empty();
        Put(core, 0, 5, 5, dir: 0);
        var victim = Put(core, 1, 9, 5);
        core.Fire(0);
        Steps(core, 8);
        Assert.False(victim.Alive);
        Assert.Equal(1, core.Tanks[0].Frags);
        Assert.Equal(0, victim.Frags);
        Assert.Empty(core.Shells);
        Assert.False(core.Fire(1));             // підбитий не стріляє
    }

    [Fact]
    public void A_hit_tank_comes_back_home_with_a_shield_that_shells_pass_through()
    {
        var core = Empty();
        var shooter = Put(core, 0, 5, 5, dir: 0);
        var victim = Put(core, 1, 9, 5);
        victim.Home = TanksCore.Corners[2];     // повертається на свій кут, а не туди, де його підбили
        core.Fire(0);
        Steps(core, 8);
        Assert.False(victim.Alive);
        Assert.InRange(victim.Respawn, 1, TanksCore.RespawnTicks);   // влучило на п'ятому тику, решта вже минула
        Steps(core, victim.Respawn - 1);
        Assert.False(victim.Alive);
        Steps(core, 1);
        Assert.True(victim.Alive);
        Assert.Equal(TanksCore.Corners[2], victim.Cell);
        Assert.Equal(TanksCore.ShieldTicks, victim.Shield);

        // Стрілець стає навпроти й б'є ще раз: щит рятує, а коли він згас — уже ні.
        Put(core, 0, TanksCore.X(TanksCore.Corners[2]) - 3, TanksCore.Y(TanksCore.Corners[2]), dir: 0);
        core.Fire(0);
        Steps(core, 8);
        Assert.True(victim.Alive);              // щит
        Assert.Equal(1, shooter.Frags);
        Steps(core, TanksCore.ShieldTicks);
        Assert.Equal(0, victim.Shield);
        shooter.Reload = 0;
        core.Fire(0);
        Steps(core, 8);
        Assert.False(victim.Alive);
        Assert.Equal(2, shooter.Frags);
    }

    [Fact]
    public void A_taken_home_corner_sends_the_returning_tank_to_the_nearest_free_one()
    {
        var core = Empty();
        var victim = Put(core, 0, 1, 1);
        victim.Home = TanksCore.Cell(1, 1);
        Put(core, 1, 1, 1);                     // хтось стоїть на його старті
        Put(core, 2, 10, 7, dir: 0);
        victim.Cell = TanksCore.Cell(12, 7);
        core.Tanks[2].Dir = 0;
        core.Fire(2);
        Steps(core, 8);
        Assert.False(victim.Alive);
        Steps(core, TanksCore.RespawnTicks);
        Assert.True(victim.Alive);
        Assert.NotEqual(TanksCore.Cell(1, 1), victim.Cell);
        Assert.Contains(victim.Cell, TanksCore.Corners);
    }

    [Fact]
    public void Two_shells_cancel_each_other()
    {
        var core = Empty();
        Put(core, 0, 4, 5, dir: 0);
        Put(core, 1, 10, 5, dir: 2);
        core.Fire(0);
        core.Fire(1);
        Steps(core, 12);
        Assert.Empty(core.Shells);
        Assert.True(core.Tanks[0].Alive);
        Assert.True(core.Tanks[1].Alive);
        Assert.False(core.Tanks[0].ShellOut);
        Assert.True(core.Fire(0));              // снаряд зник — можна знову
    }

    [Fact]
    public void A_shell_never_hits_its_own_tank()
    {
        var core = Empty();
        var t = Put(core, 0, 5, 5, dir: 0);
        core.Fire(0);
        core.Shells[0].X = TanksCore.CenterX(t);   // штучно: снаряд «у» своєму танку
        core.Shells[0].Y = TanksCore.CenterY(t);
        Steps(core, 1);
        Assert.True(t.Alive);
    }

    [Fact]
    public void A_dropped_seat_vanishes_but_keeps_its_frags()
    {
        var core = Empty();
        var t = Put(core, 0, 5, 5);
        t.Frags = 3;
        core.Drop(0);
        Assert.False(t.Alive);
        Assert.False(t.Plays);
        Assert.Equal(3, t.Frags);
        Steps(core, TanksCore.RespawnTicks + 5);
        Assert.False(t.Alive);
    }

    // ---------- партія ----------

    [Fact]
    public void The_table_waits_then_plays_then_is_over_with_a_journal_line()
    {
        var h = Table();
        Assert.Equal("start", Phase(h));
        Ready(h);
        var v = h.View(null);
        Assert.Equal(TanksCore.W, v.GetProperty("width").GetInt32());
        Assert.Equal(5, v.GetProperty("need").GetInt32());
        Assert.True(v.GetProperty("walls").GetArrayLength() >= 2 * TanksCore.W + 2 * TanksCore.H - 4);
        Assert.Equal(2, v.GetProperty("p").GetArrayLength() - 2);   // чотири місця в кадрі завжди
    }

    [Fact]
    public void Frags_to_win_end_the_match_and_score_everyone()
    {
        var h = Table();
        Ready(h);
        var core = Core(h);
        core.Tanks[0].Frags = 4;
        core.Tanks[1].Frags = 2;
        // Жовтий стріляє в зеленого через чистий ряд: п'ятий фраг.
        Put(core, 0, 5, 5, dir: 0);
        Put(core, 1, 8, 5);
        for (var x = 5; x <= 8; x++) core.Tiles[TanksCore.Cell(x, 5)] = TankTile.Free;
        h.Input(0, "fire");
        h.Tick(10);
        Assert.Equal("over", Phase(h));
        var done = Assert.Single(h.Finished);
        Assert.Equal([0], done.Result.Winners);
        Assert.Equal("Танчики: Оля 5 : Петро 2", done.Result.Text);
        Assert.Equal(2, h.Scores.Count);
        Assert.Contains(h.Scores, s => s.Nick == "Оля" && s.Score == 5);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 2);
    }

    [Fact]
    public void The_option_raises_the_bar_to_ten()
    {
        var h = Table(options: new { frags = "10" });
        Ready(h);
        Assert.Equal(10, h.View(null).GetProperty("need").GetInt32());
        Core(h).Tanks[0].Frags = 5;
        h.Tick(5);
        Assert.Equal("go", Phase(h));
    }

    [Fact]
    public void Time_out_gives_the_match_to_the_leader_or_calls_a_draw()
    {
        var h = Table();
        Ready(h);
        Core(h).Tanks[1].Frags = 2;
        h.Tick(TanksCore.MatchTicks);
        Assert.Equal("over", Phase(h));
        Assert.Equal([1], Assert.Single(h.Finished).Result.Winners);

        var d = Table(seed: 3);
        Ready(d);
        d.Tick(TanksCore.MatchTicks);
        var draw = Assert.Single(d.Finished);
        Assert.True(draw.Result.Draw);
        Assert.EndsWith("— нічия", draw.Result.Text);
    }

    [Fact]
    public void Leaving_on_four_keeps_the_match_going_and_on_two_ends_it()
    {
        var h = Table(4);
        Ready(h);
        h.Leave("Ганна");
        Assert.Equal("go", Phase(h));
        Assert.Empty(h.Finished);
        Assert.False(Core(h).Tanks[2].Alive);

        var two = Table();
        Ready(two);
        two.Leave("Петро");
        var done = Assert.Single(two.Finished);
        Assert.Equal([0], done.Result.Winners);
        Assert.Contains("встав з-за столу", done.Result.Text);
    }

    [Fact]
    public void Move_accepts_an_object_or_a_bare_number_and_rejects_junk()
    {
        var h = Table();
        Ready(h);
        var core = Core(h);
        h.Input(0, "move", new { dir = 1 });
        Assert.Equal(1, core.Tanks[0].Want);
        h.Input(0, "move", 3);
        Assert.Equal(3, core.Tanks[0].Want);
        h.Input(0, "move", new { dir = 7 });
        Assert.Equal(3, core.Tanks[0].Want);    // сміття не «стоп», а відмова
        h.Input(0, "move", -1);
        Assert.Equal(-1, core.Tanks[0].Want);
    }

    [Fact]
    public void Firing_before_go_is_refused_but_a_held_direction_is_kept()
    {
        var h = Table();
        Assert.Equal("start", Phase(h));
        h.Input(0, "move", new { dir = 0 });
        h.Input(0, "fire");
        Assert.Empty(Core(h).Shells);
        Assert.Equal(0, Core(h).Tanks[0].Want);
        Ready(h);
        h.Tick(4);
        Assert.NotEqual(TanksCore.Corners[0], Core(h).Tanks[0].Cell);   // поїхав з першого тика
    }

    [Fact]
    public void The_frame_carries_shells_with_ids_and_the_bricks()
    {
        var h = Table();
        Ready(h);
        h.Input(0, "fire");
        h.Tick(1);
        var v = h.View(null);
        var shot = Assert.Single(v.GetProperty("s").EnumerateArray());
        Assert.True(shot.GetProperty("i").GetInt32() > 0);
        Assert.True(v.GetProperty("bricks").GetArrayLength() > 10);
        Assert.True(v.GetProperty("left").GetInt32() < TanksCore.MatchTicks);
    }

    [Fact]
    public void Four_tanks_shooting_for_two_minutes_stay_cheap()
    {
        var h = Table(4);
        Ready(h);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < TanksCore.MatchTicks && Phase(h) == "go"; i++)
        {
            for (var s = 0; s < 4; s++)
            {
                h.Input(s, "move", new { dir = (i / 7 + s) % 4 });
                h.Input(s, "fire");
            }
            h.Tick(1);
        }
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    // ---------- доступ до ядра ----------

    static TanksCore Core(RoomHarness h)
    {
        var game = (Tanks)h.Room.Game;
        var field = typeof(Tanks).GetField("_core", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (TanksCore)field.GetValue(game)!;
    }
}
