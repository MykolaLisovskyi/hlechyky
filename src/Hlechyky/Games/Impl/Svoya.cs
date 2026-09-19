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
public sealed class Svoya : Game
{
    public const int TickMs = 250;
    const int Seats = 9;
    public const int PickMs = 30_000;
    public const int RevealMs = 5_000;
    public const int AppealMs = 15_000;
    /// <summary>Скільки чекати на репліку, що ще озвучується, перш ніж іти далі мовчки.</summary>
    public const int VoiceWaitMs = 5_000;
    /// <summary>Живий ведучий читає сам; якщо він забув натиснути «Кнопка!» — кнопка відкриється сама.</summary>
    public const int LiveReadMs = 60_000;
    public const int IntroThemeMs = 1_000;
    public const int MaxAnswer = 120;
    /// <summary>Скільки знаків за секунду «читає» ведучий без голосу (оцінка для таймера).</summary>
    const double CharsPerSec = 14;

    public const string Auto = "auto", Live = "live";
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
            new GameOption("early", "Кнопка під час читання", [("on", "Можна одразу"), ("off", "Лише після читання")], "on"),
            new GameOption("voice", "Голос ведучого", [("ostap", "Остап"), ("polina", "Поліна"), ("none", "Без голосу")], "ostap"),
        ],
        Hint: "Поле тем і цін, хто перший натиснув — той відповідає. Пакет обирає господар; ведучий — автомат або ти сам");

    public override string SeatName(int seat) => _mode == Live && seat == (_host >= 0 ? _host : Ctx.HostSeat) ? "🎙 ведучий" : $"гравець {seat + 1}";

    // ---------- налаштування ----------
    ISvoyaPackSource? _packs;
    ISvoyaVoice _voice = NoVoice.Instance;
    string _mode = Auto;
    int _answerSec = 15, _buzzSec = 10;
    bool _early = true;
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
    readonly List<Press> _presses = [];
    DateTimeOffset _opened;
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

    sealed record Try(int Seat, string? Text, bool Ok);
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
        _mode = options.GetValueOrDefault("host") == Live ? Live : Auto;
        if (int.TryParse(options.GetValueOrDefault("answer"), out var a) && AnswerChoices.Contains(a)) _answerSec = a;
        if (int.TryParse(options.GetValueOrDefault("buzz"), out var b) && BuzzChoices.Contains(b)) _buzzSec = b;
        _early = options.GetValueOrDefault("early") != "off";
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
        ClearQuestion();
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

    /// <summary>Попросити озвучити раунд наперед: вступ, усі запитання, відповіді й коментарі.</summary>
    void PrepareRound(int round)
    {
        if (!VoiceOn || _pack is null || round >= _pack.Rounds.Count) return;
        Prepare(SvoyaLines.Round(_pack.Rounds[round]));
    }

    public override TickResult Tick()
    {
        var now = Now;
        if (_phase is Done or Lobby) return Flush();
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
            case Reading: BeginBuzz(); break;
            case Buzz: BeginReveal(); break;
            case Answering:
                // живий ведучий судить сам: його таймер — лише підказка гравцеві, що час би вже й сказати
                if (_mode == Auto && _answering is { } s) Wrong(s, null);
                else { _until = null; _dirty = true; }
                break;
            case Reveal: AfterReveal(); break;
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
            Intro => Math.Max((int)(_speech * 1000) + 800, R.Themes.Count * IntroThemeMs + 1500),
            Board => PickMs,
            Reading => ReadMs(),
            Buzz => _buzzSec * 1000,
            Answering => _answerSec * 1000,
            Reveal => Math.Max(RevealMs, (int)(_speech * 1000) + 1200),
            _ => 0,
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
        return Math.Max((int)(_speech * 1000), media) + 400;
    }

    // =========================================================================================
    // Фази
    // =========================================================================================

    void BeginIntro()
    {
        Phase(Intro);
        ClearQuestion();
        if (Machine) Speak(SvoyaLines.Intro(R)); else Silence();
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
        BeginReading();
    }

    void BeginReading()
    {
        Phase(Reading);
        if (Machine) Speak(_q!.Text); else Silence();
        Arm();
    }

    void BeginBuzz()
    {
        _answering = null;
        if (!Players().Any(s => !_wrong.Contains(s))) { BeginReveal(); return; }
        Phase(Buzz);
        _opened = Now;
        Arm();
    }

    void BeginAnswering(int seat)
    {
        _answering = seat;
        Phase(Answering);
        if (VoiceOn) Prepare([RightLine(seat), WrongLine()], urgent: true);
        Arm();
    }

    string RightLine(int seat) => SvoyaLines.Right(Ctx.NickOf(seat), _price, _q);

    string WrongLine() => SvoyaLines.Wrong(_price);

    void Right(int seat)
    {
        _scores[seat] += _price;
        _correct = seat;
        _chooser = seat;
        _answering = null;
        BeginReveal();
    }

    void Wrong(int seat, string? text)
    {
        _scores[seat] -= _price;
        _wrong.Add(seat);
        _answering = null;
        if (_mode == Auto) Speak(WrongLine(), wait: false);
        BeginBuzz();
    }

    void BeginReveal()
    {
        _answering = null;
        Phase(Reveal);
        if (Machine) Speak(_correct is { } s ? RightLine(s) : SvoyaLines.Nobody(_q!)); else Silence();
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
        // фінал (і спецклітинки) — етап 4; поки що партія закінчується на останньому звичайному раунді
        if (_round >= _pack!.Rounds.Count || R.IsFinal) { Over(null); return; }
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
        _tries.Clear();
        _presses.Clear();
        _appeals.Clear();
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
        _result = new { winners, scores = (int[])_scores.Clone() };
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
        if (_answering == seat) BeginBuzz();       // пішов, не відповівши — кнопка знову відкрита, без штрафу
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
        if (_wrong.Contains(seat)) return ActResult.Fail("Свою спробу на це запитання ти вже використав(-ла)");
        if (_phase == Reading && !_early) return ActResult.Fail("Ще читають — зачекай");
        if (_phase is Answering && _answering is { } who)
        {
            Note(seat);
            return ActResult.Fail(who == seat ? "Ти вже відповідаєш" : $"Не встиг — відповідає {Ctx.NickOf(who)}");
        }
        if (_phase is not (Reading or Buzz)) return ActResult.Fail("Кнопка закрита");
        if (_phase == Reading) _opened = Now;
        Note(seat);
        BeginAnswering(seat);
        return ActResult.Done;
    }

    /// <summary>Записати натискання з мілісекундами від відкриття кнопки — живий ведучий бачить черговість.</summary>
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
        if (accept)
        {
            _scores[who] += 2 * _price;          // мінус скасовано, плюс нараховано
            _wrong.Remove(who);
            var i = _tries.FindLastIndex(t => t.Seat == who && !t.Ok);
            if (i >= 0) _tries[i] = _tries[i] with { Ok = true };
            _correct ??= who;
            _chooser = _correct;
        }
        _dirty = true;
        return ActResult.Accept(accept ? $"{Ctx.NickOf(who)}: зараховано" : $"{Ctx.NickOf(who)}: не зараховано");
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
        switch (action)
        {
            case "open":
                if (_phase != Reading) return ActResult.Fail("Зараз нема що відкривати");
                BeginBuzz();
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
        var open = _phase is Reveal;
        var showQuestion = _q is not null && _phase is Reading or Buzz or Answering or Reveal;
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
            appeals = _appeals.Select(a => new { seat = a.Seat, text = a.Text }).ToArray(),
            say = _say is { } say ? new { id = say.Id, text = say.Text, url = say.Url } : null,
            me = seat is not { } me ? null : new
            {
                isHost,
                canPick = _phase == Board && !_paused && (me == _chooser || isHost),
                canBuzz = !isHost && IsPlayer(me) && !_paused && !_wrong.Contains(me) && _answering is null
                    && (_phase == Buzz || (_phase == Reading && _early)),
                canAnswer = _mode == Auto && _phase == Answering && _answering == me,
                canAppeal = _mode == Auto && _phase == Reveal && !_appeals.Any(a => a.Seat == me)
                    && _tries.Any(t => t.Seat == me && !t.Ok && t.Text is not null),
                canJudge = _mode == Auto && _phase == Reveal && _appeals.Count > 0 && me == Ctx.HostSeat,
                canChoosePack = _phase == Lobby && me == Ctx.HostSeat,
            },
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
