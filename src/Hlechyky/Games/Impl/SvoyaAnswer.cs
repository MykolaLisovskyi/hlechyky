namespace Hlechyky.Games.Impl;

/// <summary>
/// Чи зараховується написана відповідь (specs/svoya.md §10). Основа — <see cref="MelodyAnswer.Hits"/>: регістр,
/// розділові й апострофи не важать, одна помилка на п'ять літер, «Тарас Шевченко» влучає в «Шевченко»
/// (цілими словами), кирилиця й латиниця зводяться одна до одної. Зверху — числа: «у 1991 році» і
/// «двадцять п'ять» на відповідь «25». Решту (відмінки, синоніми) бере <c>accept</c> пакета й апеляція.
/// </summary>
public static class SvoyaAnswer
{
    public static bool Hits(string? text, IEnumerable<string> answers)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var keys = answers.Select(MelodyAnswer.Key).Where(k => k.Length > 0).Distinct().ToList();
        if (keys.Count == 0) return false;
        if (MelodyAnswer.Hits(text, keys)) return true;

        var numbers = keys.Where(IsNumber).ToList();
        if (numbers.Count == 0) return false;
        var said = Numbers(MelodyAnswer.Key(text));
        return numbers.Any(n => said.Contains(long.Parse(n)));
    }

    static bool IsNumber(string key) => key.Length is > 0 and <= 15 && key.All(char.IsAsciiDigit);

    static readonly Dictionary<string, int> Units = new()
    {
        ["нуль"] = 0, ["один"] = 1, ["одна"] = 1, ["одне"] = 1, ["два"] = 2, ["дві"] = 2, ["три"] = 3, ["чотири"] = 4,
        ["пять"] = 5, ["шість"] = 6, ["сім"] = 7, ["вісім"] = 8, ["девять"] = 9, ["десять"] = 10, ["одинадцять"] = 11,
        ["дванадцять"] = 12, ["тринадцять"] = 13, ["чотирнадцять"] = 14, ["пятнадцять"] = 15, ["шістнадцять"] = 16,
        ["сімнадцять"] = 17, ["вісімнадцять"] = 18, ["девятнадцять"] = 19,
    };

    static readonly Dictionary<string, int> Tens = new()
    {
        ["двадцять"] = 20, ["тридцять"] = 30, ["сорок"] = 40, ["пятдесят"] = 50, ["шістдесят"] = 60,
        ["сімдесят"] = 70, ["вісімдесят"] = 80, ["девяносто"] = 90,
    };

    /// <summary>Усі числа в тексті: цифрами і простими словами 0..99 («сорок два» → 42).</summary>
    public static HashSet<long> Numbers(string key)
    {
        var found = new HashSet<long>();
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.All(char.IsAsciiDigit) && w.Length <= 15) { found.Add(long.Parse(w)); continue; }
            if (Tens.TryGetValue(w, out var tens))
            {
                if (i + 1 < words.Length && Units.TryGetValue(words[i + 1], out var u) && u is > 0 and < 10) { found.Add(tens + u); i++; }
                else found.Add(tens);
                continue;
            }
            if (Units.TryGetValue(w, out var unit)) found.Add(unit);
        }
        return found;
    }
}
