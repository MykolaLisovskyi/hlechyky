using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Спецклітинки й фінал «Своєї гри» (specs/svoya.md §3.2): кіт у мішку, аукціон, фінал зі ставками. Окремим
/// файлом, бо звичайна клітинка — це читання → кнопка → відповідь, а тут своя черга ходів і свої таймери.
/// </summary>
public sealed partial class Svoya
{
    public const int CatMs = 15_000;
    public const int AuctionTurnMs = 15_000;
    public const int StrikeMs = 15_000;
    public const int BetMs = 30_000;
    public const int FinalAnswerMs = 30_000;
    public const int FinalRevealMs = 4_000;

    public const string Cat = "cat", Auction = "auction", Strike = "strike", Bet = "bet", FinalQuestion = "final",
        FinalJudge = "judging", FinalReveal = "finale";

    // ---------- кіт і аукціон ----------
    /// <summary>Хто відповідає сам, без кнопки (одержувач кота, переможець торгів).</summary>
    int? _solo;
    int? _catTo;
    bool _catChoosing;

    int _nominal;
    int _bid;
    int? _holder;
    bool _allIn;
    int? _turn;
    readonly HashSet<int> _passed = [];
    readonly List<(int Seat, string What, int Amount)> _bids = [];

    // ---------- фінал ----------
    readonly List<int> _finalists = [];
    readonly HashSet<int> _struck = [];
    readonly Dictionary<int, int> _bets = [];
    readonly Dictionary<int, string> _finalAnswers = [];
    readonly Dictionary<int, bool> _finalOk = [];
    readonly List<int> _revealOrder = [];
    int _revealed;
    int _finalTheme = -1;

    void ClearSpecial()
    {
        _solo = null;
        _catTo = null;
        _catChoosing = false;
        _nominal = _bid = 0;
        _holder = null;
        _allIn = false;
        _turn = null;
        _passed.Clear();
        _bids.Clear();
    }

    void ClearFinal()
    {
        _finalists.Clear();
        _struck.Clear();
        _bets.Clear();
        _finalAnswers.Clear();
        _finalOk.Clear();
        _revealOrder.Clear();
        _revealed = 0;
        _finalTheme = -1;
    }

    /// <summary>Таймер фаз, яких нема в <see cref="Arm"/> основного файла.</summary>
    int SpecialMs() => _phase switch
    {
        Cat => CatMs + (int)(_speech * 1000),
        Auction => AuctionTurnMs + (_bids.Count == 0 ? (int)(_speech * 1000) : 0),
        Strike => StrikeMs + (_struck.Count == 0 ? (int)(_speech * 1000) : 0),
        Bet => BetMs,
        FinalQuestion => (int)(_speech * 1000) + (_q?.Media?.Seconds ?? 0) * 1000 + FinalAnswerMs,
        FinalJudge => 0,
        FinalReveal => Math.Max(FinalRevealMs, (int)(_speech * 1000) + 800),
        _ => 0,
    };

    void SpecialTimeout()
    {
        switch (_phase)
        {
            case Cat:
                if (_catTo is null) Give(RandomReceiver());
                else if (_catChoosing) SetCatPrice(max: false);
                break;
            case Auction: if (_turn is { } t) PassTurn(t); break;
            case Strike: if (_turn is { } s) StrikeTheme(s, RandomOpenTheme()); break;
            case Bet: CloseBets(); break;
            case FinalQuestion: CloseFinalAnswers(); break;
            case FinalReveal: NextFinalReveal(); break;
        }
    }

    // =========================================================================================
    // Кіт у мішку
    // =========================================================================================

    void BeginCat()
    {
        Phase(Cat);
        _catTo = null;
        _catChoosing = false;
        if (Machine) Speak("Кіт у мішку!"); else Silence();
        // один гравець — віддавати нікому: кіт дістається йому ж
        if (Players().Count() == 1) { Give(_chooser ?? Players().First()); return; }
        Arm();
    }

    int RandomReceiver()
    {
        var others = Players().Where(s => s != _chooser).ToList();
        return others.Count == 0 ? _chooser ?? Players().First() : others[Ctx.Rng.Next(others.Count)];
    }

    ActResult GiveAct(int seat, JsonElement payload)
    {
        if (_phase != Cat || _catTo is not null) return ActResult.Fail("Зараз нема кого обдаровувати");
        if (seat != _chooser && seat != _host) return ActResult.Fail("Кота передає той, хто його відкрив");
        if (Int(payload, "seat") is not { } to || !IsPlayer(to)) return ActResult.Fail("Такого гравця нема");
        if (to == _chooser && Players().Count() > 1) return ActResult.Fail("Кота треба віддати комусь іншому");
        Give(to);
        return ActResult.Accept($"Кіт — у {Ctx.NickOf(to)}");
    }

    void Give(int to)
    {
        _catTo = to;
        _solo = to;
        if (_q!.CatPrice is { } fixedPrice)
        {
            _price = fixedPrice;
            BeginReading();
            return;
        }
        // ціну обирає одержувач: найменша або найбільша в раунді
        _catChoosing = true;
        Arm();
        _dirty = true;
    }

    (int Min, int Max) RoundPrices()
    {
        var prices = R.Themes.SelectMany(t => t.Questions).Select(q => q.Price).Where(p => p > 0).ToList();
        return prices.Count == 0 ? (_price, _price) : (prices.Min(), prices.Max());
    }

    ActResult CatPriceAct(int seat, JsonElement payload)
    {
        if (_phase != Cat || !_catChoosing) return ActResult.Fail("Ціну зараз не обирають");
        if (seat != _catTo) return ActResult.Fail("Ціну обирає той, кому дістався кіт");
        SetCatPrice(Bool(payload, "max") ?? false);
        return ActResult.Done;
    }

    void SetCatPrice(bool max)
    {
        var (lo, hi) = RoundPrices();
        _price = max ? hi : lo;
        _catChoosing = false;
        BeginReading();
    }

    // =========================================================================================
    // Аукціон
    // =========================================================================================

    int Step => Math.Max(1, RoundPrices().Min);

    void BeginAuction()
    {
        Phase(Auction);
        _nominal = _bid = _price;
        _holder = null;
        _allIn = false;
        _passed.Clear();
        _bids.Clear();
        _turn = _chooser;
        if (Machine) Speak("Аукціон!"); else Silence();
        AdvanceAuction(fromCurrent: true);
    }

    /// <summary>Порядок торгів: від обирача по колу місць.</summary>
    List<int> Circle()
    {
        var players = Players().ToList();
        var start = _chooser is { } c ? players.IndexOf(c) : 0;
        if (start < 0) start = 0;
        return [.. players.Skip(start), .. players.Take(start)];
    }

    /// <summary>Мінімальна звичайна ставка зараз.</summary>
    int MinBid => _holder is null ? _nominal : _bid + Step;

    bool CanRaise(int seat) => !_allIn && _scores[seat] >= MinBid;

    /// <summary>Ва-банк — усі свої бали; має бути більшим за поточну ставку (після ва-банку — лише більшим ва-банком).</summary>
    bool CanAllIn(int seat) => _scores[seat] > 0 && (_holder is null ? _scores[seat] >= _nominal : _scores[seat] > _bid);

    bool InAuction(int seat) => IsPlayer(seat) && !_passed.Contains(seat) && seat != _holder && (CanRaise(seat) || CanAllIn(seat));

    /// <summary>Передати хід наступному, хто ще може торгуватись, або закінчити торги.</summary>
    void AdvanceAuction(bool fromCurrent = false)
    {
        var circle = Circle();
        if (circle.Count == 0 || !circle.Any(InAuction)) { EndAuction(); return; }
        var at = _turn is { } t ? circle.IndexOf(t) : -1;
        if (at < 0) { at = 0; fromCurrent = true; }
        for (var i = fromCurrent ? 0 : 1; i <= circle.Count; i++)
        {
            var s = circle[(at + i) % circle.Count];
            if (!InAuction(s)) continue;
            _turn = s;
            Arm();
            _dirty = true;
            return;
        }
        EndAuction();
    }

    void EndAuction()
    {
        _turn = null;
        // ніхто не підняв — запитання за номіналом дістається обирачу
        var winner = _holder ?? _chooser ?? Players().First();
        _price = _holder is null ? _nominal : _bid;
        _solo = winner;
        BeginReading();
    }

    ActResult BidAct(int seat, string action, JsonElement payload)
    {
        if (_phase != Auction) return ActResult.Fail("Зараз не торгуються");
        if (_turn != seat) return ActResult.Fail(_turn is { } t ? $"Зараз торгується {Ctx.NickOf(t)}" : "Торги закінчились");
        switch (action)
        {
            case "pass":
                PassTurn(seat);
                return ActResult.Done;
            case "allin":
                if (!CanAllIn(seat)) return ActResult.Fail("Ва-банк тут не перебиває");
                _bid = _scores[seat];
                _holder = seat;
                _allIn = true;
                _bids.Add((seat, "allin", _bid));
                AdvanceAuction();
                return ActResult.Accept($"Ва-банк: {_bid}");
            default:
                if (!CanRaise(seat)) return ActResult.Fail(_allIn ? "Після ва-банку — лише більший ва-банк" : "Тобі бракує балів — лише пас або ва-банк");
                var amount = Int(payload, "amount") ?? 0;
                if (amount < MinBid) return ActResult.Fail($"Щонайменше {MinBid}");
                if (amount > _scores[seat]) return ActResult.Fail("Більше, ніж у тебе є");
                if (amount == _scores[seat]) return BidAct(seat, "allin", payload);
                _bid = amount;
                _holder = seat;
                _bids.Add((seat, "bid", amount));
                AdvanceAuction();
                return ActResult.Accept($"Ставка {amount}");
        }
    }

    void PassTurn(int seat)
    {
        _passed.Add(seat);
        _bids.Add((seat, "pass", 0));
        AdvanceAuction();
    }

    // =========================================================================================
    // Фінал
    // =========================================================================================

    void BeginFinal()
    {
        ClearQuestion();
        ClearFinal();
        _finalists.AddRange(Players().Where(s => _scores[s] > 0).OrderBy(s => _scores[s]).ThenBy(s => s));
        if (_finalists.Count == 0) { Over(null); return; }
        PrepareRound(_round);
        Phase(Strike);
        _turn = _finalists[0];
        if (Machine) Speak(SvoyaLines.Intro(R)); else Silence();
        if (R.Themes.Count == 1) { ChooseFinalTheme(0); return; }
        Arm();
    }

    int RandomOpenTheme()
    {
        var open = Enumerable.Range(0, R.Themes.Count).Where(i => !_struck.Contains(i)).ToList();
        return open[Ctx.Rng.Next(open.Count)];
    }

    ActResult StrikeAct(int seat, JsonElement payload)
    {
        if (_phase != Strike) return ActResult.Fail("Теми зараз не викреслюють");
        if (_turn != seat && seat != _host) return ActResult.Fail(_turn is { } t ? $"Викреслює {Ctx.NickOf(t)}" : "Уже викреслили");
        if (Int(payload, "theme") is not { } th || th < 0 || th >= R.Themes.Count || _struck.Contains(th)) return ActResult.Fail("Такої теми вже нема");
        StrikeTheme(_turn!.Value, th);
        return ActResult.Done;
    }

    void StrikeTheme(int seat, int theme)
    {
        _struck.Add(theme);
        var open = Enumerable.Range(0, R.Themes.Count).Where(i => !_struck.Contains(i)).ToList();
        if (open.Count == 1) { ChooseFinalTheme(open[0]); return; }
        var live = _finalists.Where(IsPlayer).ToList();
        var at = live.IndexOf(seat);
        _turn = live[(at + 1) % live.Count];
        Silence();
        Arm();
    }

    void ChooseFinalTheme(int theme)
    {
        _finalTheme = theme;
        _turn = null;
        _q = R.Themes[theme].Questions[0];
        _cell = (theme, 0);
        Phase(Bet);
        if (Machine) Speak($"Тема фіналу — {R.Themes[theme].Name}. Робіть ставки."); else Silence();
        Arm();
    }

    ActResult BetAct(int seat, JsonElement payload)
    {
        if (_phase != Bet) return ActResult.Fail("Ставки зараз не приймають");
        if (!_finalists.Contains(seat)) return ActResult.Fail("У фіналі грають ті, хто в плюсі");
        var amount = Int(payload, "amount") ?? 0;
        if (amount < 1 || amount > _scores[seat]) return ActResult.Fail($"Ставка від 1 до {_scores[seat]}");
        _bets[seat] = amount;
        _dirty = true;
        if (_finalists.Where(IsPlayer).All(_bets.ContainsKey)) CloseBets();
        return ActResult.Accept($"Ставка {amount}");
    }

    void CloseBets()
    {
        foreach (var s in _finalists) _bets.TryAdd(s, 1);            // не встиг — ставка 1
        Phase(FinalQuestion);
        _price = 0;
        if (Machine) Speak(_q!.Text); else Silence();
        Arm();
    }

    ActResult FinalAnswerAct(int seat, JsonElement payload)
    {
        if (!_finalists.Contains(seat)) return ActResult.Fail("У фіналі грають ті, хто в плюсі");
        var text = (Str(payload, "text") ?? "").Trim();
        if (text.Length == 0) return ActResult.Fail("Напиши відповідь");
        if (text.Length > MaxAnswer) text = text[..MaxAnswer];
        _finalAnswers[seat] = text;                                 // до кінця часу можна переписати
        _dirty = true;
        if (_finalists.Where(IsPlayer).All(_finalAnswers.ContainsKey) && _mode == Auto) CloseFinalAnswers();
        return ActResult.Accept("Відповідь прийнято");
    }

    void CloseFinalAnswers()
    {
        foreach (var s in _finalists)
            if (_mode == Auto) _finalOk[s] = SvoyaAnswer.Hits(_finalAnswers.GetValueOrDefault(s), _q!.Answers);
        if (_mode == Live) { Phase(FinalJudge); Silence(); _until = null; _dirty = true; return; }
        BeginFinalReveal();
    }

    ActResult FinalVerdictAct(JsonElement payload)
    {
        if (_phase != FinalJudge) return ActResult.Fail("Зараз не судять фінал");
        if (Int(payload, "seat") is not { } s || !_finalists.Contains(s)) return ActResult.Fail("Цей гравець не у фіналі");
        _finalOk[s] = Bool(payload, "ok") ?? false;
        _dirty = true;
        return ActResult.Done;
    }

    void BeginFinalReveal()
    {
        _revealOrder.Clear();
        _revealOrder.AddRange(_finalists.Where(s => !_left.Contains(s)).OrderBy(s => _scores[s]).ThenBy(s => s));
        _revealed = 0;
        if (VoiceOn) Prepare(_revealOrder.Select(FinalLine), urgent: true);
        Phase(FinalReveal);
        NextFinalReveal();
    }

    string FinalLine(int seat)
    {
        var ok = _finalOk.GetValueOrDefault(seat);
        var said = _finalAnswers.GetValueOrDefault(seat);
        return $"{Ctx.NickOf(seat)}: {(string.IsNullOrEmpty(said) ? "без відповіді" : said)}. "
            + (ok ? "Правильно! Плюс " : "Ні. Мінус ") + NumberWords.Say(_bets.GetValueOrDefault(seat, 1)) + ".";
    }

    void NextFinalReveal()
    {
        if (_revealed >= _revealOrder.Count) { Over(null); return; }
        var s = _revealOrder[_revealed++];
        var bet = _bets.GetValueOrDefault(s, 1);
        _scores[s] += _finalOk.GetValueOrDefault(s) ? bet : -bet;
        if (Machine) Speak(FinalLine(s)); else Silence();
        Arm();
        _dirty = true;
    }

    // =========================================================================================
    // Ходи й вихід
    // =========================================================================================

    /// <summary>Ходи спецфаз від гравця; null — це не спецхід, хай розбирається основний Act.</summary>
    ActResult? SpecialAct(int seat, string action, JsonElement payload) => action switch
    {
        "give" => GiveAct(seat, payload),
        "catPrice" => CatPriceAct(seat, payload),
        "bid" or "pass" or "allin" => BidAct(seat, action, payload),
        "strike" => StrikeAct(seat, payload),
        "bet" => BetAct(seat, payload),
        "answer" when _phase == FinalQuestion => FinalAnswerAct(seat, payload),
        _ => null,
    };

    /// <summary>Ходи живого ведучого у спецфазах; null — не його спецхід.</summary>
    ActResult? SpecialHostAct(string action, JsonElement payload)
    {
        switch (action)
        {
            case "give": return GiveAct(_host, payload);
            case "strike": return StrikeAct(_host, payload);
            case "passFor":
                if (_phase != Auction || _turn is not { } t) return ActResult.Fail("Зараз не торгуються");
                if (Int(payload, "seat") is { } who && who != t) return ActResult.Fail($"Зараз торгується {Ctx.NickOf(t)}");
                PassTurn(t);
                return ActResult.Accept($"Пас за {Ctx.NickOf(t)}");
            case "finalVerdict": return FinalVerdictAct(payload);
            case "next" when _phase == FinalJudge:
                if (_finalists.Where(IsPlayer).Any(s => !_finalOk.ContainsKey(s))) return ActResult.Fail("Спершу оціни кожну відповідь");
                BeginFinalReveal();
                return ActResult.Done;
            case "next" when _phase == FinalReveal:
                NextFinalReveal();
                return ActResult.Done;
        }
        return null;
    }

    /// <summary>Хтось пішов посеред спецфази.</summary>
    void SpecialLeave(int seat)
    {
        switch (_phase)
        {
            case Cat:
                if (_catTo == seat) { _solo = null; BeginReveal(); }
                break;
            case Auction:
                if (_holder == seat) { _holder = null; _bid = _nominal; _allIn = false; }
                _passed.Add(seat);
                if (_turn == seat) AdvanceAuction(); else AdvanceAuction(fromCurrent: true);
                break;
            case Strike:
                if (_finalists.Where(IsPlayer).ToList() is { Count: > 0 } live)
                {
                    if (_turn == seat) _turn = live[0];
                }
                break;
            case Bet:
                if (_finalists.Where(IsPlayer).All(_bets.ContainsKey)) CloseBets();
                break;
        }
        if (_finalists.Count > 0 && !_finalists.Any(IsPlayer) && _phase is Strike or Bet or FinalQuestion or FinalJudge) Over(null);
    }

    // =========================================================================================
    // Вид
    // =========================================================================================

    object? CatView() => _phase != Cat && _catTo is null ? null : new
    {
        from = _chooser,
        to = _catTo,
        price = _catChoosing || _catTo is null ? (int?)null : _price,
        choosing = _catChoosing,
        min = RoundPricesSafe().Min,
        max = RoundPricesSafe().Max,
    };

    (int Min, int Max) RoundPricesSafe() => _pack is null || _phase is Lobby or Done || _round >= _pack.Rounds.Count ? (0, 0) : RoundPrices();

    object? AuctionView() => _phase != Auction && _bids.Count == 0 && _nominal == 0 ? null : new
    {
        nominal = _nominal,
        current = _bid,
        holder = _holder,
        allIn = _allIn,
        turn = _turn,
        minBid = _phase == Auction ? MinBid : 0,
        passed = _passed.Order().ToArray(),
        bids = _bids.Select(b => new { seat = b.Seat, what = b.What, amount = b.Amount }).ToArray(),
    };

    bool InFinal => _phase is Strike or Bet or FinalQuestion or FinalJudge or FinalReveal;

    /// <summary>Фінал: ставки й відповіді інших — лише після розкриття; свої — завжди; живий ведучий бачить відповіді на суді.</summary>
    object? FinalView(int? seat, bool isHost)
    {
        if (!InFinal && _finalists.Count == 0) return null;
        var shown = _revealOrder.Take(_revealed).ToHashSet();
        bool Sees(int s) => shown.Contains(s) || s == seat || (isHost && _phase is FinalJudge or FinalReveal);
        return new
        {
            themes = _pack is not null && _round < _pack.Rounds.Count && R.IsFinal ? R.Themes.Select(t => t.Name).ToArray() : [],
            struck = _struck.Order().ToArray(),
            theme = _finalTheme,
            turn = _phase == Strike ? _turn : null,
            finalists = _finalists.ToArray(),
            betted = _bets.Keys.Order().ToArray(),
            answered = _finalAnswers.Keys.Order().ToArray(),
            revealed = _revealOrder.Take(_revealed).ToArray(),
            rows = _finalists.Where(Sees).Select(s => new
            {
                seat = s,
                bet = _bets.TryGetValue(s, out var b) ? b : (int?)null,
                answer = _finalAnswers.GetValueOrDefault(s),
                ok = shown.Contains(s) || (isHost && _finalOk.ContainsKey(s)) ? _finalOk.GetValueOrDefault(s) : (bool?)null,
            }).ToArray(),
        };
    }

    object SpecialMe(int me, bool isHost) => new
    {
        canGive = _phase == Cat && _catTo is null && (me == _chooser || isHost) && Players().Count() > 1,
        canCatPrice = _phase == Cat && _catChoosing && me == _catTo,
        canBid = _phase == Auction && _turn == me && CanRaise(me),
        canAllIn = _phase == Auction && _turn == me && CanAllIn(me),
        canPass = _phase == Auction && _turn == me,
        canPassFor = _phase == Auction && isHost && _turn is not null,
        canStrike = _phase == Strike && (_turn == me || isHost),
        canBet = _phase == Bet && _finalists.Contains(me) && !_left.Contains(me),
        canFinalAnswer = _phase == FinalQuestion && _finalists.Contains(me) && !_left.Contains(me),
        canFinalJudge = _phase == FinalJudge && isHost,
        bet = _bets.TryGetValue(me, out var b) ? b : (int?)null,
        answer = _finalAnswers.GetValueOrDefault(me),
    };
}
