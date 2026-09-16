namespace Hlechyky;

/// <summary>
/// Команди чату — те, що починається зі скісної. Кидає сервер, а не браузер, щоб результат був
/// один для всіх і його не можна було підкрутити в консолі. Нова команда — гілка в Run і рядок
/// у COMMANDS на фронті.
/// </summary>
public static class ChatCommands
{
    /// <summary>
    /// Error бачить лише той, хто набрав; Text іде в чат усім. Непорожній <see cref="Rooms"/> — відповідь
    /// особиста (/столи): хаб шле її самому питальнику й у базу не кладе.
    /// </summary>
    public sealed record Result(string? Error = null, string? Text = null, string Kind = "chat", IReadOnlyList<string>? Rooms = null);

    /// <summary>
    /// <paramref name="live"/> — id живих столів (спершу ті, куди ще можна сісти). Null — звідси столів не
    /// видно: так /столи виглядає для агента, у якого для цього є свій list_rooms.
    /// </summary>
    public static Result Run(string text, Func<IReadOnlyList<string>>? live = null)
    {
        var space = text.IndexOf(' ');
        var name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var args = space < 0 ? "" : text[(space + 1)..].Trim();
        return name switch
        {
            "/roll" or "/кубик" => Roll(args),
            "/coin" or "/монетка" => Coin(),
            "/choose" or "/обери" or "/вибери" => Choose(args),
            "/8ball" or "/куля" or "/глек" => Ball(args),
            "/tables" or "/столи" or "/стіл" => Tables(live),
            _ => new(Error: $"Команди {name} нема. Є /roll, /coin, /choose, /8ball і /столи"),
        };
    }

    /// <summary>Більше живих столів за раз і не буває (Rooms.MaxRooms), але межа тут своя — картка не гумова.</summary>
    public const int MaxTables = 12;

    /// <summary>
    /// /столи — які столи зараз живі, з кнопками до кожного. Відповідь особиста: у базу не лягає й іншим не
    /// летить, бо це не репліка, а погляд у лобі, не виходячи з балачок. Самі столи браузер уже знає з
    /// події <c>rooms</c>, тож звідси йдуть лише id — щоб картка не показувала вчорашній склад.
    /// </summary>
    static Result Tables(Func<IReadOnlyList<string>>? live)
    {
        if (live is null) return new(Error: "Звідси столів не видно");
        var ids = live();
        if (ids.Count == 0) return new(Error: "Живих столів нема. Постав свій у розділі «Ігри»");
        return new(Text: ids.Count == 1 ? "Живий стіл" : $"Живих столів: {ids.Count}", Kind: "tables",
            Rooms: [.. ids.Take(MaxTables)]);
    }

    /// <summary>/roll — 1–6, /roll 100 — 1–100, /roll 2-12 — свої межі (включно).</summary>
    static Result Roll(string args)
    {
        var (min, max) = (1, 6);
        if (args.Length > 0)
        {
            var parts = args.Split(['-', '–', '—', ' ', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out var n)) (min, max) = (1, n);
            else if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b)) (min, max) = (a, b);
            else return new(Error: "Не зрозумів межі. Кидай так: /roll, /roll 100 або /roll 2-12");
        }
        if (min > max) (min, max) = (max, min);
        if (min < 0 || max > 1_000_000) return new(Error: "Тримайся в межах від 0 до мільйона");
        if (min == max) return new(Error: "З таких меж кубик нецікавий");
        return new(Text: $"🎲 {Random.Shared.Next(min, max + 1)} ({min}–{max})", Kind: "dice");
    }

    // -------------------------------------------------------------------------------------------
    // Глечикова ворожба: монетка, вибір за тебе і куля Дядька Глека (specs/chat-commands.md).
    // Випадковість тут — Random.Shared, як у кубика: це не гра, партію ніхто не відтворює.
    // -------------------------------------------------------------------------------------------

    /// <summary>Обидва боки монети. Фронт малює переворот, а тест перевіряє, що третього боку не буває.</summary>
    public static readonly IReadOnlyList<string> CoinSides = ["Орел", "Решка"];

    /// <summary>/coin — орел чи решка, чесно навпіл: без пам'яті про минулі кидки й без «щасливих серій».</summary>
    static Result Coin() => new(Text: "🪙 " + CoinSides[Random.Shared.Next(CoinSides.Count)], Kind: "coin");

    /// <summary>Більше десяти варіантів у рядок чату вже не влазить, та й вибір із них не читається.</summary>
    public const int MaxChoices = 10;
    /// <summary>Довший «варіант» — це вже не варіант, а речення.</summary>
    public const int MaxChoiceLength = 40;

    /// <summary>/choose а | б | в — вибір за тебе. Кома теж роздільник: з телефона так швидше набирати.</summary>
    static Result Choose(string args)
    {
        var parts = SplitChoices(args);
        if (parts.Count < 2) return new(Error: "Дай хоч два варіанти через |");
        if (parts.Count > MaxChoices) return new(Error: $"Забагато варіантів, більше {MaxChoices} я не перебираю");
        if (parts.Exists(p => p.Length > MaxChoiceLength)) return new(Error: $"Варіант задовгий — до {MaxChoiceLength} символів кожен");
        var pick = parts[Random.Shared.Next(parts.Count)];
        return new(Text: $"🤔 Обираю: {pick} (з: {string.Join(", ", parts)})", Kind: "choose");
    }

    /// <summary>
    /// Скісна риска — головний роздільник; коли її нема, пробуємо кому. Порожні шматки викидаємо, щоб
    /// «чай | | кава» не рахувалось за три варіанти, один з яких — ніщо.
    /// </summary>
    public static List<string> SplitChoices(string args)
    {
        var raw = (args ?? "").Contains('|') ? args!.Split('|') : (args ?? "").Split(',');
        return [.. raw.Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>Настрій відповіді кулі: щоб куля не була ні надто доброю, ні надто злою.</summary>
    public enum BallMood { Yes, No, Fog }

    /// <summary>Одна відповідь кулі: текст і настрій, за яким тест перевіряє, що всі три купки на місці.</summary>
    public sealed record BallAnswer(string Text, BallMood Mood);

    /// <summary>
    /// Банк відповідей Дядька Глека. Порядок ні на що не впливає, але кожна відповідь тут одна:
    /// повторів у банку не має бути (тест), інакше одна з них випадала б удвічі частіше за решту.
    /// </summary>
    public static readonly IReadOnlyList<BallAnswer> BallAnswers =
    [
        new("Так, і не сумнівайся", BallMood.Yes),
        new("Виходить, що так", BallMood.Yes),
        new("Так, глек на це кивнув", BallMood.Yes),
        new("Так, тільки не тягни", BallMood.Yes),
        new("Певно, що так — я вже й черепок відклав", BallMood.Yes),
        new("Так, аж двічі так", BallMood.Yes),
        new("Так, і воно тобі ще й сподобається", BallMood.Yes),

        new("Ні, навіть не починай", BallMood.No),
        new("Ні, глиняне серце каже ні", BallMood.No),
        new("Оце вже точно ні", BallMood.No),
        new("Ні, побережи черепки", BallMood.No),
        new("Ні. Спитай щось інше", BallMood.No),
        new("Ні, і я на цьому стою", BallMood.No),
        new("Ні, друже, зовсім не туди", BallMood.No),

        new("Туманно. Перепитай згодом", BallMood.Fog),
        new("Глек мовчить, а я за ним", BallMood.Fog),
        new("Спитай, коли дограє платівка", BallMood.Fog),
        new("Половина на половину, як завжди", BallMood.Fog),
        new("Не бачу — тут глина каламутна", BallMood.Fog),
        new("Може, й так. А може, й ні", BallMood.Fog),
        new("Я б на твоєму місці перепитав завтра", BallMood.Fog),
    ];

    /// <summary>Коротше — це вже не питання, а випадкове натискання.</summary>
    public const int MinQuestion = 3;
    /// <summary>Довше питання в рядок чату не влазить; решту ховаємо за трикрапкою.</summary>
    public const int MaxQuestion = 200;

    /// <summary>/8ball питання — куля Дядька Глека. Питання лишається в рядку, щоб відповідь мала до чого чіплятись.</summary>
    static Result Ball(string args)
    {
        var q = (args ?? "").Trim();
        if (q.Length < MinQuestion) return new(Error: "Спитай щось довше: /8ball чи буде дощ?");
        if (q.Length > MaxQuestion) q = Cut(q, MaxQuestion) + "…";
        var a = BallAnswers[Random.Shared.Next(BallAnswers.Count)];
        return new(Text: $"{q} — 🔮 Дядько Глек каже: „{a.Text}“", Kind: "8ball");
    }

    /// <summary>Обрізаємо по символах, але не посеред смайла: у .NET він займає дві клітинки рядка.</summary>
    static string Cut(string s, int max) => s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)];
}
