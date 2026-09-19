using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Звідки гра бере пакет. Справжнє — <see cref="SvoyaPacks"/>; у тестах — підробка з фікстурою.</summary>
public interface ISvoyaPackSource
{
    /// <summary>Копія пакета, у який цей господар може грати, або null.</summary>
    SvoyaPack? Playable(string id, string hostNick);
    void NotePlayed(string id);
}

/// <summary>Готова репліка ведучого: адреса mp3 і скільки вона звучить.</summary>
public sealed record SvoyaClip(string Url, double Seconds);

/// <summary>
/// Голос ведучого (specs/svoya.md §4). Гра нічого не чекає під замком: просить приготувати репліки наперед
/// (<see cref="Prepare"/>) і на кожному тику питає, чи готова потрібна (<see cref="Ready"/>).
/// </summary>
public interface ISvoyaVoice
{
    /// <summary>Чи є голос узагалі (edge-tts стоїть і ввімкнений). Ні — ведучий «читає» мовчки, за оцінкою часу.</summary>
    bool Enabled { get; }
    /// <summary>Поставити репліки в чергу на озвучку. Не блокує. <paramref name="urgent"/> — на початок черги.</summary>
    void Prepare(string voice, IEnumerable<string> texts, bool urgent = false);
    /// <summary>Готова репліка або null (ще готується чи не вийшла).</summary>
    SvoyaClip? Ready(string voice, string text);
}

/// <summary>Без голосу: браузер читає сам (speechSynthesis) або мовчить, час — за оцінкою.</summary>
public sealed class NoVoice : ISvoyaVoice
{
    public static readonly NoVoice Instance = new();
    public bool Enabled => false;
    public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false) { }
    public SvoyaClip? Ready(string voice, string text) => null;
}

/// <summary>
/// «Своя гра» (specs/svoya.md). Поле «теми × ціни», кнопка, відповідь, розкриття. Ведучих два: <c>auto</c> —
/// сервер із голосом, відповіді друкують і перевіряє <see cref="SvoyaAnswer"/>, промах можна оскаржити
/// господарю; <c>live</c> — господар столу сам веде партію (не грає й рахунку не має), читає вголос і судить
/// ✓/✗, гравці лише тиснуть кнопку.
///
/// Пакет обирає господар ще в лобі (<see cref="ActsInLobby"/>): у статичні опції кімнати список пакетів не
/// передати. Час — через <see cref="IRoomContext.Clock"/>, тик раз на <see cref="TickMs"/>.
/// </summary>
public sealed partial class Svoya : Game
{
    public const int TickMs = 250;
    const int Seats = 9;
    public const int PickMs = 30_000;
    /// <summary>Найменше показу відповіді; репліка ведучого довша — показ триває, поки він говорить (+ <see cref="AfterSpeechMs"/>).</summary>
    public const int RevealMs = 3_000;
    public const int AppealMs = 15_000;
    /// <summary>Скільки чекати на репліку, що ще озвучується, перш ніж іти далі мовчки.</summary>
    public const int VoiceWaitMs = 5_000;
    /// <summary>Живий ведучий читає сам; якщо він забув натиснути «Кнопка!» — кнопка відкриється сама.</summary>
    public const int LiveReadMs = 60_000;
    /// <summary>Фальстарт (early=lock): натиснув під час читання — кнопка для нього відкриється на стільки пізніше за інших.</summary>
    public const int FalseStartMs = 2_000;
    public const int IntroThemeMs = 500;
    /// <summary>Пауза після репліки, перш ніж гра піде далі: щоб останнє слово не обрубалось, але без затяжки.</summary>
    public const int AfterSpeechMs = 500;
    /// <summary>Скільки після голосу (чи медіа) чекати з кнопкою: голос дочитав — кнопка майже одразу.</summary>
    public const int AfterReadMs = 200;
    public const int MaxAnswer = 120;
    /// <summary>Скільки знаків за секунду «читає» ведучий без голосу (оцінка для таймера).</summary>
    const double CharsPerSec = 14;

    public const string Auto = "auto", Live = "live";
    public const string EarlyOn = "on", EarlyOff = "off", EarlyLock = "lock";
    public static readonly int[] AnswerChoices = [10, 15, 20];
    public static readonly int[] BuzzChoices = [5, 10, 15];

    public const string Lobby = "lobby", Intro = "intro", Board = "board", Reading = "reading", Buzz = "buzz",
        Answering = "answering", Reveal = "reveal", Done = "done";

    public override GameInfo Info { get; } = new(
        "svoya", "Своя гра", "«Свою гру»", GameGroup.Party, 1, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("host", "Ведучий", [(Auto, "Автомат із голосом"), (Live, "Жива людина (господар)")], Auto),
            new GameOption("answer", "На відповідь", [.. AnswerChoices.Select(n => (n.ToString(), $"{n} с"))], "15"),
            new GameOption("buzz", "На кнопку", [.. BuzzChoices.Select(n => (n.ToString(), $"{n} с"))], "10"),
            new GameOption("early", "Кнопка під час читання",
                [(EarlyOn, "Можна одразу"), (EarlyOff, "Лише після читання"), (EarlyLock, $"Фальстарт: блок на {FalseStartMs / 1000} с")], EarlyOn),
            new GameOption("voice", "Голос ведучого", [("ostap", "Остап"), ("polina", "Поліна"), ("none", "Без голосу")], "ostap"),
        ],
        Hint: "Поле тем і цін, хто перший натиснув — той відповідає. Пакет обирає господар; ведучий — автомат або ти сам");

    public override string SeatName(int seat) => _mode == Live && seat == (_host >= 0 ? _host : Ctx.HostSeat) ? "🎙 ведучий" : $"гравець {seat + 1}";

    // ---------- налаштування ----------
    ISvoyaPackSource? _packs;
    ISvoyaVoice _voice = NoVoice.Instance;
    SvoyaPhrases _phrases = SvoyaPhrases.Plain;
    string _mode = Auto;
    int _answerSec = 15, _buzzSec = 10;
    string _early = EarlyOn;
    string _voiceName = "ostap";
    /// <summary>Живий ведучий попросив, щоб запитання читав голос (тумблер на пульті).</summary>
    bool _liveVoice;

    // ---------- пакет ----------
    SvoyaPack? _pack;

    // ---------- партія ----------
    string _phase = Lobby;
    /// <summary>Живий ведучий (місце господаря на старті); -1 в auto.</summary>
    int _host = -1;
    int _round;
    readonly HashSet<(int T, int Q)> _played = [];
    int? _chooser;
    (int T, int Q)? _cell;
    SvoyaQuestion? _q;
    int _price;
    int? _answering;
    int? _correct;
    readonly HashSet<int> _wrong = [];
    readonly List<Try> _tries = [];
    /// <summary>Вироки на це запитання по порядку (і серія гравця до вироку) — щоб прийнята апеляція відкотила все, що було після.</summary>
    readonly List<Verdict> _verdicts = [];
    readonly List<Press> _presses = [];
    DateTimeOffset _opened;
    /// <summary>Хто натиснув під час читання при early=lock — їм кнопка відкриється на <see cref="FalseStartMs"/> пізніше.</summary>
    readonly HashSet<int> _falseStart = [];
    /// <summary>Коли для фальстартерів відкриється кнопка (виставляється, щойно кнопка відкрилась усім); чи вже розповіли, що відкрилась.</summary>
    DateTimeOffset? _lockUntil;
    bool _unlocked;
    /// <summary>Коли голос дочитає запитання (і скільки читання тривало всього) — натиснули раніше, а він дочитує.</summary>
    DateTimeOffset? _readEnd;
    int _readMs;
    readonly List<Appeal> _appeals = [];
    readonly int[] _scores = new int[Seats];
    readonly HashSet<int> _left = [];
    DateTimeOffset? _until;
    int _totalMs;
    bool _paused;
    TimeSpan _pauseLeft;
    object? _result;
    string? _error;
    bool _dirty;

    // ---------- голос ----------
    int _sayId;
    Say? _say;
    /// <summary>Репліка, яку ще озвучують; поки вона не готова (або не минуло <see cref="VoiceWaitMs"/>), таймер фази стоїть.</summary>
    string? _pending;
    DateTimeOffset _pendingUntil;
    double _speech;

    // ---------- характер ведучого (specs/svoya.md §11) ----------
    /// <summary>Яку репліку з пулу казали минулого разу — щоб не повторювати ту саму двічі поспіль.</summary>
    readonly Dictionary<string, int> _lastPick = [];
    /// <summary>Скільки правильних поспіль у кожного (скидається його ж помилкою).</summary>
    readonly int[] _streak = new int[Seats];
    bool _anyRight;
    /// <summary>Скільки запитань поспіль лишились без відповіді.</summary>
    int _nobodyRun;
    /// <summary>Репліки, обрані наперед (щоб озвучити, поки гравець думає): на «так», «ні», час, промах, що закриває запитання, «ніхто».</summary>
    string? _rightLine, _wrongLine, _timeoutLine, _missLine, _nobodyLine;
    /// <summary>Хто помилився останнім на цьому запитанні — його промах міг закрити запитання.</summary>
    int? _lastWrong;
    /// <summary>Підсумок партії, обраний наперед, і рахунок, для якого його обирали (розійшовся — обрати наново).</summary>
    string? _endLine;
    int[]? _endScores;

    sealed record Try(int Seat, string? Text, bool Ok);
    sealed record Verdict(int Seat, bool Ok, int Streak);
    sealed record Press(int Seat, int Ms);
    sealed record Appeal(int Seat, string Text);
    sealed record Say(int Id, string Text, string? Url);

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    // Голос — чужий код (черга, диск). Його збій не має валити партію: гра тоді просто читає мовчки.
    void Prepare(IEnumerable<string> texts, bool urgent = false)
    {
        try { _voice.Prepare(_voiceName, texts, urgent); }
        catch (Exception) { /* без голосу */ }
    }

    SvoyaClip? Clip(string text)
    {
        try { return _voice.Ready(_voiceName, text); }
        catch (Exception) { return null; }
    }

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _packs = Ctx.Services.GetService<ISvoyaPackSource>();
        _voice = Ctx.Services.GetService<ISvoyaVoice>() ?? NoVoice.Instance;
        _phrases = Ctx.Services.GetService<SvoyaPhrases>() ?? SvoyaPhrases.Plain;
        _mode = options.GetValueOrDefault("host") == Live ? Live : Auto;
        if (int.TryParse(options.GetValueOrDefault("answer"), out var a) && AnswerChoices.Contains(a)) _answerSec = a;
        if (int.TryParse(options.GetValueOrDefault("buzz"), out var b) && BuzzChoices.Contains(b)) _buzzSec = b;
        _early = options.GetValueOrDefault("early") is EarlyOff or EarlyLock ? options["early"] : EarlyOn;
        _voiceName = options.GetValueOrDefault("voice") is "polina" or "none" ? options["voice"] : "ostap";
    }

    // =========================================================================================
    // Лобі: вибір пакета
    // =========================================================================================

    public override bool ActsInLobby => true;

    public override string? CanStart()
    {
        if (_pack is null) return "Оберіть пакет";
        if (_mode == Live && Enumerable.Range(0, Seats).Count(Ctx.Seated) < 2) return "Треба ще хоч одного гравця, крім ведучого";
        return null;
    }

    ActResult PickPack(int seat, JsonElement payload)
    {
        if (seat != Ctx.HostSeat) return ActResult.Fail("Пакет обирає господар столу");
        if (_packs is null) return ActResult.Fail("Пакети зараз недоступні");
        var id = Str(payload, "id");
        if (string.IsNullOrEmpty(id)) return ActResult.Fail("Оберіть пакет");
        var pack = _packs.Playable(id, Ctx.NickOf(seat) ?? "");
        if (pack is null) return ActResult.Fail("У цей пакет грати не можна — він чужий, прихований або ще не дороблений");
        _pack = pack;
        _dirty = true;
        return ActResult.Accept($"Пакет «{pack.Title}»");
    }

    // =========================================================================================
    // Старт і хід часу
    // =========================================================================================

    public override void Start()
    {
        _host = _mode == Live ? Ctx.HostSeat ?? 0 : -1;
        Array.Clear(_scores);
        _left.Clear();
        _result = null;
        _error = null;
        _paused = false;
        _say = null;
        _pending = null;
        _round = 0;
        _played.Clear();
        _lastPick.Clear();
        Array.Clear(_streak);
        _anyRight = false;
        _nobodyRun = 0;
        _endLine = null;
        _endScores = null;
        ClearQuestion();
        ClearFinal();
        var players = Players().ToList();
        _chooser = players.Count > 0 ? players[Ctx.Rng.Next(players.Count)] : null;
        if (_pack is not null) _packs?.NotePlayed(_pack.Id);
        PrepareRound(0);
        BeginIntro();
    }

    /// <summary>Голос звучить: в auto — якщо його не вимкнули в лобі; у live — якщо ведучий попросив читати за нього.</summary>
    bool VoiceOn => (_mode == Auto || _liveVoice) && _voiceName != "none" && _voice.Enabled;

    /// <summary>Хто в цій партії читає вголос: автомат (auto, або live з увімкненим голосом) чи жива людина.</summary>
    bool Machine => _mode == Auto || _liveVoice;

    /// <summary>Попросити озвучити раунд наперед: вступ (усі варіанти), запитання, відповіді, коментарі й репліки без підстановок.</summary>
    void PrepareRound(int round)
    {
        if (!VoiceOn || _pack is null || round >= _pack.Rounds.Count) return;
        var r = _pack.Rounds[round];
        Prepare(SvoyaLines.Round(r));
        var themes = string.Join(", ", r.Themes.Select(t => t.Name));
        Prepare(_phrases.Pool("intro").Select(t => SvoyaPhrases.Fill(t, ("round", r.Name), ("themes", themes))));
        Prepare(_phrases.Pure());
    }

    // ---------- характер ведучого ----------

    /// <summary>Репліка з пулу навмання (не та, що минулого разу) з підстановками.</summary>
    string Line(string key, params (string Key, string Value)[] vars) => SvoyaPhrases.Fill(_phrases.Pick(key, Ctx.Rng, _lastPick), vars);

    static string Tail(SvoyaQuestion? q) => q?.Comment is { Length: > 0 } c ? " " + c : "";

    /// <summary>Вступ раунду; з другого раунду — з лідером, якщо він один і в плюсі.</summary>
    string IntroLine()
    {
        var themes = string.Join(", ", R.Themes.Select(t => t.Name));
        if (_round > 0 && Leader() is { } l)
            return Line("introLead", ("round", R.Name), ("themes", themes), ("nick", Ctx.NickOf(l) ?? ""), ("sum", NumberWords.Say(_scores[l])));
        return Line("intro", ("round", R.Name), ("themes", themes));
    }

    /// <summary>Єдиний лідер у плюсі серед двох і більше гравців; інакше null.</summary>
    int? Leader()
    {
        var players = Players().ToList();
        if (players.Count < 2) return null;
        var best = players.Max(s => _scores[s]);
        if (best <= 0) return null;
        var tops = players.Where(s => _scores[s] == best).ToList();
        return tops.Count == 1 ? tops[0] : null;
    }

    /// <summary>
    /// Обрати репліки на обидва результати ще до відповіді (їх треба озвучити, поки гравець думає) — за
    /// ситуацією: перша правильна в партії, серія з трьох, вихід у лідери, з мінуса, найдорожча клітинка;
    /// помилка — у мінус, на найдорожчій, звичайна; окремо — час вийшов і промах, після якого запитання закрите
    /// (кіт, аукціон, останній із гравців): тоді «ніхто» не звучить, і правильну відповідь каже ця репліка.
    /// </summary>
    void ChooseVerdicts(int seat)
    {
        var nick = Ctx.NickOf(seat) ?? "";
        var sum = NumberWords.Say(_price);
        var others = Players().Where(s => s != seat).ToList();
        var top = others.Count > 0 ? others.Max(s => _scores[s]) : 0;
        var big = _price >= RoundPrices().Max;
        var rightKey = !_anyRight ? "rightFirst"
            : _streak[seat] >= 2 ? "rightStreak"
            : others.Count > 0 && _scores[seat] <= top && _scores[seat] + _price > top ? "rightLead"
            : _scores[seat] < 0 && _scores[seat] + _price >= 0 ? "rightBack"
            : big ? "rightBig" : "right";
        var wrongKey = _scores[seat] >= 0 && _scores[seat] - _price < 0 ? "wrongMinus" : big ? "wrongBig" : "wrong";
        _rightLine = Line(rightKey, ("nick", nick), ("sum", sum)) + Tail(_q);
        _wrongLine = Line(wrongKey, ("nick", nick), ("sum", sum));
        _timeoutLine = Line("timeout", ("nick", nick), ("sum", sum));
        _missLine = Line("wrongLast", ("nick", nick), ("sum", sum), ("answer", _q!.Answer)) + Tail(_q);
    }

    /// <summary>Підсумок партії за рахунком: перемога, нічия, ніхто в плюсі. Порожньо — мовчати.</summary>
    string EndLine(int[] scores)
    {
        var seats = Players().ToArray();
        var best = seats.Length == 0 ? 0 : seats.Max(s => scores[s]);
        var winners = best > 0 ? seats.Where(s => scores[s] == best).ToList() : [];
        return winners.Count == 0 ? Line("endNobody")
            : winners.Count == 1 ? Line("endWin", ("nick", Ctx.NickOf(winners[0]) ?? ""), ("sum", NumberWords.Say(best)))
            : Line("endDraw", ("nicks", string.Join(" і ", winners.Select(s => Ctx.NickOf(s) ?? ""))));
    }

    /// <summary>Обрати й озвучити підсумок наперед: результат уже видно (остання клітинка чи ставки фіналу), а часу — секунди.</summary>
    void PrepareEnd(int[] scores)
    {
        _endLine = EndLine(scores);
        _endScores = (int[])scores.Clone();
        if (VoiceOn && _endLine.Length > 0) Prepare([_endLine], urgent: true);
    }

    public override TickResult Tick()
    {
        var now = Now;
        if (_phase is Done or Lobby) return Flush();
        if (_lockUntil is { } lu && !_unlocked && now >= lu) { _unlocked = true; _dirty = true; }
        if (_pending is not null)
        {
            var clip = Clip(_pending);
            if (clip is null && now < _pendingUntil) return Flush();
            Voiced(_pending, clip);
            Arm();
        }
        if (_paused || _until is not { } until || now < until) return Flush();

        switch (_phase)
        {
            case Intro: BeginBoard(); break;
            case Board: AutoPick(); break;
            case Reading: AfterReading(); break;
            case Buzz: BeginReveal(); break;
            case Answering:
                // живий ведучий судить сам: його таймер — лише підказка гравцеві, що час би вже й сказати
                if (_mode == Auto && _answering is { } s) Wrong(s, null);
                else { _until = null; _dirty = true; }
                break;
            case Reveal: AfterReveal(); break;
            default: SpecialTimeout(); break;
        }
        return Flush();
    }

    TickResult Flush()
    {
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(Frame: false, View: true);
    }

    /// <summary>Гравці: сидять, не пішли, і не живий ведучий.</summary>
    IEnumerable<int> Players() => Enumerable.Range(0, Seats).Where(s => Ctx.Seated(s) && !_left.Contains(s) && s != _host);

    bool IsPlayer(int seat) => Ctx.Seated(seat) && !_left.Contains(seat) && seat != _host;

    SvoyaRound R => _pack!.Rounds[_round];

    void Phase(string phase)
    {
        _phase = phase;
        _dirty = true;
    }

    /// <summary>Виставити таймер фази з урахуванням того, скільки звучить репліка.</summary>
    void Arm()
    {
        var ms = _phase switch
        {
            Intro => Math.Max((int)(_speech * 1000) + AfterSpeechMs, R.Themes.Count * IntroThemeMs + AfterSpeechMs),
            Board => PickMs,
            Reading => ReadMs(),
            Buzz => _buzzSec * 1000,
            Answering => _answerSec * 1000,
            // правильно відповіли, поки голос ще читав: репліка про відповідь прозвучить після запитання
            Reveal => Math.Max(RevealMs, (int)(_speech * 1000) + AfterSpeechMs) + ReadLeftMs(),
            _ => SpecialMs(),
        };
        _totalMs = ms;
        _until = _pending is not null || ms <= 0 ? null : Now.AddMilliseconds(ms);
        _dirty = true;
    }

    int ReadMs()
    {
        var media = (_q?.Media?.Seconds ?? 0) * 1000;
        // живий ведучий читає сам; якщо ж голос читає за нього — кнопка відкривається, як у автомата
        if (_mode == Live && !_liveVoice) return _q is { Text.Length: 0 } && media > 0 ? media + 500 : LiveReadMs;
        return Math.Max((int)(_speech * 1000), media) + AfterReadMs;
    }

    int ReadLeftMs() => _readEnd is { } e && e > Now ? (int)(e - Now).TotalMilliseconds : 0;

    // =========================================================================================
    // Фази
    // =========================================================================================

    void BeginIntro()
    {
        Phase(Intro);
        ClearQuestion();
        if (Machine) Speak(IntroLine()); else Silence();
        Arm();
    }

    void BeginBoard()
    {
        ClearQuestion();
        if (!HasOpen()) { NextRound(); return; }
        if (_chooser is not { } c || !IsPlayer(c)) _chooser = Players().Select(s => (int?)s).FirstOrDefault();
        Phase(Board);
        Silence();
        Arm();
    }

    bool HasOpen() => R.Themes.Select((t, ti) => t.Questions.Select((_, qi) => (ti, qi))).SelectMany(x => x).Any(c => !_played.Contains(c));

    /// <summary>Обирач задумався — сервер бере найдешевшу відкриту клітинку (першу за темами).</summary>
    void AutoPick()
    {
        var cell = R.Themes.SelectMany((t, ti) => t.Questions.Select((q, qi) => (ti, qi, q.Price)))
            .Where(c => !_played.Contains((c.ti, c.qi))).OrderBy(c => c.Price).ThenBy(c => c.ti).First();
        Open(cell.ti, cell.qi);
    }

    void Open(int t, int q)
    {
        _played.Add((t, q));
        ClearQuestion();
        _cell = (t, q);
        _q = R.Themes[t].Questions[q];
        _price = _q.Price;
        // «ніхто» обираємо вже тут: до розкриття — читання й кнопка, голос устигне
        _nobodyLine = Line(_nobodyRun > 0 ? "nobodyAgain" : "nobody", ("answer", _q.Answer)) + Tail(_q);
        if (VoiceOn) Prepare([_nobodyLine], urgent: true);
        if (_q.Type == SvoyaQuestion.Cat) BeginCat();
        else if (_q.Type == SvoyaQuestion.Auction) BeginAuction();
        else BeginReading();
    }

    void BeginReading()
    {
        Phase(Reading);
        if (Machine) Speak(_q!.Text); else Silence();
        Arm();
    }

    /// <summary>Дочитали: звичайне запитання — кнопка; кіт чи аукціон — одразу відповідає той, кому воно дісталось.</summary>
    void AfterReading()
    {
        if (_solo is { } s && IsPlayer(s)) BeginAnswering(s);
        else if (_solo is not null) BeginReveal();
        else BeginBuzz();
    }

    void BeginBuzz()
    {
        _answering = null;
        if (!Players().Any(s => !_wrong.Contains(s))) { BeginReveal(); return; }
        if (_readEnd is { } end && end > Now)
        {
            // натиснули раніше й помилились, а голос ще читає: дочитуємо, кнопка — як і була, під час читання
            Phase(Reading);
            _totalMs = _readMs;
            _until = end;
            return;
        }
        Phase(Buzz);
        _opened = Now;
        if (_falseStart.Count > 0 && _lockUntil is null) _lockUntil = Now.AddMilliseconds(FalseStartMs);
        Arm();
    }

    void BeginAnswering(int seat)
    {
        _answering = seat;
        Phase(Answering);
        ChooseVerdicts(seat);
        if (VoiceOn) Prepare(new[] { _rightLine, _wrongLine, _timeoutLine, _missLine }.OfType<string>(), urgent: true);
        Arm();
    }

    void Right(int seat)
    {
        _verdicts.Add(new Verdict(seat, true, _streak[seat]));
        _scores[seat] += _price;
        _streak[seat]++;
        _anyRight = true;
        _correct = seat;
        _chooser = seat;
        _answering = null;
        BeginReveal();
    }

    void Wrong(int seat, string? text)
    {
        _verdicts.Add(new Verdict(seat, false, _streak[seat]));
        _scores[seat] -= _price;
        _streak[seat] = 0;
        _wrong.Add(seat);
        _lastWrong = seat;
        _answering = null;
        // кіт і аукціон — для одного: помилився, і запитання закрите
        if (_solo is not null) { BeginReveal(); return; }
        // голос ще читає — не перебиваємо його «Ні»: хрестик і мінус і так видно. Без тексту — вийшов час.
        // Промахнувся останній, кому було можна, — «Ні» скаже розкриття разом із відповіддю (BeginReveal)
        var closes = !Players().Any(s => !_wrong.Contains(s));
        if (_mode == Auto && ReadLeftMs() == 0 && !closes) Speak((text is null ? _timeoutLine : _wrongLine) ?? SvoyaLines.Wrong(_price), wait: false);
        NextOrBuzz();
    }

    /// <summary>Хто натиснув слідом (і ще не помилявся) — відповідає одразу; черга порожня — кнопка знову відкрита.</summary>
    void NextOrBuzz()
    {
        var next = _presses.Select(p => (int?)p.Seat).FirstOrDefault(s => !_wrong.Contains(s!.Value) && IsPlayer(s.Value));
        if (next is { } n) BeginAnswering(n); else BeginBuzz();
    }

    void BeginReveal()
    {
        _answering = null;
        Phase(Reveal);
        // запитання закрив чийсь промах (кіт, аукціон, останній із гравців) — це не «ніхто»
        var missed = _correct is null && _lastWrong is { } lw && (_solo == lw || !Players().Any(s => !_wrong.Contains(s)));
        _nobodyRun = _correct is null && !missed ? _nobodyRun + 1 : 0;
        if (Machine)
            Speak(_correct is { } s ? _rightLine ?? SvoyaLines.Right(Ctx.NickOf(s), _price, _q)
                : missed ? _missLine ?? SvoyaLines.Nobody(_q!)
                : _nobodyLine ?? SvoyaLines.Nobody(_q!));
        else Silence();
        // остання клітинка партії без фіналу: переможець уже відомий — хай підсумок озвучиться, поки показуємо відповідь
        if (!HasOpen() && _round + 1 >= _pack!.Rounds.Count) PrepareEnd(_scores);
        Arm();
    }

    void AfterReveal()
    {
        _appeals.Clear();
        BeginBoard();
    }

    void NextRound()
    {
        _round++;
        _played.Clear();
        if (_round >= _pack!.Rounds.Count) { Over(null); return; }
        if (R.IsFinal) { BeginFinal(); return; }
        PrepareRound(_round);
        BeginIntro();
    }

    void ClearQuestion()
    {
        _cell = null;
        _q = null;
        _price = 0;
        _answering = null;
        _correct = null;
        _wrong.Clear();
        _lastWrong = null;
        _tries.Clear();
        _verdicts.Clear();
        _presses.Clear();
        _falseStart.Clear();
        _lockUntil = null;
        _unlocked = false;
        _readEnd = null;
        _appeals.Clear();
        ClearSpecial();
    }

    void Over(string? error)
    {
        if (_phase == Done) return;
        Phase(Done);
        _error = error;
        _until = null;
        _pending = null;
        ClearQuestion();
        var seats = Players().ToArray();
        var best = seats.Length == 0 ? 0 : seats.Max(s => _scores[s]);
        int[] winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats) Ctx.Score(s, _scores[s]);
        // «Знавець» — лише за перемогу над кимось: соло-партія з автоматом ачівки не дає
        if (seats.Length >= 2) foreach (var w in winners) Ctx.Award(w, 0, "ach:svoya-win");
        _result = new { winners, scores = (int[])_scores.Clone() };
        // підсумок голосом: обраний наперед, якщо рахунок відтоді не змінився (апеляція на останньому запитанні — змінює)
        if (error is null && Machine)
        {
            if (_endLine is null || _endScores is null || !_endScores.SequenceEqual(_scores)) _endLine = EndLine(_scores);
            Speak(_endLine, wait: false);
        }
        if (error is not null && seats.Length == 0)
        {
            Ctx.Finish([], $"{Info.Title}: {error}");
            return;
        }
        var parts = seats.OrderByDescending(s => _scores[s]).Select(s => $"{Ctx.NickOf(s)} {_scores[s]}");
        var tail = error ?? (winners.Length == 0 ? "ніхто не вийшов у плюс" : (winners.Length == 1 ? "перемога: " : "перемогли ") + string.Join(" і ", winners.Select(Ctx.NickOf)));
        Ctx.Finish(winners, $"{Info.Title}: {string.Join(", ", parts)} — {tail}", seats.ToDictionary(s => s, s => (long)_scores[s]));
    }

    public override void OnLeave(int seat)
    {
        if (_phase is Done or Lobby) return;
        if (seat == _host)
        {
            // без живого ведучого грати нема як: партія без переможця
            _left.Add(seat);
            Phase(Done);
            _until = null;
            _error = "Ведучий пішов";
            ClearQuestion();
            _result = new { winners = Array.Empty<int>(), scores = (int[])_scores.Clone() };
            Ctx.Finish([], $"{Info.Title}: ведучий пішов, партію не дограли");
            return;
        }
        _left.Add(seat);
        _dirty = true;
        if (!Players().Any()) { Over(null); return; }
        if (_chooser == seat) _chooser = Players().First();
        if (_answering == seat)
        {
            // пішов, не відповівши: звичайне запитання — наступний у черзі або кнопка, кіт чи аукціон — закрите; без штрафу
            if (_solo == seat) BeginReveal(); else NextOrBuzz();
            return;
        }
        SpecialLeave(seat);
    }

    // =========================================================================================
    // Голос
    // =========================================================================================

    /// <summary>
    /// Сказати репліку. <paramref name="wait"/> — таймер фази чекає на неї (читання запитання, вступ); без
    /// очікування — репліка йде як є, хай навіть браузеру доведеться читати її самому («Ні. Мінус сто»).
    /// </summary>
    void Speak(string? text, bool wait = true)
    {
        _pending = null;
        _speech = 0;
        if (string.IsNullOrWhiteSpace(text)) { Silence(); return; }
        if (!VoiceOn) { Voiced(text, null); return; }
        var clip = Clip(text);
        if (clip is not null || !wait) { Voiced(text, clip); return; }
        Prepare([text], urgent: true);
        _say = null;
        _pending = text;
        _pendingUntil = Now.AddMilliseconds(VoiceWaitMs);
    }

    /// <summary>Ведучий мовчить: таймер фази — без репліки.</summary>
    void Silence()
    {
        _say = null;
        _pending = null;
        _speech = 0;
    }

    void Voiced(string text, SvoyaClip? clip)
    {
        _pending = null;
        _say = new Say(++_sayId, text, clip?.Url);
        _speech = clip?.Seconds ?? Math.Max(1.5, text.Length / CharsPerSec);
        if (_phase == Reading)
        {
            _readMs = ReadMs();
            _readEnd = Now.AddMilliseconds(_readMs);
        }
        _dirty = true;
    }

    // =========================================================================================
    // Ходи
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == Lobby) return action == "pack" ? PickPack(seat, payload) : ActResult.Fail("Партія ще не почалась");
        if (_phase == Done) return ActResult.Fail("Партію зіграно");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже встав з-за столу");
        if (seat == _host) return HostAct(action, payload);
        if (_paused && action is not "appeal") return ActResult.Fail("Пауза");
        if (SpecialAct(seat, action, payload) is { } special) return special;
        return action switch
        {
            "pick" => Pick(seat, payload),
            "buzz" => BuzzIn(seat),
            "answer" => AnswerAct(seat, payload),
            "appeal" => AppealAct(seat),
            "judge" => Judge(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Pick(int seat, JsonElement payload)
    {
        if (_phase != Board) return ActResult.Fail("Зараз не обирають");
        if (_paused) return ActResult.Fail("Пауза");
        if (seat != _chooser && seat != _host) return ActResult.Fail($"Обирає {Ctx.NickOf(_chooser ?? -1)}");
        var t = Int(payload, "theme");
        var q = Int(payload, "q");
        if (t is not { } ti || q is not { } qi || ti < 0 || ti >= R.Themes.Count || qi < 0 || qi >= R.Themes[ti].Questions.Count)
            return ActResult.Fail("Такої клітинки нема");
        if (_played.Contains((ti, qi))) return ActResult.Fail("Це запитання вже зіграно");
        Open(ti, qi);
        return ActResult.Done;
    }

    ActResult BuzzIn(int seat)
    {
        if (!IsPlayer(seat)) return ActResult.Fail("Ти тут не граєш");
        if (_paused) return ActResult.Fail("Пауза");
        if (_solo is not null) return ActResult.Fail(_solo == seat ? "Відповідай — кнопка тут не потрібна" : "Це запитання — лише для одного гравця");
        if (_wrong.Contains(seat)) return ActResult.Fail("Свою спробу на це запитання ти вже використав(-ла)");
        if (_phase == Reading && _early == EarlyOff) return ActResult.Fail("Ще читають — зачекай");
        if (_phase == Reading && _early == EarlyLock)
        {
            // фальстарт: кнопка «жива», але хто не дотерпів — той відкриється пізніше за інших
            if (!_falseStart.Add(seat)) return ActResult.Fail("Фальстарт уже був — дочекайся кінця читання");
            _dirty = true;
            return ActResult.Fail($"Фальстарт! Кнопка відкриється для тебе на {FalseStartMs / 1000} с пізніше");
        }
        if (LockLeftMs(seat) is > 0 and var left) return ActResult.Fail($"Фальстарт — ще {left / 1000.0:0.#} с");
        if (_phase is Answering && _answering is { } who)
        {
            if (who == seat) return ActResult.Fail("Ти вже відповідаєш");
            if (_presses.Any(p => p.Seat == seat)) return ActResult.Fail("Ти вже в черзі");
            // відповідає інший — стаєш у чергу: помилиться він, відповідатимеш ти
            Note(seat);
            var place = _presses.Where(p => !_wrong.Contains(p.Seat) && p.Seat != who).Count();
            return ActResult.Accept($"Ти в черзі {place}-й, після {Ctx.NickOf(who)}");
        }
        if (_phase is not (Reading or Buzz)) return ActResult.Fail("Кнопка закрита");
        if (_phase == Reading) _opened = Now;
        Note(seat);
        BeginAnswering(seat);
        return ActResult.Done;
    }

    /// <summary>Скільки ще фальстартеру чекати кнопки (0 — не чекати).</summary>
    int LockLeftMs(int seat) => _falseStart.Contains(seat) && _lockUntil is { } u && u > Now ? (int)(u - Now).TotalMilliseconds : 0;

    /// <summary>Записати натискання з мілісекундами від відкриття кнопки — це й черга, і те, що бачать усі.</summary>
    void Note(int seat)
    {
        if (_presses.Any(p => p.Seat == seat)) return;
        _presses.Add(new Press(seat, (int)Math.Max(0, (Now - _opened).TotalMilliseconds)));
        _dirty = true;
    }

    ActResult AnswerAct(int seat, JsonElement payload)
    {
        if (_mode == Live) return ActResult.Fail("Кажи вголос — ведучий слухає");
        if (_phase != Answering || _answering != seat) return ActResult.Fail("Зараз відповідаєш не ти");
        var text = (Str(payload, "text") ?? "").Trim();
        if (text.Length == 0) return ActResult.Fail("Напиши відповідь");
        if (text.Length > MaxAnswer) text = text[..MaxAnswer];
        var ok = SvoyaAnswer.Hits(text, _q!.Answers);
        _tries.Add(new Try(seat, text, ok));
        if (ok) { Right(seat); return ActResult.Accept($"✅ +{_price}"); }
        Wrong(seat, text);
        return ActResult.Accept($"❌ −{_price}");
    }

    ActResult AppealAct(int seat)
    {
        if (_mode == Live) return ActResult.Fail("Суддя тут — ведучий");
        if (_phase != Reveal) return ActResult.Fail("Оскаржити можна, коли показали відповідь");
        var mine = _tries.LastOrDefault(t => t.Seat == seat && !t.Ok && t.Text is not null);
        if (mine is null) return ActResult.Fail("Тобі нема чого оскаржувати");
        if (_appeals.Any(a => a.Seat == seat)) return ActResult.Fail("Уже чекаємо на рішення господаря");
        _appeals.Add(new Appeal(seat, mine.Text!));
        _until = Max(_until, Now.AddMilliseconds(AppealMs));
        _totalMs = Math.Max(_totalMs, AppealMs);
        _dirty = true;
        return ActResult.Accept("Господар столу вирішить");
    }

    ActResult Judge(int seat, JsonElement payload)
    {
        if (seat != Ctx.HostSeat) return ActResult.Fail("Судить господар столу");
        if (_phase != Reveal || _appeals.Count == 0) return ActResult.Fail("Нема що судити");
        var who = Int(payload, "seat") ?? _appeals[0].Seat;
        var appeal = _appeals.FirstOrDefault(a => a.Seat == who);
        if (appeal is null) return ActResult.Fail("Цей гравець не оскаржував");
        _appeals.Remove(appeal);
        var accept = Bool(payload, "accept") ?? false;
        var voided = accept ? Accept(who) : 0;
        _dirty = true;
        return ActResult.Accept(!accept ? $"{Ctx.NickOf(who)}: не зараховано"
            : voided == 0 ? $"{Ctx.NickOf(who)}: зараховано"
            : $"{Ctx.NickOf(who)}: зараховано, відповіді після — скасовано");
    }

    /// <summary>
    /// Апеляцію прийнято: відповідь була правильна, отже запитання закрилося ще тоді. Мінус скасовано, плюс нараховано,
    /// а все, що сталося після (чужий плюс, чужі мінуси, прострочки), відкочується — як у турнірному регламенті.
    /// Повертає, скільки пізніших вироків скасовано.
    /// </summary>
    int Accept(int who)
    {
        var at = _verdicts.FindLastIndex(v => v.Seat == who && !v.Ok);
        var later = at >= 0 ? _verdicts.Skip(at + 1).ToList() : [];
        for (var k = later.Count - 1; k >= 0; k--)
        {
            var v = later[k];
            _scores[v.Seat] += v.Ok ? -_price : _price;
            _streak[v.Seat] = v.Streak;
            _wrong.Remove(v.Seat);
            _appeals.RemoveAll(a => a.Seat == v.Seat);
            var t = _tries.FindLastIndex(x => x.Seat == v.Seat);
            if (t >= 0) _tries.RemoveAt(t);
        }
        var prev = at >= 0 ? _verdicts[at].Streak : _streak[who];   // серія, яку його «промах» був обірвав
        if (at >= 0) { _verdicts.RemoveRange(at, _verdicts.Count - at); _verdicts.Add(new Verdict(who, true, prev)); }
        _scores[who] += 2 * _price;          // мінус скасовано, плюс нараховано
        _streak[who] = prev + 1;
        _anyRight = true;
        _wrong.Remove(who);
        var i = _tries.FindLastIndex(t => t.Seat == who && !t.Ok);
        if (i >= 0) _tries[i] = _tries[i] with { Ok = true };
        _lastWrong = _verdicts.LastOrDefault(v => !v.Ok)?.Seat;
        _correct = who;
        _chooser = who;
        _nobodyRun = 0;
        return later.Count;
    }

    // ---------- живий ведучий ----------

    ActResult HostAct(string action, JsonElement payload)
    {
        switch (action)
        {
            case "pick": return Pick(_host, payload);
            case "pause":
                if (_paused) return ActResult.Fail("Уже пауза");
                _paused = true;
                _pauseLeft = _until is { } u ? u - Now : TimeSpan.Zero;
                _dirty = true;
                return ActResult.Done;
            case "resume":
                if (!_paused) return ActResult.Fail("Гра й так іде");
                _paused = false;
                if (_until is not null) _until = Now + (_pauseLeft > TimeSpan.Zero ? _pauseLeft : TimeSpan.Zero);
                _dirty = true;
                return ActResult.Done;
            case "voice":
                if (_voiceName == "none" || !_voice.Enabled) return ActResult.Fail("Голосу на цьому столі нема");
                _liveVoice = Bool(payload, "on") ?? !_liveVoice;
                if (_liveVoice) PrepareRound(_round);
                _dirty = true;
                return ActResult.Accept(_liveVoice ? "Голос читає за тебе" : "Читаєш сам");
            case "adjust":
                if (Int(payload, "seat") is not { } s || s < 0 || s >= Seats || s == _host || !Ctx.Seated(s)) return ActResult.Fail("Такого гравця нема");
                var delta = Int(payload, "delta") ?? 0;
                if (delta == 0 || Math.Abs(delta) > SvoyaPack.PriceMax) return ActResult.Fail("Скільки додати?");
                _scores[s] += delta;
                _dirty = true;
                return ActResult.Accept($"{Ctx.NickOf(s)} {(delta > 0 ? "+" : "−")}{Math.Abs(delta)}");
        }
        if (_paused) return ActResult.Fail("Пауза — спершу «Далі гра»");
        if (SpecialHostAct(action, payload) is { } special) return special;
        switch (action)
        {
            case "open":
                if (_phase != Reading) return ActResult.Fail("Зараз нема що відкривати");
                AfterReading();
                return ActResult.Done;
            case "verdict":
                if (_phase != Answering || _answering is not { } who) return ActResult.Fail("Зараз ніхто не відповідає");
                if (Bool(payload, "ok") == true) Right(who); else Wrong(who, null);
                return ActResult.Done;
            case "nobody":
                if (_phase is not (Reading or Buzz or Answering)) return ActResult.Fail("Зараз нема що закривати");
                BeginReveal();
                return ActResult.Done;
            case "next":
                if (_phase == Intro) { BeginBoard(); return ActResult.Done; }
                if (_phase == Reveal) { AfterReveal(); return ActResult.Done; }
                return ActResult.Fail("Далі — коли покажуть відповідь");
        }
        return ActResult.Fail("Ведучий так не ходить");
    }

    // =========================================================================================
    // Вид
    // =========================================================================================

    public override object View(int? seat)
    {
        var isHost = _mode == Live && seat is { } hs && hs == (_phase == Lobby ? Ctx.HostSeat : _host);
        var open = _phase is Reveal or FinalReveal;
        var showQuestion = _q is not null && _phase is Reading or Buzz or Answering or Reveal or FinalQuestion or FinalJudge or FinalReveal;
        var inRound = _pack is not null && _phase is not (Lobby or Done) && _round < _pack.Rounds.Count;
        return new
        {
            phase = _phase,
            mode = _mode,
            host = _mode == Live ? (_phase == Lobby ? Ctx.HostSeat : _host) : (int?)null,
            options = new { answer = _answerSec, buzz = _buzzSec, early = _early, voice = _voiceName },
            voice = new { on = VoiceOn, available = _voiceName != "none" && _voice.Enabled },
            pack = _pack is null ? null : new
            {
                id = _pack.Id,
                title = _pack.Title,
                description = _pack.Description,
                author = _pack.Author,
                rounds = _pack.Rounds.Select(r => new { name = r.Name, final = r.IsFinal, themes = r.Themes.Select(t => t.Name).ToArray() }).ToArray(),
            },
            round = inRound ? _round + 1 : 0,
            rounds = _pack?.Rounds.Count ?? 0,
            roundName = inRound ? R.Name : null,
            board = inRound && !R.IsFinal
                ? R.Themes.Select((t, ti) => new
                {
                    theme = t.Name,
                    cells = t.Questions.Select((q, qi) => new { price = q.Price, open = !_played.Contains((ti, qi)) }).ToArray(),
                }).ToArray()
                : null,
            chooser = _chooser,
            cell = _cell is { } c ? new { theme = c.T, q = c.Q } : null,
            question = showQuestion ? new
            {
                theme = R.Themes[_cell!.Value.T].Name,
                price = _price,
                text = _q!.Text,
                media = _q.Media,
            } : null,
            // відповідь: усім — після розкриття; живому ведучому — одразу, щойно відкрилось запитання
            answer = showQuestion && (open || isHost) ? new
            {
                text = _q!.Answer,
                accept = _q.Accept.ToArray(),
                comment = _q.Comment,
                media = _q.AnswerMedia,
            } : null,
            answering = _answering,
            correct = _phase == Reveal ? _correct : null,
            until = _paused ? null : _until,
            totalMs = _totalMs,
            paused = _paused,
            leftMs = _paused ? (int)_pauseLeft.TotalMilliseconds : (int?)null,
            waiting = _pending is not null,
            scores = (int[])_scores.Clone(),
            wrong = _wrong.Order().ToArray(),
            tries = _tries.Select(t => new { seat = t.Seat, text = t.Text, ok = t.Ok }).ToArray(),
            presses = _presses.Select(p => new { seat = p.Seat, ms = p.Ms }).ToArray(),
            falseStart = _falseStart.Order().ToArray(),
            appeals = _appeals.Select(a => new { seat = a.Seat, text = a.Text }).ToArray(),
            say = _say is { } say ? new { id = say.Id, text = say.Text, url = say.Url } : null,
            me = seat is not { } me ? null : new
            {
                isHost,
                canPick = _phase == Board && !_paused && (me == _chooser || isHost),
                // при early=lock кнопка під час читання «жива» (інакше фальстарту не буває), а хто не дотерпів — чекає
                canBuzz = !isHost && IsPlayer(me) && !_paused && !_wrong.Contains(me) && _solo is null && LockLeftMs(me) == 0
                    && (_answering is null
                        ? _phase == Buzz || (_phase == Reading && (_early == EarlyOn || (_early == EarlyLock && !_falseStart.Contains(me))))
                        : _phase == Answering && _answering != me && !_presses.Any(p => p.Seat == me)),
                lockMs = LockLeftMs(me),
                canAnswer = _mode == Auto && _phase == Answering && _answering == me,
                canAppeal = _mode == Auto && _phase == Reveal && !_appeals.Any(a => a.Seat == me)
                    && _tries.Any(t => t.Seat == me && !t.Ok && t.Text is not null),
                canJudge = _mode == Auto && _phase == Reveal && _appeals.Count > 0 && me == Ctx.HostSeat,
                canChoosePack = _phase == Lobby && me == Ctx.HostSeat,
                special = SpecialMe(me, isHost),
            },
            solo = _solo,
            cat = CatView(),
            auction = AuctionView(),
            final = FinalView(seat, isHost),
            left = _left.Order().ToArray(),
            error = _error,
            result = _result,
        };
    }

    // ---------- дрібне ----------

    static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? Int(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    static bool? Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
