using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Категорії пісень у «Вгадай мелодію» (опція столу <c>cat</c>, можна кілька). Три з радіо — українське й решта
/// (<see cref="MelodyLanguage"/>) з того, що звучало, та <see cref="Fav"/> — те, що крутиться найчастіше або
/// лайкнуте, — і добірки з <see cref="MelodyClassics"/>, які гра докачує сама. <see cref="All"/> — «усе»: типове
/// значення multi-опції, каркас зводить до нього порожній вибір.
/// </summary>
public static class MelodyCategories
{
    public const string All = "all", Ua = "ua", World = "world", Fav = "fav";

    /// <summary>Обидві мовні категорії з радіо — те, що гра брала до появи добірок.</summary>
    public static readonly IReadOnlyList<string> Radio = [Ua, World];

    /// <summary>Коди, які не можна віддати добірці з файла.</summary>
    public static readonly IReadOnlyList<string> Reserved = [All, Ua, World, Fav];

    public static IReadOnlyList<(string Value, string Label)> Values(MelodyClassics classics) =>
        [(All, "Усе"), (Fav, "Ті, що ми слухаємо"), (Ua, "Українські з радіо"), (World, "Світові з радіо"), .. classics.Categories];

    /// <summary>Обрані категорії з рядка опції: «all», порожньо або самі невідомі — усі, крім самого «all».</summary>
    public static IReadOnlyList<string> Parse(string? value, MelodyClassics classics)
    {
        var known = Values(classics).Select(v => v.Value).Where(v => v != All).ToList();
        var picked = GameOption.Split(value);
        if (picked.Contains(All, StringComparer.Ordinal)) return known;
        var chosen = known.Where(k => picked.Contains(k, StringComparer.Ordinal)).ToList();
        return chosen.Count == 0 ? known : chosen;
    }
}

/// <summary>Пісня з добірки: що вгадувати. <see cref="Key"/> — та сама пісня в базі радіо (<see cref="SongKey"/>).</summary>
public sealed record ClassicEntry(string Category, string Artist, string Title)
{
    public string Key { get; } = SongKey.Of(Artist, Title);
}

/// <summary>
/// Добірки для «Вгадай мелодію» з <c>data/melody/classics.txt</c>: світова й українська класика, якої на радіо
/// могло й не бути. Рядок «[код] Назва» відкриває категорію (код латиницею, не з <see cref="MelodyCategories"/>),
/// далі «Виконавець — Назва» по пісні на рядок. Категорії стають значеннями опції столу в порядку файла.
/// </summary>
public sealed partial class MelodyClassics
{
    public const string FileName = "data/melody/classics.txt";

    static readonly Lazy<MelodyClassics> Cached = new(() => Load(Paths.Resolve(FileName)));
    public static MelodyClassics Default => Cached.Value;
    public static readonly MelodyClassics Empty = new([], []);

    public IReadOnlyList<(string Value, string Label)> Categories { get; }
    public IReadOnlyList<ClassicEntry> Entries { get; }

    MelodyClassics(IReadOnlyList<(string, string)> categories, IReadOnlyList<ClassicEntry> entries)
    {
        Categories = categories;
        Entries = entries;
    }

    public IEnumerable<ClassicEntry> In(string category) => Entries.Where(e => e.Category == category);

    public static MelodyClassics Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Empty; }
    }

    [GeneratedRegex(@"^\[([a-z0-9_-]+)\]\s*(.+?)\s*$")]
    private static partial Regex Header();

    static readonly string[] Dashes = [" — ", " – ", " - "];

    public static MelodyClassics Parse(IEnumerable<string> lines)
    {
        var categories = new List<(string, string)>();
        var entries = new List<ClassicEntry>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (Header().Match(line) is { Success: true } h)
            {
                var code = h.Groups[1].Value;
                if (MelodyCategories.Reserved.Contains(code, StringComparer.Ordinal) || categories.Any(c => c.Item1 == code)) { current = null; continue; }
                categories.Add((code, h.Groups[2].Value));
                current = code;
                continue;
            }
            if (current is null) continue;
            var at = -1;
            var sep = "";
            foreach (var d in Dashes)
            {
                var i = line.IndexOf(d, StringComparison.Ordinal);
                if (i > 0 && (at < 0 || i < at)) { at = i; sep = d; }
            }
            if (at < 0) continue;
            var artist = line[..at].Trim();
            var title = line[(at + sep.Length)..].Trim();
            if (artist.Length == 0 || title.Length == 0) continue;
            var e = new ClassicEntry(current, artist, title);
            if (e.Key.Length > 0 && keys.Add(e.Key)) entries.Add(e);
        }
        return new MelodyClassics(categories, entries);
    }
}
