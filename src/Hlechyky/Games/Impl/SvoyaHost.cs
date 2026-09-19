using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Характер ведучого (specs/svoya.md §11): пули реплік на кожну ситуацію з <c>data/svoya/host.json</c>.
/// Гра обирає репліку навмання (<see cref="Pick"/>), не повторюючи попередню з того ж пулу, і підставляє
/// нік, суму, відповідь (<see cref="Fill"/>). Файла нема або він кривий — <see cref="Plain"/>: сухий
/// ведучий, як був до характеру, і гра нічого не помічає. Пул із помилкою в підстановках теж падає на Plain.
/// </summary>
public sealed partial class SvoyaPhrases
{
    /// <summary>Які підстановки дозволені в якому пулі. Пул, якого тут нема, — невідомий (попередження в лозі).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        ["intro"] = ["round", "themes"],
        ["introLead"] = ["round", "themes", "nick", "sum"],
        ["right"] = ["nick", "sum"],
        ["rightFirst"] = ["nick", "sum"],
        ["rightStreak"] = ["nick", "sum"],
        ["rightLead"] = ["nick", "sum"],
        ["rightBack"] = ["nick", "sum"],
        ["rightBig"] = ["nick", "sum"],
        ["wrong"] = ["nick", "sum"],
        ["wrongBig"] = ["nick", "sum"],
        ["wrongMinus"] = ["nick", "sum"],
        ["timeout"] = ["nick", "sum"],
        ["wrongLast"] = ["nick", "sum", "answer"],
        ["nobody"] = ["answer"],
        ["nobodyAgain"] = ["answer"],
        ["cat"] = [],
        ["auction"] = [],
        ["finalBets"] = ["theme"],
        ["finalRight"] = ["nick", "said", "sum"],
        ["finalWrong"] = ["nick", "said", "sum"],
        ["finalNone"] = ["nick", "sum"],
        ["endWin"] = ["nick", "sum"],
        ["endDraw"] = ["nicks"],
        ["endNobody"] = [],
    };

    /// <summary>Сухий ведучий: по одній репліці, як було до характеру. Кінець партії — мовчки.</summary>
    public static SvoyaPhrases Plain { get; } = new(new Dictionary<string, string[]>
    {
        ["intro"] = ["{round}. Теми: {themes}."],
        ["introLead"] = ["{round}. Теми: {themes}."],
        ["right"] = ["Правильно, {nick}! Плюс {sum}."],
        ["rightFirst"] = ["Правильно, {nick}! Плюс {sum}."],
        ["rightStreak"] = ["Правильно, {nick}! Плюс {sum}."],
        ["rightLead"] = ["Правильно, {nick}! Плюс {sum}."],
        ["rightBack"] = ["Правильно, {nick}! Плюс {sum}."],
        ["rightBig"] = ["Правильно, {nick}! Плюс {sum}."],
        ["wrong"] = ["Ні. Мінус {sum}."],
        ["wrongBig"] = ["Ні. Мінус {sum}."],
        ["wrongMinus"] = ["Ні. Мінус {sum}."],
        ["timeout"] = ["Ні. Мінус {sum}."],
        ["wrongLast"] = ["Правильна відповідь — {answer}."],
        ["nobody"] = ["Правильна відповідь — {answer}."],
        ["nobodyAgain"] = ["Правильна відповідь — {answer}."],
        ["cat"] = ["Кіт у мішку!"],
        ["auction"] = ["Аукціон!"],
        ["finalBets"] = ["Тема фіналу — {theme}. Робіть ставки."],
        ["finalRight"] = ["{nick}: {said}. Правильно! Плюс {sum}."],
        ["finalWrong"] = ["{nick}: {said}. Ні. Мінус {sum}."],
        ["finalNone"] = ["{nick}: без відповіді. Ні. Мінус {sum}."],
        ["endWin"] = [],
        ["endDraw"] = [],
        ["endNobody"] = [],
    });

    readonly Dictionary<string, string[]> _pools;

    SvoyaPhrases(Dictionary<string, string[]> pools) => _pools = pools;

    /// <summary>Скільки реплік у пулі (0 — нема такого).</summary>
    public int Count(string key) => _pools.TryGetValue(key, out var p) ? p.Length : 0;

    public IReadOnlyList<string> Pool(string key) => _pools.TryGetValue(key, out var p) ? p : [];

    /// <summary>Усі репліки без підстановок — їх можна озвучити наперед, ще до партії.</summary>
    public IEnumerable<string> Pure() => _pools.Values.SelectMany(p => p).Where(t => !Placeholder().IsMatch(t));

    /// <summary>
    /// Репліка з пулу навмання, не та сама, що минулого разу (<paramref name="last"/> — пам'ять гри по ключах).
    /// Порожній пул — сухий варіант із <see cref="Plain"/>; нема й там — порожній рядок (ведучий мовчить).
    /// </summary>
    public string Pick(string key, Random rng, IDictionary<string, int> last)
    {
        var pool = Count(key) > 0 ? _pools[key] : Plain._pools.GetValueOrDefault(key, []);
        if (pool.Length == 0) return "";
        if (pool.Length == 1) return pool[0];
        int i;
        if (last.TryGetValue(key, out var prev) && prev < pool.Length)
        {
            i = rng.Next(pool.Length - 1);
            if (i >= prev) i++;
        }
        else i = rng.Next(pool.Length);
        last[key] = i;
        return pool[i];
    }

    public static string Fill(string template, params (string Key, string Value)[] vars)
    {
        foreach (var (k, v) in vars) template = template.Replace("{" + k + "}", v, StringComparison.Ordinal);
        return template;
    }

    /// <summary>Пули з JSON: <c>{ "right": ["…", "…"], … }</c>; ключі з підкресленням — коментарі автора.</summary>
    public static (SvoyaPhrases Phrases, IReadOnlyList<string> Problems) Parse(string json)
    {
        var problems = new List<string>();
        var pools = new Dictionary<string, string[]>();
        Dictionary<string, JsonElement>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json); }
        catch (JsonException e) { return (Plain, [$"кривий JSON: {e.Message}"]); }
        if (raw is null) return (Plain, ["порожній файл"]);
        foreach (var (key, el) in raw)
        {
            if (key.StartsWith('_')) continue;
            if (!Allowed.TryGetValue(key, out var allowed)) { problems.Add($"{key}: невідомий пул"); continue; }
            if (el.ValueKind != JsonValueKind.Array) { problems.Add($"{key}: має бути список реплік"); continue; }
            var lines = el.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToArray();
            var bad = lines.SelectMany(l => Placeholder().Matches(l).Select(m => m.Groups[1].Value)).Where(p => !allowed.Contains(p)).Distinct().ToList();
            if (bad.Count > 0) { problems.Add($"{key}: невідомі підстановки {string.Join(", ", bad.Select(b => "{" + b + "}"))}"); continue; }
            pools[key] = lines;
        }
        foreach (var key in Allowed.Keys.Where(k => !pools.ContainsKey(k) && !raw.ContainsKey(k)))
            if (Plain.Count(key) > 0) problems.Add($"{key}: пулу нема — ведучий тут сухий");
        return (new SvoyaPhrases(pools), problems);
    }

    /// <summary>Прочитати файл; нема або кривий — <see cref="Plain"/> і рядок у лозі.</summary>
    public static SvoyaPhrases Load(string path, ILogger? log = null)
    {
        if (!File.Exists(path))
        {
            log?.LogWarning("своя гра: книги фраз ведучого {Path} нема — ведучий сухий", path);
            return Plain;
        }
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException e) { log?.LogWarning("своя гра: книгу фраз {Path} не прочитати: {Err}", path, e.Message); return Plain; }
        var (phrases, problems) = Parse(text);
        foreach (var p in problems) log?.LogWarning("своя гра: книга фраз ведучого: {Problem}", p);
        return phrases;
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
