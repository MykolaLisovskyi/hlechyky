using System.Text.Json;
using Hlechyky.Games;

namespace Hlechyky.Tests.Support;

// Заглушки для перевірок каркаса, яких не дають чотири справжні гри: старт господарем, кімната на чотирьох,
// приховані види, соло з Persistent, тик, який вибухає. Id починаються з «t-», щоб не зіткнутись із живими.

/// <summary>Кімната на 2–4 з ручним стартом: господар, MinPlayers, обертання місць у «Ще раз».</summary>
public sealed class TestParty : Game
{
    public override GameInfo Info { get; } = new(
        "t-party", "Тестова компанія", "тестову компанію", GameGroup.Party, 2, 4, Start: StartMode.ByHost);

    public int Starts { get; private set; }
    public int Acts { get; private set; }

    public override void Start() => Starts++;

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        Acts++;
        return action switch
        {
            "win" => Win(seat),
            "draw" => Draw(),
            "boom" => throw new InvalidOperationException("тест"),
            "no" => ActResult.Fail("Так не можна"),
            _ => ActResult.Done,
        };
    }

    ActResult Win(int seat)
    {
        Ctx.Finish([seat], $"компанія: виграв {Ctx.NickOf(seat)}");
        return ActResult.Done;
    }

    ActResult Draw()
    {
        Ctx.Finish([], "компанія: нічия");
        return ActResult.Done;
    }

    public override object View(int? seat) => new { turn = 0, starts = Starts, acts = Acts, me = seat };
}

/// <summary>
/// Стіл, що починає сам, щойно його поставили, і вільного місця нікому вже не обіцяє. Живих таких ігор
/// нема (усі Immediate у нас приватні), але гілка в <c>Rooms.Create</c> є: заклик «сідай» має мовчати там,
/// де сідати нема куди.
/// </summary>
public sealed class TestNow : Game
{
    public override GameInfo Info { get; } = new(
        "t-now", "Тестовий одразу", "тестовий одразу", GameGroup.Live, 1, 2, Start: StartMode.Immediate);

    public override void Start() { }

    public override object View(int? seat) => new { turn = (int?)null };
}

/// <summary>Гра з прихованою інформацією: у виді кожного місця своє, глядач не бачить нічого зайвого.</summary>
public sealed class TestHidden : Game
{
    public override GameInfo Info { get; } = new(
        "t-hidden", "Тестові карти", "тестові карти", GameGroup.Board, 2, 2, Hidden: true);

    public override void Start() { }

    public override object View(int? seat) => new { turn = 0, secret = seat is { } s ? $"карта-{s}" : null };
}

/// <summary>Соло-гра: приватна, стартує одразу, зберігається, віддає результат у таблицю й нараховує черепки.</summary>
public sealed class TestSolo : Game
{
    long _value;

    public override GameInfo Info { get; } = new(
        "t-solo", "Тестове соло", "тестове соло", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter);

    public override void Start() => _value = 0;

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "add":
                _value += payload.TryGetProperty("v", out var v) ? v.GetInt64() : 1;
                return ActResult.Done;
            case "score":
                Ctx.Score(seat, _value);
                return ActResult.Done;
            case "award":
                Ctx.Award(seat, 3, "test");
                return ActResult.Done;
            case "ach":
                // нуль черепків — це не «нічого», а прохання видати ачівку (ARCHITECTURE §4.3)
                Ctx.Award(seat, 0, "ach:test-key");
                return ActResult.Done;
            case "done":
                Ctx.Score(seat, _value);
                Ctx.Finish([seat], "соло: готово");
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    public override object View(int? seat) => new { turn = (int?)null, value = _value };

    public override string? Save() => JsonSerializer.Serialize(new { value = _value });

    public override void Load(string json) => _value = JsonDocument.Parse(json).RootElement.GetProperty("value").GetInt64();
}

/// <summary>Реалтайм-заглушка: рахує тики, а на бажаному тику падає — щоб перевірити, що цикл це переживе.</summary>
public sealed class TestTicker : Game
{
    int _boom;

    public override GameInfo Info { get; } = new(
        "t-tick", "Тестовий тик", "тестовий тик", GameGroup.Live, 2, 2, TickMs: 100, Rated: true,
        Options: [new GameOption("boom", "На якому тику вибухнути", [], "0")]);

    public int Ticks { get; private set; }

    public override void Configure(IReadOnlyDictionary<string, string> options) =>
        _boom = options.TryGetValue("boom", out var raw) && int.TryParse(raw, out var n) ? n : 0;

    public override void Start() => Ticks = 0;

    public override TickResult Tick()
    {
        Ticks++;
        if (_boom > 0 && Ticks == _boom) throw new InvalidOperationException("тестовий вибух");
        return TickResult.FrameOnly;
    }

    public override object? Frame() => new { ticks = Ticks };

    public override object View(int? seat) => new { turn = (int?)null, ticks = Ticks };
}

/// <summary>Рейтингова гра на двох без правил: тільки щоб перевірити ставки, виплати й подвійний Finish.</summary>
public sealed class TestDuel : Game
{
    public override GameInfo Info { get; } = new(
        "t-duel", "Тестова дуель", "тестову дуель", GameGroup.Board, 2, 2, Rated: true);

    public override void Start() { }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "win":
                Ctx.Finish([seat], $"дуель: виграв {Ctx.NickOf(seat)}");
                return ActResult.Done;
            case "draw":
                Ctx.Finish([], "дуель: нічия");
                return ActResult.Done;
            case "double":
                // Гра з багом: оголошує кінець двічі. Каркас мусить порахувати лише перший раз.
                Ctx.Finish([seat], "дуель: перший раз");
                Ctx.Finish([seat == 0 ? 1 : 0], "дуель: другий раз");
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    public override object View(int? seat) => new { turn = 0 };
}

/// <summary>Гра з типовою помилкою автора: масив назв коротший за кількість місць. Лобі має вижити.</summary>
public sealed class TestBadSeats : Game
{
    static readonly string[] Names = ["єдина"];

    public override GameInfo Info { get; } = new(
        "t-badseat", "Тестова крива", "тестову криву", GameGroup.Board, 2, 2);

    public override string SeatName(int seat) => Names[seat];

    public override void Start() { }

    public override object View(int? seat) => new { turn = 0 };
}

/// <summary>Гра, яка падає в Configure(): гравець має побачити український текст, а не виняток сервера.</summary>
public sealed class TestBadConfigure : Game
{
    public override GameInfo Info { get; } = new(
        "t-badconfig", "Тестова ламана", "тестову ламану", GameGroup.Board, 2, 2);

    public override void Configure(IReadOnlyDictionary<string, string> options) =>
        throw new InvalidOperationException("ключ бази даних не знайдено");

    public override void Start() { }

    public override object View(int? seat) => new { turn = 0 };
}

/// <summary>
/// Стіл, який налаштовують у лобі (як пакет «Своєї гри»): господар обирає «пакет» ходом ще до старту, а без
/// нього «Почати» відмовляє. Бачить, хто зараз господар (<see cref="IRoomContext.HostSeat"/>). З тиком — бо в лобі
/// тика нема, і вид після ходу мусить розіслати сам каркас.
/// </summary>
public sealed class TestLobby : Game
{
    public override GameInfo Info { get; } = new(
        "t-lobby", "Тестове лобі", "тестове лобі", GameGroup.Party, 1, 4, TickMs: 100, Start: StartMode.ByHost);

    public string? Pack { get; private set; }

    public override bool ActsInLobby => true;

    public override string? CanStart() => Pack is null ? "Оберіть пакет" : null;

    public override void Start() { }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "pack") return ActResult.Fail("Тут так не ходять");
        if (seat != Ctx.HostSeat) return ActResult.Fail("Пакет обирає господар");
        Pack = payload.GetProperty("id").GetString();
        return ActResult.Done;
    }

    public override object View(int? seat) => new { pack = Pack, host = Ctx.HostSeat };
}
