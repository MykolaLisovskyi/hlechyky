using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Міст між подіями каркаса і сервісами: партія скінчилась — записати результат, порахувати Ело,
/// виплатити черепки в межах денної стелі й перевірити ачівки. Підписки живуть, поки живе сервер
/// (<see cref="IHostedService"/>), у тестах — <see cref="Attach"/>/<see cref="Detach"/> руками.
/// </summary>
public sealed class Rewards(GameEvents events, Economy economy, EconomyStore store, Ratings ratings,
    Achievements achievements, Daily daily, GameNames names, IClock clock,
    IOptionsMonitor<EconomyOptions> opts, ILogger<Rewards> log) : IHostedService
{
    /// <summary>Далі цієї межі результат уже не «кількість спроб», а мілісекунди: жодна головоломка не
    /// потребує тисячі спроб і жодна не розв'язується за секунду.</summary>
    const long MsThreshold = 1_000;

    bool _attached;

    public void Attach()
    {
        if (_attached) return;
        events.RoomFinished += OnRoomFinished;
        events.SoloScored += OnSolo;
        events.Awarded += OnAward;
        _attached = true;
    }

    public void Detach()
    {
        if (!_attached) return;
        events.RoomFinished -= OnRoomFinished;
        events.SoloScored -= OnSolo;
        events.Awarded -= OnAward;
        _attached = false;
    }

    public Task StartAsync(CancellationToken ct) { Attach(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { Detach(); return Task.CompletedTask; }

    // ---------- партія скінчилась ----------

    public void OnRoomFinished(RoomFinishedEvent e)
    {
        try { Finish(e); }
        catch (Exception ex) { log.LogWarning(ex, "не порахував партію {Room} ({Game})", e.RoomId, e.GameId); }
    }

    void Finish(RoomFinishedEvent e)
    {
        names.Learn(e.Info);
        var seated = e.Seats
            .Select((nick, seat) => (Nick: nick, Seat: seat))
            .Where(x => !string.IsNullOrWhiteSpace(x.Nick))
            .Select(x => (Nick: x.Nick!, x.Seat))
            .ToList();
        // партія «сам із собою» або соло-кімната — це не мультиплеєр, черепків і рейтингу тут нема
        if (seated.Select(x => Economy.Key(x.Nick)).Distinct(StringComparer.Ordinal).Count() < 2) return;

        var o = opts.CurrentValue;
        var seconds = (e.FinishedAt - e.StartedAt).TotalSeconds;
        var rewarded = e.Moves >= o.MinRewardMoves || seconds >= o.MinRewardSeconds;

        var rows = seated.Select(x =>
        {
            var outcome = e.Result.Draw ? "draw" : e.Result.Winners.Contains(x.Seat) ? "win" : "loss";
            long? score = e.Result.Scores is not null && e.Result.Scores.TryGetValue(x.Seat, out var s) ? s : null;
            var opponents = string.Join(", ", seated.Where(y => y.Seat != x.Seat).Select(y => y.Nick));
            return new ResultRow(e.RoomId, e.GameId, e.Round, Economy.Key(x.Nick), x.Nick,
                outcome, score, opponents, e.Stake, e.FinishedAt);
        }).ToList();

        // усі учасники — однією транзакцією: або партія записалась цілком, або жодного рядка. Інакше збій
        // посеред списку лишив би її напівпорахованою, а повторна подія додала б Ело вдруге
        var fresh = store.AddResults(rows);
        var written = rows.Where((_, i) => fresh[i]).ToList();
        if (written.Count == 0) return;   // подія прилетіла вдруге — нічого не подвоюємо

        if (rewarded)
            foreach (var row in written)
            {
                var (amount, reason) = row.Outcome switch
                {
                    "win" => (o.WinReward, $"win:{e.GameId}"),
                    "draw" => (o.DrawReward, $"draw:{e.GameId}"),
                    _ => (o.PlayReward, $"play:{e.GameId}"),
                };
                // виняток на одному ніку не має лишати решту без черепків
                try
                {
                    economy.GrantCapped(row.Nick, amount, reason, $"game:{e.RoomId}:{e.Round}:{row.NickKey}",
                        $"game:{e.GameId}", o.RewardedGamesPerDay);
                }
                catch (Exception ex) { log.LogWarning(ex, "не виплатив за партію {Room} нікові {Nick}", e.RoomId, row.Nick); }
            }

        // Ело рахуємо тільки коли партія справді нова цілком: половина свіжих рядків — це або збій, або
        // повтор події, і в обох випадках нуль-сумовий рейтинг чіпати не можна
        if (written.Count == rows.Count)
        {
            try { Elo(e); }
            catch (Exception ex) { log.LogWarning(ex, "не порахував Ело за партію {Room}", e.RoomId); }
        }
        else log.LogWarning("партія {Room} записалась частково ({Fresh} з {All}) — Ело пропущено",
            e.RoomId, written.Count, rows.Count);

        foreach (var row in written)
        {
            try { achievements.OnRoomFinished(e, row.Nick, row.Outcome); }
            catch (Exception ex) { log.LogWarning(ex, "не перевірив ачівки для {Nick}", row.Nick); }
        }
    }

    void Elo(RoomFinishedEvent e)
    {
        if (!e.Info.Rated || e.Info.MaxPlayers != 2 || e.Seats.Count < 2) return;
        var a = e.Seats[0];
        var b = e.Seats[1];
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return;
        if (Economy.Key(a) == Economy.Key(b)) return;

        var scoreA = e.Result.Draw ? 0.5
            : e.Result.Winners.Contains(0) ? 1.0
            : e.Result.Winners.Contains(1) ? 0.0
            : 0.5;   // «переможців нема, але й не нічия» рахуємо як нічию
        ratings.Apply(e.GameId, (Economy.Key(a), a), (Economy.Key(b), b), scoreA);
    }

    // ---------- соло-результат ----------

    public void OnSolo(SoloScoreEvent e)
    {
        try { Solo(e); }
        catch (Exception ex) { log.LogWarning(ex, "не записав соло-результат {Game} для {Nick}", e.GameId, e.Nick); }
    }

    void Solo(SoloScoreEvent e)
    {
        var order = e.Order != ScoreOrder.None ? e.Order : names.Get(e.GameId)?.Score ?? ScoreOrder.HigherIsBetter;
        var key = e.Key ?? $"solo:{e.GameId}:{Economy.Key(e.Nick)}";
        // день у ключі рядка — щоб «найкращий результат за період» був справді за період: без нього
        // гончарне коло мало б один вічний рядок, і місячної давнини рекорд лежав би в топі за сьогодні
        var room = $"{key}:{Days.Of(e.At)}";
        store.AddSolo(new ResultRow(room, e.GameId, 0, Economy.Key(e.Nick), e.Nick, "solo", e.Score, null, 0, e.At),
            order == ScoreOrder.HigherIsBetter);

        if (e.Key is not null && e.Key.StartsWith("daily:", StringComparison.Ordinal))
        {
            var parts = e.Key.Split(':');
            var day = parts.Length >= 4 ? parts[2] : "";
            // день беремо з ключа, який склала гра: зіпсований формат мовчки створив би «день», якого
            // не побачить ні серія, ні панель, і він назавжди завис би в базі
            if (parts.Length >= 4 && DateOnly.TryParseExact(day, "yyyy-MM-dd", out _))
            {
                var game = parts[1];
                // подія несе одне число; за домовленістю (див. specs/daily.md, «Як реалізовано»)
                // маленьке — це спроби, велике — мілісекунди
                var (attempts, ms) = e.Score >= MsThreshold
                    ? (1, (int)Math.Min(int.MaxValue, e.Score))
                    : ((int)Math.Max(1, e.Score), 0);
                var row = daily.Record(game, e.Nick, solved: true, attempts, ms, day);
                achievements.OnDaily(e.Nick, row, daily.Streak(e.Nick, game));
            }
            else log.LogWarning("ключ щоденної кімнати {Key} не має вигляду daily:<гра>:<рррр-мм-дд>:<нік> — записав як звичайний соло-результат", e.Key);
        }
        achievements.OnSolo(e);
    }

    // ---------- позастандартна нагорода ----------

    public void OnAward(AwardEvent e)
    {
        try { Award(e); }
        catch (Exception ex) { log.LogWarning(ex, "не нарахував нагороду {Reason} для {Nick}", e.Reason, e.Nick); }
    }

    void Award(AwardEvent e)
    {
        var o = opts.CurrentValue;
        var nickKey = Economy.Key(e.Nick);
        var day = Days.Today(clock);

        if (e.Reason.StartsWith("daily:", StringComparison.Ordinal))
        {
            // головоломка може не називати суму — тоді платимо типову з налаштувань;
            // раз на день на головоломку — це вже забезпечує сам ref
            var shards = e.Shards > 0 ? e.Shards : o.DailyReward;
            economy.Grant(e.Nick, shards, e.Reason, $"{e.Reason}:{day}:{nickKey}");
        }
        else if (e.Shards > 0)
        {
            if (e.Reason == "clicker" || e.Reason.StartsWith("clicker:", StringComparison.Ordinal))
                economy.GrantSequenced(e.Nick, e.Shards, "clicker", n => $"clicker:{nickKey}:{day}:{n}",
                    "clicker", ClickerCap(e.Reason, o), e.Shards);
            else if (e.Reason.StartsWith("ad:", StringComparison.Ordinal))
                economy.Grant(e.Nick, e.Shards, e.Reason, $"ad:{e.RoomId}:{e.Reason[3..]}:{nickKey}");
            // Очки партії, які гра сама перевела в черепки («Скільки?»): без денної стелі, і соло теж. Після
            // двокрапки — номер партії в кімнаті, тож «Ще раз» за тим самим столом платить заново, а повтор
            // тієї самої події — ні.
            else if (e.Reason.StartsWith("points:", StringComparison.Ordinal))
                economy.Grant(e.Nick, e.Shards, $"points:{e.GameId}", $"points:{e.GameId}:{e.RoomId}:{e.Reason[7..]}:{nickKey}");
            else if (!e.Reason.StartsWith("ach:", StringComparison.Ordinal))
                economy.GrantCapped(e.Nick, e.Shards, $"award:{e.Reason}",
                    $"award:{e.RoomId}:{e.Reason}:{nickKey}", "award", o.AwardDailyCap, e.Shards);
        }
        achievements.OnAward(e);
    }

    /// <summary>
    /// Денна стеля обміну в гончарному колі. Клейма майстра піднімають її, і гра каже власне число в причині
    /// («clicker:34»); вірити йому можна лише в межах від <see cref="EconomyOptions.ClickerDailyCap"/> до
    /// <see cref="EconomyOptions.ClickerDailyCapMax"/> — усе, що поза ними, ріжемо.
    /// </summary>
    static int ClickerCap(string reason, EconomyOptions o)
    {
        var floor = o.ClickerDailyCap;
        if (floor <= 0) return floor;                 // обмін вимкнено — клейма його не вмикають
        var ceiling = Math.Max(floor, o.ClickerDailyCapMax);
        return reason.Length > 8 && int.TryParse(reason.AsSpan(8), out var asked)
            ? Math.Clamp(asked, floor, ceiling)
            : floor;
    }
}
