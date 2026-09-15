using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Налаштування столу в мафії: темп, склад ролей, тумблери правил і три додаткові ролі (дон, маньяк, кума).
/// Правило перевірки те саме, що і в <see cref="MafiaTests"/>: тест дізнається про партію рівно те, що
/// дізнається людина, — зі свого виду. Жодних дверцят у стан гри звідси нема.
/// </summary>
public class MafiaOptionsTests
{
    static readonly string[] Villagers =
        ["Оля", "Петро", "Ганна", "Микола", "Іван", "Марія", "Тарас", "Соломія", "Богдан", "Леся", "Остап", "Дарина"];

    static RoomHarness Table(object? options = null, int players = 6, int seed = 1)
    {
        var h = new RoomHarness("mafia", options, seed);
        for (var i = 0; i < players; i++) h.Join(Villagers[i]);
        h.Start();
        return h;
    }

    // ---------- як тест дивиться на село ----------

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static int Day(RoomHarness h) => h.View(null).GetProperty("day").GetInt32();
    static JsonElement Rules(RoomHarness h) => h.View(null).GetProperty("rules");

    static IEnumerable<JsonElement> Players(RoomHarness h, int? seat = null) =>
        h.View(seat).GetProperty("players").EnumerateArray();

    static Dictionary<int, string> Roles(RoomHarness h)
    {
        var map = new Dictionary<int, string>();
        for (var seat = 0; seat < h.Room.Seats.Length; seat++)
        {
            if (h.Room.Seats[seat] is null) continue;
            var me = h.View(seat).GetProperty("me");
            if (me.ValueKind == JsonValueKind.Object) map[seat] = me.GetProperty("role").GetString()!;
        }
        return map;
    }

    static int Seat(RoomHarness h, string role) => Roles(h).First(p => p.Value == role).Key;
    static int? SeatOrNull(RoomHarness h, string role) =>
        Roles(h).Where(p => p.Value == role).Select(p => (int?)p.Key).FirstOrDefault();
    static int[] SeatsOf(RoomHarness h, string role) =>
        [.. Roles(h).Where(p => p.Value == role).Select(p => p.Key).Order()];

    static bool AliveAt(RoomHarness h, int seat) =>
        Players(h).First(p => p.GetProperty("seat").GetInt32() == seat).GetProperty("alive").GetBoolean();

    static int[] AliveSeats(RoomHarness h) =>
        [.. Players(h).Where(p => p.GetProperty("alive").GetBoolean()).Select(p => p.GetProperty("seat").GetInt32())];

    static string[] Log(RoomHarness h) =>
        [.. h.View(null).GetProperty("log").EnumerateArray().Select(x => x.GetString()!)];

    static void Until(RoomHarness h, Func<bool> done, int limit = 600)
    {
        for (var i = 0; i < limit && !done(); i++) h.Tick();
        Assert.True(done(), $"не дочекались: фаза {Phase(h)}, день {Day(h)}");
    }

    static void To(RoomHarness h, string phase) => Until(h, () => Phase(h) == phase);

    /// <summary>Місце, яке не входить у мафію і не названо окремо.</summary>
    static int OtherCivil(RoomHarness h, params int[] except)
    {
        var mafia = SeatsOf(h, "mafia").Concat(SeatsOf(h, "don")).ToArray();
        return AliveSeats(h).First(s => !mafia.Contains(s) && !except.Contains(s));
    }

    // =========================================================================================
    // Темп
    // =========================================================================================

    [Fact]
    public void A_fast_table_runs_shorter_phases_and_says_so_in_the_view()
    {
        var h = Table(new { pace = "fast" }, 6);
        var rules = Rules(h);

        Assert.Equal("fast", rules.GetProperty("pace").GetString());
        Assert.Equal(25_000, rules.GetProperty("nightMs").GetInt32());
        // Картка малює дугу за phaseMs, а не за власною константою: зараз іде знайомство.
        Assert.Equal(8_000, h.View(null).GetProperty("phaseMs").GetInt32());
    }

    [Fact]
    public void A_slow_table_stretches_the_day()
    {
        var rules = Rules(Table(new { pace = "slow" }, 6));

        Assert.Equal(5_000 + 150_000, rules.GetProperty("dayMs").GetInt32());
        Assert.Equal(60_000, rules.GetProperty("voteMs").GetInt32());
    }

    [Fact]
    public void A_fast_night_really_ends_sooner()
    {
        var h = Table(new { pace = "fast" }, 6);
        To(h, "night");
        // 25 секунд швидкої ночі проти 45 спокійної: за 30 тиків ніч має скінчитись сама, без жодної дії.
        h.Tick(30);

        Assert.NotEqual("night", Phase(h));
    }

    [Fact]
    public void An_unknown_pace_falls_back_to_the_calm_one()
    {
        // Каркас зводить чуже значення до типового ще до Configure — гра про це навіть не дізнається.
        var rules = Rules(Table(new { pace = "турбо" }, 6));

        Assert.Equal("calm", rules.GetProperty("pace").GetString());
        Assert.Equal(Mafia.NightMs, rules.GetProperty("nightMs").GetInt32());
    }

    // =========================================================================================
    // Склад ролей
    // =========================================================================================

    [Fact]
    public void The_table_can_ask_for_three_mafia()
    {
        var h = Table(new { mafia = "3" }, 8);

        Assert.Equal(3, SeatsOf(h, "mafia").Length);
        Assert.Equal(3, Rules(h).GetProperty("mafia").GetInt32());
    }

    [Fact]
    public void Mafia_never_gets_half_the_village()
    {
        // Троє мафіозі на чотирьох — це не партія, а оголошення результату: зводимо до одного.
        var h = Table(new { mafia = "3" }, 4);

        Assert.Single(SeatsOf(h, "mafia"));
        Assert.NotEqual("done", Phase(h));
    }

    [Fact]
    public void The_sheriff_can_be_left_out()
    {
        var h = Table(new { sheriff = "off" }, 6);

        Assert.Empty(SeatsOf(h, "sheriff"));
        Assert.False(Rules(h).GetProperty("sheriff").GetBoolean());
    }

    [Fact]
    public void The_doctor_can_be_invited_to_a_table_of_four()
    {
        var h = Table(new { doctor = "on" }, 4);

        Assert.Single(SeatsOf(h, "doctor"));
    }

    [Fact]
    public void The_doctor_can_be_sent_home_from_a_table_of_eight()
    {
        var h = Table(new { doctor = "off" }, 8);

        Assert.Empty(SeatsOf(h, "doctor"));
        Assert.False(Rules(h).GetProperty("doctor").GetBoolean());
    }

    [Fact]
    public void Extra_roles_step_aside_when_there_is_no_room_for_a_plain_villager()
    {
        // Мафія, комісар, лікар, маньяк і кума на чотирьох — це п'ять ролей на чотири стільці.
        var h = Table(new { doctor = "on", extra = "maniac,kuma" }, 4);
        var roles = Roles(h);

        Assert.Equal(4, roles.Count);
        Assert.Contains("civil", roles.Values);
        Assert.Empty(SeatsOf(h, "kuma"));
    }

    [Fact]
    public void The_plan_always_keeps_one_plain_villager()
    {
        var game = new Mafia();
        game.Configure(new Dictionary<string, string> { ["doctor"] = "on", ["extra"] = "don,maniac,kuma", ["mafia"] = "3" });
        for (var n = 4; n <= 12; n++)
        {
            var cast = game.Plan(n);
            var taken = cast.Mafia + cast.Sheriff + cast.Doctor + cast.Maniac + cast.Kuma;
            Assert.True(taken <= n - 1, $"на {n} гравців ролей {taken} — мирному місця не лишилось");
            Assert.True(cast.Mafia * 2 < n, $"на {n} гравців мафії {cast.Mafia} — це вже не партія");
        }
    }

    // =========================================================================================
    // Тумблери правил
    // =========================================================================================

    [Fact]
    public void A_doctor_who_may_not_heal_himself_is_told_so()
    {
        var h = Table(new { selfheal = "off" }, 6);
        var doctor = Seat(h, "doctor");
        To(h, "night");

        var r = h.Act(doctor, "heal", new { seat = doctor });

        Assert.False(r.Ok);
        Assert.Contains("Себе", r.Message);
        Assert.False(Rules(h).GetProperty("selfHeal").GetBoolean());
    }

    [Fact]
    public void By_default_the_doctor_may_still_cover_himself()
    {
        var h = Table(players: 6);
        var doctor = Seat(h, "doctor");
        To(h, "night");

        Assert.True(h.Act(doctor, "heal", new { seat = doctor }).Ok);
    }

    [Fact]
    public void Secret_votes_show_everyone_only_his_own_finger()
    {
        var h = Table(new { votes = "secret" }, 6);
        To(h, "vote");
        var alive = AliveSeats(h);
        var (me, other, target) = (alive[0], alive[1], alive[2]);
        h.Act(me, "vote", new { seat = target });
        h.Act(other, "vote", new { seat = target });

        var votes = h.View(me).GetProperty("votes");
        Assert.Equal(target, votes.GetProperty(me.ToString()).GetInt32());
        Assert.False(votes.TryGetProperty(other.ToString(), out _));
        // Хто вже визначився — не таємниця: інакше ніхто б не знав, чи чекати ще.
        var voted = h.View(me).GetProperty("voted").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Contains(other, voted);
    }

    [Fact]
    public void A_spectator_sees_no_secret_votes_at_all()
    {
        var h = Table(new { votes = "secret" }, 6);
        To(h, "vote");
        var alive = AliveSeats(h);
        h.Act(alive[0], "vote", new { seat = alive[1] });

        Assert.Empty(h.View(null).GetProperty("votes").EnumerateObject());
    }

    [Fact]
    public void Open_votes_stay_open_for_everyone()
    {
        var h = Table(players: 6);
        To(h, "vote");
        var alive = AliveSeats(h);
        h.Act(alive[0], "vote", new { seat = alive[1] });

        Assert.Equal(alive[1], h.View(alive[2]).GetProperty("votes").GetProperty(alive[0].ToString()).GetInt32());
    }

    [Fact]
    public void A_table_that_hides_roles_says_nothing_about_the_exiled()
    {
        var h = Table(new { reveal = "off" }, 6);
        To(h, "vote");
        var alive = AliveSeats(h);
        var victim = alive[0];
        var watcher = alive[1];
        foreach (var s in alive.Where(s => s != victim)) h.Act(s, "vote", new { seat = victim });
        Until(h, () => !AliveAt(h, victim));

        Assert.Contains(Log(h), l => l.Contains("іде за ворота") && !l.Contains('('));
        // Роль вигнаного лишається таємницею навіть тоді, коли його вже нема серед живих.
        var seen = Players(h, watcher).First(p => p.GetProperty("seat").GetInt32() == victim).GetProperty("role");
        Assert.Equal(JsonValueKind.Null, seen.ValueKind);
    }

    [Fact]
    public void A_table_that_hides_roles_keeps_the_secret_of_those_who_left()
    {
        var h = Table(new { reveal = "off" }, 6);
        To(h, "night");
        var alive = AliveSeats(h);
        var gone = alive[0];
        var watcher = alive[1];
        h.Leave(h.NickOf(gone));

        Assert.Contains(Log(h), l => l.Contains("виїхав із села") && !l.Contains('('));
        Assert.Equal(JsonValueKind.Null,
            Players(h, watcher).First(p => p.GetProperty("seat").GetInt32() == gone).GetProperty("role").ValueKind);
    }

    [Fact]
    public void A_quiet_first_night_refuses_the_knife()
    {
        var h = Table(new { first = "quiet" }, 6);
        var mafia = SeatsOf(h, "mafia")[0];
        To(h, "night");

        var r = h.Act(mafia, "kill", new { seat = OtherCivil(h) });

        Assert.False(r.Ok);
        Assert.Contains("тиха", r.Message);
        Assert.False(Rules(h).GetProperty("firstNightKill").GetBoolean());
    }

    [Fact]
    public void A_quiet_first_night_still_lets_the_mafia_whisper()
    {
        var h = Table(new { first = "quiet" }, 6);
        var mafia = SeatsOf(h, "mafia")[0];
        To(h, "night");

        Assert.True(h.Act(mafia, "say", new { text = "поки що тільки дивимось" }).Ok);
    }

    [Fact]
    public void A_quiet_first_night_runs_its_full_length_and_leaves_everyone_alive()
    {
        var h = Table(new { first = "quiet" }, 6);
        To(h, "night");
        var sheriff = Seat(h, "sheriff");
        var doctor = Seat(h, "doctor");
        h.Act(sheriff, "check", new { seat = OtherCivil(h, sheriff) });
        h.Act(doctor, "heal", new { seat = doctor });
        // Усі, кому було що робити, зробили — і все одно ніч не кінчається достроково: вона для шепоту.
        h.Tick(20);
        Assert.Equal("night", Phase(h));

        To(h, "day");
        Assert.Equal(6, AliveSeats(h).Length);
        Assert.Contains(Log(h), l => l.Contains("тиха ніч"));
    }

    [Fact]
    public void The_second_night_of_a_quiet_table_is_a_normal_one()
    {
        var h = Table(new { first = "quiet" }, 6);
        To(h, "vote");
        // Ніхто не голосує — село нікого не виганяє, і настає друга ніч.
        Until(h, () => Day(h) == 2 && Phase(h) == "night");
        var mafia = SeatsOf(h, "mafia")[0];
        var victim = OtherCivil(h);

        Assert.True(h.Act(mafia, "kill", new { seat = victim }).Ok);
    }

    // =========================================================================================
    // Дон
    // =========================================================================================

    [Fact]
    public void The_don_is_one_of_the_mafia_and_his_own_know_him()
    {
        var h = Table(new { extra = "don" }, 6);
        var don = Seat(h, "don");
        var mafia = SeatsOf(h, "mafia")[0];

        Assert.True(Rules(h).GetProperty("don").GetBoolean());
        Assert.Equal("don", Players(h, mafia).First(p => p.GetProperty("seat").GetInt32() == don).GetProperty("role").GetString());
        Assert.Equal("mafia", Players(h, don).First(p => p.GetProperty("seat").GetInt32() == mafia).GetProperty("role").GetString());
    }

    [Fact]
    public void The_sheriff_reads_the_don_as_a_plain_villager()
    {
        var h = Table(new { extra = "don" }, 6);
        var sheriff = Seat(h, "sheriff");
        var don = Seat(h, "don");
        To(h, "night");

        var r = h.Act(sheriff, "check", new { seat = don });

        Assert.True(r.Ok);
        Assert.Contains("не мафія", r.Message);
        var check = h.View(sheriff).GetProperty("night").GetProperty("myCheck").EnumerateArray().First();
        Assert.False(check.GetProperty("mafia").GetBoolean());
        // Ачівку комісару платять за справжню знахідку, а не за папірець дона.
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:sheriff");
    }

    [Fact]
    public void The_dons_word_beats_the_count()
    {
        var h = Table(new { extra = "don", mafia = "2" }, 6);
        var don = Seat(h, "don");
        var mafia = SeatsOf(h, "mafia")[0];
        To(h, "night");
        var first = OtherCivil(h);
        var second = OtherCivil(h, first);

        // Рядовий визначився раніше: без дона при рівності 1:1 пішов би саме його вибір.
        h.Act(mafia, "kill", new { seat = first });
        h.Act(don, "kill", new { seat = second });
        To(h, "day");

        Assert.True(AliveAt(h, first));
        Assert.False(AliveAt(h, second));
    }

    // =========================================================================================
    // Маньяк
    // =========================================================================================

    [Fact]
    public void The_maniac_sits_apart_from_everyone()
    {
        var h = Table(new { extra = "maniac" }, 6);
        var maniac = Seat(h, "maniac");

        Assert.True(Rules(h).GetProperty("maniac").GetBoolean());
        // Він не свій нікому: чужих ролей у його виді нема жодної.
        Assert.All(Players(h, maniac).Where(p => p.GetProperty("seat").GetInt32() != maniac),
            p => Assert.Equal(JsonValueKind.Null, p.GetProperty("role").ValueKind));
        // А мафія його не бачить так само, як не бачить мирних.
        var mafia = SeatsOf(h, "mafia")[0];
        Assert.Equal(JsonValueKind.Null,
            Players(h, mafia).First(p => p.GetProperty("seat").GetInt32() == maniac).GetProperty("role").ValueKind);
    }

    [Fact]
    public void The_maniac_kills_on_his_own_and_may_take_a_mafioso()
    {
        var h = Table(new { extra = "maniac" }, 6);
        var maniac = Seat(h, "maniac");
        var mafia = SeatsOf(h, "mafia")[0];
        To(h, "night");
        var victim = OtherCivil(h, maniac);

        h.Act(mafia, "kill", new { seat = victim });
        Assert.True(h.Act(maniac, "kill", new { seat = mafia }).Ok);
        To(h, "day");

        Assert.False(AliveAt(h, victim));
        Assert.False(AliveAt(h, mafia));
        Assert.Contains(Log(h), l => l.Contains("не прокинулись"));
    }

    [Fact]
    public void The_maniac_does_not_stab_himself()
    {
        var h = Table(new { extra = "maniac" }, 6);
        var maniac = Seat(h, "maniac");
        To(h, "night");

        Assert.False(h.Act(maniac, "kill", new { seat = maniac }).Ok);
    }

    [Fact]
    public void The_doctor_can_cover_the_maniacs_target()
    {
        var h = Table(new { extra = "maniac", doctor = "on" }, 8);
        var maniac = Seat(h, "maniac");
        var doctor = Seat(h, "doctor");
        To(h, "night");
        var victim = OtherCivil(h, maniac, doctor);
        foreach (var m in SeatsOf(h, "mafia")) h.Act(m, "kill", new { seat = doctor });
        h.Act(maniac, "kill", new { seat = victim });
        h.Act(doctor, "heal", new { seat = victim });
        To(h, "day");

        Assert.True(AliveAt(h, victim));
        Assert.False(AliveAt(h, doctor));
    }

    [Fact]
    public void Mafia_wins_nothing_while_the_maniac_is_still_walking()
    {
        var h = Table(new { extra = "maniac" }, 5);
        var maniac = Seat(h, "maniac");
        var mafia = SeatsOf(h, "mafia")[0];
        var sheriff = Seat(h, "sheriff");
        To(h, "night");
        h.Act(mafia, "kill", new { seat = sheriff });
        h.Act(maniac, "kill", new { seat = OtherCivil(h, maniac, sheriff) });
        To(h, "day");

        // Живих троє: мафіозі, маньяк і мирний. Мафії вже не менше, ніж мирних, — але партія триває.
        Assert.Equal(3, AliveSeats(h).Length);
        Assert.NotEqual("done", Phase(h));
    }

    [Fact]
    public void The_maniac_wins_when_only_he_and_one_villager_are_left()
    {
        var h = Table(new { extra = "maniac" }, 4);
        var maniac = Seat(h, "maniac");
        var mafia = SeatsOf(h, "mafia")[0];
        var sheriff = Seat(h, "sheriff");
        To(h, "night");
        h.Act(mafia, "kill", new { seat = sheriff });
        h.Act(maniac, "kill", new { seat = mafia });
        Until(h, () => Phase(h) == "done");

        var result = h.View(null).GetProperty("result");
        Assert.Equal("maniac", result.GetProperty("team").GetString());
        Assert.Equal([maniac], result.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Contains(h.Awards, a => a.Reason == "ach:mafia-maniac");
        Assert.Contains(Log(h), l => l.Contains("Переміг маньяк"));
    }

    [Fact]
    public void The_maniac_gets_no_win_for_an_empty_village()
    {
        var h = Table(new { extra = "maniac" }, 4);
        var maniac = Seat(h, "maniac");
        var mafia = SeatsOf(h, "mafia")[0];
        var sheriff = Seat(h, "sheriff");
        To(h, "night");
        h.Act(mafia, "kill", new { seat = sheriff });
        To(h, "day");
        // Мафіозі встав з-за столу, і в селі лишились маньяк та один мирний. Це не перемога, це неявка.
        h.Leave(h.NickOf(mafia));

        Assert.Equal("done", Phase(h));
        Assert.Equal("draw", h.View(null).GetProperty("result").GetProperty("team").GetString());
    }

    // =========================================================================================
    // Кума
    // =========================================================================================

    [Fact]
    public void The_kuma_spends_the_night_at_someones_house()
    {
        var h = Table(new { extra = "kuma" }, 8);
        var kuma = Seat(h, "kuma");
        To(h, "night");
        var host = OtherCivil(h, kuma);

        Assert.True(h.Act(kuma, "block", new { seat = host }).Ok);
        Assert.Equal(host, h.View(kuma).GetProperty("night").GetProperty("blocked").GetInt32());
        // Своєї хати їй мало, а до себе в гості не ходять.
        Assert.False(h.Act(kuma, "block", new { seat = kuma }).Ok);
    }

    [Fact]
    public void A_blocked_doctor_saves_nobody()
    {
        var h = Table(new { extra = "kuma", doctor = "on" }, 8);
        var kuma = Seat(h, "kuma");
        var doctor = Seat(h, "doctor");
        To(h, "night");
        var victim = OtherCivil(h, kuma, doctor);
        foreach (var m in SeatsOf(h, "mafia")) h.Act(m, "kill", new { seat = victim });
        h.Act(doctor, "heal", new { seat = victim });
        h.Act(kuma, "block", new { seat = doctor });
        To(h, "day");

        Assert.False(AliveAt(h, victim));
    }

    [Fact]
    public void A_blocked_mafioso_leaves_the_night_empty()
    {
        var h = Table(new { extra = "kuma", mafia = "1" }, 8);
        var kuma = Seat(h, "kuma");
        var mafia = SeatsOf(h, "mafia")[0];
        var doctor = SeatOrNull(h, "doctor");
        To(h, "night");
        h.Act(mafia, "kill", new { seat = OtherCivil(h, kuma) });
        h.Act(kuma, "block", new { seat = mafia });
        if (doctor is { } d) h.Act(d, "heal", new { seat = d });
        To(h, "day");

        Assert.Equal(8, AliveSeats(h).Length);
        Assert.Contains(Log(h), l => l.Contains("усі вціліли"));
    }

    [Fact]
    public void The_kuma_does_not_sleep_in_the_same_house_twice()
    {
        var h = Table(new { extra = "kuma", mafia = "1" }, 8);
        var kuma = Seat(h, "kuma");
        To(h, "night");
        var host = OtherCivil(h, kuma);
        h.Act(kuma, "block", new { seat = host });
        Until(h, () => Day(h) == 2 && Phase(h) == "night");

        var again = h.Act(kuma, "block", new { seat = host });

        Assert.False(again.Ok);
        Assert.Contains("ночувала", again.Message);
    }

    [Fact]
    public void The_kuma_does_not_stop_the_sheriff()
    {
        var h = Table(new { extra = "kuma" }, 8);
        var kuma = Seat(h, "kuma");
        var sheriff = Seat(h, "sheriff");
        To(h, "night");
        h.Act(kuma, "block", new { seat = sheriff });

        // До комісара кума не ходить: він і так не спить, а перевірка нікого не зачіпає.
        Assert.True(h.Act(sheriff, "check", new { seat = SeatsOf(h, "mafia")[0] }).Ok);
        Assert.NotEmpty(h.View(sheriff).GetProperty("night").GetProperty("myCheck").EnumerateArray());
    }

    [Fact]
    public void Only_the_kuma_may_go_visiting()
    {
        var h = Table(new { extra = "kuma" }, 8);
        var civil = Seat(h, "civil");
        To(h, "night");

        var r = h.Act(civil, "block", new { seat = Seat(h, "kuma") });

        Assert.False(r.Ok);
        Assert.Contains("не твоя справа", r.Message);
    }

    // =========================================================================================
    // Паспорт і збереження
    // =========================================================================================

    [Fact]
    public void The_passport_offers_every_setting_with_a_sane_default()
    {
        var info = new Mafia().Info;
        var options = info.Options!.ToDictionary(o => o.Key);

        Assert.Equal(["pace", "mafia", "sheriff", "doctor", "selfheal", "votes", "reveal", "first", "extra"],
            info.Options!.Select(o => o.Key));
        // Типове значення завжди має бути серед своїх — інакше каркас звів би його до першого-ліпшого.
        Assert.All(info.Options!, o => Assert.Contains(o.Default, o.Values.Select(v => v.Value)));
        // Кілька значень має лише «Додаткові ролі», і типове там — «без них».
        Assert.True(options["extra"].Multi);
        Assert.Equal("none", options["extra"].Default);
    }

    [Fact]
    public void A_saved_table_keeps_its_settings()
    {
        var h = Table(new { pace = "fast", extra = "don,maniac", votes = "secret", reveal = "off" }, 8, seed: 5);
        To(h, "night");
        var saved = h.Room.Game.Save()!;

        // Другий стіл грає геть за іншими правилами — але після Load має стати тим самим селом.
        var other = Table(new { pace = "slow", mafia = "1" }, 8, seed: 9);
        other.Room.Game.Load(saved);

        Assert.Equal(Views.Text(h.Room.Game.View(null)), Views.Text(other.Room.Game.View(null)));
        Assert.Equal("fast", Views.Json(other.Room.Game.View(null)).GetProperty("rules").GetProperty("pace").GetString());
    }

    [Fact]
    public void A_full_house_of_extras_still_plays_to_the_end()
    {
        // Найгустіший склад, який тільки можна замовити: дон, маньяк, кума, лікар і троє мафіозі.
        var h = Table(new { mafia = "3", doctor = "on", extra = "don,maniac,kuma" }, 12, seed: 3);
        var roles = Roles(h);

        Assert.Equal(12, roles.Count);
        Assert.Single(roles.Values, r => r == "don");
        Assert.Equal(2, roles.Values.Count(r => r == "mafia"));
        Assert.Single(roles.Values, r => r == "maniac");
        Assert.Single(roles.Values, r => r == "kuma");

        // Партія має дожити хоча б до голосування, не спіткнувшись об жодну з нових ролей.
        To(h, "vote");
        Assert.Equal("vote", Phase(h));
    }
}
