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
/// брав +2, і кожне питання важило одне очко різниці. Шкала — <see cref="Accuracy"/>; найближчий завжди бере
/// ще <see cref="BestBonus"/>, а за однакової відстані швидший на секунду — ще <see cref="SpeedBonus"/>.
/// </para>
/// <para>
/// Запитання не повторюються без потреби: <see cref="SkilkySeen"/> пам'ятає, хто яке вже бачив, і в партію
/// йдуть ті, яких ніхто за столом не бачив або бачив найдавніше.
/// </para>
/// <para>
/// Партія живе не від ходу до ходу, а від тика (раз на секунду): фази міняє час, а не гравці, і саме тому
/// вся логіка переходів зібрана в одному <see cref="Tick"/> — з <see cref="Act"/> нічого не «стрибає».
/// Гра <c>Hidden</c>: доки триває фаза відповіді, чужих чисел у виді нема взагалі, лише галочки «відповів».
/// </para>
/// </summary>
public sealed class Skilky : Game
{
    /// <summary>Скільки запитань у партії, якщо господар не обрав іншого. Менше буває лише тоді, коли тема геть куца.</summary>
    public const int Questions = 5;
    /// <summary>Скільки секунд дано на число, якщо господар не обрав іншого.</summary>
    public const int AskSeconds = 30;
    /// <summary>Скільки очок партії коштує один черепок. Без денної стелі — рішення господаря сайту.</summary>
    public const int PointsPerShard = 5;

    static readonly int[] QuestionChoices = [3, 5, 7, 10, 15];
    static readonly int[] SecondChoices = [15, 30, 45, 60];
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
    /// <summary>Промах на одиницю на цілій відповіді — «майже»: щонайменше стільки, хоч у відсотках це й 11 %.</summary>
    public const int OffByOne = 4;
    /// <summary>
    /// Найближчому за столом — завжди, навіть коли за точність він нічого не взяв: тоді це його єдине очко.
    /// Крім гри самому: «найближчий з одного» — не заслуга.
    /// </summary>
    public const int BestBonus = 1;
    /// <summary>За однакової відстані швидшому хоча б на <see cref="SpeedGap"/>.</summary>
    public const int SpeedBonus = 1;
    public static readonly TimeSpan SpeedGap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Шкала точності для звичайних чисел: промах у частках від правильної відповіді → очки. Далі за 50 %
    /// ще є 1 «до двох разів» (<see cref="Accuracy"/>): удвічі більше й удвічі менше — однаково далеко.
    /// </summary>
    static readonly (double Off, int Points)[] Tiers = [(0.02, Bullseye), (0.10, 4), (0.25, 3), (0.50, 2)];
    /// <summary>Шкала для років: там відсотки брешуть (1900 проти 1990 — «лише 4,5 %»), тож міряємо роками.</summary>
    static readonly (double Off, int Points)[] YearTiers = [(0, Bullseye), (2, 4), (5, 3), (15, 2), (50, 1)];

    /// <summary>Рядок таблиці розкриття: чиє число, наскільки повз і скільки за що дали.</summary>
    sealed record Row(int Seat, double Value, double Diff, int Accuracy, int Bonus = 0, int Fast = 0)
    {
        public int Points => Accuracy + Bonus + Fast;
    }

    // Мінімум — один: господар може почати й сам, а хто встигне підсісти до старту, грає разом.
    public override GameInfo Info { get; } = new(
        "skilky", "Скільки?", "«Скільки?»", GameGroup.Party, 1, MaxSeats,
        TickMs: 1000, Start: StartMode.ByHost, Hidden: true, Rated: false,
        Options:
        [
            new GameOption("questions", "Питань", [.. QuestionChoices.Select(n => (Str(n), Str(n)))], Str(Questions)),
            new GameOption("seconds", "Час на відповідь", [.. SecondChoices.Select(n => (Str(n), $"{n} с"))], Str(AskSeconds)),
            new GameOption("topic", "Теми", SkilkyTopics.All, SkilkyTopics.Any, Multi: true),
        ],
        Hint: "Питання, на яке ніхто не знає точної відповіді. Кожен пише число: що ближче — то більше очок і черепків. Можна й самому");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Скільки запитань у цій кімнаті (опція «Питань»).</summary>
    int _questions = Questions;
    /// <summary>Скільки секунд на число в цій кімнаті (опція «Час на відповідь»).</summary>
    int _seconds = AskSeconds;
    /// <summary>Теми запитань цієї кімнати (опція «Теми», можна кілька); null — усі.</summary>
    IReadOnlySet<string>? _topics;

    /// <summary>Запитання цієї партії разом із уже порахованою правильною відповіддю.</summary>
    readonly List<(SkilkyQuestion Q, double A)> _asked = [];
    readonly double?[] _answers = new double?[MaxSeats];
    /// <summary>Коли прийшло останнє число місця: за однакової відстані швидший бере <see cref="SpeedBonus"/>.</summary>
    readonly DateTimeOffset[] _answeredAt = new DateTimeOffset[MaxSeats];
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
    /// <summary>Хто які запитання вже бачив. Без бази — мовчить, і запитання просто тасуються.</summary>
    SkilkySeen? _seen;

    /// <summary>Місця називаємо числами: на столі їх до дванадцяти, і «гравець одинадцятий» у чіп не влізе.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // партія
    // ---------------------------------------------------------------------------------------

    /// <summary>Опції столу. Каркас уже звів кожну до одного з дозволених значень, лишається прочитати.</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("questions", out var q) && int.TryParse(q, CultureInfo.InvariantCulture, out var n)
            && QuestionChoices.Contains(n)) _questions = n;
        if (options.TryGetValue("seconds", out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var sec)
            && SecondChoices.Contains(sec)) _seconds = sec;
        if (options.TryGetValue("topic", out var t)) _topics = SkilkyTopics.Parse(t);
    }

    public override void Start()
    {
        // Сервіси беремо тут, а не в конструкторі: гру створює реєстр без параметрів (INTEGRATION-NOTES §1).
        // Db може не бути взагалі (тести з порожнім провайдером) — тоді динамічні запитання просто не грають.
        var db = Ctx.Services.GetService<Db>();
        _stats ??= new SkilkyStats(db, Ctx.Clock);
        _seen ??= new SkilkySeen(db);

        Array.Clear(_scores);
        Array.Clear(_answers);
        Array.Clear(_answeredAt);
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
    /// Різні запитання обраних тем на партію: спершу ті, яких ніхто за столом ще не бачив, далі — бачені
    /// найдавніше. Динамічне беремо лише тоді, коли база вже щось назбирала: питати «скільки треків
    /// зіграло», коли відповідь нуль, — не загадка, а знущання.
    /// </summary>
    List<(SkilkyQuestion Q, double A)> Pick()
    {
        var pool = new List<(SkilkyQuestion Q, double A)>();
        foreach (var q in SkilkyBank.All)
        {
            if (!SkilkyTopics.Fits(q, _topics)) continue;
            if (q.IsDynamic)
            {
                var value = _stats!.Value(q.Dyn);
                if (value > 0) pool.Add((q, value));
            }
            else if (q.A is { } a) pool.Add((q, a));
        }
        // Повне тасування Фішера — Єйтса: серед однаково свіжих (зокрема всіх небачених) порядок випадковий.
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var last = _seen!.LastSeen(Nicks());
        return SkilkySeen.Freshest(pool, x => x.Q.Key, last, _questions);
    }

    /// <summary>Ключі ніків за столом — так їх пам'ятає <see cref="SkilkySeen"/>.</summary>
    List<string> Nicks() => [.. Enumerable.Range(0, MaxSeats)
        .Where(Ctx.Seated).Select(s => Ctx.NickOf(s)).OfType<string>().Select(SkilkySeen.NickKey).Distinct()];

    /// <summary>Нове запитання: чистий стіл і коротке «готуйсь».</summary>
    void Open(DateTimeOffset now)
    {
        Array.Clear(_answers);
        Array.Clear(_answeredAt);
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
        // Час саме останнього числа: хто передумав, той і відповів пізніше.
        _answeredAt[seat] = Ctx.Clock.UtcNow;
        _touched = true;
        // Реалтайм-кімната не розсилає види з Act (Rooms.Act, counts == false), тож підтвердження
        // гравцеві — оцей рядок; галочки в усіх інших приїдуть найближчим тиком.
        return ActResult.Accept($"Записав: {(Current is { } q && IsYears(q) && value == Math.Round(value) ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : Num(value))}");
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
                _endsAt = now.AddSeconds(_seconds);
                // «Бачив» — з тієї секунди, коли запитання з'явилось на екрані. Ті, до яких недограна партія
                // так і не дійшла, лишаються свіжими.
                _seen!.Mark(Nicks(), _asked[_at].Q, now);
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
    /// (<see cref="Accuracy"/>). Найближчим — ще <see cref="BestBonus"/> (двоє однаково близьких беруть обидва,
    /// і навіть коли всі далеко: тоді найближчий хоч одне очко таки має). У кожній групі однакової відстані
    /// той, хто відповів раніше за решту хоча б на <see cref="SpeedGap"/>, бере ще <see cref="SpeedBonus"/>.
    /// </summary>
    void Reveal(DateTimeOffset now)
    {
        var (question, target) = _asked[_at];
        var years = IsYears(question);
        var sorted = new List<Row>();
        for (var s = 0; s < MaxSeats; s++)
            if (Ctx.Seated(s) && _answers[s] is { } raw)
            {
                var v = InUnits(raw, target, question.Unit);
                sorted.Add(new Row(s, v, Math.Abs(v - target), Accuracy(v, target, years)));
            }
        sorted = [.. sorted.OrderBy(r => r.Diff).ThenBy(r => r.Seat)];

        // Групи однакової відстані рахуємо з допуском, а не точною рівністю double: 36.4 і 36.8 промахнулись
        // повз 36.6 однаково, але в бітах це 0.20000000000000284 і 0.19999999999999574. Гравці побачили б
        // однакову різницю й бонус лише в одного. Усередині групи першим стоїть швидший.
        var rows = new List<Row>(sorted.Count);
        var company = Enumerable.Range(0, MaxSeats).Count(Ctx.Seated) > 1;
        for (var i = 0; i < sorted.Count;)
        {
            var j = i + 1;
            while (j < sorted.Count && SameDiff(sorted[j].Diff, sorted[i].Diff)) j++;
            var group = sorted.GetRange(i, j - i).OrderBy(r => _answeredAt[r.Seat]).ThenBy(r => r.Seat).ToList();
            var faster = group.Count > 1 && _answeredAt[group[1].Seat] - _answeredAt[group[0].Seat] >= SpeedGap;
            for (var k = 0; k < group.Count; k++)
                rows.Add(group[k] with { Bonus = i == 0 && company ? BestBonus : 0, Fast = faster && k == 0 ? SpeedBonus : 0 });
            i = j;
        }
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

    /// <summary>Множник, закладений в одиницю: «млн км» означає, що відповідь у мільйонах кілометрів.</summary>
    static double Multiplier(string? unit)
    {
        var u = unit?.TrimStart() ?? "";
        return u.StartsWith("тис", StringComparison.Ordinal) ? 1e3
            : u.StartsWith("млрд", StringComparison.Ordinal) ? 1e9
            : u.StartsWith("млн", StringComparison.Ordinal) ? 1e6
            : u.StartsWith("трлн", StringComparison.Ordinal) ? 1e12
            : 1;
    }

    /// <summary>
    /// Число в одиницях запитання. Там, де відповідь у мільярдах років, людина цілком може написати повне
    /// 13 800 000 000 — і отримала б нуль за «помилку в мільярд разів». Тож якщо число не менше за множник,
    /// беремо те прочитання (як є чи поділене), яке ближче до правди за порядком величин: 1200 «тис. км²»
    /// лишається 1200, а 357 000 стає 357.
    /// </summary>
    public static double InUnits(double guess, double target, string? unit)
    {
        var m = Multiplier(unit);
        if (m == 1 || target <= 0 || guess < m) return guess;
        var scaled = guess / m;
        return Math.Abs(Math.Log(scaled / target)) < Math.Abs(Math.Log(guess / target)) ? scaled : guess;
    }

    /// <summary>
    /// Очки за точність одного числа, без огляду на суперників — тому шкала однаково чесна вдвох і
    /// вдванадцятьох. Звичайні числа: промах до 2 % — <see cref="Bullseye"/>, до 10 % — 4, до 25 % — 3,
    /// до 50 % — 2, до двох разів — 1, далі — 0; на цілій відповіді промах на одиницю — щонайменше
    /// <see cref="OffByOne"/>. Роки: точно — 5, ±2 — 4, ±5 — 3, ±15 — 2, ±50 — 1.
    /// Межі включні й із допуском: 2,794 проти 2,54 — рівно 10 %, хоч у double це 0.10000000000000009.
    /// </summary>
    public static int Accuracy(double guess, double target, bool years)
    {
        const double eps = 1e-9;
        var diff = Math.Abs(guess - target);
        if (years)
        {
            foreach (var (off, p) in YearTiers) if (diff <= off + eps) return p;
            return 0;
        }
        // У банку відповіді лише додатні (динамічні з нулем у партію не беремо), тож ділити є на що. Нуль і
        // від'ємне число проти додатної відповіді — не порядок величин, а мимо.
        if (target <= 0) return SameDiff(guess, target) ? Bullseye : 0;
        if (guess <= 0) return 0;
        var miss = diff / target;
        var points = 0;
        foreach (var (off, p) in Tiers) if (miss <= off + eps) { points = p; break; }
        if (points == 0 && guess >= target / 2 * (1 - eps) && guess <= target * 2 * (1 + eps)) points = 1;
        // На малих цілих відсотки кусаються: 8 замість 9 — це вже 11 %, а для людини — «майже». На дробових
        // відповідях (1,852 милі) одиниця — це пів відповіді, тож там правило не діє.
        if (Math.Abs(target - Math.Round(target)) < eps && diff <= 1 + eps) points = Math.Max(points, OffByOne);
        return points;
    }

    /// <summary>
    /// Чи це та сама відстань. Допуск відносний: на числах банку (від одиниць до сотень тисяч) абсолютний
    /// поріг був би або надто грубим, або марним.
    /// </summary>
    static bool SameDiff(double a, double b) =>
        Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    /// <summary>
    /// Кінець партії: лідери беруть перемогу, а якщо ніхто не набрав жодного очка — нічия. Кожен, хто дограв,
    /// отримує черепок за кожні <see cref="PointsPerShard"/> очок — і вдвох, і самому. Це понад звичайну
    /// виплату каркаса за перемогу чи участь (та — лише в компанії й зі стелею партій на день).
    /// </summary>
    void Done()
    {
        _phase = PhaseDone;
        var seats = Enumerable.Range(0, MaxSeats).Where(Ctx.Seated).ToList();
        var best = seats.Count == 0 ? 0 : seats.Max(s => _scores[s]);
        _winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        var scores = seats.ToDictionary(s => s, s => _scores[s]);
        foreach (var s in seats)
            if (Shards(_scores[s]) is > 0 and var shards)
                // Номер партії в причині: «Ще раз» за тим самим столом — нова виплата, а не повтор старої.
                Ctx.Award(s, shards, $"points:{Ctx.Round.ToString(CultureInfo.InvariantCulture)}");
        Ctx.Finish(_winners, Summary(seats, best), scores);
    }

    /// <summary>Скільки черепків за стільки очок партії.</summary>
    public static int Shards(long points) => points <= 0 ? 0 : (int)Math.Min(int.MaxValue, points / PointsPerShard);

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
    /// Хтось встав посеред партії. Компанійська гра це переживає: решта грає далі (навіть один — тоді вже сам),
    /// а того, хто пішов, просто не рахуємо. Партія закінчується лише тоді, коли за столом нікого.
    /// </summary>
    public override void OnLeave(int seat)
    {
        _answers[seat] = null;
        var left = Enumerable.Range(0, MaxSeats).Where(s => s != seat && Ctx.Seated(s)).ToList();
        if (left.Count >= Info.MinPlayers) return;
        _phase = PhaseDone;
        _winners = [];
        Ctx.Finish(_winners, $"{Info.Title}: гравці розійшлись, партію не дограли");
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
        // Скільки триває відповідь у цій кімнаті — клієнтові для повної дуги таймера.
        seconds = _seconds,
        answered = Answered(),
        // Єдине, що в цьому виді своє для кожного місця: чуже число до розкриття не бачить ніхто.
        my = seat is { } s && s >= 0 && s < MaxSeats ? _answers[s] : null,
        reveal = _reveal is null ? null : new
        {
            answer = _answer,
            // Клієнтові треба знати, як підписати промах: «на 12 % менше» чи просто «різниця 12» для років.
            years = _years,
            rows = _reveal.Select(r => new
            {
                seat = r.Seat, value = r.Value, diff = r.Diff, points = r.Points,
                accuracy = r.Accuracy, bonus = r.Bonus, fast = r.Fast,
            }).ToArray(),
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

    /// <summary>Найближчий і той нічого не взяв за точність: хвалити нема за що, хіба одним очком за першість.</summary>
    static readonly string[] Wide =
    [
        "Ех, ніхто навіть близько. Найближче — {0}, і той повз на {1}. Одне очко — за сміливість.",
        "Мимо всі. Найменший промах — {0}: {1} убік. Тримай одне очко втіхи.",
        "Порядок величин сьогодні не з нами: найближче — {0}, різниця {1}. Очко за першість, і все.",
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
        var bank = best.Accuracy == 0 ? Wide : SameDiff(best.Diff, 0) ? Exact : Flavors;
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
