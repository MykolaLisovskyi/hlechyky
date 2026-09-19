using System.Text.Json;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>Модель пакета «Своєї гри»: шаблон, нормалізація, дві перевірки (specs/svoya.md §2).</summary>
public class SvoyaPackTests
{
    public static string FixturePath => Paths.Resolve("tests/Hlechyky.Tests/Fixtures/svoya-mini.json");

    public static SvoyaPack Mini() => SvoyaPack.Parse(File.ReadAllText(FixturePath))!.Normalize();

    /// <summary>Класичний шаблон, заповнений так, що в нього можна грати.</summary>
    public static SvoyaPack FilledClassic(string title = "Повний")
    {
        var p = SvoyaPack.Classic(title);
        var n = 0;
        foreach (var r in p.Rounds)
            foreach (var t in r.Themes)
            {
                t.Name = "Тема " + ++n;
                foreach (var q in t.Questions) { q.Text = "Запитання " + n; q.Answer = "Відповідь " + n; }
            }
        return p.Normalize();
    }

    static bool Has(IEnumerable<string> errors, string part) => errors.Any(e => e.Contains(part, StringComparison.Ordinal));

    [Fact]
    public void Fixture_is_valid()
    {
        var p = Mini();
        Assert.Empty(p.Validate());
        Assert.Equal(6, p.QuestionCount);
        Assert.True(p.Rounds[^1].IsFinal);
    }

    [Fact]
    public void Classic_template_has_three_rounds_and_a_final()
    {
        var p = SvoyaPack.Classic();
        Assert.Equal(4, p.Rounds.Count);
        Assert.Equal([100, 200, 300, 400, 500], p.Rounds[0].Themes[0].Questions.Select(q => q.Price));
        Assert.Equal([200, 400, 600, 800, 1000], p.Rounds[1].Themes[4].Questions.Select(q => q.Price));
        Assert.Equal([300, 600, 900, 1200, 1500], p.Rounds[2].Themes[2].Questions.Select(q => q.Price));
        Assert.All(p.Rounds.Take(3), r => Assert.Equal(5, r.Themes.Count));
        Assert.True(p.Rounds[3].IsFinal);
        Assert.Equal(5, p.Rounds[3].Themes.Count);
        Assert.All(p.Rounds[3].Themes, t => Assert.Single(t.Questions));
        Assert.Equal(80, p.QuestionCount);
    }

    [Fact]
    public void Empty_template_saves_as_draft_but_is_not_playable()
    {
        var p = SvoyaPack.Classic().Normalize();
        Assert.Empty(p.Check());
        var errors = p.Validate();
        Assert.True(Has(errors, "нема відповіді"));
        Assert.True(Has(errors, "тема без назви"));
    }

    [Fact]
    public void Filled_template_is_playable() => Assert.Empty(FilledClassic().Validate());

    [Fact]
    public void Prices_follow_step_times_round() => Assert.Equal([200, 400, 600], SvoyaPack.Prices(2, 3));

    [Fact]
    public void No_rounds_is_an_error() => Assert.True(Has(new SvoyaPack { Title = "x" }.Validate(), "Нема жодного раунду"));

    [Fact]
    public void Untitled_pack_is_rejected_even_as_draft() => Assert.True(Has(new SvoyaPack().Check(), "без назви"));

    [Fact]
    public void Normal_round_needs_a_theme()
    {
        var p = Mini();
        p.Rounds[0].Themes.Clear();
        Assert.True(Has(p.Validate(), "Раунд 1: нема жодної теми"));
    }

    [Fact]
    public void Theme_needs_one_to_eight_questions()
    {
        var p = Mini();
        p.Rounds[0].Themes[0].Questions.Clear();
        Assert.True(Has(p.Validate(), "нема жодного запитання"));

        var q = Mini();
        q.Rounds[0].Themes[0].Questions = [.. Enumerable.Range(1, 9).Select(i => new SvoyaQuestion { Price = i * 100, Text = "т", Answer = "в" })];
        Assert.True(Has(q.Check(), "запитань більше за 8"));
    }

    [Fact]
    public void Price_must_be_positive_and_unique_in_theme()
    {
        var p = Mini();
        p.Rounds[0].Themes[0].Questions[1].Price = 100;
        Assert.True(Has(p.Validate(), "ціна 100 двічі"));

        var q = Mini();
        q.Rounds[0].Themes[1].Questions[0].Price = 0;
        Assert.True(Has(q.Validate(), "ціна має бути більшою за нуль"));

        var same = Mini();
        same.Rounds[0].Themes[1].Questions[0].Price = 200;   // та сама ціна, але в різних темах — нормально
        same.Rounds[0].Themes[1].Questions[1].Price = 300;
        Assert.Empty(same.Validate());
    }

    [Fact]
    public void Final_needs_two_themes_with_one_question_each()
    {
        var p = Mini();
        p.Rounds[1].Themes.RemoveAt(1);
        Assert.True(Has(p.Validate(), "у фіналі потрібно щонайменше 2 теми"));

        var q = Mini();
        q.Rounds[1].Themes[0].Questions.Add(new SvoyaQuestion { Text = "ще", Answer = "так" });
        Assert.True(Has(q.Validate(), "рівно одне запитання"));
    }

    [Fact]
    public void Final_is_single_and_last()
    {
        var p = Mini();
        p.Rounds.Add(p.Clone().Rounds[1]);
        Assert.True(Has(p.Validate(), "Фінал може бути лише один"));

        var first = Mini();
        first.Rounds.Reverse();
        Assert.True(Has(first.Validate(), "Фінал має бути останнім"));

        var only = Mini();
        only.Rounds.RemoveAt(0);
        Assert.True(Has(only.Validate(), "потрібен хоч один звичайний раунд"));
    }

    [Fact]
    public void Round_without_final_is_fine()
    {
        var p = Mini();
        p.Rounds.RemoveAt(1);
        Assert.Empty(p.Validate());
    }

    [Fact]
    public void Question_needs_text_or_media_and_an_answer()
    {
        var p = Mini();
        p.Rounds[0].Themes[0].Questions[0].Text = "";
        Assert.True(Has(p.Validate(), "нема ні тексту, ні медіа"));

        p.Rounds[0].Themes[0].Questions[0].Media = new SvoyaMedia { Kind = "image", File = "0123456789abcdef.jpg" };
        Assert.Empty(p.Validate());

        p.Rounds[0].Themes[0].Questions[1].Answer = "  ";
        Assert.True(Has(p.Normalize().Validate(), "Раунд 1, тема «Література», 200: нема відповіді"));
    }

    [Fact]
    public void Lengths_are_limited()
    {
        var p = Mini();
        p.Title = new string('а', 61);
        p.Rounds[0].Themes[0].Name = new string('б', 61);
        p.Rounds[0].Themes[0].Questions[0].Text = new string('в', 601);
        p.Rounds[0].Themes[0].Questions[1].Answer = new string('г', 121);
        var errors = p.Check();
        Assert.True(Has(errors, "Назва пакета довша за 60"));
        Assert.True(Has(errors, "назва довша за 60"));
        Assert.True(Has(errors, "текст довший за 600"));
        Assert.True(Has(errors, "відповідь довша за 120"));
    }

    [Fact]
    public void Media_file_must_exist_and_fit_the_limit()
    {
        var p = Mini();
        p.Rounds[0].Themes[0].Questions[0].Media = new SvoyaMedia { Kind = "audio", File = "aaaaaaaaaaaa.mp3", Seconds = 10 };
        p.Rounds[0].Themes[1].Questions[0].AnswerMedia = new SvoyaMedia { Kind = "image", File = "bbbbbbbbbbbb.png" };
        Assert.Empty(p.Validate());                                                 // без перевірки файлів
        Assert.True(Has(p.Validate(_ => null), "Медіа-файла aaaaaaaaaaaa.mp3 нема"));
        Assert.Empty(p.Validate(_ => 1000, 5000));
        Assert.True(Has(p.Validate(_ => 3 * 1024 * 1024, 5L * 1024 * 1024), "Медіа пакета більше за 5 МБ"));
    }

    [Fact]
    public void Media_name_cannot_escape_the_pack_folder()
    {
        var p = Mini();
        p.Rounds[0].Themes[0].Questions[0].Media = new SvoyaMedia { Kind = "image", File = "../../hlechyky.db" };
        p.Rounds[0].Themes[0].Questions[1].Media = new SvoyaMedia { Kind = "gif-animation", File = "cccccccccccc.gif" };
        var errors = p.Normalize().Check();
        Assert.True(Has(errors, "дивне ім'я медіа-файла"));
        Assert.True(Has(errors, "невідомий тип медіа"));
    }

    [Fact]
    public void Normalize_cleans_what_the_form_brings()
    {
        var p = Mini();
        var q = p.Rounds[0].Themes[0].Questions[0];
        q.Answer = "  Іван   Котляревський ";
        q.Accept = ["Котляревський", " котляревський", "", "Іван Котляревський", "Котляревський"];
        q.Type = "WEIRD";
        q.CatPrice = 300;
        p.Rounds[1].Themes[0].Questions[0].Type = "auction";
        p.Normalize();
        Assert.Equal("Іван Котляревський", q.Answer);
        Assert.Equal(["Котляревський", "котляревський"], q.Accept);
        Assert.Equal(SvoyaQuestion.Normal, q.Type);
        Assert.Null(q.CatPrice);
        Assert.Equal(SvoyaQuestion.Normal, p.Rounds[1].Themes[0].Questions[0].Type);   // у фіналі спецклітинок нема
    }

    [Fact]
    public void Cat_keeps_its_price()
    {
        var p = Mini();
        var q = p.Rounds[0].Themes[0].Questions[0];
        q.Type = "cat";
        q.CatPrice = 300;
        p.Normalize();
        Assert.Equal(300, q.CatPrice);
        Assert.Empty(p.Validate());
    }

    [Fact]
    public void Json_round_trip_uses_camel_case()
    {
        var p = Mini();
        p.Rounds[0].Themes[0].Questions[0].AnswerMedia = new SvoyaMedia { Kind = "image", File = "dddddddddddd.jpg" };
        var json = p.ToJson();
        Assert.Contains("\"answerMedia\"", json);
        Assert.Contains("Котляревський", json);                   // кирилиця не \u-кодом
        Assert.DoesNotContain("\"questionCount\"", json);
        var back = SvoyaPack.Parse(json)!;
        Assert.Equal(p.ToJson(), back.ToJson());
    }

    [Fact]
    public void Broken_json_is_null_not_an_exception()
    {
        Assert.Null(SvoyaPack.Parse("{ nope"));
        Assert.Null(SvoyaPack.Parse("{\"rounds\": 5}"));
    }

    [Fact]
    public void Error_addresses_name_round_theme_and_price()
    {
        var p = Mini();
        p.Rounds[0].Themes[1].Questions[1].Answer = "";
        var e = Assert.Single(p.Validate());
        Assert.Equal("Раунд 1, тема «Географія», 200: нема відповіді", e);
    }
}
