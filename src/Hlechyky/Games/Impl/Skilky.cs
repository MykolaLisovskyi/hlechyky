using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Скільки?» — гра на відчуття числа. П'ять запитань, на які ніхто не знає точної відповіді: кожен пише
/// своє число, потім усе розкривається, і кожен бере очки за те, наскільки близько влучив, а найближчий —
/// ще й бонус. Виграє не той, хто знає, а той, хто краще відчуває порядок величин.
/// <para>
/// Очки за точність, а не за місце: при місцях 3/2/1 удвох той, хто промазав у десять разів, однаково
/// брав +2, і кожне питання важило одне очко різниці. Шкала — <see cref="Accuracy"/>.
/// </para>
/// <para>
/// Партія живе не від ходу до ходу, а від тика (раз на секунду): фази міняє час, а не гравці, і саме тому
/// вся логіка переходів зібрана в одному <see cref="Tick"/> — з <see cref="Act"/> нічого не «стрибає».
/// Гра <c>Hidden</c>: доки триває фаза відповіді, чужих чисел у виді нема взагалі, лише галочки «відповів».
/// </para>
/// </summary>
public sealed class Skilky : Game
{
    /// <summary>Скільки запитань у партії. Менше буває лише тоді, коли банк геть куций.</summary>
    public const int Questions = 5;
    /// <summary>Скільки секунд дано на число.</summary>
    public const int AskSeconds = 30;
    /// <summary>Скільки секунд висить розкриття, перш ніж поїхати далі.</summary>
    public const int RevealSeconds = 6;
    /// <summary>Коротка пауза перед кожним запитанням: «зараз буде», щоб питання не впало людям на голову.</summary>
    public const int BetweenSeconds = 3;
    /// <summary>Найбільший стіл. Більше дванадцяти чисел на одному екрані вже не роздивитись.</summary>
    public const int MaxSeats = 12;

    public const string PhaseBetween = "between";
    public const string PhaseAsk = "ask";
    public const string PhaseReveal = "reveal";
    public const string PhaseDone = "done";

    /// <summary>Очки за влучання «в яблучко» — найвищий ярус шкали точності.</summary>
    public const int Bullseye = 5;
    /// <summary>Бонус найближчому за столом — лише якщо він і сам хоч щось набрав за точність.</summary>
    public const int BestBonus = 2;

    /// <summary>
    /// Шкала точності для звичайних чисел: промах у частках від правильної відповіді → очки. Далі за 25 %
    /// ще є +1 «до двох разів» (<see cref="Accuracy"/>): удвічі більше й удвічі менше — однаково далеко.
    /// </summary>
    static readonly (double Off, int Points)[] Tiers = [(0.02, Bullseye), (0.10, 3), (0.25, 2)];
    /// <summary>Шкала для років: там відсотки брешуть (1900 проти 1990 — «лише 4,5 %»), тож міряємо роками.</summary>
    static readonly (double Off, int Points)[] YearTiers = [(0, Bullseye), (3, 3), (10, 2), (50, 1)];

    /// <summary>Рядок таблиці розкриття: чиє число, наскільки повз і скільки за це дали (разом із бонусом).</summary>
    sealed record Row(int Seat, double Value, double Diff, int Points, int Bonus);

    public override GameInfo Info { get; } = new(
        "skilky", "Скільки?", "«Скільки?»", GameGroup.Party, 2, MaxSeats,
        TickMs: 1000, Start: StartMode.ByHost, Hidden: true, Rated: false,
        Hint: "Питання, на яке ніхто не знає точної відповіді. Кожен пише число: що ближче — то більше очок, найближчому ще й бонус. П'ять питань");

    /// <summary>Запитання цієї партії разом із уже порахованою правильною відповіддю.</summary>
    readonly List<(SkilkyQuestion Q, double A)> _asked = [];
    readonly double?[] _answers = new double?[MaxSeats];
    readonly long[] _scores = new long[MaxSeats];

    int _at;
    string _phase = PhaseBetween;
    DateTimeOffset _endsAt;
    IReadOnlyList<Row>? _reveal;
    double _answer;
    bool _years;
    int[]? _winners;
    /// <summary>Хтось щойно написав число — на наступному тику треба розіслати не лише кадр, а й види.</summary>
    bool _touched;

    /// <summary>Статистика радіо для динамічних запитань. Береться в <see cref="Start"/>, як велить каркас.</summary>
    SkilkyStats? _stats;

    /// <summary>Місця називаємо числами: на столі їх до дванадцяти, і «гравець одинадцятий» у чіп не влізе.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // партія
    // ---------------------------------------------------------------------------------------

    public override void Start()
    {
        // Сервіси беремо тут, а не в конструкторі: гру створює реєстр без параметрів (INTEGRATION-NOTES §1).
        // Db може не бути взагалі (тести з порожнім провайдером) — тоді динамічні запитання просто не грають.
        _stats ??= new SkilkyStats(Ctx.Services.GetService<Db>(), Ctx.Clock);

        Array.Clear(_scores);
        Array.Clear(_answers);
        _asked.Clear();
        _asked.AddRange(Pick());
        _at = 0;
        _reveal = null;
        _winners = null;
        _touched = false;

        if (_asked.Count == 0)
        {
            // Без банку грати нема в що. Кажемо це один раз і чесно, а не мовчимо порожнім екраном.
            _phase = PhaseDone;
            _endsAt = Ctx.Clock.UtcNow;
            Ctx.Finish([], $"{Info.Title}: банк запитань не знайшовся, партії не буде");
            return;
        }
        Open(Ctx.Clock.UtcNow);
    }

    /// <summary>
    /// П'ять різних запитань на партію. Динамічне беремо лише тоді, коли база вже щось назбирала: питати
    /// «скільки треків зіграло», коли відповідь нуль, — не загадка, а знущання.
    /// </summary>
    List<(SkilkyQuestion Q, double A)> Pick()
    {
        var pool = new List<(SkilkyQuestion Q, double A)>();
        foreach (var q in SkilkyBank.All)
        {
            if (q.IsDynamic)
            {
                var value = _stats!.Value(q.Dyn);
                if (value > 0) pool.Add((q, value));
            }
            else if (q.A is { } a) pool.Add((q, a));
        }
        // Часткове тасування Фішера — Єйтса: витягуємо рівно стільки, скільки треба, і жодного двічі.
        var take = Math.Min(Questions, pool.Count);
        for (var i = 0; i < take; i++)
        {
            var j = i + Ctx.Rng.Next(pool.Count - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return [.. pool.Take(take)];
    }

    /// <summary>Нове запитання: чистий стіл і коротке «готуйсь».</summary>
    void Open(DateTimeOffset now)
    {
        Array.Clear(_answers);
        _reveal = null;
        _touched = false;
        _phase = PhaseBetween;
        _endsAt = now.AddSeconds(BetweenSeconds);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "answer") return ActResult.Fail("Тут так не ходять");
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (_phase != PhaseAsk) return ActResult.Fail("Зачекай на запитання");
        if (Ctx.Clock.UtcNow >= _endsAt) return ActResult.Fail("Час вийшов");
        if (Number(payload) is not { } value) return ActResult.Fail("Тут треба число");
        if (double.IsNaN(value) || double.IsInfinity(value)) return ActResult.Fail("Тут треба число");
        if (Math.Abs(value) > 1e15) return ActResult.Fail("Це вже занадто велике число");

        _answers[seat] = value;
        _touched = true;
        // Реалтайм-кімната не розсилає види з Act (Rooms.Act, counts == false), тож підтвердження
        // гравцеві — оцей рядок; галочки в усіх інших приїдуть найближчим тиком.
        return ActResult.Accept($"Записав: {Num(value)}");
    }

    /// <summary>Число приймаємо і як <c>{value: 2061}</c>, і як голе число, і як рядок — клієнтам так простіше.</summary>
    static double? Number(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetDouble(out var n) => n,
        JsonValueKind.String => Text(payload.GetString()),
        JsonValueKind.Object when payload.TryGetProperty("value", out var v) => Number(v),
        _ => null,
    };

    /// <summary>Рядок із поля вводу: кома замість крапки й пробіли між тисячами — звична річ.</summary>
    static double? Text(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Пробіли між тисячами (звичайні й нерозривні — char.IsWhiteSpace знає обидва) прибираємо,
        // а кому читаємо як крапку: людина пише «10 000» і «2,54», а не «10000» і «2.54».
        var clean = new string([.. raw.Where(ch => !char.IsWhiteSpace(ch))]).Replace(',', '.');
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public override TickResult Tick()
    {
        if (_phase == PhaseDone) return TickResult.None;
        var now = Ctx.Clock.UtcNow;

        // Усі, хто за столом, уже написали — чекати на таймер нема сенсу. Розкриємо наступним рухом циклу,
        // щоб усі переходи фаз лишались в одному місці.
        if (_phase == PhaseAsk && AllAnswered()) _endsAt = now;

        if (now < _endsAt)
        {
            if (!_touched) return TickResult.FrameOnly;
            _touched = false;
            return TickResult.Both;      // хтось відповів: галочка всім, «моє число» — авторові
        }

        _touched = false;
        switch (_phase)
        {
            case PhaseBetween:
                _phase = PhaseAsk;
                _endsAt = now.AddSeconds(AskSeconds);
                break;
            case PhaseAsk:
                Reveal(now);
                break;
            case PhaseReveal:
                if (_at + 1 < _asked.Count) { _at++; Open(now); }
                else Done();
                break;
        }
        return TickResult.Both;
    }

    bool AllAnswered()
    {
        var seated = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!Ctx.Seated(s)) continue;
            seated++;
            if (_answers[s] is null) return false;
        }
        return seated > 0;
    }

    /// <summary>
    /// Розкриття: правильна відповідь, усі числа за відстанню, кожному — очки за точність
    /// (<see cref="Accuracy"/>), а найближчому ще <see cref="BestBonus"/>. Рівна відстань — рівний бонус:
    /// двоє однаково близьких беруть його обидва.
    /// </summary>
    void Reveal(DateTimeOffset now)
    {
        var (question, target) = _asked[_at];
        var years = IsYears(question);
        var rows = new List<Row>();
        for (var s = 0; s < MaxSeats; s++)
            if (Ctx.Seated(s) && _answers[s] is { } v)
                rows.Add(new Row(s, v, Math.Abs(v - target), Accuracy(v, target, years), 0));
        rows = [.. rows.OrderBy(r => r.Diff).ThenBy(r => r.Seat)];

        // Хто найближчий, вирішуємо з допуском, а не точною рівністю double: 36.4 і 36.8 промахнулись повз
        // 36.6 однаково, але в бітах це 0.20000000000000284 і 0.19999999999999574. Гравці побачили б однакову
        // різницю й бонус лише в одного. А промах удесятеро бонусу не бере, навіть якщо інші ще далі:
        // інакше вдвох той, хто хоч якось ближче, знову мав би очки задарма.
        var closest = rows.Count > 0 ? rows[0].Diff : 0;
        rows = [.. rows.Select(r => SameDiff(r.Diff, closest) && r.Points > 0
            ? r with { Points = r.Points + BestBonus, Bonus = BestBonus } : r)];
        foreach (var r in rows) _scores[r.Seat] += r.Points;

        _answer = target;
        _years = years;
        _reveal = rows;
        Ctx.Say(Flavor(rows));
        _phase = PhaseReveal;
        _endsAt = now.AddSeconds(RevealSeconds);
    }

    /// <summary>Запитання про рік міряємо в роках, решту — у частках від відповіді.</summary>
    static bool IsYears(SkilkyQuestion q) => q.Unit == "рік";

    /// <summary>
    /// Очки за точність одного числа, без огляду на суперників — тому шкала однаково чесна вдвох і
    /// вдванадцятьох. Звичайні числа: промах до 2 % — <see cref="Bullseye"/>, до 10 % — 3, до 25 % — 2,
    /// від половини до подвоєної відповіді — 1, далі — 0. Роки: точно — 5, ±3 — 3, ±10 — 2, ±50 — 1.
    /// Межі включні й із допуском: 2,794 проти 2,54 — рівно 10 %, хоч у double це 0.10000000000000009.
    /// </summary>
    public static int Accuracy(double guess, double target, bool years)
    {
        const double eps = 1e-9;
        if (years)
        {
            var d = Math.Abs(guess - target);
            foreach (var (off, points) in YearTiers) if (d <= off + eps) return points;
            return 0;
        }
        // У банку відповіді лише додатні (динамічні з нулем у партію не беремо), тож ділити є на що. Нуль і
        // від'ємне число проти додатної відповіді — не порядок величин, а мимо.
        if (target <= 0) return SameDiff(guess, target) ? Bullseye : 0;
        if (guess <= 0) return 0;
        var miss = Math.Abs(guess - target) / target;
        foreach (var (off, points) in Tiers) if (miss <= off + eps) return points;
        return guess >= target / 2 * (1 - eps) && guess <= target * 2 * (1 + eps) ? 1 : 0;
    }

    /// <summary>
    /// Чи це та сама відстань. Допуск відносний: на числах банку (від одиниць до сотень тисяч) абсолютний
    /// поріг був би або надто грубим, або марним.
    /// </summary>
    static bool SameDiff(double a, double b) =>
        Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    /// <summary>Кінець партії: лідери беруть перемогу, а якщо ніхто не набрав жодного очка — нічия.</summary>
    void Done()
    {
        _phase = PhaseDone;
        var seats = Enumerable.Range(0, MaxSeats).Where(Ctx.Seated).ToList();
        var best = seats.Count == 0 ? 0 : seats.Max(s => _scores[s]);
        _winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        var scores = seats.ToDictionary(s => s, s => _scores[s]);
        Ctx.Finish(_winners, Summary(seats, best), scores);
    }

    /// <summary>Рядок Журналу: рахунок усіх за столом від більшого, бо ніки відмінювати нема як.</summary>
    string Summary(List<int> seats, long best)
    {
        if (seats.Count == 0) return $"{Info.Title}: за столом уже нікого";
        var line = string.Join(", ", seats
            .OrderByDescending(s => _scores[s]).ThenBy(s => s)
            .Select(s => $"{Ctx.NickOf(s)} {_scores[s]}"));
        return best > 0 ? $"{Info.Title}: {line}" : $"{Info.Title}: {line} — ніхто нічого не вгадав";
    }

    /// <summary>
    /// Хтось встав посеред партії. Компанійська гра це переживає: решта грає далі, а того, хто пішов,
    /// просто не рахуємо. Партія закінчується лише тоді, коли грати вже нема кому.
    /// </summary>
    public override void OnLeave(int seat)
    {
        _answers[seat] = null;
        var left = Enumerable.Range(0, MaxSeats).Where(s => s != seat && Ctx.Seated(s)).ToList();
        if (left.Count >= Info.MinPlayers) return;
        _phase = PhaseDone;
        _winners = [.. left];
        Ctx.Finish(_winners, left.Count == 1
            ? $"{Info.Title}: усі, крім {Ctx.NickOf(left[0])}, розійшлись"
            : $"{Info.Title}: гравці розійшлись, партію не дограли");
    }

    // ---------------------------------------------------------------------------------------
    // види
    // ---------------------------------------------------------------------------------------

    public override object View(int? seat) => new
    {
        round = _at + 1,
        of = _asked.Count,
        phase = _phase,
        // У паузі перед запитанням його ще не показуємо: тексту нема ні на екрані, ні у виді, тож
        // зазирнути в консоль на три секунди раніше за інших не вийде.
        question = _phase == PhaseBetween ? "" : Current?.Q ?? "",
        unit = _phase == PhaseBetween ? null : Current?.Unit,
        endsAt = _endsAt,
        answered = Answered(),
        // Єдине, що в цьому виді своє для кожного місця: чуже число до розкриття не бачить ніхто.
        my = seat is { } s && s >= 0 && s < MaxSeats ? _answers[s] : null,
        reveal = _reveal is null ? null : new
        {
            answer = _answer,
            // Клієнтові треба знати, як підписати промах: «на 12 % менше» чи просто «різниця 12» для років.
            years = _years,
            rows = _reveal.Select(r => new { seat = r.Seat, value = r.Value, diff = r.Diff, points = r.Points, bonus = r.Bonus }).ToArray(),
        },
        scores = (long[])_scores.Clone(),
        result = _winners is null ? null : new { winners = (int[])_winners.Clone(), scores = (long[])_scores.Clone() },
    };

    /// <summary>Кадр раз на секунду: відлік, галочки й рахунок. Нічого прихованого — кадр летить усій кімнаті.</summary>
    public override object? Frame() => new
    {
        round = _at + 1,
        of = _asked.Count,
        phase = _phase,
        endsAt = _endsAt,
        answered = Answered(),
        scores = (long[])_scores.Clone(),
    };

    SkilkyQuestion? Current => _at >= 0 && _at < _asked.Count ? _asked[_at].Q : null;

    bool[] Answered()
    {
        var flags = new bool[MaxSeats];
        for (var s = 0; s < MaxSeats; s++) flags[s] = _answers[s] is not null;
        return flags;
    }

    // ---------------------------------------------------------------------------------------
    // Дядько Глек
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Що Глек каже перед розкриттям. Фрази статичні (модель заради одного рядка не будимо), і нік у них
    /// завжди стоїть у називному — підметом або одразу після тире. Інакше вилазить «Пальма першості в
    /// Петро» і «промахнулась» на чоловічому ніку: чужі імена ми відмінювати не вміємо.
    /// </summary>
    static readonly string[] Flavors =
    [
        "Найближче — {0}: різниця {1}.",
        "{0} — найточніше око цього раунду, повз усього на {1}.",
        "Ближче за всіх — {0}, {1} убік.",
        "Перше місце в цьому питанні — {0}, промах {1}.",
        "{0} майже в яблучко: {1} різниці.",
        "Найточніше — {0}: {1} повз.",
        "{0} на першому місці, різниця {1}. Непогано.",
        "Точніше за всіх — {0}: лише {1} убік.",
        "Найкращий результат — {0}, і той повз на {1}.",
        "Найкраще чуття цього раунду — {0}, різниця {1}.",
        "{0} — переможець раунду з різницею {1}.",
        "Пальма першості цього раунду — {0}, {1} убік.",
        "{0} тримає марку: {1} різниці.",
        "Тут виграє {0} — {1} повз ціль.",
        "Найближче до правди — {0}, {1} убік.",
    ];

    /// <summary>Хтось назвав рівно правильне число — «різниця 0» тут звучала б як глузд.</summary>
    static readonly string[] Exact =
    [
        "Точнісінько — {0}! Шапки геть.",
        "{0} — рівно в ціль, без жодної похибки.",
        "В яблучко, і не збоку, а в саму серцевину — {0}.",
    ];

    /// <summary>Найближчий і той нічого не взяв за точність: хвалити нема за що.</summary>
    static readonly string[] Wide =
    [
        "Ех, ніхто навіть близько. Найближче — {0}, і той повз на {1}.",
        "Мимо всі. Найменший промах — {0}: {1} убік. Очок за таке не дають.",
        "Порядок величин сьогодні не з нами: найближче — {0}, різниця {1}.",
    ];

    static readonly string[] Silence =
    [
        "Тиша. Ну добре, наступне.",
        "Жодного числа. Буває.",
        "Ніхто й не спробував — рахунок стоїть на місці.",
    ];

    string Flavor(IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return Silence[Ctx.Rng.Next(Silence.Length)];
        var best = rows[0];
        var bank = best.Points == 0 ? Wide : SameDiff(best.Diff, 0) ? Exact : Flavors;
        return string.Format(CultureInfo.InvariantCulture, bank[Ctx.Rng.Next(bank.Length)],
            Ctx.NickOf(best.Seat) ?? SeatName(best.Seat), Num(best.Diff));
    }

    /// <summary>
    /// Число для людини: ціле — з пробілами між тисячами, дробове — з комою й без хвоста нулів. Кому
    /// ставимо руками, а не культурою «uk-UA»: збірка може піти в режимі InvariantGlobalization, і тоді
    /// культура мовчки віддала б крапку — а людина набирала «2,5» і чекає «2,5» назад.
    /// </summary>
    static string Num(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15
            ? Math.Round(v).ToString("#,##0", CultureInfo.InvariantCulture).Replace(",", " ")
            : v.ToString("#,##0.###", CultureInfo.InvariantCulture).Replace(",", " ").Replace('.', ',');
}
