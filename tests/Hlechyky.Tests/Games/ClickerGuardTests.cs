using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.ClickerGuard;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Око майстра — захист Гончарного кола від автоклікерів і скриптів (<see cref="ClickerGuard"/>): почерк кліків,
/// перевірка картинкою-полицею і десятихвилинна пауза кола. Головне тут — дві речі: людину ніколи не приймаємо за
/// робота, а робот без людини не заробляє кліками нічого.
/// </summary>
public class ClickerGuardTests
{
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static long Pots(RoomHarness h) => View(h).GetProperty("pots").GetInt64();
    static JsonElement Guard(RoomHarness h) => View(h).GetProperty("guard");
    static bool Free(RoomHarness h) => Guard(h).ValueKind == JsonValueKind.Null;

    static ActResult Human(RoomHarness h, int n = 12) => h.Act(0, "spin", PotterHands.Human(n));
    static ActResult Robot(RoomHarness h, int n = 12, int dt = 100, int press = 0) => h.Act(0, "spin", PotterHands.Robot(n, dt, press));

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void LeftBeforeCheck(RoomHarness h, int left) => Patch(h, s => s["guard"]!["left"] = left);

    /// <summary>Наклацати пачками по 12 із секундою між ними (відро не ріже), поки майстер не спитає чи не скінчиться ліміт.</summary>
    static int ClickUntilAsked(RoomHarness h, int limit)
    {
        var clicks = 0;
        while (clicks < limit && Free(h))
        {
            Human(h);
            clicks += 12;
            h.Clock.Advance(1);
        }
        return clicks;
    }

    /// <summary>Спіймати робота: три пачки мишачого софту — 36 кліків, вистачає для суду.</summary>
    static void CatchRobot(RoomHarness h)
    {
        for (var i = 0; i < 3; i++)
        {
            Robot(h);
            h.Clock.Advance(1);
        }
    }

    // ---------- людина — не робот ----------

    [Fact]
    public void A_human_hand_clicking_for_ten_minutes_is_never_taken_for_a_robot()
    {
        var h = Wheel();
        for (var i = 0; i < 600; i++)
        {
            if (!Free(h))
            {
                // Майстер питає, як і всіх, — але ніколи не через почерк.
                Assert.Equal("", Guard(h).GetProperty("why").GetString());
                Assert.Equal(JsonValueKind.Null, Guard(h).GetProperty("lockUntil").ValueKind);
                PotterHands.Pass(h);
            }
            Human(h, 8);
            h.Clock.Advance(1);
        }

        Assert.Equal(4800, Pots(h));
    }

    [Fact]
    public void A_steady_hand_on_a_125_hz_mouse_is_still_a_hand()
    {
        // Кліки падають на сітку 8 мс, а рука тримає ~150 мс із розкидом ~12 мс: значення повторюються, але розкидані.
        var rng = new Random(5);
        var hands = Enumerable.Range(0, Window)
            .Select(_ => new Hand(8 * (int)Math.Round((150 + Normal(rng) * 12) / 8), 8 * (int)Math.Round((85 + Normal(rng) * 18) / 8), 500, 500, Source.Mouse))
            .ToList();
        Assert.Null(ClickerGuard.Robot(hands));
    }

    [Fact]
    public void A_browser_with_coarse_time_is_left_to_the_picture_not_judged()
    {
        // Firefox із resistFingerprinting кругляє час до кадру: рівна рука тоді дає 150 мс майже щоразу, а 133 і 167 —
        // зрідка. Без поправки на сітку це виглядало б як ритм робота (два вікна накривають 37 з 40).
        var dts = Enumerable.Repeat(150, 33).Concat([133, 133, 133, 133, 167, 167, 167]).ToList();
        int[] presses = [50, 67, 83, 67, 50, 67, 83, 100];
        var hands = dts.Select((dt, i) => new Hand(dt, presses[i % presses.Length], 500, 500, Source.Mouse)).ToList();
        Assert.Null(ClickerGuard.Robot(hands));
    }

    [Fact]
    public void Too_few_clicks_are_not_judged_at_all()
    {
        var hands = Enumerable.Range(0, MinJudge - 1).Select(_ => new Hand(100, 0, 500, 500, Source.Mouse)).ToList();
        Assert.Null(ClickerGuard.Robot(hands));
    }

    // ---------- робот ----------

    [Theory]
    [InlineData(100, 0, "rhythm")]      // крок 100 мс і миттєве відпускання — типовий автоклікер
    [InlineData(100, 60, "rhythm")]     // зі сталим «тримати 60 мс»
    [InlineData(33, 1, "rhythm")]       // 30 кліків на секунду
    public void Mouse_software_is_caught_by_its_handwriting(int dt, int press, string why)
    {
        var hands = Enumerable.Range(0, Window).Select(_ => new Hand(dt, press, 500, 500, Source.Mouse)).ToList();
        Assert.Equal(why, ClickerGuard.Robot(hands));
    }

    [Fact]
    public void A_clicker_on_the_windows_timer_is_caught_despite_two_different_steps()
    {
        // Таймер Windows із кроком 15,6 мс: «100 мс» виходить то 94, то 109.
        var hands = Enumerable.Range(0, Window).Select(i => new Hand(i % 2 == 0 ? 94 : 109, 40 + i % 3, 500, 500, Source.Mouse)).ToList();
        Assert.Equal("rhythm", ClickerGuard.Robot(hands));
    }

    [Fact]
    public void A_script_with_random_steps_but_instant_release_is_caught_by_the_press()
    {
        var rng = new Random(3);
        var hands = Enumerable.Range(0, Window).Select(_ => new Hand(rng.Next(80, 300), rng.Next(0, 3), rng.Next(1000), rng.Next(1000), Source.Touch)).ToList();
        Assert.Equal("press", ClickerGuard.Robot(hands));
    }

    [Fact]
    public void A_script_with_random_steps_and_a_fixed_hold_is_caught_by_the_press()
    {
        var rng = new Random(4);
        var hands = Enumerable.Range(0, Window).Select(_ => new Hand(rng.Next(80, 300), 50 + rng.Next(0, 2), 500, 500, Source.Mouse)).ToList();
        Assert.Equal("press", ClickerGuard.Robot(hands));
    }

    [Fact]
    public void A_caught_robot_meets_the_shelf_at_once_and_earns_nothing_until_a_human_answers()
    {
        var h = Wheel();
        Human(h, 10);
        h.Clock.Advance(1);
        CatchRobot(h);

        var g = Guard(h);
        Assert.Equal("rhythm", g.GetProperty("why").GetString());
        Assert.Equal(JsonValueKind.Null, g.GetProperty("lockUntil").ValueKind);
        // Дві пачки до суду ще встигли зарахуватись (судимо з 32 кліків), третя — ні; зароблене раніше лишилось.
        Assert.Equal(10 + 24, Pots(h));

        // Бот клацає далі хоч годину — жодного глека. Відповідати він не вміє, а три промахи ставлять коло на паузу.
        for (var i = 0; i < 60; i++)
        {
            Robot(h);
            h.Clock.Advance(1);
        }
        Assert.Equal(10 + 24, Pots(h));
        for (var i = 0; i < MaxMisses; i++) PotterHands.Miss(h);
        Assert.Equal(h.Clock.UtcNow + LockFor, Guard(h).GetProperty("lockUntil").GetDateTimeOffset());
    }

    [Fact]
    public void A_steady_hand_on_two_nodes_of_a_coarse_timer_is_only_asked_never_paused()
    {
        // Рівна рука ~158±8 мс у браузері з сіткою 16,7 мс падає майже вся на 150 і 167 — це схоже на таймер
        // автоклікера. Тому за ритм — полиця, а не пауза.
        var hands = Enumerable.Range(0, Window).Select(i => new Hand(i % 3 == 0 ? 167 : 150, 83 + i % 4 * 17, 500, 500, Source.Mouse)).ToList();
        Assert.Equal("rhythm", ClickerGuard.Robot(hands));

        var h = Wheel();
        for (var i = 0; i < 4; i++)
        {
            h.Act(0, "spin", new { c = hands.Skip(i * 12).Take(12).Select(x => new[] { x.Dt, x.Press, x.X, x.Y, (int)x.Src }).ToArray() });
            h.Clock.Advance(2);
        }
        Assert.Equal(JsonValueKind.Null, Guard(h).GetProperty("lockUntil").ValueKind);
        Assert.Equal("👁 Майстер кивнув — крути далі", PotterHands.Pass(h).Message);
    }

    [Fact]
    public void Rhythm_passed_by_a_human_is_trusted_only_until_the_next_regular_check()
    {
        // Рівний ритм, але утримання людське — щоб тут судив лише ритм.
        void Metronome(RoomHarness room, int batches)
        {
            for (var i = 0; i < batches; i++)
            {
                room.Act(0, "spin", new { c = Enumerable.Range(0, 12).Select(_ => new[] { 100, Random.Shared.Next(45, 141), 500, 500, 0 }).ToArray() });
                room.Clock.Advance(1);
            }
        }

        var h = Wheel();
        Metronome(h, 3);
        Assert.Equal("rhythm", Guard(h).GetProperty("why").GetString());
        PotterHands.Pass(h);
        LeftBeforeCheck(h, 50);

        // Людина пройшла полицю — рівний ритм не судимо, аж поки не прийде звичайна перевірка.
        Metronome(h, 5);
        Assert.Equal("", Guard(h).GetProperty("why").GetString());

        PotterHands.Pass(h);
        Metronome(h, 3);
        Assert.Equal("rhythm", Guard(h).GetProperty("why").GetString());
    }

    [Fact]
    public void Instant_release_is_only_a_doubt_the_shelf_comes_at_once_without_a_pause()
    {
        // Тап по тачпаду ноутбука відпускає «кнопку» за 0–3 мс, як мишачий софт. Карати паузою не можна —
        // тільки спитати, а людина відповість.
        var h = Wheel();
        for (var i = 0; i < 3; i++)
        {
            h.Act(0, "spin", PotterHands.Touchpad(12));
            h.Clock.Advance(1);
        }

        var g = Guard(h);
        Assert.Equal("press", g.GetProperty("why").GetString());
        Assert.Equal(JsonValueKind.Null, g.GetProperty("lockUntil").ValueKind);
        Assert.Equal(24, Pots(h));                     // пачка, що викликала підозру, не зарахована

        PotterHands.Pass(h);
        for (var i = 0; i < 30; i++)
        {
            h.Act(0, "spin", PotterHands.Touchpad(12));
            h.Clock.Advance(1);
        }
        Assert.True(Free(h));                           // ту саму руку за утриманням більше не судимо
        Assert.Equal(24 + 360, Pots(h));
    }

    [Fact]
    public void A_trusted_touchpad_still_cannot_hide_a_metronome_and_the_trust_survives_a_reload()
    {
        var h = Wheel();
        for (var i = 0; i < 3; i++)
        {
            h.Act(0, "spin", PotterHands.Touchpad(12));
            h.Clock.Advance(1);
        }
        PotterHands.Pass(h);

        var again = Wheel();
        again.Clock.UtcNow = h.Clock.UtcNow;
        again.Room.Game.Load(h.Room.Game.Save()!);
        Assert.True(JsonNode.Parse(again.Room.Game.Save()!)!["guard"]!["pressTrusted"]!.GetValue<bool>());

        CatchRobot(again);
        Assert.Equal("rhythm", Guard(again).GetProperty("why").GetString());
        var pots = Pots(again);
        Robot(again);
        Assert.Equal(pots, Pots(again));
    }

    [Fact]
    public void Touchpad_trust_is_not_lost_when_three_misses_pause_the_wheel_first()
    {
        var h = Wheel();
        for (var i = 0; i < 3; i++)
        {
            h.Act(0, "spin", PotterHands.Touchpad(12));
            h.Clock.Advance(1);
        }
        for (var i = 0; i < MaxMisses; i++) PotterHands.Miss(h);
        Assert.Equal("misses", Guard(h).GetProperty("why").GetString());

        h.Clock.Advance(LockFor);
        PotterHands.Pass(h);
        var pots = Pots(h);
        for (var i = 0; i < 10; i++)
        {
            h.Act(0, "spin", PotterHands.Touchpad(12));
            h.Clock.Advance(1);
        }
        Assert.True(Free(h));                           // довіра дісталась тій полиці, яку пройшли після паузи
        Assert.Equal(pots + 120, Pots(h));
    }

    [Fact]
    public void Trusted_press_is_not_judged_but_rhythm_still_is()
    {
        var instant = Enumerable.Range(0, Window).Select(i => new Hand(120 + i * 7 % 150, 1, 500, 500, Source.Mouse)).ToList();
        Assert.Equal("press", ClickerGuard.Robot(instant));
        Assert.Null(ClickerGuard.Robot(instant, pressTrusted: true));

        var metronome = Enumerable.Range(0, Window).Select(_ => new Hand(100, 1, 500, 500, Source.Mouse)).ToList();
        Assert.Equal("rhythm", ClickerGuard.Robot(metronome, pressTrusted: true));
    }

    [Fact]
    public void While_the_master_waits_clicks_and_golden_jugs_do_not_count_but_the_rest_works()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 1_000; s["total"] = 1_000; s["upgrades"]!["kiln"] = 1; });
        CatchRobot(h);
        var pots = Pots(h);

        Assert.True(Human(h).Ok);                       // мовчки: чому — видно з виду
        Assert.Equal(pots, Pots(h));

        Patch(h, s => s["golden"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
        });
        Assert.Equal("Спершу Око майстра: покажи, що ти не автоклікер", h.Act(0, "catch").Message);

        h.Clock.Advance(10);                            // піч ліпить і без кліків
        Assert.Equal(pots + 30, Pots(h));
        Assert.True(h.Act(0, "buy", new { key = "wheel" }).Ok);
    }

    [Fact]
    public void After_the_pause_the_master_still_wants_an_answer()
    {
        var h = Wheel();
        CatchRobot(h);
        for (var i = 0; i < MaxMisses; i++) PotterHands.Miss(h);
        h.Clock.Advance(1);
        var answer = h.Act(0, "answer", PotterHands.RightTaps(PotterHands.Shelf(h)!));
        Assert.Equal("Коло стоїть ще 9:59", answer.Message);   // третя помилка була секунду тому

        h.Clock.Advance(LockFor);
        var g = Guard(h);
        Assert.Equal(JsonValueKind.Null, g.GetProperty("lockUntil").ValueKind);
        Assert.StartsWith("data:image/png;base64,", g.GetProperty("png").GetString());
        var pots = Pots(h);
        Human(h);
        Assert.Equal(pots, Pots(h));                   // бот просто перечекати не може

        Assert.Equal("👁 Майстер кивнув — крути далі", PotterHands.Pass(h).Message);
        Human(h);
        Assert.Equal(pots + 12, Pots(h));
        Assert.True(Free(h));
    }

    // ---------- перевірка картинкою ----------

    [Fact]
    public void The_master_asks_every_six_hundred_to_thousand_clicks()
    {
        var h = Wheel();
        var clicks = ClickUntilAsked(h, 2_000);

        Assert.InRange(clicks, CheckMin, CheckMax + 11);
        var g = Guard(h);
        Assert.InRange(g.GetProperty("count").GetInt32(), ClickerPicture.MinJugs, ClickerPicture.MaxJugs);
        Assert.Equal(0, g.GetProperty("misses").GetInt32());

        // Поки не відповів — кліки не рахуються.
        var pots = Pots(h);
        Human(h);
        Assert.Equal(pots, Pots(h));

        PotterHands.Pass(h);
        Assert.True(Free(h));
        var again = ClickUntilAsked(h, 2_000);
        Assert.InRange(again, CheckMin, CheckMax + 11);
    }

    [Fact]
    public void The_view_carries_the_picture_but_never_the_key()
    {
        var h = Wheel();
        LeftBeforeCheck(h, 1);
        Human(h, 1);

        var g = Guard(h);
        var keys = g.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["count", "height", "lockUntil", "maxMisses", "misses", "png", "serial", "why", "width"], keys);
        var png = Convert.FromBase64String(g.GetProperty("png").GetString()!["data:image/png;base64,".Length..]);
        Assert.Equal(ClickerPicture.Png(PotterHands.Shelf(h)!), png);
        Assert.DoesNotContain(Convert.ToBase64String(PotterHands.Shelf(h)!), Views.Text(View(h)));
    }

    [Fact]
    public void Every_shelf_gets_its_own_256_bit_key_so_it_cannot_be_brute_forced()
    {
        // З 31-бітного зерна System.Random полицю знаходили перебором за хвилини. Ключ на 256 біт — ні.
        var h = Wheel();
        LeftBeforeCheck(h, 1);
        Human(h, 1);
        var first = PotterHands.Shelf(h)!;
        PotterHands.Miss(h);
        var second = PotterHands.Shelf(h)!;

        Assert.Equal(ClickerPicture.KeySize, first.Length);
        Assert.Equal(32, ClickerPicture.KeySize);
        Assert.NotEqual(first, second);
        Assert.NotEqual(ClickerPicture.Png(first), ClickerPicture.Png(second));
    }

    [Fact]
    public void The_realtime_input_path_cannot_click_or_answer_in_the_workshop()
    {
        // Input не зберігає стан: відповідь «на пробу» через нього, а тоді вийти й зайти — і спроби знову повні.
        var h = Wheel();
        h.Input(0, "spin", PotterHands.Human(12));
        Assert.Equal(0, Pots(h));

        LeftBeforeCheck(h, 1);
        Human(h, 1);
        h.Input(0, "answer", PotterHands.RightTaps(PotterHands.Shelf(h)!));
        Assert.False(Free(h));
    }

    [Fact]
    public void Nobody_opens_someone_elses_workshop_by_its_key()
    {
        var h = Wheel("Оля");
        Patch(h, s => { s["pots"] = 5_000; s["total"] = 5_000; });

        var theft = h.Rooms.OpenSolo("Мирко", "clicker", "clicker:оля");
        Assert.False(theft.Reply.Ok);
        Assert.Equal("Чужу кімнату не відкрити", theft.Reply.Message);
        Assert.True(h.Rooms.OpenSolo("Оля", "clicker", "clicker:оля").Reply.Ok);   // свій ключ — будь ласка
        Assert.True(h.Rooms.OpenSolo("Мирко", "clicker", null).Reply.Ok);          // і своя порожня майстерня
    }

    [Fact]
    public void A_wrong_answer_brings_a_new_shelf_and_three_in_a_row_stop_the_wheel()
    {
        var h = Wheel();
        LeftBeforeCheck(h, 1);
        Human(h, 1);
        var serial = Guard(h).GetProperty("serial").GetInt32();

        for (var miss = 1; miss < MaxMisses; miss++)
        {
            var shelf = PotterHands.Shelf(h)!;
            Assert.True(PotterHands.Miss(h).Ok);
            var g = Guard(h);
            Assert.Equal(miss, g.GetProperty("misses").GetInt32());
            Assert.Equal(serial + miss, g.GetProperty("serial").GetInt32());
            Assert.NotEqual(shelf, PotterHands.Shelf(h));
        }

        PotterHands.Miss(h);
        var locked = Guard(h);
        Assert.Equal("misses", locked.GetProperty("why").GetString());
        Assert.Equal(h.Clock.UtcNow + LockFor, locked.GetProperty("lockUntil").GetDateTimeOffset());
        Assert.Equal(0, locked.GetProperty("misses").GetInt32());
    }

    [Fact]
    public void Answering_when_nobody_asks_is_refused()
    {
        var h = Wheel();
        Assert.Equal("Майстер ні про що не питає — крути коло", h.Act(0, "answer", new { taps = new[] { new[] { 1.0, 1.0 } } }).Message);
    }

    [Theory]
    [InlineData("""{"taps":"усі"}""")]
    [InlineData("""{"taps":[[1]]}""")]
    [InlineData("""{"taps":[["1","2"]]}""")]
    [InlineData("""{"taps":[[1e999,2]]}""")]                 // не влазить у double — не виняток, а відмова
    [InlineData("""{"taps":[[1,1],[1,1],[1,1],[1,1],[1,1],[1,1],[1,1],[1,1],[1,1]]}""")]
    public void Broken_taps_are_refused_without_spending_a_try(string payload)
    {
        var h = Wheel();
        LeftBeforeCheck(h, 1);
        Human(h, 1);

        Assert.Equal("Торкання прийшли зіпсовані", h.Act(0, "answer", JsonDocument.Parse(payload).RootElement).Message);
        Assert.Equal(0, Guard(h).GetProperty("misses").GetInt32());
    }

    [Fact]
    public void The_master_waits_for_the_fair_to_end_before_asking()
    {
        var h = Wheel();
        LeftBeforeCheck(h, 1);
        Patch(h, s => s["fairUntil"] = (h.Clock.UtcNow + TimeSpan.FromSeconds(30)).ToString("O"));

        Human(h);
        Assert.True(Free(h));
        Assert.Equal(12 * 7, Pots(h));

        h.Clock.Advance(31);
        Human(h);
        Assert.False(Free(h));
    }

    [Fact]
    public void A_bot_that_only_catches_golden_jugs_meets_the_master_too()
    {
        var h = Wheel();
        LeftBeforeCheck(h, CatchWeight + 1);
        void JugNow() => Patch(h, s => s["golden"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
        });

        JugNow();
        Assert.True(h.Act(0, "catch").Ok);             // спійманий глек коштує CatchWeight кліків
        JugNow();
        Assert.True(h.Act(0, "catch").Ok);             // лічильник пішов у мінус
        JugNow();
        Assert.Equal("👁 Майстер хоче глянути на твої руки — торкнись глечиків", h.Act(0, "catch").Message);
        Assert.Equal("Спершу Око майстра: покажи, що ти не автоклікер", h.Act(0, "catch").Message);

        PotterHands.Pass(h);
        Assert.StartsWith("🧺", h.Act(0, "catch").Message);   // той самий глек ще на полиці — ловиться
    }

    [Fact]
    public void Reloading_the_page_neither_lifts_the_pause_nor_skips_the_question()
    {
        var h = Wheel();
        CatchRobot(h);
        var saved = h.Room.Game.Save()!;

        var again = Wheel();
        again.Clock.UtcNow = h.Clock.UtcNow;
        again.Room.Game.Load(saved);

        Assert.Equal(Views.Text(Guard(h)), Views.Text(Guard(again)));
        var pots = Pots(again);
        Human(again);
        Assert.Equal(pots, Pots(again));
    }

    [Fact]
    public void An_old_save_from_before_the_master_starts_with_a_free_wheel()
    {
        var h = Wheel();
        Patch(h, s => s.Remove("guard"));
        Assert.True(Free(h));
        Human(h);
        Assert.Equal(12, Pots(h));
    }

    [Fact]
    public void A_forged_guard_in_the_save_is_trimmed_to_sane_values()
    {
        var h = Wheel();
        Patch(h, s => s["guard"] = new JsonObject
        {
            ["left"] = 99_999_999, ["shelf"] = Convert.ToBase64String(new byte[8]), ["misses"] = 77, ["why"] = "<script>", ["doubt"] = "misses",
        });
        Human(h);                                       // лічильник обрізано до CheckMax, куций ключ відкинуто
        var saved = JsonNode.Parse(h.Room.Game.Save()!)!["guard"]!;
        Assert.True(saved["left"]!.GetValue<int>() <= CheckMax);
        Assert.Null(saved["shelf"]);
        Assert.Equal("", saved["why"]!.GetValue<string>());
        Assert.Equal("", saved["doubt"]!.GetValue<string>());
    }

    // ---------- картинка ----------

    [Fact]
    public void The_shelf_is_a_real_png_of_the_promised_size()
    {
        var png = ClickerPicture.Png(PotterHands.Key(42));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(ClickerPicture.Width, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(ClickerPicture.Height, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Assert.InRange(png.Length, 1_000, 40_000);      // палітра: кілобайти, а не сотні
        Assert.Equal(png, ClickerPicture.Png(PotterHands.Key(42)));      // той самий ключ — та сама полиця
    }

    [Fact]
    public void Every_shelf_has_two_to_four_jugs_and_decoys_to_confuse()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var scene = ClickerPicture.Scene(PotterHands.Key(seed));
            Assert.InRange(ClickerPicture.Jugs(scene), ClickerPicture.MinJugs, ClickerPicture.MaxJugs);
            Assert.Contains(scene, i => i.Thing == ShelfThing.Pot);
            Assert.True(scene.Count(i => i.Thing != ShelfThing.Jug) >= 3, $"зерно {seed}: замало приманок");
            Assert.All(scene, i => Assert.True(i.X > 0 && i.X < ClickerPicture.Width && i.Y > 0 && i.Y < ClickerPicture.Height));
        }
    }

    [Fact]
    public void Tapping_every_jug_passes_in_any_order_and_anything_else_does_not()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var scene = ClickerPicture.Scene(PotterHands.Key(seed));
            var jugs = scene.Where(i => i.Thing == ShelfThing.Jug).Select(i => (i.X, i.Y)).ToList();
            Assert.True(ClickerPicture.Solve(scene, jugs), $"зерно {seed}");
            Assert.True(ClickerPicture.Solve(scene, Enumerable.Reverse(jugs).Select(j => (j.X + 4, j.Y - 4)).ToList()), $"зерно {seed}: трохи збоку");

            Assert.False(ClickerPicture.Solve(scene, jugs.Skip(1).ToList()));                          // не всі
            Assert.False(ClickerPicture.Solve(scene, jugs.Append((-40.0, -40.0)).ToList()));            // зайве
            Assert.False(ClickerPicture.Solve(scene, jugs.Select(_ => jugs[0]).ToList()));              // той самий двічі
            var decoy = scene.FirstOrDefault(i => i.Thing != ShelfThing.Jug && !scene.Any(j => j.Thing == ShelfThing.Jug && ClickerPicture.Near(j, i.X, i.Y)));
            if (decoy is not null)
                Assert.False(ClickerPicture.Solve(scene, jugs.Skip(1).Append((decoy.X, decoy.Y)).ToList()), $"зерно {seed}: приманка");
        }
    }

    [Fact]
    public void Guessing_taps_at_random_almost_never_passes()
    {
        var rng = new Random(9);
        var passed = 0;
        for (var i = 0; i < 5_000; i++)
        {
            var scene = ClickerPicture.Scene(PotterHands.Key(i));
            var taps = Enumerable.Range(0, ClickerPicture.Jugs(scene))
                .Select(_ => (rng.NextDouble() * ClickerPicture.Width, rng.NextDouble() * ClickerPicture.Height)).ToList();
            if (ClickerPicture.Solve(scene, taps)) passed++;
        }
        Assert.True(passed <= 25, $"навмання пройшло {passed} з 5000");
    }

    static double Normal(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}
