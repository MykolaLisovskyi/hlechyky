using System.Reflection;

namespace Hlechyky.Games;

/// <summary>Опція гри так, як її бачить лобі: пари [значення, підпис], що обрано типово і чи можна обрати кілька.</summary>
public sealed record CatalogOption(string Key, string Label, IReadOnlyList<string[]> Values, string Default, bool Multi);

/// <summary>Рядок каталогу <c>GET /api/games/catalog</c> (PROTOCOL §2).</summary>
public sealed record CatalogGame(
    string Id,
    string Title,
    string Accusative,
    string Group,
    int MinPlayers,
    int MaxPlayers,
    int TickMs,
    string Start,
    bool Hidden,
    bool Private,
    bool Rated,
    IReadOnlyList<CatalogOption> Options,
    string Hint,
    bool HasCss,
    bool Daily,
    /// <summary>Ім'я файла модуля без розширення: завантажувач бере <c>/games/{Module}.js</c>. Кілька ігор можуть ділити один.</summary>
    string Module);

/// <summary>Відповідь каталогу: ігри й дозволені ставки.</summary>
public sealed record Catalog(IReadOnlyList<CatalogGame> Games, IReadOnlyList<int> Stakes);

/// <summary>
/// Усі ігри збірки. Нова гра = новий клас-нащадок <see cref="Game"/> з публічним конструктором без
/// параметрів; сюди її вписувати не треба, реєстр знайде сам (ARCHITECTURE §4.5). Скан робиться один раз
/// на процес: рефлексія недешева, а набір класів між тестами не міняється.
/// </summary>
public sealed class Registry
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, IReadOnlyList<(Type Type, GameInfo Info)>> Cache = new();

    readonly Dictionary<string, (Type Type, GameInfo Info)> _byId;

    /// <param name="log">Лог для попереджень про ігри без клієнтського модуля; null — мовчки.</param>
    /// <param name="extra">Додаткові збірки з іграми. Проду не треба; тести кладуть сюди свої заглушки.</param>
    public Registry(ILogger<Registry>? log = null, params Assembly[] extra)
    {
        var found = Scan(typeof(Game).Assembly).Concat(extra.SelectMany(Scan)).ToList();
        EnsureUniqueIds(found);
        _byId = found.ToDictionary(g => g.Info.Id, g => g, StringComparer.Ordinal);
        var ordered = found.OrderBy(g => g.Info.Group).ThenBy(g => g.Info.Id, StringComparer.Ordinal).ToList();
        Games = [.. ordered.Select(g => g.Info)];
        Catalog = [.. ordered.Select(g => Describe(g.Type, g.Info))];
        foreach (var info in Games)
            if (log is not null && !File.Exists(Paths.Resolve($"web/games/{info.Module}.js")))
                log.LogWarning("гра {Id}: нема web/games/{Module}.js, у лобі вона так і писатиме «завантажую…»", info.Id, info.Module);
    }

    /// <summary>Паспорти всіх ігор, у порядку вкладок лобі.</summary>
    public IReadOnlyList<GameInfo> Games { get; }

    /// <summary>Готовий каталог для лобі (перерахований на старті, далі не міняється).</summary>
    public IReadOnlyList<CatalogGame> Catalog { get; }

    public bool Has(string id) => _byId.ContainsKey(id);

    public GameInfo? Info(string id) => _byId.TryGetValue(id, out var g) ? g.Info : null;

    /// <summary>Новий екземпляр гри під нову кімнату; null — такої гри нема.</summary>
    public Game? Create(string id) =>
        _byId.TryGetValue(id, out var g) ? (Game)Activator.CreateInstance(g.Type)! : null;

    static CatalogGame Describe(Type type, GameInfo i) => new(
        i.Id, i.Title, i.Accusative, Camel(i.Group.ToString()), i.MinPlayers, i.MaxPlayers, i.TickMs,
        Camel(i.Start.ToString()), i.Hidden, i.Private, i.Rated,
        [.. (i.Options ?? []).Select(o => new CatalogOption(o.Key, o.Label, [.. o.Values.Select(v => new[] { v.Value, v.Label })], o.Default, o.Multi))],
        i.Hint,
        File.Exists(Paths.Resolve($"web/games/{i.Module}.css")),
        typeof(IDailyGame).IsAssignableFrom(type),
        i.Module);

    /// <summary>«WhenFull» → «whenFull», «Board» → «board»: на дроті camelCase, як і решта JSON.</summary>
    static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>Id — це ще й ім'я модуля <c>web/games/&lt;id&gt;.js</c>: двох однакових бути не може, і дізнатись про це треба на старті.</summary>
    public static void EnsureUniqueIds(IReadOnlyList<(Type Type, GameInfo Info)> games)
    {
        var dup = games.GroupBy(g => g.Info.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new InvalidOperationException(
                $"дві гри з однаковим Id «{dup.Key}»: {string.Join(", ", dup.Select(g => g.Type.FullName))}");
    }

    static IReadOnlyList<(Type Type, GameInfo Info)> Scan(Assembly assembly) => Cache.GetOrAdd(assembly, a =>
    {
        var found = new List<(Type Type, GameInfo Info)>();
        foreach (var type in a.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass || !typeof(Game).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is null) continue;
            var sample = (Game)Activator.CreateInstance(type)!;
            found.Add((type, sample.Info));
        }
        return found;
    });
}
