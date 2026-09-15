using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одне запитання банку «Скільки?». Або <see cref="A"/> — перевірене число з файла, або <see cref="Dyn"/> —
/// ключ, за яким відповідь щоразу рахує <see cref="SkilkyStats"/> із бази радіо. Обидва разом не бувають.
/// </summary>
public sealed class SkilkyQuestion
{
    /// <summary>Текст запитання, українською, з «?» на кінці.</summary>
    public string Q { get; init; } = "";
    /// <summary>Правильна відповідь. null — запитання динамічне.</summary>
    public double? A { get; init; }
    /// <summary>Ключ статистики радіо («plays7d»), якщо відповідь рахується на льоту.</summary>
    public string? Dyn { get; init; }
    /// <summary>Одиниця виміру для підпису під полем вводу («м», «років»); null — просто число.</summary>
    public string? Unit { get; init; }
    /// <summary>Звідки число, якщо його варто чимось підперти. У гру не потрапляє — це записка для людей.</summary>
    public string? Src { get; init; }
    /// <summary>Тема для налаштування столу — один із ключів <see cref="SkilkyTopics.All"/>.</summary>
    public string? Topic { get; init; }

    public bool IsDynamic => !string.IsNullOrWhiteSpace(Dyn);

    string? _key;

    /// <summary>
    /// Стабільний короткий ключ запитання для пам'яті «хто вже бачив» (<see cref="SkilkySeen"/>): 16 hex-знаків
    /// SHA-256 від тексту. Від тексту, а не від номера в файлі: банк дописується й сортується, а переписане
    /// запитання й так варто вважати новим.
    /// </summary>
    public string Key => _key ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Q.Trim())))[..16];
}

/// <summary>
/// Теми банку. Господар обирає одну, кілька або «Усі теми». «radio» — динамічні запитання про наше радіо:
/// окремою темою їх не обирають, вони йдуть лише в «Усі теми».
/// </summary>
public static class SkilkyTopics
{
    public const string Any = "all";
    public const string Radio = "radio";

    /// <summary>Те, що бачить господар у списку при «+ Стіл», у тому самому порядку.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Any, "Усі теми"),
        ("ukraine", "Україна"),
        ("culture", "Музика, кіно й ігри"),
        ("science", "Наука, природа й тіло"),
        ("world", "Світ, спорт і побут"),
        ("tech", "IT, техніка й історія"),
    ];

    /// <summary>
    /// Обрані теми з опції столу («ukraine,science»). null — усі теми разом із радіо: так і коли обрано «Усі
    /// теми», і коли зі знайомих тем не лишилось жодної.
    /// </summary>
    public static IReadOnlySet<string>? Parse(string? option)
    {
        var picked = GameOption.Split(option);
        if (picked.Contains(Any)) return null;
        var known = picked.Where(k => All.Any(t => t.Key == k)).ToHashSet(StringComparer.Ordinal);
        return known.Count == 0 ? null : known;
    }

    /// <summary>Чи годиться запитання для обраних тем (null — годиться будь-яке).</summary>
    public static bool Fits(SkilkyQuestion q, IReadOnlySet<string>? topics) =>
        topics is null || q.Topic is { } t && topics.Contains(t);
}

/// <summary>
/// Банк запитань із <c>data/questions/skilky.json</c>. Читається один раз на процес: файл маленький, а
/// кімнат «Скільки?» може бути кілька одночасно, і кожній перечитувати диск ні до чого.
/// Ніколи не кидає: нема файла або він битий — банк порожній, а гра про це чесно скаже гравцям.
/// </summary>
public static class SkilkyBank
{
    /// <summary>Де лежить файл банку відносно кореня репозиторію.</summary>
    public const string FileName = "data/questions/skilky.json";

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };
    static readonly Lazy<IReadOnlyList<SkilkyQuestion>> Cached = new(() => Load(Paths.Resolve(FileName)));

    /// <summary>Усі придатні запитання банку.</summary>
    public static IReadOnlyList<SkilkyQuestion> All => Cached.Value;

    /// <summary>
    /// Прочитати банк із конкретного файла (тести читають той самий, що й прод, але явно). Криві записи —
    /// без тексту або без жодної відповіді — просто відкидаємо: одна помилка в JSON не має гасити гру.
    /// </summary>
    public static IReadOnlyList<SkilkyQuestion> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            var list = Parse(json);
            return [.. list.Where(q => !string.IsNullOrWhiteSpace(q.Q) && (q.A is not null || q.IsDynamic))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Корінь файла — або об'єкт із полем <c>questions</c>, або одразу масив.</summary>
    static List<SkilkyQuestion> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var q) ? q
            : default;
        if (array.ValueKind != JsonValueKind.Array) return [];
        return JsonSerializer.Deserialize<List<SkilkyQuestion>>(array.GetRawText(), Options) ?? [];
    }
}
