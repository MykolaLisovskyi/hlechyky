namespace Hlechyky.Games.Impl;

/// <summary>
/// Числа словами для голосу: «плюс триста», «мінус тисяча двісті». edge-tts цифри читає, але в репліці
/// «Правильно, Оля, плюс 300» наголос і відмінок виходять випадкові — словами рівніше (specs/svoya.md §4).
/// </summary>
public static class NumberWords
{
    static readonly string[] Units = ["", "один", "два", "три", "чотири", "п'ять", "шість", "сім", "вісім", "дев'ять",
        "десять", "одинадцять", "дванадцять", "тринадцять", "чотирнадцять", "п'ятнадцять", "шістнадцять", "сімнадцять",
        "вісімнадцять", "дев'ятнадцять"];
    static readonly string[] Tens = ["", "", "двадцять", "тридцять", "сорок", "п'ятдесят", "шістдесят", "сімдесят", "вісімдесят", "дев'яносто"];
    static readonly string[] Hundreds = ["", "сто", "двісті", "триста", "чотириста", "п'ятсот", "шістсот", "сімсот", "вісімсот", "дев'ятсот"];

    /// <summary>0..999 999 словами; більше або менше — цифрами, як є.</summary>
    public static string Say(int n)
    {
        if (n == 0) return "нуль";
        if (n < 0) return "мінус " + Say(-n);
        if (n > 999_999) return n.ToString();
        var parts = new List<string>();
        var thousands = n / 1000;
        if (thousands == 1) parts.Add("тисяча");       // «плюс тисяча», а не «плюс одна тисяча»
        else if (thousands > 0)
        {
            parts.Add(Three(thousands, feminine: true));
            parts.Add(Thousand(thousands));
        }
        if (n % 1000 > 0) parts.Add(Three(n % 1000, feminine: false));
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    static string Three(int n, bool feminine)
    {
        var parts = new List<string> { Hundreds[n / 100] };
        var rest = n % 100;
        if (rest < 20) parts.Add(Unit(rest, feminine));
        else { parts.Add(Tens[rest / 10]); parts.Add(Unit(rest % 10, feminine)); }
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    static string Unit(int n, bool feminine) => feminine && n == 1 ? "одна" : feminine && n == 2 ? "дві" : Units[n];

    static string Thousand(int n)
    {
        var last2 = n % 100;
        var last = n % 10;
        if (last2 is >= 11 and <= 14) return "тисяч";
        return last == 1 ? "тисяча" : last is >= 2 and <= 4 ? "тисячі" : "тисяч";
    }
}

/// <summary>
/// Репліки ведучого. В одному місці — бо ті самі тексти й кажуть у грі, й озвучують наперед: різниця в одну
/// кому дала б інший хеш і репліку, якої в кеші нема.
/// </summary>
public static class SvoyaLines
{
    public static string Intro(SvoyaRound r) => $"{r.Name}. Теми: {string.Join(", ", r.Themes.Select(t => t.Name))}.";

    public static string Nobody(SvoyaQuestion q) => $"Правильна відповідь — {q.Answer}." + Tail(q);

    public static string Right(string? nick, int price, SvoyaQuestion? q) =>
        $"Правильно, {nick}! Плюс {NumberWords.Say(price)}." + (q is null ? "" : Tail(q));

    public static string Wrong(int price) => $"Ні. Мінус {NumberWords.Say(price)}.";

    static string Tail(SvoyaQuestion q) => q.Comment is { Length: > 0 } c ? " " + c : "";

    /// <summary>Усе, що ведучий скаже з цього пакета незалежно від гравців: вступи, запитання, розкриття.</summary>
    public static IEnumerable<string> All(SvoyaPack pack) => pack.Rounds.SelectMany(Round);

    public static IEnumerable<string> Round(SvoyaRound r)
    {
        yield return Intro(r);
        foreach (var q in r.Themes.SelectMany(t => t.Questions))
        {
            if (q.Text.Length > 0) yield return q.Text;
            yield return Nobody(q);
        }
    }
}

/// <summary>Голос гри поверх <see cref="TtsService"/>: адреса репліки — <c>/api/games/svoya/tts/&lt;хеш&gt;.mp3</c>.</summary>
public sealed class SvoyaVoice(TtsService tts) : ISvoyaVoice
{
    public const string UrlPrefix = "/api/games/svoya/tts/";

    public bool Enabled => tts.Enabled;

    public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false) => tts.Enqueue(voice, texts, urgent);

    public SvoyaClip? Ready(string voice, string text) =>
        tts.TryGet(voice, text) is { } clip ? new SvoyaClip(UrlPrefix + clip.Hash + ".mp3", clip.Seconds) : null;

    public static void Map(WebApplication app) =>
        app.MapGet(UrlPrefix + "{name}", (string name, HttpContext c, TtsService tts) =>
        {
            if (tts.FileOf(name) is not { } path) return Results.NotFound();
            // ім'я — хеш тексту й голосу: що за ним лежить, не зміниться ніколи
            c.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "audio/mpeg");
        });
}
