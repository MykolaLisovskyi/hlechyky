using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Що дає верстат: більше глеків за клік, глеки без тебе, множник до всього чи вправність (розгін, глеки з полиці).</summary>
public enum ClickerKind { Click, Idle, Mult, Skill }

/// <summary>Віха верстата: одноразове покращення, яке відкривається, коли верстат доріс до рівня.</summary>
public sealed record ClickerMark(int Level, string Name);

/// <summary>
/// Один верстат майстерні: скільки коштує перший рівень, що дає і чи є в нього стеля. Ціна росте в
/// <c>GrowNum/GrowDen</c> разів за кожен куплений рівень: перші верстати — у півтора раза (3/2), нові
/// дорогі — на п'ятнадцять відсотків (23/20), бо інакше в них не було б і двадцяти рівнів.
/// </summary>
/// <param name="Rate">Глеків за секунду з одного рівня (для <see cref="ClickerKind.Idle"/>).</param>
/// <param name="MaxLevel">0 — купуй скільки хочеш; більше нуля — далі цього рівня верстат не тягнеться.</param>
public sealed record ClickerUpgrade(string Key, string Name, string Desc, long Base, ClickerKind Kind,
    double Rate = 0, int MaxLevel = 0, int GrowNum = 3, int GrowDen = 2, ClickerMark[]? Marks = null)
{
    /// <summary>Віху купують за вісім цін того рівня, на якому вона відкривається.</summary>
    public const int MarkFactor = 8;

    /// <summary>ln(long.MaxValue) з запасом: далі ціна однаково не влізе в long, і рахувати її нема сенсу.</summary>
    const double LnCeiling = 43.6;

    public ClickerMark[] Steps => Marks ?? [];

    public bool Capped(int level) => MaxLevel > 0 && level >= MaxLevel;

    /// <summary>
    /// Ціна наступного рівня: <c>ceil(Base × (GrowNum/GrowDen)^level)</c>. Рахуємо цілими, бо double на
    /// високих рівнях промахується на одиницю, а ціна в магазині мусить бути та сама і в тесті, і на екрані.
    /// </summary>
    public long Price(int level)
    {
        if (level < 0) level = 0;
        if (Math.Log(Base) + level * Math.Log((double)GrowNum / GrowDen) > LnCeiling) return long.MaxValue;
        var num = BigInteger.Pow(GrowNum, level) * Base;
        var den = BigInteger.Pow(GrowDen, level);
        var price = (num + den - 1) / den;
        return price > long.MaxValue ? long.MaxValue : (long)price;
    }

    /// <summary>Ціна i-ї віхи: вісім цін її рівня.</summary>
    public long MarkPrice(int i) => Clicker.Mul(Price(Steps[i].Level), MarkFactor);
}

/// <summary>Родинний секрет: вічне покращення за клейма майстра, обпал його не забирає.</summary>
public sealed record ClickerSecret(string Key, string Name, string Desc, int Price);

/// <summary>Розпис для глека: колекція на всі обпали, кожен розпис — плюс п'ять відсотків до всього.</summary>
public sealed record ClickerStyle(string Key, string Name, long Price);

/// <summary>
/// Гончарне коло — соло-клікер на одного назавжди. Кімната приватна й persistent: закрив вкладку, прийшов
/// через тиждень — коло крутилось і без тебе (але не більше ніж вісім годин, інакше з відпустки повертались
/// би мільйонери). Валюта тут своя, глеки; у черепки вони переходять лише через прилавок, сотнями.
///
/// Понад кліки й верстати тут є ще кілька речей, заради яких варто повертатись: віхи верстатів (×2), розписний
/// глек, що з'являється на колі на кілька секунд, обпал (скинути майстерню за вічні клейма майстра) і колекція
/// розписів. А щоб і клацати було за що: розгін кола («Маховик» — швидкі кліки поспіль дають дедалі більше)
/// і глек, що раз на хвилину-дві падає з полиці: спіймав — кілька хвилин пасиву однією жменею, не спіймав —
/// черепки. Правила живуть тільки тут: клієнт батчить кліки й малює плавний долік, але кожен глек рахує
/// сервер за <see cref="IRoomContext.Clock"/>.
///
/// Від автоклікерів і скриптів коло стереже Око майстра (<see cref="ClickerGuard"/>): кліки приходять із почерком,
/// за робочий почерк і раз на кілька сотень кліків треба торкнутись глечиків на картинці, за три помилки — пауза.
/// </summary>
public sealed class Clicker : Game
{
    /// <summary>Скільки глеків іде за один черепок.</summary>
    public const int Rate = 100;
    /// <summary>Більше кліків за секунду — це вже не палець, а скрипт; зайве мовчки відкидаємо.</summary>
    public const int MaxClicksPerSecond = 12;
    /// <summary>Скільки черепків на день можна виміняти, коли економіки поруч нема (тести, гола гра).</summary>
    public const int DefaultDailyCap = 20;
    /// <summary>Скільки черепків до денної стелі додають клейма: одне місце на кожні <see cref="StampsPerCap"/>, не більше.</summary>
    public const int MaxStampCap = 20;
    public const int StampsPerCap = 10;
    /// <summary>За скільки офлайну коло ще платить. Далі — тиша: інакше відпустка коштувала б грі балансу.</summary>
    public static readonly TimeSpan OfflineCap = TimeSpan.FromHours(8);
    /// <summary>Те саме з родинним секретом «Довга ніч».</summary>
    public static readonly TimeSpan LongOfflineCap = TimeSpan.FromHours(12);
    /// <summary>Скільки рівнів можна купити одним натиском «макс».</summary>
    public const int MaxBuy = 1000;

    // ---------- розписний глек ----------

    /// <summary>Скільки розписний глек стоїть на колі.</summary>
    public static readonly TimeSpan GoldenShown = TimeSpan.FromSeconds(12);
    /// <summary>Запас на дорогу: клік, що вилетів в останню мить, приїде на сервер трохи пізніше.</summary>
    public static readonly TimeSpan CatchGrace = TimeSpan.FromSeconds(2);
    /// <summary>Годинник клієнта міряється серверним «зараз» із виду, але пінг лишається — рання мить теж рахується.</summary>
    public static readonly TimeSpan EarlyGrace = TimeSpan.FromSeconds(1);
    public const int GoldenMinSeconds = 180, GoldenMaxSeconds = 480;
    /// <summary>З «Прикметою» глеки частіші.</summary>
    public const int OmenMinSeconds = 120, OmenMaxSeconds = 360;
    public const double FairMult = 7;
    public static readonly TimeSpan FairFor = TimeSpan.FromSeconds(66);
    public const double InspireMult = 25;
    public static readonly TimeSpan InspireFor = TimeSpan.FromSeconds(15);
    /// <summary>Щедрий купець: п'ятнадцять відсотків того, що лежить, але не більше ніж чверть години роботи.</summary>
    public const double MerchantShare = 0.15;
    public const double MerchantSeconds = 900;
    public const int GoldenForAchievement = 50;

    /// <summary>Що буде в розписному глеку. Вирішується, коли глек з'являється, а гравцеві показується, лише коли впіймав.</summary>
    public enum GoldenKind { Merchant, Fair, Inspire }

    // ---------- розгін кола ----------

    /// <summary>За скільки секунд розгін спадає в e разів: перестав клацати — за кілька секунд коло стало.</summary>
    public const double HeatTau = 3.0;
    /// <summary>Стільки «гарячих» кліків — повний розгін: приблизно шість кліків за секунду протягом трьох секунд.</summary>
    public const double HeatFull = 18;
    /// <summary>Скільки до стелі розгону додає один рівень «Маховика». Без маховика коло не розганяється зовсім.</summary>
    public const double FlywheelStep = 0.5;

    // ---------- глек з полиці ----------

    /// <summary>Скільки глек летить з полиці до долівки: спіймати треба рукою, а не прочитати у виді.</summary>
    public static readonly TimeSpan FallShown = TimeSpan.FromSeconds(3.2);
    public const int FallMinSeconds = 50, FallMaxSeconds = 130;
    /// <summary>З «Котом на полиці» глеки падають частіше.</summary>
    public const int CatMinSeconds = 30, CatMaxSeconds = 80;
    /// <summary>Що в спійманому глеку: дві хвилини пасиву плюс сотня кліків — щоб і новачкові, і магнатові було за що ловити.</summary>
    public const double FallSeconds = 120;
    public const int FallClicks = 100;
    /// <summary>Дно: навіть на голому колі спійманий глек — це відчутно.</summary>
    public const int FallFloor = 20;
    /// <summary>Кожен рівень «Кошика» — плюс п'ята частина до глека з полиці.</summary>
    public const double BasketBonus = 0.2;
    /// <summary>Серія спійманих поспіль: +10 % за кожен, не більше десяти. Розбився — серія обірвалась.</summary>
    public const double StreakBonus = 0.1;
    public const int StreakMax = 10;
    public const int GrabsForAchievement = 100, StreakForAchievement = 10;

    // ---------- обпал ----------

    /// <summary>Клейма рахуються від глеків за весь час: <c>⌊√(total / 1 млрд)⌋</c>.</summary>
    public const double StampUnit = 1e9;
    public const double StampBonus = 0.02, SealStampBonus = 0.03;
    public const double StyleBonus = 0.05;
    /// <summary>Скільки рівнів дає «Родинний круг» після обпалу.</summary>
    public const int KinLevels = 10;

    /// <summary>Оголошено ДО магазину навмисно: статичні поля ініціалізуються згори вниз, а підписи верстатів
    /// уже форматують числа.</summary>
    static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("uk-UA");

    /// <summary>
    /// Магазин. Порядок тут — це порядок кнопок на екрані. Перші чотири — ті, що були з першого дня (ціни ×1,5),
    /// далі драбина, прорахована симуляцією: новий верстат приблизно раз на кілька днів гри, останні — вже
    /// після обпалів.
    /// </summary>
    public static readonly ClickerUpgrade[] Shop =
    [
        new("wheel", "Швидше коло", "+1 глек за клік", 15, ClickerKind.Click,
            Marks: [new(10, "Ножний привід"), new(25, "Легка рука"), new(50, "Руки майстра")]),
        new("apprentice", "Підмайстер", "+0,5 глека за секунду", 100, ClickerKind.Idle, Rate: 0.5,
            Marks: [new(10, "Учні з Опішні"), new(25, "Кухоль узвару"), new(50, "Цехова грамота")]),
        new("kiln", "Піч", "+3 глеки за секунду", 1_000, ClickerKind.Idle, Rate: 3,
            Marks: [new(10, "Дубові дрова"), new(25, "Двоярусний горн"), new(50, "Вічний вогонь")]),
        new("clay", "Гарна глина", "×1,25 до всього", 10_000, ClickerKind.Mult, MaxLevel: 5),
        new("flywheel", "Маховик", "Швидкі кліки поспіль розкручують коло: +0,5 до стелі розгону", 250, ClickerKind.Skill, MaxLevel: 8),
        new("basket", "Кошик під полицею", "+20 % до глеків, що падають з полиці", 2_500, ClickerKind.Skill, MaxLevel: 10),
        Tier("workshop", "Гончарня", 100_000, 25, "Новий дах", "Полиці до стелі", "Вивіска на всю вулицю"),
        Tier("fair", "Ярмарок у Сорочинцях", 2_000_000, 150, "Намет із прапорцем", "Ярмаркові зазивали", "Гоголь приїхав"),
        Tier("artel", "Артіль в Опішні", 50_000_000, 900, "Спільна глина", "Артільний кошовий", "Знак Опішні"),
        Tier("chumaks", "Чумацький обоз", 1_000_000_000, 5_000, "Сіль у дорогу", "Круторогі воли", "Чумацький Шлях"),
        Tier("pit", "Глинище", 25_000_000_000, 32_000, "Голуба глина", "Кінний підйомник", "Глибокий пласт"),
        Tier("school", "Школа гончарів", 500_000_000_000, 200_000, "Підручник гончаря", "Майстер-клас", "Випускний у глині"),
        Tier("chaika", "Чайка до Царграда", 12_000_000_000_000, 1_200_000, "Козацька чайка", "Попутний вітер", "Царградський базар"),
        Tier("museum", "Музей гончарства", 250_000_000_000_000, 7_000_000, "Екскурсовод", "Вітрина скарбів", "Ніч у музеї"),
        Tier("tsar", "Цар-глек", 5_000_000_000_000_000, 45_000_000, "Глек на всю хату", "Глек на все село", "Глек видно з Місяця"),
    ];

    static ClickerUpgrade Tier(string key, string name, long price, double rate, string m25, string m50, string m100) =>
        new(key, name, $"+{Short(rate)} {Pots(rate)} за секунду", price, ClickerKind.Idle, Rate: rate,
            GrowNum: 23, GrowDen: 20, Marks: [new(25, m25), new(50, m50), new(100, m100)]);

    /// <summary>Родинні секрети — за клейма майстра, від дешевого до дорогого.</summary>
    public static readonly ClickerSecret[] Secrets =
    [
        new("night", "Довга ніч", "Коло крутиться без тебе 12 годин замість 8", 3),
        new("omen", "Прикмета", "Розписні глеки з'являються частіше", 5),
        new("cat", "Кіт на полиці", "Глеки падають з полиці частіше — котові нудно", 6),
        new("kin", "Родинний круг", $"Після обпалу коло, підмайстри й піч одразу на рівні {KinLevels}", 8),
        new("longfair", "Довгий ярмарок", "Бонуси розписних глеків тривають удвічі довше", 12),
        new("recipe", "Бабусин рецепт", "Гарна глина не згорає при обпалі", 20),
        new("memory", "Пам'ять рук", "Віхи верстатів не згорають при обпалі", 40),
        new("seal", "Родове клеймо", "Кожне клеймо дає +3 % замість +2 %", 80),
    ];

    /// <summary>Розписи — від чорнодимленого до трипільського. Колекція лишається назавжди.</summary>
    public static readonly ClickerStyle[] Styles =
    [
        new("gavarets", "Гаварецька чорнодимлена", 1_000_000),
        new("vasylkiv", "Васильківська майоліка", 25_000_000),
        new("bubnivka", "Бубнівська", 500_000_000),
        new("kosiv", "Косівська", 10_000_000_000),
        new("opishnia", "Опішнянська", 250_000_000_000),
        new("mezhyhirya", "Межигірський фаянс", 5_000_000_000_000),
        new("petrykivka", "Петриківський розпис", 100_000_000_000_000),
        new("trypillia", "Трипільська", 2_000_000_000_000_000),
    ];

    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "clicker", "Гончарне коло", "гончарне коло", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Крути коло, ліпи глеки, лови розписні. Глеки — у черепки, майстерню — в обпал за клейма майстра. "
            + "Коло крутиться і без тебе, поки ти слухаєш радіо");

    readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);
    /// <summary>Куплені віхи, ключ — «верстат:рівень».</summary>
    readonly HashSet<string> _marks = new(StringComparer.Ordinal);
    readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    readonly HashSet<string> _styles = new(StringComparer.Ordinal);
    string _wear = "";

    /// <summary>
    /// Відро дозволів на кліки: дванадцять на дні, доливається дванадцять за секунду. Жорстке вікно «12 за
    /// останню секунду» тут не годиться — клієнт шле пачку раз на 700 мс, і дві сусідні пачки завжди падали б
    /// в одне вікно, з'їдаючи в чесного гравця кожен другий клік. Відро тримає ту саму швидкість, але не
    /// карає за те, що кліки приїхали купкою.
    /// </summary>
    double _tokens;
    DateTimeOffset _tokensAt;

    /// <summary>Око майстра: почерк кліків, перевірка картинкою й пауза кола (див. <see cref="ClickerGuard"/>).</summary>
    readonly ClickerGuard _guard = new();

    long _pots;
    long _total;
    /// <summary>Недоліплений глек: пасив рідко дає ціле число, а губити півглека щосекунди — це половина доходу.</summary>
    double _carry;
    DateTimeOffset _lastSync;
    string _soldDay = "";
    int _soldShards;
    /// <summary>Останнє число, яке вже пішло в таблицю: те саме слати вдруге — марно смикати базу.</summary>
    long _scored = -1;
    DateTimeOffset _scoredAt;
    IOptionsMonitor<EconomyOptions>? _opts;

    GoldenRow _golden = new(default, default, GoldenKind.Merchant, 0, 0);
    DateTimeOffset _fairUntil;
    DateTimeOffset _inspireUntil;
    int _caught;
    int _stamps;
    int _firings;

    /// <summary>
    /// Розгін кола: «гарячі» кліки, що спадають експоненційно (<see cref="HeatTau"/>). Рахуємо на мить
    /// <c>_heatAt</c>; що більше кліків підряд — то більший множник, аж до стелі маховика.
    /// </summary>
    double _heat;
    DateTimeOffset _heatAt;

    /// <summary>Наступний глек з полиці, серія спійманих поспіль і спіймані за весь час.</summary>
    FallRow _fall = new(default, default, 0);
    int _fallStreak;
    int _grabbed;

    /// <summary>
    /// Як часто число з таблиці оновлюється під час клацання. Кожна пачка кліків — це запис у ту саму
    /// SQLite, у яку пише ефір, тож півхвилини затримки в таблиці «Гончарі» коштують дешевше, ніж
    /// півтора запису на секунду з кожного гончаря.
    /// </summary>
    static readonly TimeSpan ScoreEvery = TimeSpan.FromSeconds(30);

    /// <summary>Пороги ачівок: платформа бачить їх саме з таблиці, тож ці числа мусять летіти негайно,
    /// а не чекати своєї півхвилини.</summary>
    static readonly long[] Milestones = [1_000, 100_000, 1_000_000, 1_000_000_000, 1_000_000_000_000];

    // ---------- те, з чого складається дохід ----------

    int Level(string key) => _levels.TryGetValue(key, out var n) ? n : 0;

    bool Has(string secret) => _secrets.Contains(secret);

    static string MarkKey(ClickerUpgrade up, int i) => $"{up.Key}:{up.Steps[i].Level}";

    int MarksOf(ClickerUpgrade up)
    {
        var n = 0;
        for (var i = 0; i < up.Steps.Length; i++)
            if (_marks.Contains(MarkKey(up, i))) n++;
        return n;
    }

    /// <summary>
    /// Множник до всього: глина (1,25^n), розписи (+5 % кожен) і клейма (+2 % кожне, з «Родовим клеймом» +3 %).
    /// Складаються множенням між собою, а всередині кожного — додаванням, як і обіцяє підпис.
    /// </summary>
    double AllMult => Math.Pow(1.25, Level("clay"))
        * (1 + StyleBonus * _styles.Count)
        * (1 + (Has("seal") ? SealStampBonus : StampBonus) * _stamps);

    /// <summary>Скільки глеків за секунду дає один наступний рівень верстата (без ярмарку).</summary>
    double GainOf(ClickerUpgrade up) => up.Rate * Math.Pow(2, MarksOf(up)) * AllMult;

    /// <summary>Глеків за секунду без тебе — без ярмарку розписного глека.</summary>
    double PassiveBase
    {
        get
        {
            var sum = 0.0;
            foreach (var up in Shop)
                if (up.Kind == ClickerKind.Idle) sum += Level(up.Key) * up.Rate * Math.Pow(2, MarksOf(up));
            return sum * AllMult;
        }
    }

    bool FairOn => Ctx.Clock.UtcNow < _fairUntil;
    bool InspireOn => Ctx.Clock.UtcNow < _inspireUntil;

    /// <summary>Скільки пасиву йде в кожен клік: «Легка рука» — один відсоток, «Руки майстра» — ще два.</summary>
    double ClickShare
    {
        get
        {
            var wheel = Shop[0];
            return (_marks.Contains(MarkKey(wheel, 1)) ? 0.01 : 0) + (_marks.Contains(MarkKey(wheel, 2)) ? 0.02 : 0);
        }
    }

    /// <summary>
    /// Глеків за один клік без бонусів розписного глека. Клік мусить бути цілим числом — «+1,25 глека» на
    /// екрані виглядало б як помилка, тож множник глини тут округлюємо, а дробову частину віддаємо пасиву,
    /// де вона рахується чесно.
    /// </summary>
    long ClickBase
    {
        get
        {
            var wheel = Shop[0];
            var hands = (1 + Level("wheel")) * (_marks.Contains(MarkKey(wheel, 0)) ? 2 : 1) * AllMult;
            return Math.Max(1, ToLong(Math.Round(hands + PassiveBase * ClickShare, MidpointRounding.AwayFromZero)));
        }
    }

    /// <summary>Глеків за один клік просто зараз — з натхненням і ярмарком, якщо вони тривають.</summary>
    public long PerClick => ToLong(ClickBase * (InspireOn ? InspireMult : 1) * (FairOn ? FairMult : 1));

    /// <summary>Глеків за секунду без тебе просто зараз.</summary>
    public double PerSecond => PassiveBase * (FairOn ? FairMult : 1);

    /// <summary>Стеля розгону: ×1 без маховика (коло не розганяється), +0,5 за кожен його рівень — до ×5.</summary>
    public double MomentumMax => 1 + FlywheelStep * Level("flywheel");

    /// <summary>Розгін на мить <paramref name="now"/>: те, що було, спадає в e разів за <see cref="HeatTau"/> секунд.</summary>
    double HeatAt(DateTimeOffset now) =>
        _heatAt == default || !(_heat > 0) ? 0 : _heat * Math.Exp(-Math.Max(0, (now - _heatAt).TotalSeconds) / HeatTau);

    /// <summary>Множник кліка від розгону: лінійно від ×1 на холодному колі до стелі на <see cref="HeatFull"/> гарячих кліках.</summary>
    public double MomentumOf(double heat) => 1 + (MomentumMax - 1) * Math.Min(1, Math.Max(0, heat) / HeatFull);

    /// <summary>
    /// Що дасть глек з полиці, якщо спіймати його просто зараз: дві хвилини пасиву й сотня кліків, помножені на
    /// кошик, серію і ярмарок, плюс дно. Ця сама сума їде у вид — клієнт малює її над спійманим глеком, не чекаючи відповіді.
    /// </summary>
    long FallGain()
    {
        var raw = PassiveBase * FallSeconds + (double)ClickBase * FallClicks;
        var mult = (1 + BasketBonus * Level("basket")) * (1 + StreakBonus * Math.Min(_fallStreak, StreakMax)) * (FairOn ? FairMult : 1);
        return Sum(ToLong(raw * mult), FallFloor);
    }

    TimeSpan OfflineNow => Has("night") ? LongOfflineCap : OfflineCap;

    /// <summary>
    /// Стеля обміну на сьогодні: з налаштувань економіки (без неї — типова) плюс те, що заробили клейма, але
    /// не вище за <see cref="EconomyOptions.ClickerDailyCapMax"/>. Рівно те саме число пропустить і економіка
    /// (Rewards.ClickerCap): покажи гра більше — глеки списались би, а черепки не прийшли. Нульова стеля —
    /// обмін вимкнено, і клейма його не вмикають.
    /// </summary>
    int BaseCap => Math.Max(0, _opts?.CurrentValue.ClickerDailyCap ?? DefaultDailyCap);
    int CapMax => Math.Max(BaseCap, _opts?.CurrentValue.ClickerDailyCapMax ?? DefaultDailyCap + MaxStampCap);
    int DailyCap => BaseCap == 0 ? 0 : Math.Min(BaseCap + Math.Min(MaxStampCap, _stamps / StampsPerCap), CapMax);
    /// <summary>Скільки черепків до стелі справді додали клейма (після всіх обмежень).</summary>
    int StampCap => DailyCap - BaseCap;

    /// <summary>Скільки вже виміняно САМЕ сьогодні: після півночі лічильник сам стає нулем.</summary>
    int SoldToday => _soldDay == Days.Today(Ctx.Clock) ? _soldShards : 0;

    /// <summary>Скільки клейм дають глеки за весь час — усього, а не «ще».</summary>
    public static int StampsFor(long total) => total <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Floor(Math.Sqrt(total / StampUnit)));

    /// <summary>Скільки глеків за весь час треба для n клейм.</summary>
    public static long TotalFor(int stamps) => ToLong((double)stamps * stamps * StampUnit);

    int StampsSpent => Secrets.Where(s => _secrets.Contains(s.Key)).Sum(s => s.Price);

    // ---------- життя партії ----------

    public override string SeatName(int seat) => "гончар";

    public override void Start()
    {
        // Сервіси беремо тут: гру створює реєстр конструктором без параметрів, а в тестах економіки
        // взагалі нема — тоді стеля обміну лишається типовою, і гра від цього не ламається.
        _opts = Ctx.Services.GetService<IOptionsMonitor<EconomyOptions>>();

        _pots = 0;
        _total = 0;
        _carry = 0;
        _scored = -1;
        _scoredAt = default;
        _levels.Clear();
        foreach (var up in Shop) _levels[up.Key] = 0;
        _marks.Clear();
        _secrets.Clear();
        _styles.Clear();
        _wear = "";
        _soldDay = Days.Today(Ctx.Clock);
        _soldShards = 0;
        _lastSync = Ctx.Clock.UtcNow;
        _tokens = MaxClicksPerSecond;
        _tokensAt = _lastSync;
        _fairUntil = default;
        _inspireUntil = default;
        _caught = 0;
        _stamps = 0;
        _firings = 0;
        ScheduleGolden(_lastSync);
        _guard.Reset(Ctx.Rng);
        _heat = 0;
        _heatAt = _lastSync;
        _fallStreak = 0;
        _grabbed = 0;
        ScheduleFall(_lastSync);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        // Пасив дораховуємо перед кожною дією: і клік, і покупка мусять бачити однакове число глеків.
        Sync();
        var result = action switch
        {
            "spin" => Spin(payload),
            "buy" => Buy(payload),
            "mark" => BuyMark(payload),
            "sell" => Sell(payload),
            "catch" => Catch(),
            "grab" => Grab(),
            // Клієнт питає свіжий вид, коли розписний глек утік чи глек з полиці розбився: наступний розклад знає лише сервер.
            "look" => ActResult.Done,
            "fire" => Fire(),
            "secret" => BuySecret(payload),
            "paint" => Paint(payload),
            "wear" => Wear(payload),
            "answer" => Answer(payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
        // Таблиця «Гончарі» — це глеки за весь час; те саме число вдруге їй нічого не додасть.
        if (result.Ok && WorthScoring())
        {
            _scored = _total;
            _scoredAt = Ctx.Clock.UtcNow;
            Ctx.Score(0, _total);
        }
        return result;
    }

    /// <summary>
    /// Чи варто зараз оновлювати рядок у таблиці. Клієнт шле пачку кліків раз на 700 мс, і <c>_total</c>
    /// росте від кожної — писати в базу півтора рази на секунду задорого. Тому під час клацання число
    /// оновлюється раз на <see cref="ScoreEvery"/>, а пороги ачівок ідуть одразу.
    /// </summary>
    bool WorthScoring()
    {
        if (_total == _scored) return false;
        if (_scored < 0) return true;                                    // перше число після Start/Load
        if (Ctx.Clock.UtcNow - _scoredAt >= ScoreEvery) return true;
        foreach (var mark in Milestones)
            if (_scored < mark && _total >= mark) return true;
        return false;
    }

    /// <summary>
    /// Пасив від останньої синхронізації. Стеля 8 год — не за сесію, а за один проміжок: хто заходить
    /// щодня, її й не помітить, а хто зник на тиждень, дістане рівно вісім годин роботи підмайстрів.
    /// Ярмарок множить лише ту частину проміжку, яку він справді тривав.
    /// </summary>
    void Sync()
    {
        RollDay();
        var now = Ctx.Clock.UtcNow;
        var from = _lastSync;
        _lastSync = now;
        if (now > from)
        {
            var paid = now - from;
            if (paid > OfflineNow) paid = OfflineNow;
            // Ярмарок завжди починається з дії (а дія спершу синхронізує), тож він не може початись раніше
            // за from: досить обрізати його кінцем проміжку.
            var fair = _fairUntil > from ? (_fairUntil < now ? _fairUntil : now) - from : TimeSpan.Zero;
            if (fair > paid) fair = paid;
            Earn(PassiveBase * (paid.TotalSeconds + (FairMult - 1) * fair.TotalSeconds));
        }
        // Утік — наступний. Від «зараз», а не від кінця старого: хто повернувся за добу, не мусить
        // перебирати пропущені глеки, щоб дійти до сьогоднішнього.
        if (now > _golden.Until + CatchGrace) ScheduleGolden(now);
        // Глек з полиці, якого ніхто не спіймав, — розбитий: серія обірвалась. Спійманий сюди не доходить —
        // Grab одразу ставить на полицю наступний.
        if (now > _fall.Until + CatchGrace)
        {
            _fallStreak = 0;
            ScheduleFall(now);
        }
    }

    /// <summary>Нарахувати пасив разом із недоліпленим залишком.</summary>
    void Earn(double amount)
    {
        if (!(amount > 0)) return;
        _carry += amount;
        if (_carry < 1) return;
        var whole = Math.Floor(_carry);
        _carry -= whole;
        if (!double.IsFinite(_carry) || _carry < 0 || _carry >= 1) _carry = 0;   // на трильйонах double уже не тримає дробів
        Add(ToLong(whole));
    }

    void Add(long pots)
    {
        _pots = Sum(_pots, pots);
        _total = Sum(_total, pots);
    }

    /// <summary>
    /// Перекинути лічильник обміну на новий день. Робиться одним місцем навмисно: доти, доки день чистили
    /// «по дорозі», вчорашня сума встигала переїхати в сьогодні й з'їдала гравцеві цілу денну стелю.
    /// </summary>
    void RollDay()
    {
        var today = Days.Today(Ctx.Clock);
        if (_soldDay == today) return;
        _soldDay = today;
        _soldShards = 0;
    }

    void ScheduleGolden(DateTimeOffset from)
    {
        var (min, max) = Has("omen") ? (OmenMinSeconds, OmenMaxSeconds) : (GoldenMinSeconds, GoldenMaxSeconds);
        var at = from + TimeSpan.FromSeconds(min + Ctx.Rng.NextDouble() * (max - min));
        var roll = Ctx.Rng.Next(100);
        var kind = roll < 45 ? GoldenKind.Merchant : roll < 85 ? GoldenKind.Fair : GoldenKind.Inspire;
        // Де саме на сцені: лівий верхній кут у відсотках. Глек завширшки ~58 px, сцена на телефоні ~300 px —
        // тож праворуч лишаємо чверть, щоб він не вилазив за картку.
        _golden = new GoldenRow(at, at + GoldenShown, kind, Ctx.Rng.Next(4, 77), Ctx.Rng.Next(2, 70));
    }

    /// <summary>Коли й де впаде наступний глек з полиці: за хвилину-дві (з котом — частіше), x — у відсотках сцени.</summary>
    void ScheduleFall(DateTimeOffset from)
    {
        var (min, max) = Has("cat") ? (CatMinSeconds, CatMaxSeconds) : (FallMinSeconds, FallMaxSeconds);
        var at = from + TimeSpan.FromSeconds(min + Ctx.Rng.NextDouble() * (max - min));
        _fall = new FallRow(at, at + FallShown, Ctx.Rng.Next(8, 80));
    }

    // ---------- дії ----------

    /// <summary>
    /// Клік. Клієнт батчить: замість дванадцяти повідомлень за секунду шле одне з пачкою відбитків
    /// <c>c: [[dt, press, x, y, src], …]</c> — по одному на кожен справжній натиск (див. <see cref="ClickerGuard"/>).
    /// Понад дванадцять кліків на секунду не рахуємо, але й не сваримось — чесний гравець із лагом не має
    /// бачити червоних тостів через власний інтернет. Так само мовчки не рахуємо, поки коло стоїть чи майстер
    /// чекає відповіді: чому — покаже вид.
    /// </summary>
    ActResult Spin(JsonElement payload)
    {
        // Кліки без почерку не приймаємо зовсім: так клацав би кожен скрипт. Стара вкладка (до Ока майстра) теж
        // тут — їй досить перезавантажитись.
        if (ClickerGuard.Parse(payload) is not { } hands)
            return ActResult.Fail("Коло оновилось — перезавантаж сторінку");
        var now = Ctx.Clock.UtcNow;
        if (_guard.Locked(now) || _guard.Pending) return ActResult.Done;

        if (_guard.Judge(hands) is { } why)
        {
            // Пачка, на якій почерк видав робота, не рахується, і далі — жодного кліка, доки не пройде полицю.
            // Глеків, зароблених раніше, не забираємо: так само виглядають тачпад і рівна рука на грубому таймері.
            _guard.Suspect(why);
            return ActResult.Done;
        }

        var taken = Math.Min(hands.Count, Allowance());
        _tokens -= taken;
        Add(ClickGain(taken));
        _guard.Spend(taken);
        // Під ярмарком і натхненням не перебиваємо: бонус тікає секундами, а перевірка почекає до його кінця.
        if (_guard.Due && !FairOn && !InspireOn) _guard.Check();
        return ActResult.Done;
    }

    /// <summary>
    /// Відповідь майстрові: торкання полиці <c>taps: [[x, y], …]</c> у пікселях картинки. Влучив — коло крутиться
    /// далі; не влучив — нова полиця; три помилки поспіль — коло стоїть десять хвилин. Невдача — теж успішна дія
    /// (Done): стан змінився (нова полиця, лічильник спроб), і вид мусить до гравця долетіти.
    /// </summary>
    ActResult Answer(JsonElement payload)
    {
        var now = Ctx.Clock.UtcNow;
        if (!_guard.Pending) return ActResult.Fail("Майстер ні про що не питає — крути коло");
        if (_guard.Locked(now)) return ActResult.Fail($"Коло стоїть ще {Wait(_guard.LockUntil - now)}");
        if (ClickerGuard.Taps(payload) is not { } taps) return ActResult.Fail("Торкання прийшли зіпсовані");
        return _guard.Answer(taps, now, Ctx.Rng) == ClickerGuard.Verdict.Passed
            ? ActResult.Accept("👁 Майстер кивнув — крути далі")
            : ActResult.Done;
    }

    /// <summary>«9:05» — скільки ще стояти колу.</summary>
    static string Wait(TimeSpan left)
    {
        var s = (int)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Скільки кліків відро готове віддати просто зараз; заразом і доливає його.</summary>
    int Allowance()
    {
        var now = Ctx.Clock.UtcNow;
        var seconds = Math.Max(0, (now - _tokensAt).TotalSeconds);
        _tokens = Math.Min(MaxClicksPerSecond, _tokens + seconds * MaxClicksPerSecond);
        _tokensAt = now;
        return (int)Math.Floor(_tokens);
    }

    /// <summary>
    /// Купити рівні верстата: <c>n</c> — скільки хочеться (кнопки «×10» і «макс»), купується стільки, скільки
    /// влазить у глеки. Хоч один — уже покупка; жодного — відмова з тим, чого бракує.
    /// </summary>
    ActResult Buy(JsonElement payload)
    {
        if (Shop.FirstOrDefault(u => u.Key == Str(payload, "key")) is not { } up)
            return ActResult.Fail("Такого верстата в майстерні нема");
        var raw = Num(payload, "n");
        if (raw is <= 0) return ActResult.Fail("Скільки купити — хоч один");
        var want = (int)Math.Clamp(raw ?? 1, 1, MaxBuy);

        var level = Level(up.Key);
        if (up.Capped(level)) return ActResult.Fail($"{up.Name}: кращої вже не буває");

        var bought = 0;
        while (bought < want && !up.Capped(level))
        {
            var price = up.Price(level);
            if (_pots < price)
            {
                if (bought == 0) return ActResult.Fail($"Бракує глеків: треба ще {Short(price - _pots)}");
                break;
            }
            _pots -= price;
            level++;
            bought++;
        }
        _levels[up.Key] = level;
        return ActResult.Accept(bought == 1 ? $"{up.Name} — рівень {level}" : $"{up.Name} +{bought} — рівень {level}");
    }

    /// <summary>Віха: відкривається рівнем верстата, купується один раз.</summary>
    ActResult BuyMark(JsonElement payload)
    {
        var key = Str(payload, "key");
        foreach (var up in Shop)
            for (var i = 0; i < up.Steps.Length; i++)
            {
                if (MarkKey(up, i) != key) continue;
                var step = up.Steps[i];
                if (_marks.Contains(key)) return ActResult.Fail($"«{step.Name}» уже є");
                if (Level(up.Key) < step.Level) return ActResult.Fail($"Спершу {up.Name} до рівня {step.Level}");
                var price = up.MarkPrice(i);
                if (_pots < price) return ActResult.Fail($"Бракує глеків: треба ще {Short(price - _pots)}");
                _pots -= price;
                _marks.Add(key);
                return ActResult.Accept($"«{step.Name}»: {MarkDesc(up, i)}");
            }
        return ActResult.Fail("Такої віхи нема");
    }

    static string MarkDesc(ClickerUpgrade up, int i) => up.Kind != ClickerKind.Click
        ? $"{up.Name} ×2"
        : i switch { 0 => "клік ×2", 1 => "клік +1 % пасиву", _ => "клік ще +2 % пасиву" };

    /// <summary>Прилавок: сотня глеків за черепок, не більше <see cref="DailyCap"/> черепків на день.</summary>
    ActResult Sell(JsonElement payload)
    {
        RollDay();   // після київської півночі лічильник дня чистий — і перевірка, і запис бачать нуль
        var pots = Num(payload, "pots") ?? 0;
        if (pots <= 0 || pots % Rate != 0) return ActResult.Fail($"Міняю сотнями: {Rate} глеків — один черепок");
        if (pots > _pots) return ActResult.Fail("Стільки глеків ще не наліплено");

        var left = Math.Max(0, DailyCap - _soldShards);
        if (left <= 0) return ActResult.Fail("Сьогодні черепки скінчились, приходь завтра");
        // Рахуємо в long і ріжемо стелею ДО приведення: глеків у стані може лежати скільки завгодно
        // (стан — це JSON у базі), а (int) від такої частки мовчки загорнувся б у мінус.
        var want = pots / Rate;
        if (want > left) return ActResult.Fail($"Сьогодні лишилось {left} — більше не візьму");
        var shards = (int)want;

        _pots -= pots;
        _soldShards += shards;
        // Стеля дня в економіці своя (Economy:ClickerDailyCap). Коли клейма її підняли, кажемо економіці
        // власне число — вона однаково не пустить вище за Economy:ClickerDailyCapMax.
        Ctx.Award(0, shards, StampCap > 0 ? $"clicker:{DailyCap}" : "clicker");
        return ActResult.Accept($"Обміняв {pots} глеків на {shards} {Shards(shards)}");
    }

    /// <summary>Розписний глек: впіймав у вікні — бонус, запізнився — він уже втік.</summary>
    ActResult Catch()
    {
        var now = Ctx.Clock.UtcNow;
        if (now < _golden.At - EarlyGrace || now > _golden.Until + CatchGrace)
            return ActResult.Fail("Розписний глек уже втік");
        // Глек ловиться скриптом так само легко, як клацається коло (вид знає, де й коли він стоїть), тож і тут
        // Око майстра: поки коло стоїть чи чекає відповіді, глеки не ловляться, а кожен спійманий наближає
        // перевірку. Хто лише ловить глеки й не клацає, зустріне майстра тут, а не в Spin.
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");
        if (_guard.Due && !FairOn && !InspireOn)
        {
            _guard.Check();
            return ActResult.Accept("👁 Майстер хоче глянути на твої руки — торкнись глечиків");
        }

        var longer = Has("longfair") ? 2 : 1;
        string text;
        switch (_golden.Kind)
        {
            case GoldenKind.Fair:
                _fairUntil = now + FairFor * longer;
                text = $"🎪 Ярмарок! Усе ×{FairMult:0} на {(FairFor * longer).TotalSeconds:0} с";
                break;
            case GoldenKind.Inspire:
                _inspireUntil = now + InspireFor * longer;
                text = $"✨ Натхнення! Клік ×{InspireMult:0} на {(InspireFor * longer).TotalSeconds:0} с";
                break;
            default:
                var gain = Sum(ToLong(Math.Min(_pots * MerchantShare, PassiveBase * MerchantSeconds)), 13);
                Add(gain);
                text = $"🧺 Щедрий купець: +{Short(gain)} {Pots(gain)}";
                break;
        }
        _caught++;
        _guard.Spend(ClickerGuard.CatchWeight);
        if (_caught == GoldenForAchievement) Ctx.Award(0, 0, "ach:potter-golden");
        ScheduleGolden(now);
        return ActResult.Accept(text);
    }

    /// <summary>
    /// Глеків за пачку кліків з розгоном. Розгін спадає від останньої пачки, а множник беремо на середині цієї:
    /// перші її кліки холодніші за останні. Пачка, з якої відро нічого не віддало, кола не гріє. Без маховика
    /// множник рівно один — і рахуємо, як рахували, цілими, без double.
    /// </summary>
    long ClickGain(int taken)
    {
        var now = Ctx.Clock.UtcNow;
        var heat = HeatAt(now);
        var mult = MomentumOf(heat + taken / 2.0);
        if (taken > 0)
        {
            _heat = heat + taken;
            _heatAt = now;
        }
        return mult <= 1 ? Mul(PerClick, taken) : ToLong((double)PerClick * taken * mult);
    }

    /// <summary>
    /// Глек з полиці: спіймав, поки летів, — жменя глеків (див. <see cref="FallGain"/>) і плюс один до серії;
    /// не встиг — він уже черепки. Те саме Око майстра, що й у розписного: скрипт бачить у виді, коли й де
    /// впаде, тож поки коло стоїть чи майстер чекає — не ловиться, а кожен спійманий наближає перевірку.
    /// </summary>
    ActResult Grab()
    {
        var now = Ctx.Clock.UtcNow;
        if (now < _fall.At - EarlyGrace || now > _fall.Until + CatchGrace)
            return ActResult.Fail("Глек уже розбився");
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");
        if (_guard.Due && !FairOn && !InspireOn)
        {
            _guard.Check();
            return ActResult.Accept("👁 Майстер хоче глянути на твої руки — торкнись глечиків");
        }

        var gain = FallGain();
        Add(gain);
        _fallStreak++;
        _grabbed++;
        _guard.Spend(ClickerGuard.CatchWeight);
        if (_grabbed == GrabsForAchievement) Ctx.Award(0, 0, "ach:potter-grab");
        if (_fallStreak == StreakForAchievement) Ctx.Award(0, 0, "ach:potter-streak");
        ScheduleFall(now);
        return ActResult.Accept($"🤲 Спіймав! +{Short(gain)} {Pots(gain)}" + (_fallStreak > 1 ? $" · серія {_fallStreak}" : ""));
    }

    /// <summary>
    /// Обпал: глеки, верстати й віхи згорають, а клейма за глеки за весь час лишаються назавжди. Клейма
    /// рахуються від усього наліпленого, тож ранній обпал нічого не губить: пізніше дорахується решта.
    /// </summary>
    ActResult Fire()
    {
        var gain = StampsFor(_total) - _stamps;
        if (gain < 1)
            return ActResult.Fail($"Ще рано: наступне клеймо — на {Short(TotalFor(StampsFor(_total) + 1))} глеків за весь час");

        _stamps += gain;
        _firings++;
        _pots = 0;
        _carry = 0;
        foreach (var up in Shop)
            if (!(up.Key == "clay" && Has("recipe"))) _levels[up.Key] = 0;
        if (Has("kin"))
            foreach (var key in new[] { "wheel", "apprentice", "kiln" }) _levels[key] = KinLevels;
        if (!Has("memory")) _marks.Clear();

        if (_firings == 1) Ctx.Award(0, 0, "ach:potter-fire");
        var bonus = (Has("seal") ? SealStampBonus : StampBonus) * _stamps * 100;
        return ActResult.Accept($"🔥 Обпал! +{gain} {Stamps(gain)} — тепер +{bonus.ToString("0.#", Uk)} % до всього");
    }

    ActResult BuySecret(JsonElement payload)
    {
        if (Secrets.FirstOrDefault(s => s.Key == Str(payload, "key")) is not { } secret)
            return ActResult.Fail("Такого секрету в родині нема");
        if (_secrets.Contains(secret.Key)) return ActResult.Fail($"«{secret.Name}» уже знаєш");
        var free = _stamps - StampsSpent;
        if (free < secret.Price) return ActResult.Fail($"Бракує клейм: треба ще {secret.Price - free}");
        _secrets.Add(secret.Key);
        return ActResult.Accept($"🤫 {secret.Name}: {secret.Desc.ToLowerInvariant()}");
    }

    ActResult Paint(JsonElement payload)
    {
        if (Styles.FirstOrDefault(s => s.Key == Str(payload, "key")) is not { } style)
            return ActResult.Fail("Такого розпису нема");
        if (_styles.Contains(style.Key)) return ActResult.Fail($"{style.Name} уже в колекції");
        if (_pots < style.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(style.Price - _pots)}");
        _pots -= style.Price;
        _styles.Add(style.Key);
        _wear = style.Key;                     // новий розпис хочеться одразу побачити на колі
        if (_styles.Count == Styles.Length) Ctx.Award(0, 0, "ach:potter-museum");
        return ActResult.Accept($"🎨 {style.Name} — +5 % до всього");
    }

    /// <summary>Який розпис стоїть на колі. Порожній ключ — простий глиняний глек.</summary>
    ActResult Wear(JsonElement payload)
    {
        var key = Str(payload, "key");
        if (key.Length > 0 && !_styles.Contains(key)) return ActResult.Fail("Цього розпису ще нема в колекції");
        _wear = key;
        return ActResult.Done;
    }

    // ---------- слова ----------

    /// <summary>Черепок / черепки / черепків — «2 черепків» ріже око так само, як і в гаманці.</summary>
    static string Shards(long n) => Plural(n, "черепок", "черепки", "черепків");
    static string Stamps(long n) => Plural(n, "клеймо", "клейма", "клейм");

    /// <summary>Глек / глеки / глеків; дробове число — «глека» («0,5 глека»).</summary>
    static string Pots(double n) => n % 1 != 0 ? "глека" : Plural((long)Math.Min(n, long.MaxValue), "глек", "глеки", "глеків");

    static string Plural(long n, string one, string few, string many) =>
        n % 100 is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };

    /// <summary>«1,09 млн» замість «1 093 232»: мільярди цифрами не читаються. До мільйона — повне число.</summary>
    public static string Short(double n)
    {
        if (Math.Abs(n) < 1_000_000) return n % 1 == 0 ? n.ToString("#,0", Uk) : n.ToString("#,0.#", Uk);
        string[] names = ["млн", "млрд", "трлн", "квдрлн", "квнтлн"];
        var i = Math.Min(names.Length - 1, (int)Math.Floor(Math.Log10(Math.Abs(n)) / 3) - 2);
        var v = n / Math.Pow(1000, i + 2);
        return $"{v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0", Uk)} {names[i]}";
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        // Пасив рахуємо і на відкритті, а не лише при дії (так каже spec): гончар, який повернувся й
        // просто дивиться на коло, мусить одразу бачити зароблене, а не чекати першого кліка. View
        // каркас кличе під замком кімнати (Rooms.ViewsFor), тож синхронізувати тут безпечно.
        Sync();
        var all = AllMult;
        var passive = PassiveBase;
        var spent = StampsSpent;
        var stampsAll = StampsFor(_total);
        var heat = HeatAt(Ctx.Clock.UtcNow);
        return new
        {
            pots = _pots,
            total = _total,
            perClick = PerClick,
            // Клік без натхнення й ярмарку: бонуси кінчаються між видами, і клієнт мусить знати, до чого вертатись.
            clickBase = ClickBase,
            perSecond = PerSecond,
            // Без ярмарку: клієнт доліковує сам і сам вимикає ярмарок, коли той скінчиться.
            baseSecond = passive,
            upgrades = Shop.Select((u, index) => (u, index)).ToDictionary(x => x.u.Key, x => (object)new
            {
                level = Level(x.u.Key),
                price = x.u.Price(Level(x.u.Key)),
                name = x.u.Name,
                desc = x.u.Desc,
                max = x.u.MaxLevel,
                kind = x.u.Kind.ToString().ToLowerInvariant(),
                // Скільки глеків за секунду додасть наступний рівень — для підказки «окупиться за».
                gain = x.u.Kind switch
                {
                    ClickerKind.Idle => GainOf(x.u),
                    ClickerKind.Mult when !x.u.Capped(Level(x.u.Key)) => passive * 0.25,
                    _ => 0,
                },
                growth = (double)x.u.GrowNum / x.u.GrowDen,
                marks = MarksOf(x.u),
                // Справжній множник від віх: у пасивних ×2 за кожну, а в колі ×2 дає лише перша (решта — відсоток пасиву).
                boost = x.u.Kind == ClickerKind.Click
                    ? (_marks.Contains(MarkKey(x.u, 0)) ? 2 : 1)
                    : Math.Pow(2, MarksOf(x.u)),
                open = Opened(x.index),
            }, StringComparer.Ordinal),
            // Лише відкриті й ще не куплені віхи: решта клієнту ні до чого, а вид летить щопачки кліків.
            marks = Shop.SelectMany(u => u.Steps.Select((m, i) => (u, m, i)))
                .Where(x => !_marks.Contains(MarkKey(x.u, x.i)) && Level(x.u.Key) >= x.m.Level)
                .Select(x => new
                {
                    key = MarkKey(x.u, x.i), on = x.u.Key, level = x.m.Level, name = x.m.Name,
                    desc = MarkDesc(x.u, x.i), price = x.u.MarkPrice(x.i),
                })
                .ToList(),
            canSellToday = Math.Max(0, DailyCap - SoldToday),
            soldToday = SoldToday,
            cap = DailyCap,
            rate = Rate,
            // Клієнт доліковує глеки від цієї мітки — тому вона мусить бути на дроті, а не лише в пам'яті.
            lastSync = _lastSync,
            // І серверне «зараз» поруч: інакше клієнт міряв би серверну мітку своїм годинником, а збитий
            // на кілька хвилин годинник малював би сотні глеків, яких на сервері нема.
            now = Ctx.Clock.UtcNow,
            offlineHours = OfflineNow.TotalHours,
            // Наступний розписний глек. Що в ньому — секрет до першого кліка.
            golden = new { at = _golden.At, until = _golden.Until, x = _golden.X, y = _golden.Y },
            caught = _caught,
            fair = new { until = _fairUntil, mult = FairMult },
            inspire = new { until = _inspireUntil, mult = InspireMult },
            allMult = all,
            stamps = _stamps,
            stampsFree = _stamps - spent,
            stampsReady = Math.Max(0, stampsAll - _stamps),
            nextStampAt = TotalFor(stampsAll + 1),
            stampBonus = Has("seal") ? SealStampBonus : StampBonus,
            stampCap = StampCap,
            firings = _firings,
            secrets = Secrets.Select(s => new { key = s.Key, name = s.Name, desc = s.Desc, price = s.Price, owned = _secrets.Contains(s.Key) }),
            styles = Styles.Select(s => new { key = s.Key, name = s.Name, price = s.Price, owned = _styles.Contains(s.Key) }),
            wear = _wear,
            // Розгін: скільки гарячих кліків зараз і що з них виходить. Клієнт веде той самий рахунок між видами.
            heat,
            heatFull = HeatFull,
            heatTau = HeatTau,
            momentum = MomentumOf(heat),
            momentumMax = MomentumMax,
            // Наступний глек з полиці і скільки він дасть, якщо спіймати просто зараз.
            fall = new { at = _fall.At, until = _fall.Until, x = _fall.X, streak = _fallStreak, gain = FallGain() },
            grabbed = _grabbed,
            // Око майстра: null, поки коло крутиться вільно; інакше полиця-картинка (без зерна) і/або пауза.
            guard = _guard.View(Ctx.Clock.UtcNow),
        };
    }

    /// <summary>
    /// Чи показувати верстат. Драбину відкриваємо поступово: куплений, не «пасивний» або наступний після
    /// вже купленого пасивного — інакше полиця з тринадцяти карток із трильйонними цінами лякала б новачка.
    /// </summary>
    bool Opened(int index)
    {
        var up = Shop[index];
        if (up.Kind != ClickerKind.Idle || Level(up.Key) > 0) return true;
        for (var i = index - 1; i >= 0; i--)
            if (Shop[i].Kind == ClickerKind.Idle) return Level(Shop[i].Key) > 0;
        return true;                                           // перший пасивний верстат видно завжди
    }

    // ---------- збереження ----------

    /// <summary>
    /// Стан у сховищі. Форма — зі spec, з дописками: <c>carry</c>, щоб недоліплений глек не губився на F5,
    /// відро дозволів замість списку кліків (див. <see cref="Allowance"/>) і все, що додав обпал: віхи,
    /// розписний глек із бонусами, клейма, секрети й розписи. Нові поля необов'язкові — старі збереження
    /// читаються як «цього ще не було».
    /// </summary>
    sealed record SoldRow(string Day, int Shards);

    sealed record BucketRow(double Tokens, DateTimeOffset At);

    sealed record GoldenRow(DateTimeOffset At, DateTimeOffset Until, GoldenKind Kind, int X, int Y);

    sealed record FallRow(DateTimeOffset At, DateTimeOffset Until, int X);

    sealed record Snapshot(
        long Pots, long Total, double Carry, DateTimeOffset LastSync,
        Dictionary<string, int> Upgrades, SoldRow SoldToday, BucketRow Clicks,
        List<string>? Marks = null, GoldenRow? Golden = null,
        DateTimeOffset FairUntil = default, DateTimeOffset InspireUntil = default, int Caught = 0,
        int Stamps = 0, int Firings = 0, List<string>? Secrets = null, List<string>? Styles = null, string? Wear = null,
        ClickerGuard.Row? Guard = null,
        FallRow? Fall = null, int FallStreak = 0, int Grabbed = 0, double Heat = 0, DateTimeOffset HeatAt = default);

    public override string? Save() => JsonSerializer.Serialize(
        new Snapshot(_pots, _total, _carry, _lastSync,
            new Dictionary<string, int>(_levels, StringComparer.Ordinal),
            new SoldRow(_soldDay, _soldShards), new BucketRow(_tokens, _tokensAt),
            _marks.Order(StringComparer.Ordinal).ToList(), _golden, _fairUntil, _inspireUntil, _caught,
            _stamps, _firings, _secrets.Order(StringComparer.Ordinal).ToList(),
            _styles.Order(StringComparer.Ordinal).ToList(), _wear, _guard.Save(),
            _fall, _fallStreak, _grabbed, _heat, _heatAt),
        Wire);

    public override void Load(string json)
    {
        // Стан лежить у базі й міг застати попередню версію гри або чиюсь правку руками: чого не зрозуміли —
        // лишаємо чистим, але партію через це не ламаємо.
        if (JsonSerializer.Deserialize<Snapshot>(json, Wire) is not { } s) return;

        _pots = Math.Max(0, s.Pots);
        _total = Math.Max(_pots, s.Total);
        _carry = double.IsFinite(s.Carry) ? Math.Clamp(s.Carry, 0, 1) : 0;
        _lastSync = s.LastSync == default ? Ctx.Clock.UtcNow : s.LastSync;
        _scored = -1;
        _scoredAt = default;

        _levels.Clear();
        foreach (var up in Shop)
        {
            var level = s.Upgrades is not null && s.Upgrades.TryGetValue(up.Key, out var n) ? n : 0;
            _levels[up.Key] = up.MaxLevel > 0 ? Math.Clamp(level, 0, up.MaxLevel) : Math.Max(0, level);
        }

        _soldDay = s.SoldToday?.Day ?? "";
        _soldShards = Math.Max(0, s.SoldToday?.Shards ?? 0);

        // Відро переживає F5 навмисно: інакше перезавантаження сторінки скидало б захист від автоклікера.
        _tokens = s.Clicks is { } b && double.IsFinite(b.Tokens) ? Math.Clamp(b.Tokens, 0, MaxClicksPerSecond) : MaxClicksPerSecond;
        _tokensAt = s.Clicks is { At: var at } && at != default ? at : _lastSync;

        // Лише ті ключі, які гра знає: вигадана віха з бази не має множити дохід.
        var marks = Shop.SelectMany(u => u.Steps.Select((_, i) => MarkKey(u, i))).ToHashSet(StringComparer.Ordinal);
        Fill(_marks, s.Marks, marks.Contains);
        Fill(_secrets, s.Secrets, k => Secrets.Any(x => x.Key == k));
        Fill(_styles, s.Styles, k => Styles.Any(x => x.Key == k));
        _wear = s.Wear is { } w && _styles.Contains(w) ? w : "";

        _stamps = Math.Max(0, s.Stamps);
        _firings = Math.Max(0, s.Firings);
        _caught = Math.Max(0, s.Caught);
        _fairUntil = s.FairUntil;
        _inspireUntil = s.InspireUntil;
        if (s.Golden is { } g && g.Until > g.At && Enum.IsDefined(g.Kind))
            _golden = g with { X = Math.Clamp(g.X, 0, 90), Y = Math.Clamp(g.Y, 0, 90) };
        else
            ScheduleGolden(Ctx.Clock.UtcNow);    // збереження з часів до розписних глеків
        // Пауза кола й недороблена перевірка переживають F5 — інакше перезавантаження знімало б і те, і те.
        _guard.Load(s.Guard, Ctx.Rng);

        // Розгін переживає F5 теж (він однаково спаде за кілька секунд), а глек з полиці — або той, що вже
        // на розкладі, або, для збережень із часів до полиці, новий.
        _heat = double.IsFinite(s.Heat) ? Math.Clamp(s.Heat, 0, 10 * HeatFull) : 0;
        _heatAt = s.HeatAt;
        _fallStreak = Math.Max(0, s.FallStreak);
        _grabbed = Math.Max(0, s.Grabbed);
        if (s.Fall is { } f && f.Until > f.At)
            _fall = f with { X = Math.Clamp(f.X, 0, 90) };
        else
            ScheduleFall(Ctx.Clock.UtcNow);
    }

    static void Fill(HashSet<string> set, List<string>? from, Func<string, bool> known)
    {
        set.Clear();
        foreach (var k in from ?? [])
            if (k is not null && known(k)) set.Add(k);
    }

    // ---------- дрібниці ----------

    /// <summary>Додати без переповнення: глеків за місяці обпалів стає більше, ніж влазить у long.</summary>
    internal static long Sum(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

    internal static long Mul(long a, long b) =>
        a <= 0 || b <= 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;

    /// <summary>double → long зі стелею: (long) від 1e19 у C# дає сміття, а не максимум.</summary>
    internal static long ToLong(double v) =>
        !(v > 0) ? 0 : v >= 9.2e18 ? long.MaxValue : (long)v;

    static long? Num(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    static string Str(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
