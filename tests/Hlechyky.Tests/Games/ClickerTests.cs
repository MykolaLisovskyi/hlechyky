using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Options;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Гончарне коло (specs/clicker.md). Тут перевіряється те, за чим гравець не встежить сам: що клік дає
/// рівно стільки, скільки обіцяно, що автоклікер нічого не виграє, що пасив рахує сервер зі стелею, і що
/// глеки міняються на черепки лише сотнями й лише в межах дня.
/// </summary>
public class ClickerTests
{
    /// <summary>Особиста кімната одного гончаря — так її відкриває клієнт кнопкою «Грати».</summary>
    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static long Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetInt64();
    static long Total(RoomHarness h) => h.View(0).GetProperty("total").GetInt64();
    static long PerClick(RoomHarness h) => h.View(0).GetProperty("perClick").GetInt64();
    static double PerSecond(RoomHarness h) => h.View(0).GetProperty("perSecond").GetDouble();
    static JsonElement Up(RoomHarness h, string key) => h.View(0).GetProperty("upgrades").GetProperty(key);
    static long Price(RoomHarness h, string key) => Up(h, key).GetProperty("price").GetInt64();
    static int LevelOf(RoomHarness h, string key) => Up(h, key).GetProperty("level").GetInt32();

    /// <summary>n людських кліків однією пачкою — так їх шле клієнт (з почерком для Ока майстра).</summary>
    static ActResult Spin(RoomHarness h, int n = 1) => h.Act(0, "spin", PotterHands.Human(n));
    static ActResult Buy(RoomHarness h, string key) => h.Act(0, "buy", new { key });
    static ActResult Sell(RoomHarness h, long pots) => h.Act(0, "sell", new { pots });

    /// <summary>Наклацати, не впираючись у ліміт: дванадцять кліків, секунда паузи, ще дванадцять.</summary>
    static void Click(RoomHarness h, int times)
    {
        while (times > 0)
        {
            var n = Math.Min(Clicker.MaxClicksPerSecond, times);
            Spin(h, n);
            times -= n;
            h.Clock.Advance(1);
        }
    }

    /// <summary>
    /// Насипати глеків прямо в збережений стан. Набивати десять тисяч кліками — то вже не тест, а бенчмарк;
    /// заразом це перевіряє, що Save/Load переживає підміну числа.
    /// </summary>
    static void Give(RoomHarness h, long pots)
    {
        var room = h.Room;
        lock (room.Sync)
        {
            var node = JsonNode.Parse(room.Game.Save()!)!;
            node["pots"] = pots;
            node["total"] = pots;
            room.Game.Load(node.ToJsonString());
        }
    }

    // ---------- паспорт і кімната ----------

    [Fact]
    public void The_potters_wheel_is_a_private_solo_room_with_its_own_module()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "clicker");

        Assert.Equal("solo", game.Group);
        Assert.Equal("immediate", game.Start);
        Assert.Equal(1, game.MinPlayers);
        Assert.Equal(1, game.MaxPlayers);
        Assert.True(game.Private);
        Assert.False(game.Rated);
        Assert.False(game.Daily);
        Assert.Equal("clicker", game.Module);
        Assert.True(game.HasCss);            // web/games/clicker.css лежить поруч із модулем
        Assert.NotEmpty(game.Hint);
    }

    [Fact]
    public void The_room_key_is_one_per_nick_forever()
    {
        var h = Wheel();
        var first = h.RoomId;

        Assert.Equal("clicker:оля", h.Room.Key);
        h.Solo("Оля");
        Assert.Equal(first, h.RoomId);      // друге «Грати» відкриває ту саму майстерню, а не нову
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        var h = Wheel();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump", new { n = 1 }).Message);
        Assert.Equal(0, Pots(h));
    }

    // ---------- клік ----------

    [Fact]
    public void One_click_with_its_handwriting_gives_one_pot()
    {
        var h = Wheel();
        Assert.True(Spin(h, 1).Ok);

        Assert.Equal(1, Pots(h));
        Assert.Equal(1, Total(h));
    }

    [Theory]
    [InlineData(null)]                                        // голий spin — так клацав би кожен скрипт
    [InlineData("""{"n":12}""")]                              // стара вкладка до Ока майстра
    [InlineData("""{"c":[]}""")]                              // пачка без жодного кліка
    [InlineData("""{"c":[[100,50,500,500]]}""")]              // відбиток без «чим клацнув»
    [InlineData("""{"c":[[100,-5,500,500,0]]}""")]            // від'ємне утримання
    [InlineData("""{"c":[[100,50,500,500,9]]}""")]            // невідоме джерело
    [InlineData("""{"c":[[100,50,5000,500,0]]}""")]           // точка поза колом
    [InlineData("""{"c":[["100",50,500,500,0]]}""")]          // число рядком
    [InlineData("""{"c":[[1e999,50,500,500,0]]}""")]          // число, що не влазить нікуди
    public void Clicks_without_a_proper_handwriting_are_not_clicks(string? payload)
    {
        // Домальовувати глек із того, що не є кліком, не можна: тоді нелегальний ввід тихо ставав би ходом.
        var h = Wheel();
        var result = h.Act(0, "spin", payload is null ? null : System.Text.Json.JsonDocument.Parse(payload).RootElement);

        Assert.False(result.Ok);
        Assert.Equal("Коло оновилось — перезавантаж сторінку", result.Message);
        Assert.Equal(0, Pots(h));
        Assert.Equal(0, Total(h));
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void A_batch_bigger_than_a_second_of_clicks_is_refused_whole()
    {
        var h = Wheel();
        Assert.False(Spin(h, Clicker.MaxClicksPerSecond + 1).Ok);
        Assert.Equal(0, Pots(h));
    }

    [Fact]
    public void A_batch_of_clicks_credits_every_one_of_them()
    {
        var h = Wheel();
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(12, Pots(h));
    }

    [Fact]
    public void More_than_twelve_clicks_a_second_are_dropped_without_a_word()
    {
        var h = Wheel();
        Spin(h, 12);

        // Хід приймаємо — чесний гравець із лагом не має бачити червоних тостів через власний інтернет.
        Assert.True(Spin(h, 12).Ok);
        Assert.True(Spin(h, 12).Ok);
        Assert.Equal(12, Pots(h));
    }

    [Fact]
    public void The_click_budget_refills_over_time_instead_of_resetting_on_the_second()
    {
        var h = Wheel();
        Spin(h, 12);                    // відро вичерпано
        h.Clock.AdvanceMs(500);
        Spin(h, 12);                    // за півсекунди долилось шість
        Assert.Equal(18, Pots(h));

        h.Clock.Advance(1);             // за секунду відро повне знову
        Spin(h, 12);
        Assert.Equal(30, Pots(h));
    }

    [Fact]
    public void A_batch_that_arrives_a_little_late_is_not_punished_for_it()
    {
        // Клієнт шле пачку раз на 700 мс. Жорстке вікно «12 за останню секунду» з'їдало б кожну другу пачку
        // в чесного гравця — відро дозволів так не робить.
        var h = Wheel();
        for (var i = 0; i < 5; i++)
        {
            Spin(h, 8);
            h.Clock.AdvanceMs(700);
        }
        Assert.Equal(40, Pots(h));
    }

    [Fact]
    public void The_click_budget_survives_a_reload()
    {
        var h = Wheel();
        Spin(h, 12);
        var saved = h.Room.Game.Save()!;

        var again = Wheel();
        again.Room.Game.Load(saved);
        Spin(again, 12);                // відро тієї ж миті так само порожнє

        Assert.Equal(12, Pots(again));
    }

    // ---------- пасив ----------

    [Fact]
    public void Nothing_ticks_until_somebody_is_hired()
    {
        var h = Wheel();
        h.Clock.Advance(TimeSpan.FromHours(3));
        Spin(h);

        Assert.Equal(0, PerSecond(h));
        Assert.Equal(1, Pots(h));
    }

    [Fact]
    public void The_kiln_earns_while_nobody_clicks()
    {
        var h = Wheel();
        Give(h, 1_000);
        Assert.True(Buy(h, "kiln").Ok);
        Assert.Equal(3, PerSecond(h));

        h.Clock.Advance(10);
        Spin(h);
        Assert.Equal(31, Pots(h));      // тридцять за десять секунд плюс власний клік
    }

    [Fact]
    public void Half_a_pot_a_second_is_not_lost_to_rounding()
    {
        var h = Wheel();
        Give(h, 100);
        Buy(h, "apprentice");
        Assert.Equal(0.5, PerSecond(h));

        h.Clock.Advance(1);
        Spin(h);
        Assert.Equal(1, Pots(h));       // півглека ще недоліплено

        h.Clock.Advance(1);
        Spin(h);
        Assert.Equal(3, Pots(h));       // дві половинки склались у цілий
    }

    [Fact]
    public void Offline_income_stops_after_eight_hours()
    {
        var h = Wheel();
        Give(h, 1_000);
        Buy(h, "kiln");

        h.Clock.Advance(TimeSpan.FromDays(1));
        Spin(h);

        Assert.Equal(8 * 3600 * 3 + 1, Pots(h));
    }

    [Fact]
    public void Reopening_the_room_restores_the_pots_and_pays_for_the_time_away()
    {
        var h = Wheel();
        Give(h, 1_000);
        Buy(h, "kiln");
        Spin(h);                        // стан ліг у сховище

        h.Leave("Оля");                 // закрив вкладку
        h.Clock.Advance(TimeSpan.FromHours(2));
        h.Solo("Оля");

        Assert.Equal(3, PerSecond(h));
        // Пасив нараховано вже на відкритті, а не з першого кліка: гончар бачить зароблене одразу.
        Assert.Equal(2 * 3600 * 3 + 1, Pots(h));
        Spin(h);
        Assert.Equal(2 * 3600 * 3 + 2, Pots(h));
    }

    [Fact]
    public void The_open_workshop_pays_for_the_time_away_without_a_single_click()
    {
        var h = Wheel();
        Give(h, 1_000);
        Buy(h, "kiln");                 // глеки пішли за піч, лишився нуль
        Assert.Equal(0, Pots(h));

        h.Clock.Advance(TimeSpan.FromHours(2));

        // Spec: пасив рахується «при кожній дії/відкритті» — саме тому Sync живе і у View.
        Assert.Equal(2 * 3600 * 3, Pots(h));
        Assert.Equal(1_000 + 2 * 3600 * 3, Total(h));   // за весь час: тисяча на піч і те, що вона наробила
    }

    // ---------- верстати ----------

    [Fact]
    public void The_faster_wheel_adds_a_pot_to_every_click()
    {
        var h = Wheel();
        Click(h, 15);
        Assert.True(Buy(h, "wheel").Ok);

        Assert.Equal(1, LevelOf(h, "wheel"));
        Assert.Equal(2, PerClick(h));
        Assert.Equal(0, Pots(h));       // п'ятнадцять глеків пішли за верстат
        Spin(h);
        Assert.Equal(2, Pots(h));
    }

    [Fact]
    public void Prices_grow_by_half_with_every_level()
    {
        var h = Wheel();
        Give(h, 1_000);

        Assert.Equal(15, Price(h, "wheel"));
        Buy(h, "wheel");
        Assert.Equal(23, Price(h, "wheel"));    // 15 × 1,5 з округленням угору
        Buy(h, "wheel");
        Assert.Equal(34, Price(h, "wheel"));
        Buy(h, "wheel");
        Assert.Equal(51, Price(h, "wheel"));
        Assert.Equal(1_000 - 15 - 23 - 34, Pots(h));
    }

    [Fact]
    public void Every_workbench_starts_at_the_price_the_spec_promises()
    {
        var h = Wheel();
        Assert.Equal(15, Price(h, "wheel"));
        Assert.Equal(100, Price(h, "apprentice"));
        Assert.Equal(1_000, Price(h, "kiln"));
        Assert.Equal(10_000, Price(h, "clay"));
    }

    [Fact]
    public void Good_clay_multiplies_both_the_click_and_the_hour_and_stops_at_five()
    {
        var h = Wheel();
        Give(h, 300_000);
        Buy(h, "wheel");
        Buy(h, "wheel");
        Buy(h, "wheel");                // за клік уже чотири
        Buy(h, "apprentice");           // пів глека за секунду
        for (var i = 0; i < 5; i++) Assert.True(Buy(h, "clay").Ok);

        Assert.Equal(5, LevelOf(h, "clay"));
        Assert.Equal(12, PerClick(h));                          // 4 × 1,25^5 = 12,2
        Assert.Equal(0.5 * Math.Pow(1.25, 5), PerSecond(h), 6);
        Assert.Equal("Гарна глина: кращої вже не буває", Buy(h, "clay").Message);
        Assert.Equal(5, LevelOf(h, "clay"));
    }

    [Fact]
    public void Buying_without_pots_is_refused_and_changes_nothing()
    {
        var h = Wheel();
        Click(h, 10);
        Spin(h);                        // мітку часу теж лишаємо свіжою — інакше вид зміниться сам собою
        var before = Views.Text(h.Room.Game.View(0));

        Assert.Equal("Бракує глеків: треба ще 4", Buy(h, "wheel").Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(0)));
    }

    [Fact]
    public void An_unknown_workbench_is_refused()
    {
        var h = Wheel();
        Give(h, 1_000);

        Assert.Equal("Такого верстата в майстерні нема", Buy(h, "лазер").Message);
        Assert.Equal("Такого верстата в майстерні нема", h.Act(0, "buy", new { nope = 1 }).Message);
        Assert.Equal(1_000, Pots(h));
    }

    // ---------- прилавок ----------

    [Fact]
    public void Pots_are_exchanged_in_hundreds_only()
    {
        var h = Wheel();
        Give(h, 250);

        Assert.Equal("Міняю сотнями: 100 глеків — один черепок", Sell(h, 150).Message);
        Assert.Equal("Міняю сотнями: 100 глеків — один черепок", Sell(h, 0).Message);
        Assert.Equal("Міняю сотнями: 100 глеків — один черепок", Sell(h, -100).Message);
        Assert.Equal(250, Pots(h));
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void You_cannot_sell_pots_you_have_not_made()
    {
        var h = Wheel();
        Give(h, 250);

        Assert.Equal("Стільки глеків ще не наліплено", Sell(h, 300).Message);
        Assert.Equal(250, Pots(h));
    }

    [Fact]
    public void A_sale_pays_shards_and_leaves_the_lifetime_total_alone()
    {
        var h = Wheel();
        Give(h, 500);

        Assert.True(Sell(h, 200).Ok);
        Assert.Equal(300, Pots(h));
        Assert.Equal(500, Total(h));    // за весь час наліплено стільки ж, скільки й було

        var award = Assert.Single(h.Awards);
        Assert.Equal("clicker", award.Reason);
        Assert.Equal("clicker", award.GameId);
        Assert.Equal(2, award.Shards);
        Assert.Equal("Оля", award.Nick);
    }

    [Fact]
    public void Twenty_shards_a_day_and_not_a_shard_more()
    {
        var h = Wheel();
        Give(h, 2_500);

        Assert.Equal("Сьогодні лишилось 20 — більше не візьму", Sell(h, 2_100).Message);
        Assert.True(Sell(h, 2_000).Ok);
        Assert.Equal(20, h.View(0).GetProperty("soldToday").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("canSellToday").GetInt32());
        Assert.Equal("Сьогодні черепки скінчились, приходь завтра", Sell(h, 100).Message);
        Assert.Equal(20, Assert.Single(h.Awards).Shards);
    }

    [Fact]
    public void The_counter_starts_over_after_kyiv_midnight()
    {
        var h = Wheel();
        Give(h, 2_500);
        Sell(h, 2_000);

        h.Clock.Advance(TimeSpan.FromHours(12));   // 15:00 → 03:00 наступного дня за Києвом
        Assert.Equal(0, h.View(0).GetProperty("soldToday").GetInt32());
        Assert.Equal(20, h.View(0).GetProperty("canSellToday").GetInt32());

        Assert.True(Sell(h, 500).Ok);
        Assert.Equal(2, h.Awards.Count);
        // Головне тут — не сам факт обміну, а лічильник ПІСЛЯ нього: доти, доки вчорашня сума переїжджала
        // в сьогодні, перший же обмін нового дня закривав гончареві всю денну стелю.
        Assert.Equal(5, h.View(0).GetProperty("soldToday").GetInt32());
        Assert.Equal(15, h.View(0).GetProperty("canSellToday").GetInt32());
    }

    [Fact]
    public void Yesterdays_sales_do_not_eat_todays_shards()
    {
        var h = Wheel();
        Give(h, 100_000);
        Assert.True(Sell(h, 2_000).Ok);            // учора вибрано всю стелю до останнього черепка

        h.Clock.Advance(TimeSpan.FromHours(12));   // київська північ позаду

        Assert.True(Sell(h, 100).Ok);
        Assert.Equal(1, h.View(0).GetProperty("soldToday").GetInt32());
        Assert.Equal(19, h.View(0).GetProperty("canSellToday").GetInt32());
        Assert.True(Sell(h, 1_900).Ok);            // решта стелі того самого дня на місці
        Assert.Equal(20, h.View(0).GetProperty("soldToday").GetInt32());
        Assert.Equal(0, h.View(0).GetProperty("canSellToday").GetInt32());
        Assert.Equal(new[] { 20, 1, 19 }, h.Awards.Select(a => a.Shards));
    }

    [Fact]
    public void A_mountain_of_pots_does_not_wrap_the_count_into_a_minus()
    {
        // Чесною грою стільки глеків не наліпиш, але стан — це JSON у базі: одна правка руками, і прилавок
        // мусить лишитись прилавком, а не роздавати від'ємні черепки.
        var h = Wheel();
        Give(h, 300_000_000_000);

        Assert.Equal("Сьогодні лишилось 20 — більше не візьму", Sell(h, 300_000_000_000).Message);
        Assert.Equal(300_000_000_000, Pots(h));
        Assert.Empty(h.Awards);

        Assert.True(Sell(h, 2_000).Ok);
        Assert.Equal(20, Assert.Single(h.Awards).Shards);
    }

    [Fact]
    public void The_daily_ceiling_comes_from_the_economy_settings()
    {
        // У проді стелю дає Economy:ClickerDailyCap — без цього тесту перевірявся б лише запасний шлях.
        var h = new RoomHarness("clicker", services: RoomHarness.WithService<IOptionsMonitor<EconomyOptions>>(
            new FixedOptions<EconomyOptions>(new EconomyOptions { ClickerDailyCap = 3 })));
        h.Solo("Оля");
        Give(h, 500);

        Assert.Equal(3, h.View(0).GetProperty("cap").GetInt32());
        Assert.Equal(3, h.View(0).GetProperty("canSellToday").GetInt32());
        Assert.Equal("Сьогодні лишилось 3 — більше не візьму", Sell(h, 400).Message);
        Assert.True(Sell(h, 300).Ok);
        Assert.Equal("Сьогодні черепки скінчились, приходь завтра", Sell(h, 100).Message);
        Assert.Equal(3, Assert.Single(h.Awards).Shards);
    }

    // ---------- таблиця, вид, збереження ----------

    [Fact]
    public void The_table_gets_the_lifetime_total_and_the_room_key()
    {
        var h = Wheel();
        Click(h, 5);

        var score = h.Scores.Last();
        Assert.Equal(5, score.Score);
        Assert.Equal(Total(h), score.Score);
        Assert.Equal("clicker", score.GameId);
        Assert.Equal("clicker:оля", score.Key);
        Assert.Equal(ScoreOrder.HigherIsBetter, score.Order);
    }

    [Fact]
    public void The_table_is_not_poked_on_every_batch_of_clicks()
    {
        // Клієнт шле пачку раз на 700 мс: якби кожна йшла в таблицю, у SQLite (ту саму, у яку пише ефір)
        // летіло б півтора запису на секунду з кожного гончаря, а колонка «спроб» рахувала б пачки.
        var h = Wheel();
        Click(h, 120);                  // десять пачок по дванадцять, десять секунд роботи

        var first = Assert.Single(h.Scores);
        Assert.Equal(12, first.Score);  // перше число після старту летить одразу

        h.Clock.Advance(30);
        Spin(h);
        Assert.Equal(2, h.Scores.Count);
        Assert.Equal(Total(h), h.Scores.Last().Score);
    }

    [Fact]
    public void A_thousand_pots_reaches_the_table_at_once_so_the_achievement_is_not_late()
    {
        // Ачівку «Гончар» платформа роздає саме з таблиці (Economy/Achievements.cs), тож поріг мусить
        // проскочити повз півхвилинну паузу.
        var h = Wheel();
        Give(h, 990);
        Spin(h);
        Assert.Equal(991, Assert.Single(h.Scores).Score);

        Spin(h, 5);                     // 996 — таблиця почекає
        Assert.Single(h.Scores);

        Spin(h, 6);                     // 1002 — поріг перетнуто, число летить негайно
        Assert.Equal(2, h.Scores.Count);
        Assert.Equal(1_002, h.Scores.Last().Score);
    }

    [Fact]
    public void Selling_does_not_touch_the_table_because_the_total_did_not_move()
    {
        var h = Wheel();
        Give(h, 500);
        Spin(h);
        var was = h.Scores.Count;

        Assert.True(Sell(h, 200).Ok);
        Assert.Equal(was, h.Scores.Count);
    }

    [Fact]
    public void The_view_has_the_shape_the_module_expects()
    {
        var h = Wheel();
        var v = h.View(0);

        Assert.Equal(0, v.GetProperty("pots").GetInt64());
        Assert.Equal(0, v.GetProperty("total").GetInt64());
        Assert.Equal(1, v.GetProperty("perClick").GetInt64());
        Assert.Equal(0, v.GetProperty("perSecond").GetDouble());
        Assert.Equal(100, v.GetProperty("rate").GetInt32());
        Assert.Equal(20, v.GetProperty("cap").GetInt32());
        Assert.Equal(20, v.GetProperty("canSellToday").GetInt32());
        Assert.Equal(0, v.GetProperty("soldToday").GetInt32());
        Assert.Equal(JsonValueKind.String, v.GetProperty("lastSync").ValueKind);
        // Серверне «зараз» поруч із міткою: клієнт міряє простій ним, а не своїм годинником.
        Assert.Equal(JsonValueKind.String, v.GetProperty("now").ValueKind);

        var ups = v.GetProperty("upgrades");
        foreach (var key in new[] { "wheel", "apprentice", "kiln", "clay" })
        {
            var u = ups.GetProperty(key);
            Assert.Equal(0, u.GetProperty("level").GetInt32());
            Assert.True(u.GetProperty("price").GetInt64() > 0);
            Assert.False(string.IsNullOrWhiteSpace(u.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(u.GetProperty("desc").GetString()));
        }
        Assert.Equal(0, ups.GetProperty("wheel").GetProperty("max").GetInt32());
        Assert.Equal(5, ups.GetProperty("clay").GetProperty("max").GetInt32());
        // Ховати нема від кого: кімната приватна, крім господаря сюди й не пустять.
        Assert.Equal(Views.Text(h.Room.Game.View(0)), Views.Text(h.Room.Game.View(null)));
    }

    [Fact]
    public void The_state_is_written_after_every_accepted_action()
    {
        var h = Wheel();
        Assert.Equal(0, h.Store.Saves);

        Spin(h);
        Assert.Equal(1, h.Store.Saves);
        Buy(h, "wheel");                       // глеків нема — це не хід, і зберігати нема чого
        Assert.Equal(1, h.Store.Saves);
        Spin(h);
        Assert.Equal(2, h.Store.Saves);
        Assert.Contains("clicker:оля", h.Store.States.Keys);
    }

    [Fact]
    public void Load_of_Save_gives_an_equivalent_view()
    {
        var h = Wheel();
        Give(h, 5_000);
        Buy(h, "wheel");
        Buy(h, "apprentice");
        Sell(h, 100);
        Spin(h, 3);
        var json = h.Room.Game.Save()!;
        var before = Views.Text(h.Room.Game.View(0));

        var again = Wheel();
        again.Room.Game.Load(json);

        Assert.Equal(before, Views.Text(again.Room.Game.View(0)));
    }

    [Fact]
    public void A_broken_saved_state_does_not_break_the_workshop()
    {
        var h = Wheel();
        // Стан лежить у базі й міг застати іншу версію гри: чого не зрозуміли — те лишається чистим.
        h.Room.Game.Load("""{"pots":-5,"total":1,"upgrades":{"clay":99,"вигадка":3},"soldToday":null}""");

        Assert.Equal(0, Pots(h));
        Assert.Equal(5, LevelOf(h, "clay"));   // вище стелі глина не піднімається навіть із бази
        Assert.Equal(0, LevelOf(h, "wheel"));
        Assert.True(Spin(h).Ok);
    }
}
