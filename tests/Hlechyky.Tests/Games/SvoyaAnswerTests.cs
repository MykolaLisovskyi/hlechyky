using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>Автоперевірка відповіді «Своєї гри» (specs/svoya.md §10) — кожен пункт окремим кейсом.</summary>
public class SvoyaAnswerTests
{
    static bool Hit(string text, params string[] answers) => SvoyaAnswer.Hits(text, answers);

    [Theory]
    [InlineData("шевченко")]
    [InlineData("  ШЕВЧЕНКО!!! ")]
    [InlineData("Шевченко.")]
    public void Case_punctuation_and_spaces_do_not_matter(string text) => Assert.True(Hit(text, "Шевченко"));

    [Fact]
    public void Apostrophes_do_not_matter() => Assert.True(Hit("пятихатки", "П'ятихатки"));

    [Fact]
    public void One_typo_per_five_letters()
    {
        Assert.True(Hit("Шевченка", "Шевченко"));
        Assert.True(Hit("Котляревскій", "Котляревський"));            // 2 помилки на 13 літер
        Assert.False(Hit("Кошляренко", "Котляревський"));
        Assert.True(Hit("онука", "ONUKA"));
        Assert.False(Hit("Кеїв", "Київ"));                             // 4 літери — без помилок
    }

    [Fact]
    public void Longer_text_counts_when_it_contains_the_answer_as_whole_words()
    {
        Assert.True(Hit("Тарас Шевченко", "Шевченко"));
        Assert.True(Hit("це Котляревський", "Іван Котляревський", "Котляревський"));
        Assert.False(Hit("Шевченкович", "Шевченко"));
        Assert.False(Hit("так ні", "ні"));                              // коротше за 3 літери — лише точно
    }

    [Fact]
    public void Cyrillic_and_latin_meet()
    {
        Assert.True(Hit("Okean Elzy", "Океан Ельзи"));
        Assert.True(Hit("Скрябін", "Skryabin"));
        Assert.True(Hit("калуш", "Kalush Orchestra", "Kalush"));
    }

    [Fact]
    public void Numbers_inside_text_count()
    {
        Assert.True(Hit("у 1991 році", "1991"));
        Assert.True(Hit("1991", "1991"));
        Assert.False(Hit("1990", "1991"));
        Assert.False(Hit("у 19911 році", "1991"));
    }

    [Fact]
    public void Simple_number_words_become_digits()
    {
        Assert.True(Hit("п'ять", "5"));
        Assert.True(Hit("двадцять п'ять", "25"));
        Assert.True(Hit("їх було сорок", "40"));
        Assert.True(Hit("дев'яносто дев'ять", "99"));
        Assert.False(Hit("двадцять шість", "25"));
        Assert.Equal([12, 7], SvoyaAnswer.Numbers("дванадцять і сім"));
    }

    [Fact]
    public void Any_of_the_accepted_answers_counts()
    {
        Assert.True(Hit("Калуш", "Kalush Orchestra", "Kalush", "Калуш"));
        Assert.True(Hit("Kalush Orchestra", "Kalush Orchestra", "Kalush"));
    }

    [Fact]
    public void Cases_and_synonyms_are_left_to_accept_and_appeal()
    {
        Assert.False(Hit("у Львові", "Львів"));                          // «львові» ≠ «львів» без помилки на 5 літер
        Assert.False(Hit("Борисфен", "Дніпро"));
    }

    [Fact]
    public void Empty_text_never_hits()
    {
        Assert.False(Hit("", "Київ"));
        Assert.False(Hit("   ", "Київ"));
        Assert.False(Hit("Київ"));
    }
}
