using System.Text.Json;

namespace Hlechyky.Games;

// ============================================================================================
// Контракт ігрової платформи. Це спільна мова трьох незалежних робіт: серверного каркаса (Rooms,
// TickEngine, Broadcaster), сервісів (черепки, рейтинги, ачівки) і самих ігор. Міняти — тільки разом
// із docs/games/ARCHITECTURE.md і з усіма, хто на цей файл спирається.
// ============================================================================================

/// <summary>Вкладка лобі, під якою гра живе.</summary>
public enum GameGroup { Board, Live, Party, Solo }

/// <summary>Коли партія стартує: щойно всі місця зайняті, коли господар натиснув «Почати», або одразу при створенні (соло).</summary>
public enum StartMode { WhenFull, ByHost, Immediate }

/// <summary>Як порівнювати соло-результати в таблиці.</summary>
public enum ScoreOrder { None, HigherIsBetter, LowerIsBetter }

public enum RoomStatus { Lobby, Playing, Finished }

/// <summary>
/// Опція, яку обирають при створенні кімнати (варіант шахів, розмір поля). Ставку каркас додає сам.
/// <para>
/// <see cref="Multi"/> — можна обрати кілька значень (теми «Скільки?»): до гри вони доходять одним рядком
/// через кому, без повторів і в порядку <see cref="Values"/>. <see cref="Default"/> такої опції — «усе»: з
/// рештою воно не поєднується, і до нього ж каркас зводить порожній або геть чужий вибір.
/// </para>
/// </summary>
public sealed record GameOption(string Key, string Label, IReadOnlyList<(string Value, string Label)> Values, string Default, bool Multi = false)
{
    /// <summary>Чим розділені значення <see cref="Multi"/>-опції в рядку.</summary>
    public const char Separator = ',';

    /// <summary>Значення <see cref="Multi"/>-опції окремо: «ukraine,science» → [ukraine, science]. Порожнє — порожній список.</summary>
    public static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(Separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Паспорт гри. Один на клас, читається реєстром через зразковий екземпляр.</summary>
public sealed record GameInfo(
    string Id,
    string Title,
    string Accusative,
    GameGroup Group,
    int MinPlayers,
    int MaxPlayers,
    int TickMs = 0,
    StartMode Start = StartMode.WhenFull,
    bool Hidden = false,
    bool Private = false,
    bool Persistent = false,
    bool Rated = false,
    ScoreOrder Score = ScoreOrder.None,
    IReadOnlyList<GameOption>? Options = null,
    string Hint = "",
    string Client = "")
{
    public bool RealTime => TickMs > 0;
    public bool Solo => MaxPlayers == 1;
    /// <summary>
    /// Ім'я клієнтського модуля без розширення: <c>web/games/&lt;Module&gt;.js</c> (і <c>.css</c>). Типово — Id.
    /// Родина ігор живе в одному файлі (зникаючі хрестики — у <c>ttt.js</c>, режими змійки — у <c>snake.js</c>):
    /// там кілька <c>HGames.register</c>, а кожна гра, крім першої, каже <c>Client: "ttt"</c>. Інакше і реєстр
    /// сварився б на відсутній файл, і завантажувач ходив би по 404.
    /// </summary>
    public string Module => string.IsNullOrEmpty(Client) ? Id : Client;
}

/// <summary>Відповідь на покроковий хід. Message бачить лише той, хто ходив; порожній — тоста нема.</summary>
public sealed record ActResult(bool Ok, string Message = "")
{
    public static readonly ActResult Done = new(true);
    public static ActResult Fail(string message) => new(false, message);
    /// <summary>Хід прийнято, і гравцеві є що сказати («Запропонував нічию»).</summary>
    public static ActResult Accept(string message) => new(true, message);
}

/// <summary>Що змінилось за тик: розіслати кадр (Frame) і/або повні види (View).</summary>
public readonly record struct TickResult(bool Frame, bool View)
{
    public static readonly TickResult None = new(false, false);
    public static readonly TickResult FrameOnly = new(true, false);
    public static readonly TickResult Both = new(true, true);
}

/// <summary>Нелегальний хід або ввід: текст доходить гравцеві тостом, стан кімнати не міняється.</summary>
public sealed class GameError(string message) : Exception(message);

/// <summary>Час для всього, що тестується: у проді — годинник, у тестах — FakeClock.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Підсумок партії, який каркас віддає сервісам (черепки, рейтинги, ачівки) і кладе в RoomSummary.result.</summary>
public sealed record RoomResult(int[] Winners, bool Draw, string Text, IReadOnlyDictionary<int, long>? Scores);

/// <summary>
/// Що гра може просити в каркаса. Живе один на кімнату; Rooms виставляє його до Configure(). Усі методи
/// викликаються під замком кімнати — з Act/Tick/Start, ніколи з інших потоків.
/// </summary>
public interface IRoomContext
{
    string RoomId { get; }
    /// <summary>Скільки місць зайнято на старті партії.</summary>
    int Players { get; }
    /// <summary>1 для першої партії, +1 на кожен «Ще раз».</summary>
    int Round { get; }
    Random Rng { get; }
    IClock Clock { get; }
    IReadOnlyDictionary<string, string> Options { get; }
    /// <summary>
    /// Сервіси сервера для ігор, яким треба більше, ніж правила: словники (Words), статистика радіо,
    /// DjBrain.FlavorAsync. Ігри створюються без параметрів (реєстр знаходить їх рефлексією), тому залежності
    /// беруться звідси в Configure()/Start(). У тестах RoomHarness підставляє свій провайдер.
    /// </summary>
    IServiceProvider Services { get; }
    string? NickOf(int seat);
    bool Seated(int seat);
    /// <summary>
    /// Місце господаря столу (того, хто тисне «Почати»); null, якщо він не сидить. Після виходу господаря ним
    /// стає найстарше зайняте місце — тому це властивість, а не число, запам'ятоване на старті.
    /// </summary>
    int? HostSeat { get; }
    /// <summary>Партія скінчилась. Порожній winners — нічия. Другий виклик у тій самій партії ігнорується.</summary>
    void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null);
    /// <summary>Рядок у Журнал усім.</summary>
    void Log(string text);
    /// <summary>Дядько Глек каже щось у Балачки.</summary>
    void Say(string text);
    /// <summary>Соло-результат у таблицю (порядок — Info.Score).</summary>
    void Score(int seat, long value);
    /// <summary>Черепки поза стандартною виплатою за партію (переможець «Скільки?», конкурс). Проходить через стелі економіки.</summary>
    void Award(int seat, int shards, string reason);
}

/// <summary>
/// Правила однієї партії. Екземпляр = стан однієї кімнати. Нова гра = нащадок цього класу з публічним
/// конструктором без параметрів: реєстр знайде його сам.
/// </summary>
public abstract class Game
{
    public abstract GameInfo Info { get; }

    /// <summary>Каркас виставляє перед Configure(). У тестах — RoomHarness.</summary>
    public IRoomContext Ctx { get; set; } = null!;

    /// <summary>«білі»/«чорні», «жовта»/«зелена» — для чіпів місць і рядків Журналу.</summary>
    public virtual string SeatName(int seat) => seat == 0 ? "перший" : seat == 1 ? "другий" : $"гравець {seat + 1}";

    /// <summary>Опції з лобі (варіант, розмір). Невалідні значення — GameError; тоді кімната не створюється.</summary>
    public virtual void Configure(IReadOnlyDictionary<string, string> options) { }

    /// <summary>
    /// Чи приймає гра ходи ще в лобі, до «Почати» — налаштування столу, яких не передати статичними опціями
    /// (пакет «Своєї гри» обирається зі списку, що росте без рестарту сервера). Типово — ні: каркас відповідає
    /// «Чекаємо на гравців».
    /// </summary>
    public virtual bool ActsInLobby => false;

    /// <summary>
    /// Чи можна вже починати (господар тисне «Почати» або хтось «Ще раз»): null — так, інакше текст відмови.
    /// Кличеться під замком кімнати, після перевірки MinPlayers.
    /// </summary>
    public virtual string? CanStart() => null;

    /// <summary>Lobby→Playing і кожен «Ще раз» (Ctx.Round уже збільшено). Має дати чистий стан партії.</summary>
    public abstract void Start();

    /// <summary>Покроковий хід. Нелегальний — ActResult.Fail або GameError; стан тоді не міняти.</summary>
    public virtual ActResult Act(int seat, string action, JsonElement payload) => ActResult.Fail("Тут так не ходять");

    /// <summary>Реалтайм: один крок. Кличе TickEngine раз на Info.TickMs, лише поки Playing.</summary>
    public virtual TickResult Tick() => TickResult.None;

    /// <summary>Що бачить місце seat; null — глядач. Повертати новий об'єкт, не внутрішні колекції.</summary>
    public abstract object View(int? seat);

    /// <summary>Компактний кадр для реалтайму; null — каркас візьме View(null).</summary>
    public virtual object? Frame() => null;

    /// <summary>Хтось встав посеред партії. Типово — техпоразка тому, хто пішов.</summary>
    public virtual void OnLeave(int seat)
    {
        var others = Enumerable.Range(0, Info.MaxPlayers).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        Ctx.Finish(others, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
    }

    /// <summary>Persistent: стан у JSON. null — нема чого зберігати.</summary>
    public virtual string? Save() => null;

    public virtual void Load(string json) { }

    /// <summary>
    /// Ключ особистої (соло) кімнати для OpenSolo, коли клієнт не передав свій: клікер — один на ніка назавжди,
    /// щоденні ігри перекривають і додають день за Києвом (<see cref="Days.Today"/>).
    /// </summary>
    public virtual string SoloKey(string nickKey, IClock clock) => $"{Info.Id}:{nickKey}";
}

/// <summary>Маркер: гра входить у «Щоденний глек» (одна головоломка на день, спільна для всіх, таблиця за днем).</summary>
public interface IDailyGame { }

/// <summary>
/// День за київським часом — спільна точка для щоденних ігор, стель «на день» і таблиць «за сьогодні».
/// Windows знає зону як "FLE Standard Time", Linux — як "Europe/Kyiv"; якщо не знайшли ні ту, ні ту — UTC+3
/// (краще стабільна помилка на годину взимку, ніж падіння сервера).
/// </summary>
public static class Days
{
    public static readonly TimeZoneInfo Kyiv = FindKyiv();

    static TimeZoneInfo FindKyiv()
    {
        foreach (var id in new[] { "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.CreateCustomTimeZone("Kyiv+3", TimeSpan.FromHours(3), "Kyiv (fallback)", "Kyiv (fallback)");
    }

    /// <summary>"2026-09-10" для моменту now.</summary>
    public static string Of(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Kyiv).ToString("yyyy-MM-dd");

    public static string Today(IClock clock) => Of(clock.UtcNow);

    /// <summary>Наступна київська північ після now (для «наступне слово через…»).</summary>
    public static DateTimeOffset NextMidnight(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Kyiv);
        var midnight = new DateTimeOffset(local.Date.AddDays(1), local.Offset);
        // на межі переходу часу зсув може змінитись; перерахуємо через зону
        var utc = TimeZoneInfo.ConvertTimeToUtc(midnight.DateTime, Kyiv);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>Стабільний сід для головоломки дня: SHA-256 від "hlechyky:{puzzle}:{day}", перші 4 байти.</summary>
    public static int Seed(string puzzleId, string day)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"hlechyky:{puzzleId}:{day}"));
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }

    /// <summary>Порядковий номер дня від запуску щоденних (10 вересня 2026 — день 1).</summary>
    public static int Number(string day) =>
        (int)(DateOnly.ParseExact(day, "yyyy-MM-dd").DayNumber - new DateOnly(2026, 9, 10).DayNumber) + 1;
}

// ---------------------------------------------------------------------------------------------
// Події для сервісів. Каркас (WP0) їх піднімає, сервіси (WP1) слухають. Жодних SignalR-типів тут.
// ---------------------------------------------------------------------------------------------

public sealed record RoomFinishedEvent(
    string RoomId, string GameId, GameInfo Info, int Round,
    IReadOnlyList<string?> Seats,       // нік на кожному місці на момент завершення (null — місце було вільне)
    RoomResult Result,
    int Stake,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    int Moves);                         // скільки Act прийнято за партію (0 для реалтайму)

public sealed record SoloScoreEvent(string GameId, string Nick, long Score, ScoreOrder Order, string? Key, DateTimeOffset At);

public sealed record AwardEvent(string GameId, string RoomId, string Nick, int Shards, string Reason);

/// <summary>Шина подій платформи. Синглтон; підписники не кидають винятків назовні (каркас логує й іде далі).</summary>
public sealed class GameEvents
{
    public event Action<RoomFinishedEvent>? RoomFinished;
    public event Action<SoloScoreEvent>? SoloScored;
    public event Action<AwardEvent>? Awarded;

    public void Raise(RoomFinishedEvent e) => RoomFinished?.Invoke(e);
    public void Raise(SoloScoreEvent e) => SoloScored?.Invoke(e);
    public void Raise(AwardEvent e) => Awarded?.Invoke(e);
}

// ---------------------------------------------------------------------------------------------
// Те, що каркас просить у сервісів. Реалізує WP1; WP0 у тестах підставляє фейки.
// ---------------------------------------------------------------------------------------------

/// <summary>Ставки: списати з гравця на старті, виплатити після партії. Ідемпотентно за refKey.</summary>
public interface IStakes
{
    int Balance(string nick);
    bool TrySpend(string nick, int amount, string reason, string refKey);
    void Grant(string nick, int amount, string reason, string refKey);
}

/// <summary>Збережений стан Persistent-ігор (щоденне, клікер).</summary>
public interface IGameStore
{
    void SaveState(string key, string json);
    string? LoadState(string key);
    void DeleteState(string key);
}

/// <summary>Повідомлення для Broadcaster'а. Каркас збирає їх у список під замком і розсилає поза ним.</summary>
public abstract record Outgoing;
public sealed record LobbyChanged : Outgoing;
public sealed record RoomViews(string RoomId) : Outgoing;
public sealed record RoomFrame(string RoomId, object Frame) : Outgoing;
/// <summary>
/// Рядок у Журнал усім. <paramref name="RoomId"/> — живий стіл, про який цей рядок: браузер малює біля
/// нього кнопку «Сісти»/«Дивитись» (PROTOCOL §2). null — просто рядок.
/// </summary>
public sealed record Journal(string Text, string? RoomId = null) : Outgoing;
public sealed record DjSays(string Text) : Outgoing;
public sealed record WalletChanged(string Nick, int Balance, int Delta, string Reason, string Text) : Outgoing;
public sealed record AchievementUnlocked(string Nick, string Key, string Title, string Text, string Icon, int Reward) : Outgoing;
public sealed record ToastFor(string Nick, string Text, string Kind) : Outgoing;
/// <summary>
/// «Влад кличе в Мафію» — заклик до столу всім, хто зараз на сайті. Летить усім разом із ніком того, хто
/// кличе: свій же заклик браузер відкидає сам, бо хто кому кличе — видно лише в браузері (там є me.nick),
/// а сервер про вкладки того самого ніка нічого корисного не знає.
/// </summary>
public sealed record Invite(string RoomId, string By, string Text) : Outgoing;

/// <summary>Куди сервіси (WP1) кладуть свої повідомлення, коли щось нарахували поза межами дії каркаса (онлайн-хвилини, ачівка).</summary>
public interface IOutbox
{
    void Post(Outgoing message);
}
