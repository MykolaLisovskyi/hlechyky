using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Роль у селі. На дроті — рядком (<c>mafia</c>, <c>don</c>, <c>sheriff</c>, <c>doctor</c>, <c>maniac</c>,
/// <c>kuma</c>, <c>civil</c>). Нові ролі дописані в кінець навмисно: у збереженні партії роль лежить числом.
/// </summary>
public enum MafiaRole { Civil, Mafia, Sheriff, Doctor, Don, Maniac, Kuma }

/// <summary>Фаза партії. <c>Lobby</c> — стіл ще збирається, <c>Done</c> — уже все.</summary>
public enum MafiaPhase { Lobby, Intro, Night, Day, Vote, Done }

// ---------------------------------------------------------------------------------------------
// Форми на дроті. Записи, а не анонімні типи: вид у мафії різний для кожного місця, і збирати його
// гілками анонімних об'єктів — найкоротший шлях до «у комісара поле є, а в мирного нема, бо гілка інша».
// ---------------------------------------------------------------------------------------------

/// <summary>Гравець у списку. <c>Role</c> — null, коли цей глядач її ще не заслужив бачити.</summary>
public sealed record MafiaPlayerView(int Seat, string? Nick, bool Alive, string? Role);

/// <summary>Рядок нічного чату мафії.</summary>
public sealed record MafiaChatLine(int Seat, string Text);

/// <summary>Результат однієї перевірки комісара.</summary>
public sealed record MafiaCheckView(int Seat, bool Mafia);

/// <summary>
/// Нічна частина виду. Кожному своє: мафії — голоси й чат, комісару — перевірки, лікарю — кого рятує,
/// кумі — до кого пішла в гості, маньякові — на кого точить ножа.
/// </summary>
public sealed record MafiaNightView(
    IReadOnlyDictionary<int, int> Votes,
    IReadOnlyList<MafiaChatLine> Chat,
    IReadOnlyList<MafiaCheckView> MyCheck,
    int? Healed,
    int? Blocked,
    int? Stab);

/// <summary>Хто я в цій партії. Для глядача — null.</summary>
public sealed record MafiaMeView(string Role, bool Alive);

/// <summary>Що сталось уночі; це знає все село. <see cref="Killed"/> лишився заради сумісності — це перший із <see cref="Fallen"/>.</summary>
public sealed record MafiaDayView(int? Killed, bool Saved, int[] Fallen);

/// <summary>Підсумок партії.</summary>
public sealed record MafiaResultView(int[] Winners, string Team);

/// <summary>Скільки кого за цим столом. <see cref="Don"/> не додає людини — це один із мафіозі.</summary>
public readonly record struct MafiaCast(int Mafia, int Sheriff, int Doctor, int Maniac, int Kuma, bool Don);

/// <summary>
/// Налаштування столу так, як їх бачить картка й аі-агент: уже зведені до чисел, а не до назв опцій.
/// Це публічна частина виду — тривалості фаз і склад ролей однаково знають усі за столом.
/// </summary>
public sealed record MafiaRulesView(
    string Pace, int IntroMs, int NightMs, int DayMs, int VoteMs,
    int Mafia, bool Don, bool Sheriff, bool Doctor, bool Maniac, bool Kuma,
    bool SelfHeal, bool OpenVotes, bool Reveal, bool FirstNightKill);

/// <summary>
/// Мафія на 4–12 душ. Уся сіль у тому, що обговорення йде у звичайних Балачках, а кімната тримає лише
/// те, чого в чаті не зробиш: таємні ролі, нічні дії наосліп і чесний підрахунок голосів. Партія
/// рухається не ходами, а годинником: фази міняє <see cref="Tick"/> раз на секунду за <c>Ctx.Clock</c>,
/// тож ніхто не може ні прискорити ніч, ні розтягнути голосування.
///
/// Правила й стан живуть тільки тут. Клієнт малює те, що йому дали у виді, і шле наміри; чого у виді
/// нема — того в консолі браузера не видобути (тому гра <c>Hidden</c>, і вид збирається окремо на кожне місце).
/// </summary>
public sealed class Mafia : Game
{
    /// <summary>Скільки всі дивляться на свою роль, перш ніж село засне (спокійний темп).</summary>
    public const int IntroMs = 10_000;
    public const int NightMs = 45_000;
    /// <summary>Оголошення ранку (5 с) плюс саме обговорення (90 с) — spec §Фази.</summary>
    public const int DayMs = 5_000 + 90_000;
    public const int VoteMs = 45_000;
    public const int TickMs = 1_000;
    /// <summary>Довші репліки в нічний чат не пускаємо — це шепіт, а не лист.</summary>
    public const int MaxSayChars = 200;
    /// <summary>Скільки рядків нічного чату тримаємо в стані.</summary>
    public const int MaxChatLines = 50;
    /// <summary>Менше трьох живих — грати вже нема в що.</summary>
    public const int MinAlive = 3;
    /// <summary>Скільки разів за партію можна смикнути модель. Партія має жити й без неї.</summary>
    public const int MaxFlavors = 4;

    /// <summary>Тривалості фаз для кожного темпу: знайомство / ніч / день / голосування.</summary>
    public static readonly IReadOnlyDictionary<string, (int Intro, int Night, int Day, int Vote)> Paces =
        new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal)
        {
            ["fast"] = (8_000, 25_000, 5_000 + 45_000, 25_000),
            ["calm"] = (IntroMs, NightMs, DayMs, VoteMs),
            ["slow"] = (12_000, 60_000, 5_000 + 150_000, 60_000),
        };

    public override GameInfo Info { get; } = new(
        "mafia", "Мафія", "мафію", GameGroup.Party, 4, 12,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Rated: false,
        Options:
        [
            new GameOption("pace", "Темп", [("calm", "Спокійний"), ("fast", "Швидкий"), ("slow", "Неспішний")], "calm"),
            new GameOption("mafia", "Мафії", [("auto", "За столом"), ("1", "Одна"), ("2", "Двоє"), ("3", "Троє")], "auto"),
            new GameOption("sheriff", "Комісар", [("on", "Є"), ("off", "Нема")], "on"),
            new GameOption("doctor", "Лікар", [("auto", "За столом"), ("on", "Є"), ("off", "Нема")], "auto"),
            new GameOption("selfheal", "Лікар себе", [("on", "Може рятувати"), ("off", "Не може")], "on"),
            new GameOption("votes", "Голоси вдень", [("open", "Відкриті"), ("secret", "Таємні")], "open"),
            new GameOption("reveal", "Роль вигнаного", [("on", "Розкривати"), ("off", "Мовчати")], "on"),
            new GameOption("first", "Перша ніч", [("kill", "З кров'ю"), ("quiet", "Тиха")], "kill"),
            new GameOption("extra", "Додаткові ролі",
                [("none", "Без них"), ("don", "Дон"), ("maniac", "Маньяк"), ("kuma", "Кума")], "none", Multi: true),
        ],
        Hint: "Село засинає, прокидається мафія. Уночі — в приваті, удень — суперечки й голосування. Ведучий — Дядько Глек");

    /// <summary>Ролі — секрет, тож місця звуться нейтрально.</summary>
    public override string SeatName(int seat) => $"гравець {seat + 1}";

    // ---------- налаштування столу ----------

    string _pace = "calm";
    int _introMs = IntroMs, _nightMs = NightMs, _dayMs = DayMs, _voteMs = VoteMs;
    /// <summary>«auto» — за таблицею складу; інакше стільки мафіозі, скільки просили (у межах розумного).</summary>
    string _mafiaOpt = "auto";
    bool _sheriffOn = true;
    string _doctorOpt = "auto";
    bool _selfHeal = true;
    bool _openVotes = true;
    bool _reveal = true;
    bool _firstKill = true;
    bool _don, _maniacOn, _kumaOn;

    // ---------- стан партії ----------

    /// <summary>Місця, які грають цю партію (зайняті на момент старту), за зростанням.</summary>
    int[] _seats = [];
    /// <summary>Нік на кожному місці на старті: той, хто виїхав із села, має лишитись іменем, а не діркою.</summary>
    string?[] _nicks = [];
    readonly Dictionary<int, MafiaRole> _roles = [];
    readonly HashSet<int> _dead = [];
    /// <summary>Кого село вже роздивилось (вигнані й від'їжджі): їхню роль показуємо всім.</summary>
    readonly HashSet<int> _revealed = [];

    MafiaPhase _phase = MafiaPhase.Lobby;
    int _day;
    DateTimeOffset _endsAt;

    // ніч
    readonly Dictionary<int, int> _kill = [];      // мафіозі → на кого показав
    readonly Dictionary<int, long> _killSeq = [];  // мафіозі → номер його останнього голосу
    long _seq;
    readonly List<MafiaChatLine> _chat = [];
    readonly Dictionary<int, bool> _checks = [];   // кого комісар перевірив → що йому показали
    bool _checkedTonight;
    int? _heal;
    /// <summary>Кого лікар рятував минулої ночі — двічі поспіль ту саму людину не можна.</summary>
    int? _healedLast;
    /// <summary>Кого маньяк намітив цієї ночі.</summary>
    int? _stab;
    /// <summary>До кого кума пішла в гості цієї ночі — його нічна справа не вийде.</summary>
    int? _block;
    int? _blockedLast;

    // ранок
    int? _killed;
    bool _saved;
    int[] _fallen = [];

    // голосування
    readonly Dictionary<int, int?> _votes = [];

    // підсумок
    readonly List<string> _log = [];
    string? _team;
    int[] _winners = [];
    bool _sheriffAwarded;

    /// <summary>Щось змінилось після останньої розсилки — найближчий тик має рознести нові види.</summary>
    bool _dirty;

    /// <summary>
    /// Чим скінчилась попередня фаза («уночі не стало Петра»). Глек каже це першим реченням наступної
    /// репліки — і байдуже, чи то початок нового дня, чи оголошення переможця: інакше останнє вбивство
    /// або останнє вигнання лишалось би непроголошеним.
    /// </summary>
    string? _lead;

    // ---------- живий Глек ----------

    DjBrain? _brain;
    int _flavors;
    /// <summary>Номер партії: відповідь моделі, яка прийшла після «Ще раз», уже нікого не стосується.</summary>
    int _gen;
    readonly object _pending = new();
    readonly List<string> _lines = [];

    // =========================================================================================
    // Налаштування
    // =========================================================================================

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("pace", out var pace) && Paces.ContainsKey(pace)) _pace = pace;
        (_introMs, _nightMs, _dayMs, _voteMs) = Paces[_pace];
        if (options.TryGetValue("mafia", out var mafia)) _mafiaOpt = mafia;
        if (options.TryGetValue("sheriff", out var sheriff)) _sheriffOn = sheriff != "off";
        if (options.TryGetValue("doctor", out var doctor)) _doctorOpt = doctor;
        if (options.TryGetValue("selfheal", out var self)) _selfHeal = self != "off";
        if (options.TryGetValue("votes", out var votes)) _openVotes = votes != "secret";
        if (options.TryGetValue("reveal", out var reveal)) _reveal = reveal != "off";
        if (options.TryGetValue("first", out var first)) _firstKill = first != "quiet";
        if (options.TryGetValue("extra", out var extra))
        {
            var picked = GameOption.Split(extra);
            _don = picked.Contains("don", StringComparer.Ordinal);
            _maniacOn = picked.Contains("maniac", StringComparer.Ordinal);
            _kumaOn = picked.Contains("kuma", StringComparer.Ordinal);
        }
    }

    // =========================================================================================
    // Старт і роздача
    // =========================================================================================

    /// <summary>Скільки кого за столом на N гравців, коли ніхто нічого не крутив (spec §Ролі).</summary>
    public static (int Mafia, int Sheriff, int Doctor) Cast(int players) => players switch
    {
        <= 5 => (1, 1, 0),
        <= 8 => (2, 1, 1),
        _ => (3, 1, 1),
    };

    /// <summary>
    /// Склад цього столу з урахуванням налаштувань. Мирних мусить лишитись хоч один, а мафії — менше
    /// половини столу, інакше партія скінчилась би, не почавшись; зайві ролі відпадають у порядку
    /// «кума → маньяк → лікар → комісар», тобто спершу ті, без яких класика все одно грається.
    /// </summary>
    public MafiaCast Plan(int players)
    {
        var (autoMafia, _, autoDoctor) = Cast(players);
        var mafia = _mafiaOpt is "1" or "2" or "3" ? int.Parse(_mafiaOpt, CultureInfo.InvariantCulture) : autoMafia;
        mafia = Math.Clamp(mafia, 1, Math.Max(1, (players - 1) / 2));
        var sheriff = _sheriffOn ? 1 : 0;
        var doctor = _doctorOpt switch { "on" => 1, "off" => 0, _ => autoDoctor };
        var maniac = _maniacOn ? 1 : 0;
        var kuma = _kumaOn ? 1 : 0;

        // Одне місце тримаємо за звичайним мирним: село без мирних — це вже не мафія, а бійка ролей.
        while (mafia + sheriff + doctor + maniac + kuma > players - 1)
        {
            if (kuma > 0) kuma = 0;
            else if (maniac > 0) maniac = 0;
            else if (doctor > 0) doctor = 0;
            else if (sheriff > 0) sheriff = 0;
            else break;
        }
        return new MafiaCast(mafia, sheriff, doctor, maniac, kuma, _don && mafia > 0);
    }

    public override void Start()
    {
        _seats = [.. Enumerable.Range(0, Info.MaxPlayers).Where(Ctx.Seated)];
        _nicks = new string?[Info.MaxPlayers];
        foreach (var seat in _seats) _nicks[seat] = Ctx.NickOf(seat);

        _roles.Clear();
        _dead.Clear();
        _revealed.Clear();
        _kill.Clear();
        _killSeq.Clear();
        _chat.Clear();
        _checks.Clear();
        _votes.Clear();
        _log.Clear();
        _seq = 0;
        _checkedTonight = false;
        _heal = null;
        _healedLast = null;
        _stab = null;
        _block = null;
        _blockedLast = null;
        _killed = null;
        _saved = false;
        _fallen = [];
        _team = null;
        _winners = [];
        _sheriffAwarded = false;
        _lead = null;
        _flavors = 0;
        // Стару чергу слівець викидаємо разом із номером партії: те, що модель надумала про минулу
        // ніч, у новій партії звучало б як марення.
        lock (_pending)
        {
            _gen++;
            _lines.Clear();
        }

        _day = 1;
        Deal();
        // Сервіси беремо тут, а не в конструкторі: гру створює реєстр без параметрів. Живого Глека
        // може й не бути (нема ключа, вимкнений бот, тести) — тоді просто мовчить.
        _brain ??= Ctx.Services.GetService<DjBrain>();
        Enter(MafiaPhase.Intro);
    }

    /// <summary>Роздача ролей тасуванням Фішера — Йетса на генераторі кімнати: той самий сід дає ту саму роздачу.</summary>
    void Deal()
    {
        var cast = Plan(_seats.Length);
        var bag = new List<int>(_seats);
        for (var i = bag.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }
        var k = 0;
        for (var i = 0; i < cast.Mafia && k < bag.Count; i++)
            // Дон — перший із мафіозі в уже перетасованому мішку, тобто такий самий випадковий, як решта.
            _roles[bag[k++]] = cast.Don && i == 0 ? MafiaRole.Don : MafiaRole.Mafia;
        for (var i = 0; i < cast.Sheriff && k < bag.Count; i++) _roles[bag[k++]] = MafiaRole.Sheriff;
        for (var i = 0; i < cast.Doctor && k < bag.Count; i++) _roles[bag[k++]] = MafiaRole.Doctor;
        for (var i = 0; i < cast.Maniac && k < bag.Count; i++) _roles[bag[k++]] = MafiaRole.Maniac;
        for (var i = 0; i < cast.Kuma && k < bag.Count; i++) _roles[bag[k++]] = MafiaRole.Kuma;
        while (k < bag.Count) _roles[bag[k++]] = MafiaRole.Civil;
    }

    /// <summary>Мафія — це і рядові, і дон: усюди, де питається «чи свій», питається саме це.</summary>
    public static bool IsMafia(MafiaRole role) => role is MafiaRole.Mafia or MafiaRole.Don;

    /// <summary>Ніч, у яку ножів не виймають: перша, якщо стіл про це домовився.</summary>
    bool QuietNight => !_firstKill && _day == 1;

    // =========================================================================================
    // Фази
    // =========================================================================================

    /// <summary>Скільки триває фаза за налаштуваннями цього столу.</summary>
    int Length(MafiaPhase phase) => phase switch
    {
        MafiaPhase.Intro => _introMs,
        MafiaPhase.Night => _nightMs,
        MafiaPhase.Day => _dayMs,
        MafiaPhase.Vote => _voteMs,
        _ => 0,
    };

    /// <summary>Перейти у фазу, виставити її дедлайн і дати Глеку сказати слово (з підводкою від попередньої фази).</summary>
    void Enter(MafiaPhase phase)
    {
        _phase = phase;
        _dirty = true;
        _endsAt = Ctx.Clock.UtcNow.AddMilliseconds(Length(phase));

        switch (phase)
        {
            case MafiaPhase.Night:
                _kill.Clear();
                _killSeq.Clear();
                _checkedTonight = false;
                _healedLast = _heal;
                _heal = null;
                _blockedLast = _block;
                _block = null;
                _stab = null;
                _killed = null;
                _saved = false;
                _fallen = [];
                break;
            case MafiaPhase.Vote:
                _votes.Clear();
                break;
        }

        var line = phase switch
        {
            MafiaPhase.Intro => MafiaGlek.Pick(Ctx.Rng, MafiaGlek.Intro),
            MafiaPhase.Night => QuietNight
                ? MafiaGlek.Pick(Ctx.Rng, MafiaGlek.QuietNight)
                : MafiaGlek.Pick(Ctx.Rng, MafiaGlek.NightFall, _day),
            MafiaPhase.Day => MafiaGlek.Pick(Ctx.Rng, MafiaGlek.DayTalk),
            MafiaPhase.Vote => MafiaGlek.Pick(Ctx.Rng, MafiaGlek.VoteTime),
            _ => "",
        };

        var what = _lead;
        Say(Lead(line));
        // Живе слівце просимо рівно на початку дня — коли є про що говорити і є кому слухати.
        if (phase == MafiaPhase.Day) Flavor(what ?? "у селі настав ранок");
    }

    public override TickResult Tick()
    {
        FlushLines();
        if (_phase is MafiaPhase.Done or MafiaPhase.Lobby) return Take(false);
        // Ніч кінчається достроково, коли всім, кому було що робити, робити вже нічого.
        if (Ctx.Clock.UtcNow < _endsAt && !NightDone()) return Take(false);
        Advance();
        return Take(true);
    }

    /// <summary>Що розіслати: кадр — лише коли змінилась фаза, види — коли є що показати.</summary>
    TickResult Take(bool phaseChanged)
    {
        var views = _dirty || phaseChanged;
        _dirty = false;
        return new TickResult(phaseChanged, views);
    }

    void Advance()
    {
        switch (_phase)
        {
            case MafiaPhase.Intro: Enter(MafiaPhase.Night); break;
            case MafiaPhase.Night: ResolveNight(); break;
            case MafiaPhase.Day: Enter(MafiaPhase.Vote); break;
            case MafiaPhase.Vote: ResolveVote(); break;
        }
    }

    /// <summary>Усі нічні справи зроблено — чекати решту секунд нема сенсу.</summary>
    bool NightDone()
    {
        if (_phase != MafiaPhase.Night) return false;
        // Тиху ніч не вкорочуємо: вона саме для того й потрібна, щоб недобрі люди встигли нашепотітись.
        if (QuietNight) return false;
        var killers = false;
        foreach (var seat in Alive())
        {
            var role = _roles[seat];
            if (IsMafia(role))
            {
                killers = true;
                if (!_kill.ContainsKey(seat)) return false;
            }
            else if (role == MafiaRole.Maniac)
            {
                killers = true;
                if (_stab is null) return false;
            }
            else if (role == MafiaRole.Sheriff && !_checkedTonight) return false;
            else if (role == MafiaRole.Doctor && _heal is null) return false;
            else if (role == MafiaRole.Kuma && _block is null) return false;
        }
        // Убивць серед живих уже нема: ніч усе одно закінчиться сама, а достроково — ні, бо це
        // видало б селу, що вбивати нікому.
        return killers;
    }

    /// <summary>Ранок: рахуємо нічні справи — кого показала мафія, кого намітив маньяк, кого встиг лікар.</summary>
    void ResolveNight()
    {
        // Кума рятує від нічної справи, лише поки вона сама в селі: хто виїхав, той і в гості не сходив.
        var kuma = Alive().Any(s => _roles[s] == MafiaRole.Kuma);
        var blocked = kuma && _block is { } b && !_dead.Contains(b) ? b : (int?)null;

        var doctor = -1;
        foreach (var s in Alive()) if (_roles[s] == MafiaRole.Doctor) doctor = s;
        // Рятує лише той лікар, який ще в селі й до якого не зайшла кума: хто виїхав посеред ночі,
        // той забрав свою допомогу з собою. Симетрично до мафії, чиї нічні голоси теж рахуються від живих.
        var heal = doctor >= 0 && blocked != doctor ? _heal : null;

        var targets = new List<int>();
        if (!QuietNight)
        {
            if (KillTarget(blocked) is { } byMafia) targets.Add(byMafia);
            if (StabTarget(blocked) is { } byManiac && !targets.Contains(byManiac)) targets.Add(byManiac);
        }

        _saved = false;
        var fallen = new List<int>();
        foreach (var t in targets)
        {
            if (heal == t) { _saved = true; continue; }
            if (_dead.Add(t)) fallen.Add(t);
        }
        _fallen = [.. fallen];
        _killed = fallen.Count > 0 ? fallen[0] : null;

        if (fallen.Count >= 2)
        {
            _log.Add($"Ніч {_day}: {Name(fallen[0])} і {Name(fallen[1])} не прокинулись");
            _lead = MafiaGlek.Pick(Ctx.Rng, MafiaGlek.KilledTwo, Name(fallen[0]), Name(fallen[1]));
        }
        else if (fallen.Count == 1)
        {
            _log.Add($"Ніч {_day}: {Name(fallen[0])} не прокинувся");
            _lead = MafiaGlek.Pick(Ctx.Rng, MafiaGlek.Killed, Name(fallen[0]));
        }
        else if (QuietNight)
        {
            _log.Add($"Ніч {_day}: тиха ніч, крові не було");
            _lead = MafiaGlek.Pick(Ctx.Rng, MafiaGlek.QuietMorning);
        }
        else
        {
            _log.Add($"Ніч {_day}: усі вціліли");
            _lead = MafiaGlek.Pick(Ctx.Rng, MafiaGlek.Saved);
        }
        if (!Over()) Enter(MafiaPhase.Day);
    }

    /// <summary>
    /// Кого мафія вибрала. Слово дона — закон: якщо він живий, не заблокований і показав, іде його ціль.
    /// Без дона — більшість голосів, а при рівності той, за кого останній голос ліг раніше (перший
    /// домовився — того й слухають).
    /// </summary>
    int? KillTarget(int? blocked)
    {
        var counted = _kill
            .Where(p => !_dead.Contains(p.Key) && p.Key != blocked && !_dead.Contains(p.Value))
            .ToList();
        if (counted.Count == 0) return null;
        foreach (var p in counted)
            if (_roles[p.Key] == MafiaRole.Don) return p.Value;
        return counted
            .GroupBy(p => p.Value)
            .Select(g => (Seat: g.Key, Count: g.Count(), Last: g.Max(p => _killSeq[p.Key])))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Last)
            .Select(x => (int?)x.Seat)
            .First();
    }

    /// <summary>Кого намітив маньяк. Він сам по собі: ні голосувати, ні домовлятись йому нема з ким.</summary>
    int? StabTarget(int? blocked)
    {
        if (_stab is not { } t || _dead.Contains(t)) return null;
        var maniac = Alive().Any(s => _roles[s] == MafiaRole.Maniac && s != blocked);
        return maniac ? t : null;
    }

    /// <summary>Підсумок голосування: вигнати можна лише більшістю живих, рівність і утримання лишають усіх на місці.</summary>
    void ResolveVote()
    {
        var alive = Alive().Count();
        var tally = _votes
            .Where(v => !_dead.Contains(v.Key) && v.Value is { } t && !_dead.Contains(t))
            .GroupBy(v => v.Value!.Value)
            .Select(g => (Seat: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        int? exiled = null;
        if (tally.Count > 0)
        {
            var top = tally[0];
            var tie = tally.Count > 1 && tally[1].Count == top.Count;
            if (!tie && top.Count * 2 > alive) exiled = top.Seat;
        }

        if (exiled is { } seat)
        {
            _dead.Add(seat);
            if (_reveal)
            {
                _revealed.Add(seat);
                _log.Add($"День {_day}: {Name(seat)} іде за ворота ({RoleName(_roles[seat])})");
                _lead = MafiaGlek.Pick(Ctx.Rng, MafiaGlek.Exiled, Name(seat), RoleName(_roles[seat]));
            }
            else
            {
                _log.Add($"День {_day}: {Name(seat)} іде за ворота");
                _lead = MafiaGlek.Pick(Ctx.Rng, MafiaGlek.ExiledQuiet, Name(seat));
            }
        }
        else
        {
            _log.Add($"День {_day}: село не дійшло згоди");
            _lead = MafiaGlek.Pick(Ctx.Rng, MafiaGlek.NoExile);
        }

        if (Over()) return;
        _day++;
        Enter(MafiaPhase.Night);
    }

    /// <summary>
    /// Перевірка кінця після кожної фази. true — партія скінчилась, далі нічого не робимо.
    /// <paramref name="leaving"/> — причина не в грі, а в тому, що хтось устав з-за столу: тоді село,
    /// у якому лишилось менше трьох душ, розходиться внічию. Інакше мафіозі отримував би перемогу за
    /// те, що двом його сусідам подзвонили, і це образливо.
    /// </summary>
    bool Over(bool leaving = false)
    {
        var alive = Alive().ToArray();
        var mafia = alive.Count(s => IsMafia(_roles[s]));
        var maniac = alive.Count(s => _roles[s] == MafiaRole.Maniac);
        var civil = alive.Length - mafia - maniac;

        if (alive.Length == 0) { Draw(); return true; }
        if (mafia == 0 && maniac == 0) { Win(false); return true; }
        if (leaving && alive.Length < MinAlive) { Draw(); return true; }
        // Маньяк виграє, коли лишився сам, — або коли з ним лишився один-єдиний мирний: удень село
        // з двох душ нікого не вижене більшістю, а вночі ніж однаково спрацює. Але тільки не тоді,
        // коли село спорожніло через чийсь вихід з-за столу: за неявку перемоги не дають і йому.
        if (maniac > 0 && (alive.Length == 1 || (alive.Length == 2 && civil == 1))) { WinManiac(); return true; }
        // Поки в селі ходить маньяк, мафія ще нічого не виграла: ніж дістанеться і їй.
        if (maniac == 0 && mafia >= civil) { Win(true); return true; }
        if (alive.Length < MinAlive) { Draw(); return true; }
        return false;
    }

    void Win(bool mafiaWon)
    {
        // Переможці — уся команда, і живі, і мертві: у мафії виграють не ті, хто дожив, а ті, хто вгадав.
        // Маньяк — сам собі команда, тож у перемогу села він не входить.
        _winners = [.. _seats.Where(s => mafiaWon ? IsMafia(_roles[s]) : !IsMafia(_roles[s]) && _roles[s] != MafiaRole.Maniac).Order()];
        _team = mafiaWon ? "mafia" : "civil";
        _phase = MafiaPhase.Done;
        _dirty = true;

        var names = string.Join(", ", _seats.Where(s => IsMafia(_roles[s])).Select(Name));
        _log.Add(mafiaWon ? $"Перемогла мафія: {names}" : $"Перемогли мирні. Мафія: {names}");
        Say(Lead(MafiaGlek.Pick(Ctx.Rng, mafiaWon ? MafiaGlek.MafiaWin : MafiaGlek.CivilWin)));
        Ctx.Finish(_winners, mafiaWon
            ? $"{Info.Title}: перемогла мафія ({names})"
            : $"{Info.Title}: перемогли мирні, мафія в кайданах ({names})");
        // Ачівку за роль платформа сама не побачить — ролі знає тільки гра (ARCHITECTURE §8).
        if (mafiaWon)
            foreach (var seat in _winners) Ctx.Award(seat, 0, "ach:mafia-win");
    }

    void WinManiac()
    {
        _winners = [.. _seats.Where(s => _roles[s] == MafiaRole.Maniac).Order()];
        _team = "maniac";
        _phase = MafiaPhase.Done;
        _dirty = true;

        var names = string.Join(", ", _winners.Select(Name));
        _log.Add($"Переміг маньяк: {names}");
        Say(Lead(MafiaGlek.Pick(Ctx.Rng, MafiaGlek.ManiacWin)));
        Ctx.Finish(_winners, $"{Info.Title}: усіх пересидів маньяк ({names})");
        foreach (var seat in _winners) Ctx.Award(seat, 0, "ach:mafia-maniac");
    }

    void Draw()
    {
        _winners = [];
        _team = "draw";
        _phase = MafiaPhase.Done;
        _dirty = true;
        _log.Add("Село спорожніло — нічия");
        Say(Lead(MafiaGlek.Pick(Ctx.Rng, MafiaGlek.Draw)));
        Ctx.Finish([], $"{Info.Title}: у селі не лишилось кому судити — нічия");
    }

    // =========================================================================================
    // Дії гравців
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == MafiaPhase.Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (_phase == MafiaPhase.Lobby) return ActResult.Fail("Партія ще не почалась");
        if (!_roles.TryGetValue(seat, out var role)) return ActResult.Fail("Ти тут не граєш");
        if (_dead.Contains(seat)) return ActResult.Fail("Мертві мовчать");

        return action switch
        {
            "kill" => Kill(seat, role, payload),
            "check" => Check(seat, role, payload),
            "heal" => Heal(seat, role, payload),
            "block" => Block(seat, role, payload),
            "say" => Whisper(seat, role, payload),
            "vote" => Vote(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Kill(int seat, MafiaRole role, JsonElement payload)
    {
        if (!IsMafia(role) && role != MafiaRole.Maniac) return ActResult.Fail("Це не твоя справа");
        if (_phase != MafiaPhase.Night) return ActResult.Fail("Зараз не час");
        if (QuietNight) return ActResult.Fail("Перша ніч тиха — сьогодні тільки придивляємось");
        if (Target(payload) is not { } t || !_roles.ContainsKey(t)) return ActResult.Fail("Не зрозумів, на кого");
        if (_dead.Contains(t)) return ActResult.Fail("Його вже нема серед живих");

        if (role == MafiaRole.Maniac)
        {
            if (t == seat) return ActResult.Fail("На себе ножа не піднімають");
            _stab = t;
            _dirty = true;
            return ActResult.Done;
        }

        if (IsMafia(_roles[t])) return ActResult.Fail("Своїх не чіпаємо");
        _kill[seat] = t;
        _killSeq[seat] = ++_seq;   // при рівності голосів вирішує, хто визначився раніше
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Check(int seat, MafiaRole role, JsonElement payload)
    {
        if (role != MafiaRole.Sheriff) return ActResult.Fail("Це не твоя справа");
        if (_phase != MafiaPhase.Night) return ActResult.Fail("Зараз не час");
        if (_checkedTonight) return ActResult.Fail("Цієї ночі ти вже перевіряв");
        if (Target(payload) is not { } t || !_roles.ContainsKey(t)) return ActResult.Fail("Не зрозумів, кого");
        if (t == seat) return ActResult.Fail("Себе ти й так знаєш");
        if (_dead.Contains(t)) return ActResult.Fail("Його вже нема серед живих");

        // Дон на те й дон, що папери в нього чисті: комісару він показується мирним.
        var mafia = _roles[t] == MafiaRole.Mafia;
        _checks[t] = mafia;
        _checkedTonight = true;
        _dirty = true;
        if (mafia && !_sheriffAwarded)
        {
            _sheriffAwarded = true;
            Ctx.Award(seat, 0, "ach:sheriff");
        }
        return ActResult.Accept(mafia ? $"{Name(t)} — мафія" : $"{Name(t)} не мафія");
    }

    ActResult Heal(int seat, MafiaRole role, JsonElement payload)
    {
        if (role != MafiaRole.Doctor) return ActResult.Fail("Це не твоя справа");
        if (_phase != MafiaPhase.Night) return ActResult.Fail("Зараз не час");
        if (Target(payload) is not { } t || !_roles.ContainsKey(t)) return ActResult.Fail("Не зрозумів, кого");
        if (_dead.Contains(t)) return ActResult.Fail("Його вже нема серед живих");
        if (t == seat && !_selfHeal) return ActResult.Fail("Себе рятувати за цим столом не домовлялись");
        if (_healedLast == t) return ActResult.Fail("Цю людину ти рятував минулої ночі");

        _heal = t;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Block(int seat, MafiaRole role, JsonElement payload)
    {
        if (role != MafiaRole.Kuma) return ActResult.Fail("Це не твоя справа");
        if (_phase != MafiaPhase.Night) return ActResult.Fail("Зараз не час");
        if (Target(payload) is not { } t || !_roles.ContainsKey(t)) return ActResult.Fail("Не зрозумів, до кого");
        if (_dead.Contains(t)) return ActResult.Fail("Його вже нема серед живих");
        if (t == seat) return ActResult.Fail("Сама до себе в гості не ходиш");
        if (_blockedLast == t) return ActResult.Fail("У цій хаті ти ночувала минулої ночі");

        _block = t;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Whisper(int seat, MafiaRole role, JsonElement payload)
    {
        if (!IsMafia(role)) return ActResult.Fail("Це не твоя справа");
        if (_phase != MafiaPhase.Night) return ActResult.Fail("Зараз не час");
        var text = Text(payload)?.Trim();
        if (string.IsNullOrEmpty(text)) return ActResult.Fail("Порожнє нікому не цікаво");
        if (text.Length > MaxSayChars) text = text[..MaxSayChars];

        _chat.Add(new MafiaChatLine(seat, text));
        if (_chat.Count > MaxChatLines) _chat.RemoveAt(0);
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Vote(int seat, JsonElement payload)
    {
        if (_phase != MafiaPhase.Vote) return ActResult.Fail("Зараз не час");
        if (Abstain(payload))
        {
            _votes[seat] = null;
            _dirty = true;
            return ActResult.Accept("Утримався");
        }
        if (Target(payload) is not { } t || !_roles.ContainsKey(t)) return ActResult.Fail("Не зрозумів, за кого");
        if (_dead.Contains(t)) return ActResult.Fail("Його вже нема серед живих");

        _votes[seat] = t;
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>Ціль ходу: приймаємо і <c>{seat:3}</c>, і голе число — клієнтам так простіше.</summary>
    static int? Target(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("seat", out var s)
            && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>Утриматись — це свідомий вибір, а не «не дійшло»: <c>null</c> або <c>{seat:null}</c>.</summary>
    static bool Abstain(JsonElement payload) =>
        payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("seat", out var s) && s.ValueKind == JsonValueKind.Null);

    static string? Text(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.String => payload.GetString(),
        JsonValueKind.Object when payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString(),
        _ => null,
    };

    // =========================================================================================
    // Вихід із-за столу
    // =========================================================================================

    /// <summary>
    /// Устав посеред партії — «виїхав із села»: вважається мертвим, роль розкривається (якщо стіл на це
    /// домовився). Техпоразки тут нема: партія на вісьмох не має вмирати від того, що комусь подзвонили.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_phase is MafiaPhase.Done or MafiaPhase.Lobby) return;
        if (!_roles.TryGetValue(seat, out var role)) return;
        if (!_dead.Add(seat)) return;

        _dirty = true;
        // Устати можна й посеред ночі, а хроніка має читатись послідовно: рядок називає ту фазу,
        // у якій людина справді пішла, а не завжди «День».
        var when = _phase == MafiaPhase.Night ? $"Ніч {_day}" : $"День {_day}";
        if (_reveal)
        {
            _revealed.Add(seat);
            _log.Add($"{when}: {Name(seat)} виїхав із села ({RoleName(role)})");
            Say(MafiaGlek.Pick(Ctx.Rng, MafiaGlek.Left, Name(seat), RoleName(role)));
        }
        else
        {
            _log.Add($"{when}: {Name(seat)} виїхав із села");
            Say(MafiaGlek.Pick(Ctx.Rng, MafiaGlek.LeftQuiet, Name(seat)));
        }
        Over(leaving: true);
    }

    // =========================================================================================
    // Види
    // =========================================================================================

    public override object View(int? seat)
    {
        var done = _phase == MafiaPhase.Done;
        // Дограний стіл каркас відкриває наново, і на вільне місце сідає хтось інший. Таке місце до
        // цієї партії вже не має стосунку: ні роллю, ні ніком, ні смертю. Інакше новачок побачив би
        // у своїй картці роль того, хто грав тут до нього.
        bool Newcomer(int x) => _phase is MafiaPhase.Lobby or MafiaPhase.Done
            && Ctx.NickOf(x) is { } now
            && !string.Equals(now, _nicks.Length > x ? _nicks[x] : null, StringComparison.Ordinal);

        // Місце вважається гравцем цієї партії лише якщо йому роздали роль: той, хто підсів до
        // дограного столу, — такий самий глядач, як і решта.
        var me = seat is { } s && _roles.ContainsKey(s) && !Newcomer(s) ? s : (int?)null;
        var myRole = me is { } m ? _roles[m] : (MafiaRole?)null;
        var dead = me is { } d && _dead.Contains(d);
        // Мертві бачать усе — інакше сидіти до кінця партії нецікаво (spec §Фази, п. 6).
        var seeAll = done || dead;

        // До старту ролей ще нема, але картку вже показують: беремо тих, хто просто сидить за столом.
        // Після партії до складу дописуємо новачків — щоб людина бачила в селі хоч саму себе.
        int[] seats = _seats.Length > 0
            ? [.. _seats.Concat(Enumerable.Range(0, Info.MaxPlayers).Where(x => Ctx.Seated(x) && Newcomer(x))).Distinct().Order()]
            : [.. Enumerable.Range(0, Info.MaxPlayers).Where(Ctx.Seated)];
        var players = seats.Select(x => new MafiaPlayerView(
            x,
            Newcomer(x) ? Ctx.NickOf(x) : _nicks.Length > x ? _nicks[x] ?? Ctx.NickOf(x) : Ctx.NickOf(x),
            Newcomer(x) || !_dead.Contains(x),
            !Newcomer(x) && ShowsRole(x, me, myRole, seeAll) && _roles.TryGetValue(x, out var r) ? Wire(r) : null)).ToArray();

        return new
        {
            phase = Wire(_phase),
            day = _day,
            endsAt = _endsAt,
            phaseMs = Length(_phase),
            rules = Rules(seats.Length),
            players,
            me = me is null || myRole is null ? null : new MafiaMeView(Wire(myRole.Value), !dead),
            night = Night(me, myRole, seeAll),
            dayInfo = _phase is MafiaPhase.Day or MafiaPhase.Vote || done ? new MafiaDayView(_killed, _saved, _fallen) : null,
            votes = Tally(me, seeAll),
            voted = _phase == MafiaPhase.Vote ? _votes.Keys.Order().ToArray() : Array.Empty<int>(),
            log = _log.ToArray(),
            result = _team is null ? null : new MafiaResultView([.. _winners], _team),
        };
    }

    /// <summary>Налаштування столу, зведені до чисел. Це не таємниця: усі за столом грають за одними правилами.</summary>
    MafiaRulesView Rules(int players)
    {
        var cast = Plan(Math.Max(players, Info.MinPlayers));
        return new MafiaRulesView(
            _pace, _introMs, _nightMs, _dayMs, _voteMs,
            cast.Mafia, cast.Don, cast.Sheriff > 0, cast.Doctor > 0, cast.Maniac > 0, cast.Kuma > 0,
            _selfHeal, _openVotes, _reveal, _firstKill);
    }

    /// <summary>
    /// Денні голоси. Відкриті — бачать усі, як і має бути в класиці «пальцем». Таємні — кожен бачить
    /// лише свій; що хтось уже визначився, видно з <c>voted</c>, а за кого — ні.
    /// </summary>
    Dictionary<int, int?> Tally(int? me, bool seeAll)
    {
        if (_phase != MafiaPhase.Vote) return new Dictionary<int, int?>();
        if (_openVotes || seeAll) return new Dictionary<int, int?>(_votes);
        if (me is { } s && _votes.TryGetValue(s, out var mine)) return new Dictionary<int, int?> { [s] = mine };
        return new Dictionary<int, int?>();
    }

    /// <summary>Чи видно цьому глядачеві роль місця <paramref name="x"/>.</summary>
    bool ShowsRole(int x, int? me, MafiaRole? myRole, bool seeAll)
    {
        if (me == x) return true;                 // свою роль бачиш завжди
        if (seeAll) return true;                  // мертві й усі після кінця партії
        if (_revealed.Contains(x)) return true;   // кого село вже роздивилось при світлі дня
        // Мафія знає одна одну — і більше нікого. Дон для своїх такий самий свій.
        return myRole is { } mine && IsMafia(mine) && _roles.TryGetValue(x, out var r) && IsMafia(r);
    }

    /// <summary>Нічна частина виду: мафії — голоси й шепіт, комісару — його перевірки, лікарю, кумі й маньякові — їхній вибір.</summary>
    MafiaNightView? Night(int? me, MafiaRole? myRole, bool seeAll)
    {
        if (me is null || myRole is null) return null;
        var mafia = seeAll || IsMafia(myRole.Value);
        var sheriff = seeAll || myRole == MafiaRole.Sheriff;
        var doctor = seeAll || myRole == MafiaRole.Doctor;
        var kuma = seeAll || myRole == MafiaRole.Kuma;
        var maniac = seeAll || myRole == MafiaRole.Maniac;
        if (!mafia && !sheriff && !doctor && !kuma && !maniac) return null;   // живому мирному вночі дивитись нема на що

        return new MafiaNightView(
            mafia ? new Dictionary<int, int>(_kill) : new Dictionary<int, int>(),
            mafia ? _chat.ToArray() : Array.Empty<MafiaChatLine>(),
            sheriff ? _checks.Select(c => new MafiaCheckView(c.Key, c.Value)).ToArray() : Array.Empty<MafiaCheckView>(),
            doctor ? _heal : null,
            kuma ? _block : null,
            maniac ? _stab : null);
    }

    public override object? Frame() => new
    {
        // Кадр публічний (летить усій групі кімнати), тому в ньому лише те, що знає й глядач.
        phase = Wire(_phase),
        day = _day,
        endsAt = _endsAt,
        phaseMs = Length(_phase),
        alive = Alive().ToArray(),
    };

    // =========================================================================================
    // Збереження стану (каркас його не просить — Persistent тут нема, але з ним партію легко
    // перевірити тестом і перенести, якщо колись знадобиться пережити рестарт)
    // =========================================================================================

    sealed record Snapshot(
        int[] Seats, string?[] Nicks, Dictionary<int, int> Roles, int[] Dead, int[] Revealed,
        int Phase, int Day, DateTimeOffset EndsAt,
        Dictionary<int, int> Kill, Dictionary<int, long> KillSeq, long Seq,
        MafiaChatLine[] Chat, Dictionary<int, bool> Checks, bool CheckedTonight,
        int? Heal, int? HealedLast, int? Killed, bool Saved,
        Dictionary<int, int?> Votes, string[] Log, string? Team, int[] Winners, bool SheriffAwarded, int Flavors,
        int? Stab, int? Block, int? BlockedLast, int[] Fallen,
        string Pace, string MafiaOpt, bool SheriffOn, string DoctorOpt,
        bool SelfHeal, bool OpenVotes, bool Reveal, bool FirstKill, bool Don, bool ManiacOn, bool KumaOn);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string? Save() => JsonSerializer.Serialize(new Snapshot(
        _seats, _nicks, _roles.ToDictionary(p => p.Key, p => (int)p.Value), [.. _dead], [.. _revealed],
        (int)_phase, _day, _endsAt,
        new Dictionary<int, int>(_kill), new Dictionary<int, long>(_killSeq), _seq,
        [.. _chat], new Dictionary<int, bool>(_checks), _checkedTonight,
        _heal, _healedLast, _killed, _saved,
        new Dictionary<int, int?>(_votes), [.. _log], _team, _winners, _sheriffAwarded, _flavors,
        _stab, _block, _blockedLast, _fallen,
        _pace, _mafiaOpt, _sheriffOn, _doctorOpt,
        _selfHeal, _openVotes, _reveal, _firstKill, _don, _maniacOn, _kumaOn), Json);

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<Snapshot>(json, Json) is not { } s) return;
        _seats = s.Seats;
        _nicks = s.Nicks;
        _roles.Clear();
        foreach (var (seat, role) in s.Roles) _roles[seat] = (MafiaRole)role;
        _dead.Clear();
        foreach (var seat in s.Dead) _dead.Add(seat);
        _revealed.Clear();
        foreach (var seat in s.Revealed) _revealed.Add(seat);
        _phase = (MafiaPhase)s.Phase;
        _day = s.Day;
        _endsAt = s.EndsAt;
        _kill.Clear();
        foreach (var (k, v) in s.Kill) _kill[k] = v;
        _killSeq.Clear();
        foreach (var (k, v) in s.KillSeq) _killSeq[k] = v;
        _seq = s.Seq;
        _chat.Clear();
        _chat.AddRange(s.Chat);
        _checks.Clear();
        foreach (var (k, v) in s.Checks) _checks[k] = v;
        _checkedTonight = s.CheckedTonight;
        _heal = s.Heal;
        _healedLast = s.HealedLast;
        _killed = s.Killed;
        _saved = s.Saved;
        _votes.Clear();
        foreach (var (k, v) in s.Votes) _votes[k] = v;
        _log.Clear();
        _log.AddRange(s.Log);
        _team = s.Team;
        _winners = s.Winners;
        _sheriffAwarded = s.SheriffAwarded;
        _flavors = s.Flavors;
        _stab = s.Stab;
        _block = s.Block;
        _blockedLast = s.BlockedLast;
        _fallen = s.Fallen ?? [];
        _pace = Paces.ContainsKey(s.Pace ?? "") ? s.Pace! : "calm";
        (_introMs, _nightMs, _dayMs, _voteMs) = Paces[_pace];
        _mafiaOpt = s.MafiaOpt ?? "auto";
        _sheriffOn = s.SheriffOn;
        _doctorOpt = s.DoctorOpt ?? "auto";
        _selfHeal = s.SelfHeal;
        _openVotes = s.OpenVotes;
        _reveal = s.Reveal;
        _firstKill = s.FirstKill;
        _don = s.Don;
        _maniacOn = s.ManiacOn;
        _kumaOn = s.KumaOn;
        _dirty = true;
    }

    // =========================================================================================
    // Дядько Глек
    // =========================================================================================

    void Say(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Ctx.Say(text);
    }

    /// <summary>Приліпити до репліки підводку від попередньої фази й забути її, щоб не сказати двічі.</summary>
    string Lead(string line)
    {
        var lead = _lead;
        _lead = null;
        return string.IsNullOrEmpty(lead) ? line : $"{lead} {line}";
    }

    /// <summary>
    /// Слівце від живої моделі. Кличемо fire-and-forget: модель думає секунди, а фаза чекати не буде
    /// (і під замком кімнати чекати не можна взагалі). Відповідь лягає в чергу, а <see cref="Tick"/>
    /// зливає її вже під замком — і тільки якщо партія ще йде.
    /// </summary>
    void Flavor(string what)
    {
        if (_brain is null || _flavors >= MaxFlavors) return;
        _flavors++;
        var brain = _brain;
        var gen = Generation;

        var instruction = "Ти ведеш партію в мафію на сільському радіо. Скажи два речення, не більше. "
            + "Ролей не видавай, нікого не звинувачуй, імен не вигадуй. Ось що сталось: " + what;
        _ = Task.Run(async () =>
        {
            string? line = null;
            try { line = await brain.FlavorAsync(instruction, MaxSayChars); }
            catch { /* Глек не в гуморі — партії від цього ні холодно, ні жарко */ }
            QueueLine(line, gen);   // слівце про минулу партію відсіється за номером
        });
    }

    /// <summary>
    /// Номер поточної партії. Шов для тестів: живого <c>DjBrain</c> у тесті не підмінити (sealed, важкий
    /// конструктор), а чергу відкладених реплік перевірити треба.
    /// </summary>
    public int Generation { get { lock (_pending) return _gen; } }

    /// <summary>Скільки слівець чекає найближчого тика (шов для тестів).</summary>
    public int PendingLines { get { lock (_pending) return _lines.Count; } }

    /// <summary>
    /// Покласти в чергу готову репліку — рівно так, як це робить відповідь моделі у <see cref="Flavor"/>
    /// (шов для тестів). Слівце з чужої партії (<paramref name="gen"/> не той) тихо викидається.
    /// </summary>
    public void QueueLine(string? text, int? gen = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_pending)
        {
            if (gen is { } g && g != _gen) return;
            _lines.Add(text.Trim());
        }
    }

    /// <summary>Злити те, що модель надумала. Кличеться з тика, тобто вже під замком кімнати.</summary>
    void FlushLines()
    {
        List<string> ready;
        lock (_pending)
        {
            if (_lines.Count == 0) return;
            ready = [.. _lines];
            _lines.Clear();
        }
        // Партія скінчилась, поки модель думала — тоді її слівце вже нікому не потрібне.
        if (_phase is MafiaPhase.Done or MafiaPhase.Lobby) return;
        foreach (var line in ready) Say(line);
    }

    // =========================================================================================
    // Дрібниці
    // =========================================================================================

    IEnumerable<int> Alive() => _seats.Where(s => !_dead.Contains(s));

    /// <summary>Ім'я місця: беремо збережене на старті, щоб той, хто виїхав, не став «гравцем 5».</summary>
    string Name(int seat) =>
        (_nicks.Length > seat ? _nicks[seat] : null) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    public static string RoleName(MafiaRole role) => role switch
    {
        MafiaRole.Mafia => "мафія",
        MafiaRole.Don => "дон",
        MafiaRole.Sheriff => "комісар",
        MafiaRole.Doctor => "лікар",
        MafiaRole.Maniac => "маньяк",
        MafiaRole.Kuma => "кума",
        _ => "мирний",
    };

    public static string Wire(MafiaRole role) => role switch
    {
        MafiaRole.Mafia => "mafia",
        MafiaRole.Don => "don",
        MafiaRole.Sheriff => "sheriff",
        MafiaRole.Doctor => "doctor",
        MafiaRole.Maniac => "maniac",
        MafiaRole.Kuma => "kuma",
        _ => "civil",
    };

    public static string Wire(MafiaPhase phase) => phase switch
    {
        MafiaPhase.Intro => "intro",
        MafiaPhase.Night => "night",
        MafiaPhase.Day => "day",
        MafiaPhase.Vote => "vote",
        MafiaPhase.Done => "done",
        _ => "lobby",
    };
}
