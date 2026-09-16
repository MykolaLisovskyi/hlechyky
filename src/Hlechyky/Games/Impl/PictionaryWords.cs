namespace Hlechyky.Games.Impl;

/// <summary>
/// Словник Піктіонарі з <c>data/pictionary/words.txt</c>: прості слова, які можна намалювати за хвилину,
/// розкладені по темах. Читається один раз на процес. Ніколи не кидає: нема файла — порожній словник, і гра
/// чесно скаже «нема слів» замість того, щоб упасти.
/// <para>
/// Тести підкладають свій екземпляр через <c>Ctx.Services</c>; у проді там нічого нема, і гра бере <see cref="Default"/>.
/// </para>
/// </summary>
public sealed class PictionaryWords
{
    /// <summary>Де лежить словник відносно кореня репозиторію.</summary>
    public const string FileName = "data/pictionary/words.txt";

    /// <summary>«Усі теми» в опції столу.</summary>
    public const string AnyTopic = "all";

    /// <summary>
    /// Теми так, як їх бачить господар при «+ Стіл». Ключі збігаються з «## ключ» у файлі; тема, якої тут
    /// нема, у файлі все одно працює — просто окремо її не обрати, лише в «Усіх темах».
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Topics =
    [
        (AnyTopic, "Усі теми"),
        ("animals", "Тварини"),
        ("food", "Їжа й напої"),
        ("home", "Дім і речі"),
        ("nature", "Природа й погода"),
        ("transport", "Транспорт"),
        ("people", "Люди й професії"),
        ("clothes", "Одяг і прикраси"),
        ("body", "Тіло"),
        ("sport", "Спорт, ігри й дозвілля"),
        ("music", "Музика й мистецтво"),
        ("places", "Місця й будівлі"),
        ("things", "Інструменти, техніка й різне"),
        ("holidays", "Свята, казки й фантазія"),
    ];

    static readonly Lazy<PictionaryWords> Cached = new(() => Load(Paths.Resolve(FileName)));

    /// <summary>Бойовий словник із репозиторію.</summary>
    public static PictionaryWords Default => Cached.Value;

    /// <summary>Тема → слова в порядку файла, без повторів у межах усього словника.</summary>
    readonly Dictionary<string, List<string>> _byTopic = new(StringComparer.Ordinal);

    public PictionaryWords(IEnumerable<(string Topic, string Word)> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (topic, raw) in entries)
        {
            var word = Clean(raw);
            if (word is null || !seen.Add(Normalize(word))) continue;
            if (!_byTopic.TryGetValue(topic, out var list)) _byTopic[topic] = list = [];
            list.Add(word);
        }
    }

    /// <summary>Скільки слів усього.</summary>
    public int Count => _byTopic.Values.Sum(l => l.Count);

    /// <summary>Скільки слів у темі (для тестів і «нема слів у цій темі»).</summary>
    public int CountIn(string topic) => _byTopic.TryGetValue(topic, out var l) ? l.Count : 0;

    /// <summary>Прочитати словник із файла.</summary>
    public static PictionaryWords Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new PictionaryWords([]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new PictionaryWords([]); }
    }

    /// <summary>Текст у форматі файла: «## ключ | Назва» відкриває тему, «#» — коментар, решта — слова.</summary>
    public static PictionaryWords Parse(string text)
    {
        var entries = new List<(string, string)>();
        var topic = "misc";
        foreach (var line in text.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0) continue;
            if (s.StartsWith("##", StringComparison.Ordinal))
            {
                var head = s[2..].Split('|')[0].Trim();
                if (head.Length > 0) topic = head;
                continue;
            }
            if (s[0] == '#') continue;
            entries.Add((topic, s));
        }
        return new PictionaryWords(entries);
    }

    /// <summary>
    /// <paramref name="count"/> різних слів з обраних тем (null — з усіх), яких ще не було в цій партії.
    /// Свіжі скінчились — беремо з усіх, повтор кращий за порожній вибір.
    /// </summary>
    public string[] Pick(Random rng, IReadOnlySet<string>? topics, IReadOnlySet<string> used, int count)
    {
        var pool = _byTopic.Where(kv => topics is null || topics.Contains(kv.Key)).SelectMany(kv => kv.Value).ToList();
        if (pool.Count == 0) return [];
        var fresh = pool.Where(w => !used.Contains(Normalize(w))).ToList();
        if (fresh.Count >= count) pool = fresh;

        var picked = new List<string>(count);
        // частковий Фішер-Єйтс: тільки стільки кроків, скільки слів треба
        for (var i = 0; i < pool.Count && picked.Count < count; i++)
        {
            var j = rng.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
            picked.Add(pool[i]);
        }
        return [.. picked];
    }

    /// <summary>Обрані теми з опції столу. null — усі (обрано «Усі теми» або жодної знайомої).</summary>
    public static IReadOnlySet<string>? ParseTopics(string? option)
    {
        var picked = GameOption.Split(option);
        if (picked.Contains(AnyTopic)) return null;
        var known = picked.Where(k => Topics.Any(t => t.Key == k && k != AnyTopic)).ToHashSet(StringComparer.Ordinal);
        return known.Count == 0 ? null : known;
    }

    /// <summary>Слово з файла: нижній регістр, один пробіл між частинами, типові апострофи зведені до «'».</summary>
    static string? Clean(string raw)
    {
        var s = string.Join(' ', raw.Trim().ToLowerInvariant()
            .Replace('’', '\'').Replace('ʼ', '\'').Replace('`', '\'')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length is >= 2 and <= 40 ? s : null;
    }

    /// <summary>
    /// Ключ для порівняння здогадки зі словом: регістр, «ё», усі апострофи, дефіси й пробіли не важать.
    /// «Хот дог», «хот-дог» і «ХОТДОГ» — одне й те саме.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            var c = ch == 'ё' ? 'е' : ch;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}
