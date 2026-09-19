using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>Картинка, звук чи відео при запитанні (або при відповіді). Файл лежить у теці пакета, ім'я — хеш вмісту.</summary>
public sealed class SvoyaMedia
{
    public const string Image = "image", Audio = "audio", Video = "video";
    public static readonly string[] Kinds = [Image, Audio, Video];

    public string Kind { get; set; } = Image;
    public string File { get; set; } = "";
    /// <summary>Скільки звучить (звук/відео). Для картинки — 0.</summary>
    public int Seconds { get; set; }
}

/// <summary>Одна клітинка поля.</summary>
public sealed class SvoyaQuestion
{
    public const string Normal = "normal", Cat = "cat", Auction = "auction";
    public static readonly string[] Types = [Normal, Cat, Auction];

    public int Price { get; set; }
    public string Type { get; set; } = Normal;
    public string Text { get; set; } = "";
    public SvoyaMedia? Media { get; set; }
    public string Answer { get; set; } = "";
    /// <summary>Що ще зараховується, крім <see cref="Answer"/>.</summary>
    public List<string> Accept { get; set; } = [];
    public SvoyaMedia? AnswerMedia { get; set; }
    /// <summary>Ведучий каже після відповіді («Перша книга сучасною українською»).</summary>
    public string? Comment { get; set; }
    /// <summary>Кіт у мішку: ціна з пакета; null — той, кому передали, обирає між найменшою й найбільшою ціною раунду.</summary>
    public int? CatPrice { get; set; }

    /// <summary>Усе, що зараховується: основна відповідь і варіанти.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Answers => [Answer, .. Accept];
}

public sealed class SvoyaTheme
{
    public string Name { get; set; } = "";
    public List<SvoyaQuestion> Questions { get; set; } = [];
}

public sealed class SvoyaRound
{
    public const string Normal = "normal", Final = "final";

    public string Name { get; set; } = "";
    public string Type { get; set; } = Normal;
    public List<SvoyaTheme> Themes { get; set; } = [];

    [JsonIgnore]
    public bool IsFinal => Type == Final;
}

/// <summary>
/// Пакет «Своєї гри» (specs/svoya.md §2): раунди → теми → запитання. Живе цілим JSON у <see cref="SvoyaStore"/>,
/// вбудовані — файлами в <c>data/svoya/builtin</c>.
///
/// Перевірок дві. <see cref="Check"/> — жорсткі межі (довжини, кількості, імена файлів): без них пакет не
/// зберігається взагалі. <see cref="Validate"/> — чи в нього можна грати (у кожного запитання є відповідь,
/// ціни різні, фінал останній…). Чернетка, яка ще не грається, зберігається спокійно — конструктор
/// автозберігає її щосекунди, і вимагати від порожнього шаблону 75 готових відповідей було б знущанням.
/// </summary>
public sealed class SvoyaPack
{
    public const string Builtin = "builtin", User = "user", Siq = "siq";
    public const int NameMax = 60, TextMax = 600, AnswerMax = 120, CommentMax = 600, DescriptionMax = 300;
    public const int MaxRounds = 10, MaxThemes = 10, MaxQuestions = 8, MinQuestions = 1, MaxAccept = 12, MinFinalThemes = 2;
    public const int PriceMax = 100_000;
    /// <summary>Крок цін класичного шаблону: раунд N — N×крок, 2N×крок … 5N×крок.</summary>
    public const int PriceStep = 100;

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Author { get; set; } = "";
    public string AuthorKey { get; set; } = "";
    public bool Public { get; set; }
    public string Source { get; set; } = User;
    public List<SvoyaRound> Rounds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON → пакет. Кривий JSON — null, а не виняток: пакет приходить із браузера і з файлів.</summary>
    public static SvoyaPack? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<SvoyaPack>(json, Json); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public SvoyaPack Clone() => Parse(ToJson())!;

    /// <summary>Скільки всього запитань (з фіналом).</summary>
    [JsonIgnore]
    public int QuestionCount => Rounds.Sum(r => r.Themes.Sum(t => t.Questions.Count));

    /// <summary>Усі медіа-файли, на які пакет посилається.</summary>
    public IEnumerable<string> MediaFiles() => Rounds.SelectMany(r => r.Themes).SelectMany(t => t.Questions)
        .SelectMany(q => new[] { q.Media, q.AnswerMedia }).Where(m => m is not null && m.File.Length > 0)
        .Select(m => m!.File).Distinct(StringComparer.Ordinal);

    /// <summary>Ціни для раунду з номером <paramref name="round"/> (від 1): крок × раунд × (1..n).</summary>
    public static int[] Prices(int round, int count = 5) =>
        [.. Enumerable.Range(1, count).Select(i => PriceStep * Math.Max(1, round) * i)];

    /// <summary>
    /// Класичний шаблон для «Новий пакет»: три раунди по 5 тем × 5 запитань (100–500 / 200–1000 / 300–1500)
    /// і фінал на 5 тем. Назви тем і тексти порожні — це чернетка, її доповнює автор.
    /// </summary>
    public static SvoyaPack Classic(string title = "Новий пакет")
    {
        string[] names = ["Перший раунд", "Другий раунд", "Третій раунд"];
        var pack = new SvoyaPack { Title = title };
        for (var r = 0; r < names.Length; r++)
            pack.Rounds.Add(new SvoyaRound
            {
                Name = names[r],
                Themes = [.. Enumerable.Range(1, 5).Select(t => new SvoyaTheme
                {
                    Name = "",
                    Questions = [.. Prices(r + 1).Select(p => new SvoyaQuestion { Price = p })],
                })],
            });
        pack.Rounds.Add(new SvoyaRound
        {
            Name = "Фінал",
            Type = SvoyaRound.Final,
            Themes = [.. Enumerable.Range(1, 5).Select(_ => new SvoyaTheme { Questions = [new SvoyaQuestion()] })],
        });
        return pack;
    }

    /// <summary>
    /// Прибрати сміття, яке приносить форма: краї рядків, порожні варіанти, невідомі типи, null-списки. Після
    /// цього і <see cref="Check"/>, і <see cref="Validate"/> працюють з уже чистим пакетом.
    /// </summary>
    public SvoyaPack Normalize()
    {
        Title = Clean(Title);
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        Rounds ??= [];
        foreach (var r in Rounds)
        {
            r.Name = Clean(r.Name);
            r.Type = r.Type?.Trim().ToLowerInvariant() == SvoyaRound.Final ? SvoyaRound.Final : SvoyaRound.Normal;
            r.Themes ??= [];
            foreach (var t in r.Themes)
            {
                t.Name = Clean(t.Name);
                t.Questions ??= [];
                foreach (var q in t.Questions)
                {
                    q.Text = (q.Text ?? "").Trim();
                    q.Answer = Clean(q.Answer);
                    q.Accept = [.. (q.Accept ?? []).Select(Clean).Where(a => a.Length > 0 && a != q.Answer).Distinct()];
                    q.Comment = string.IsNullOrWhiteSpace(q.Comment) ? null : q.Comment.Trim();
                    q.Type = q.Type?.Trim().ToLowerInvariant() is { } type && SvoyaQuestion.Types.Contains(type) ? type : SvoyaQuestion.Normal;
                    if (r.IsFinal) q.Type = SvoyaQuestion.Normal;
                    if (q.Type != SvoyaQuestion.Cat) q.CatPrice = null;
                    q.Media = CleanMedia(q.Media);
                    q.AnswerMedia = CleanMedia(q.AnswerMedia);
                }
            }
        }
        return this;
    }

    static string Clean(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static SvoyaMedia? CleanMedia(SvoyaMedia? m)
    {
        if (m is null || string.IsNullOrWhiteSpace(m.File)) return null;
        m.File = m.File.Trim();
        m.Kind = (m.Kind ?? "").Trim().ToLowerInvariant();
        m.Seconds = m.Kind == SvoyaMedia.Image ? 0 : Math.Max(0, m.Seconds);
        return m;
    }

    /// <summary>Ім'я медіа-файла: лише хеш і розширення, ніяких тек і «..».</summary>
    static readonly Regex MediaName = new(@"^[a-z0-9]{8,64}\.[a-z0-9]{2,5}$", RegexOptions.Compiled);

    /// <summary>
    /// Жорсткі межі, без яких пакет не зберігається навіть чернеткою: розміри, довжини, імена файлів. Кожна
    /// помилка — окремий рядок українською, з адресою («Раунд 2, тема «Кіно», 400: …»).
    /// </summary>
    public List<string> Check()
    {
        var errors = new List<string>();
        if (Title.Length == 0) errors.Add("Пакет без назви");
        if (Title.Length > NameMax) errors.Add($"Назва пакета довша за {NameMax} знаків");
        if ((Description?.Length ?? 0) > DescriptionMax) errors.Add($"Опис довший за {DescriptionMax} знаків");
        if (Rounds.Count > MaxRounds) errors.Add($"Раундів більше за {MaxRounds}");
        for (var ri = 0; ri < Rounds.Count; ri++)
        {
            var r = Rounds[ri];
            var rw = RoundWhere(ri);
            if (r.Name.Length > NameMax) errors.Add($"{rw}: назва довша за {NameMax} знаків");
            if (r.Themes.Count > MaxThemes) errors.Add($"{rw}: тем більше за {MaxThemes}");
            for (var ti = 0; ti < r.Themes.Count; ti++)
            {
                var t = r.Themes[ti];
                var tw = ThemeWhere(ri, ti);
                if (t.Name.Length > NameMax) errors.Add($"{tw}: назва довша за {NameMax} знаків");
                if (t.Questions.Count > MaxQuestions) errors.Add($"{tw}: запитань більше за {MaxQuestions}");
                foreach (var (q, qi) in t.Questions.Select((q, i) => (q, i)))
                {
                    var qw = QuestionWhere(ri, ti, qi);
                    if (q.Price < 0 || q.Price > PriceMax) errors.Add($"{qw}: ціна поза межами 0…{PriceMax}");
                    if (q.CatPrice is < 0 or > PriceMax) errors.Add($"{qw}: ціна кота поза межами 0…{PriceMax}");
                    if (q.Text.Length > TextMax) errors.Add($"{qw}: текст довший за {TextMax} знаків");
                    if (q.Answer.Length > AnswerMax) errors.Add($"{qw}: відповідь довша за {AnswerMax} знаків");
                    if (q.Accept.Count > MaxAccept) errors.Add($"{qw}: варіантів відповіді більше за {MaxAccept}");
                    if (q.Accept.Any(a => a.Length > AnswerMax)) errors.Add($"{qw}: варіант відповіді довший за {AnswerMax} знаків");
                    if ((q.Comment?.Length ?? 0) > CommentMax) errors.Add($"{qw}: коментар довший за {CommentMax} знаків");
                    foreach (var m in new[] { q.Media, q.AnswerMedia })
                    {
                        if (m is null) continue;
                        if (!SvoyaMedia.Kinds.Contains(m.Kind)) errors.Add($"{qw}: невідомий тип медіа «{m.Kind}»");
                        if (!MediaName.IsMatch(m.File)) errors.Add($"{qw}: дивне ім'я медіа-файла");
                    }
                }
            }
        }
        return errors;
    }

    /// <summary>
    /// Чи можна в це грати (specs/svoya.md §2). <paramref name="mediaBytes"/> — розмір файла в теці пакета
    /// або null, якщо його нема; не передали — файли не перевіряються (тести моделі, пакет без медіа).
    /// </summary>
    public List<string> Validate(Func<string, long?>? mediaBytes = null, long maxMediaBytes = long.MaxValue)
    {
        var errors = Check();
        if (Rounds.Count == 0) errors.Add("Нема жодного раунду");
        var finals = Rounds.Select((r, i) => (r, i)).Where(x => x.r.IsFinal).Select(x => x.i).ToList();
        if (finals.Count > 1) errors.Add("Фінал може бути лише один");
        if (finals.Count == 1 && finals[0] != Rounds.Count - 1) errors.Add("Фінал має бути останнім раундом");
        if (Rounds.Count > 0 && Rounds.All(r => r.IsFinal)) errors.Add("Крім фіналу, потрібен хоч один звичайний раунд");

        for (var ri = 0; ri < Rounds.Count; ri++)
        {
            var r = Rounds[ri];
            var rw = RoundWhere(ri);
            if (r.IsFinal)
            {
                if (r.Themes.Count < MinFinalThemes) errors.Add($"{rw}: у фіналі потрібно щонайменше {MinFinalThemes} теми");
            }
            else if (r.Themes.Count == 0) errors.Add($"{rw}: нема жодної теми");

            for (var ti = 0; ti < r.Themes.Count; ti++)
            {
                var t = r.Themes[ti];
                var tw = ThemeWhere(ri, ti);
                if (t.Name.Length == 0) errors.Add($"{tw}: тема без назви");
                if (r.IsFinal)
                {
                    if (t.Questions.Count != 1) errors.Add($"{tw}: у фінальній темі має бути рівно одне запитання");
                }
                else
                {
                    if (t.Questions.Count < MinQuestions) errors.Add($"{tw}: нема жодного запитання");
                    var seen = new HashSet<int>();
                    foreach (var q in t.Questions)
                    {
                        if (q.Price <= 0) continue;
                        if (!seen.Add(q.Price)) errors.Add($"{tw}: ціна {q.Price} двічі");
                    }
                }

                for (var qi = 0; qi < t.Questions.Count; qi++)
                {
                    var q = t.Questions[qi];
                    var qw = QuestionWhere(ri, ti, qi);
                    if (!r.IsFinal && q.Price <= 0) errors.Add($"{qw}: ціна має бути більшою за нуль");
                    if (q.Text.Length == 0 && q.Media is null) errors.Add($"{qw}: нема ні тексту, ні медіа");
                    if (q.Answer.Length == 0) errors.Add($"{qw}: нема відповіді");
                    if (q.Type == SvoyaQuestion.Cat && q.CatPrice is 0) errors.Add($"{qw}: ціна кота має бути більшою за нуль");
                }
            }
        }

        if (mediaBytes is not null)
        {
            long total = 0;
            foreach (var file in MediaFiles())
            {
                if (mediaBytes(file) is { } size) total += size;
                else errors.Add($"Медіа-файла {file} нема — завантаж його ще раз");
            }
            if (total > maxMediaBytes) errors.Add($"Медіа пакета більше за {maxMediaBytes / (1024 * 1024)} МБ");
        }
        return errors;
    }

    string RoundWhere(int ri) => Rounds[ri].IsFinal ? "Фінал" : $"Раунд {ri + 1}";

    string ThemeWhere(int ri, int ti)
    {
        var name = Rounds[ri].Themes[ti].Name;
        return $"{RoundWhere(ri)}, тема {(name.Length > 0 ? $"«{name}»" : (ti + 1).ToString())}";
    }

    string QuestionWhere(int ri, int ti, int qi)
    {
        var q = Rounds[ri].Themes[ti].Questions[qi];
        return Rounds[ri].IsFinal ? ThemeWhere(ri, ti) : $"{ThemeWhere(ri, ti)}, {(q.Price > 0 ? q.Price.ToString() : $"запитання {qi + 1}")}";
    }
}
