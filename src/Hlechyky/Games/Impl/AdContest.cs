using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Налаштування конкурсу реклами (секція <c>Ad</c> в appsettings.json). Числа тут, а не в коді, бо
/// «раз на скільки треків крутити рекламу» доводиться підкручувати на живому ефірі.
/// </summary>
public sealed class AdOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Скільки днів триває конкурс від відкриття.</summary>
    public int Days { get; set; } = 3;
    /// <summary>Ліміт запису: браузер зупиняє мікрофон сам, сервер додає запас і ріже.</summary>
    public int MaxSeconds { get; set; } = 30;
    /// <summary>Скільки секунд понад ліміт сервер ще терпить (браузер зупиняється не миттєво).</summary>
    public int GraceSeconds { get; set; } = 5;
    /// <summary>Відкривати новий конкурс щопонеділка, коли активного нема.</summary>
    public bool AutoOpen { get; set; } = true;
    /// <summary>О котрій годині за Києвом відкривати автоматично.</summary>
    public int OpenHour { get; set; } = 12;

    /// <summary>Переможна реклама йде в ефір джинглом. false — конкурс лишається, ефір не чіпаємо.</summary>
    public bool Jingle { get; set; } = true;
    /// <summary>Не частіше ніж раз на стільки треків…</summary>
    public int EveryTracks { get; set; } = 6;
    /// <summary>…і не частіше ніж раз на стільки хвилин.</summary>
    public int MinMinutes { get; set; } = 25;

    public int WinnerReward { get; set; } = 25;
    public int EntryReward { get; set; } = 3;
    public int VoteReward { get; set; } = 1;
}

/// <summary>
/// Хто пише сценарій реклами. У проді — Дядько Глек (<see cref="DjBrain.FlavorAsync"/>), у тестах —
/// заглушка, яка мовчить: тоді конкурс бере вбудований шаблон, і це теж робочий шлях, а не аварійний.
/// </summary>
public interface IAdScriptWriter
{
    Task<string?> WriteAsync(string instruction, int maxChars, CancellationToken ct);
}

/// <summary>Сценарій від Дядька Глека; ключа до моделі нема — повертає null, і конкурс іде шаблоном.</summary>
public sealed class DjScriptWriter(DjBrain brain) : IAdScriptWriter
{
    public Task<string?> WriteAsync(string instruction, int maxChars, CancellationToken ct) =>
        brain.FlavorAsync(instruction, maxChars, ct);
}

/// <summary>
/// Те, що конкурсу треба від <see cref="VoiceService"/>. Окремий інтерфейс тут не заради краси:
/// живий VoiceService запускає ffmpeg, а тестам потрібен запис, який «уже готовий».
/// </summary>
public interface IVoiceSaver
{
    bool Enabled { get; }
    long MaxUploadBytes { get; }
    Task<(TrackInfo Track, string FilePath)> SaveAsync(Stream body, string nick, CancellationToken ct);
    /// <summary>Файл готового запису або null, якщо його вже нема в кеші.</summary>
    string? FilePath(string id);
    void Delete(string id);
}

/// <summary>Справжній конвеєр голосових: браузер → ffmpeg → mp3 у кеші.</summary>
public sealed class VoiceSaver(VoiceService voice) : IVoiceSaver
{
    public bool Enabled => voice.Enabled;
    public long MaxUploadBytes => voice.MaxUploadBytes;

    public Task<(TrackInfo Track, string FilePath)> SaveAsync(Stream body, string nick, CancellationToken ct) =>
        voice.SaveAsync(body, nick, ct);

    public string? FilePath(string id) => voice.FilePath(id);

    public void Delete(string id)
    {
        if (voice.FilePath(id) is not { } path) return;
        try { File.Delete(path); } catch (IOException) { /* хай полежить, кеш переживе */ }
    }
}

/// <summary>Реклама, яку крутить ефір: переможна або (<c>House</c>) поставлена господарем.</summary>
public sealed record AdWinner(long ContestId, string TrackId, string Nick, int Seconds, bool House = false);

/// <summary>
/// Конкурс «Озвуч рекламу»: сценарій → записи → голоси → переможець → джингл в ефірі.
///
/// Це не гра-кімната: учасників скільки завгодно, конкурс триває днями, а зайти в нього можна з будь-якої
/// вкладки. Тому кімнатної моделі тут нема — є сервіс зі своїми таблицями і своїми ендпоінтами, а платформа
/// бачить лише виплати (<see cref="AwardEvent"/> з причинами <c>ad:winner</c>/<c>ad:entry</c>/<c>ad:vote</c>,
/// які економіка вже вміє: вона сама будує з них ref і сама вішає ачівку «Голос села»).
/// </summary>
public sealed class AdContest(
    AdContestStore store, IClock clock, GameEvents events, IOutbox outbox,
    IAdScriptWriter writer, IVoiceSaver voice, IOptionsMonitor<AdOptions> opts, ILogger<AdContest> log)
{
    /// <summary>Що просимо в Дядька Глека. Без назв брендів — реклама тут своя, глиняна.</summary>
    public const string Instruction =
        "Напиши сценарій жартівливої радіореклами глиняного глека на 15–20 секунд, українською, "
        + "одним абзацом, без назв справжніх брендів і без згадок про рекламу як таку. "
        + "Це текст, який людина прочитає вголос у мікрофон.";

    /// <summary>Дванадцять сценаріїв на випадок, коли Глек мовчить (нема ключа, стеля, він зайнятий).</summary>
    public static readonly string[] Templates =
    [
        "Глек — це не посуд, це стан душі. Наливаєш воду — а виходить криниця. Наливаєш узвар — а виходить свято. Один глек, а радості на всю хату.",
        "Кажуть, у глеку молоко не скисає, бо йому там затишно. Перевір сам: постав глека на стіл — і хата одразу стане теплішою.",
        "Що спільного в глека і в доброго друга? Обидва тримають те, що ти в них наливаєш, і нікому не розказують.",
        "Пластик мовчить, скло дзвенить, а глек — гуде. Приклади вухо: чуєш? Це в ньому ще й досі та вода, що була торік.",
        "Глиняний глек: працює без розетки, оновлень не просить, паролю не забуває. Двісті років гарантії, якщо не впустиш.",
        "Одна бабця мала глека. Глека мала одна бабця. І, знаєте, вона нікому його не віддала. Мабуть, було за що.",
        "Візьми глека в руки — він холодний. Постав на стіл — він теплий. Налий у нього узвару — і він уже твій найкращий друг.",
        "У глека немає кнопки «вимкнути». У глека взагалі немає кнопок. І саме тому він досі працює.",
        "Глек не питає, як ти спав. Глек не радить, що робити. Глек просто стоїть і тримає воду холодною. Учись у глека.",
        "Раніше в кожній хаті був глек. Потім усі купили чайники. А тепер знову шукають глека — бо чайник не гуде так гарно.",
        "Кажуть, майстер робить глека за годину, а глиняне вухо — за день. Бо вухо має слухати, як булькає вода.",
        "Глек, глечик, глечичок — три покоління на одній полиці. І кожне тримає своє: воду, узвар і чиюсь дитячу таємницю.",
    ];

    readonly object _lock = new();
    /// <summary>Кеш переможця для джингла: питати базу на кожен трек в ефірі ні до чого.</summary>
    AdWinner? _winner;
    bool _winnerKnown;

    AdOptions O => opts.CurrentValue;

    static bool Named(string? nick) => !string.IsNullOrWhiteSpace(nick) && nick != "гість";

    // =============================================================================================
    // Що бачить браузер
    // =============================================================================================

    /// <summary>Усе, що малює панель: активний конкурс із записами й голосами плюс минулі переможці.</summary>
    public object Snapshot(string nick)
    {
        var key = EconomyStore.Key(nick);
        var active = store.Active();
        object? now = null;
        if (active is not null)
        {
            var entries = store.Entries(active.Id);
            var myVote = store.VoteOf(active.Id, key);
            now = new
            {
                id = active.Id,
                script = active.Script,
                closesAt = active.ClosesAt,
                maxSeconds = Math.Max(5, O.MaxSeconds),
                entries = entries.Select(e => new
                {
                    id = e.Id,
                    nick = e.Nick,
                    trackId = e.TrackId,
                    seconds = e.Seconds,
                    votes = e.Votes,
                    mine = e.NickKey == key,
                }).ToArray(),
                myVote,
                myEntry = entries.FirstOrDefault(e => e.NickKey == key)?.Id,
            };
        }
        // Минулих переможців беремо одним запитом: панель питає снапшот кожні 15 с, і десяток окремих
        // походів у базу на кожне опитування — це вантаж рівно нізащо.
        var past = store.PastWinners(10).Select(c => new
        {
            id = c.Id,
            winner = c.Winner,
            votes = c.Votes,
            closedAt = c.ClosedAt,
            trackId = c.TrackId,
        }).ToArray();
        return new { active = now, past };
    }

    // =============================================================================================
    // Життєвий цикл
    // =============================================================================================

    /// <summary>Відкрити конкурс: сценарій пише Глек, а як мовчить — беремо шаблон дня.</summary>
    public async Task<(bool Ok, string Message)> OpenAsync(CancellationToken ct = default)
    {
        if (!O.Enabled) return (false, "Конкурс реклами вимкнено");
        if (store.Active() is not null) return (false, "Конкурс уже триває — спершу закрий той");

        var script = await ScriptAsync(ct);
        var now = clock.UtcNow;
        // Сценарій Глек пише не миттєво, і за ці секунди конкурс міг відкрити хтось інший (друга вкладка
        // адміна, тікер у понеділок). Тому вирішує не перевірка вище, а сама вставка: вона пише рядок лише
        // тоді, коли відкритого нема, і повертає 0, якщо не встигла.
        var id = store.Open(script, now, now.AddDays(Math.Clamp(O.Days, 1, 30)));
        if (id == 0) return (false, "Конкурс уже триває — спершу закрий той");
        outbox.Post(new Journal($"🎙 Новий конкурс: озвуч рекламу глека! Сценарій і мікрофон — у «Іграх», картка «Реклама глека». Приймаємо {Math.Clamp(O.Days, 1, 30)} дні."));
        log.LogInformation("конкурс реклами {Id} відкрито до {Till}", id, now.AddDays(Math.Clamp(O.Days, 1, 30)));
        return (true, "Конкурс відкрито");
    }

    async Task<string> ScriptAsync(CancellationToken ct)
    {
        string? text = null;
        try { text = await writer.WriteAsync(Instruction, 400, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Глек не написав сценарій — беремо шаблон"); }
        text = text?.Trim();
        if (!string.IsNullOrEmpty(text) && text.Length >= 40) return text;
        return Template(Days.Seed("ad-contest", Days.Today(clock)), store.Latest()?.Id ?? 0);
    }

    /// <summary>
    /// Шаблон не «випадковий», а прив'язаний до дня (та сама дата — той самий сценарій, і тест це бачить),
    /// плюс номер попереднього конкурсу: два конкурси за один день не почнуться однаково. Рахуємо в long:
    /// <see cref="Days.Seed"/> дає майже <c>int.MaxValue</c>, і в int сума з id одного дня перевернулась би
    /// в мінус — а від'ємний індекс поклав би відкриття конкурсу зовсім.
    /// </summary>
    public static string Template(int seed, long lastId) => Templates[(int)(((long)seed + lastId) % Templates.Length)];

    /// <summary>
    /// Закрити конкурс і роздати черепки. Переможець — найбільше голосів, при рівності — той, хто
    /// записався раніше. Якщо не проголосував ніхто, переможця нема: реклама, яку ніхто не обрав,
    /// в ефір не піде (записи все одно оплачуються).
    /// </summary>
    public (bool Ok, string Message) Close(long contestId)
    {
        if (store.Get(contestId) is not { } contest) return (false, "Такого конкурсу нема");
        if (contest.Closed) return (false, "Конкурс уже закрито");

        var entries = store.Entries(contestId);
        var winner = entries.Where(e => e.Votes > 0).OrderByDescending(e => e.Votes).ThenBy(e => e.Id).FirstOrDefault();

        // Закриття атомарне: якщо адмін і тікер натиснули одночасно, виплати зробить тільки один.
        if (!store.Close(contestId, winner?.NickKey, clock.UtcNow)) return (false, "Конкурс уже закрито");

        var room = contestId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in entries)
            Award(room, e.Nick, O.EntryReward, "ad:entry");
        foreach (var voter in store.Voters(contestId))
            Award(room, voter, O.VoteReward, "ad:vote");
        if (winner is not null)
            Award(room, winner.Nick, O.WinnerReward, "ad:winner");

        // Переможець тримається в ефірі, поки не з'явиться новий: конкурс, у якому ніхто не проголосував,
        // не має лишати ефір без реклами. Кеш тут міняємо лише на свіжого переможця — інакше після
        // перезапуску сервера холодний пошук (Winner) знайшов би старого й відповідь стала б іншою.
        if (winner is not null)
            lock (_lock)
            {
                _winner = new AdWinner(contestId, winner.TrackId, winner.Nick, winner.Seconds);
                _winnerKnown = true;
            }

        outbox.Post(new Journal(winner is not null
            ? $"🎙 Конкурс реклами: переміг {winner.Nick} — {Votes(winner.Votes)}. Його реклама тепер крутиться в ефірі"
            : entries.Count == 0
                ? "🎙 Конкурс реклами скінчився, а мікрофон так ніхто й не взяв"
                : "🎙 Конкурс реклами скінчився без переможця: ніхто не проголосував"));
        return (true, winner is not null ? $"Переміг {winner.Nick}" : "Конкурс закрито");
    }

    /// <summary>«7 голосів» / «1 голос» — бо «1 голосів» ріже око так само, як «2 черепків».</summary>
    static string Votes(int n)
    {
        var word = n % 100 is >= 11 and <= 14 ? "голосів"
            : (n % 10) switch { 1 => "голос", 2 or 3 or 4 => "голоси", _ => "голосів" };
        return $"{n} {word}";
    }

    /// <summary>
    /// Платить не конкурс, а економіка: причини <c>ad:*</c> вона знає сама (Rewards.Award), сама
    /// будує з них ref <c>ad:&lt;конкурс&gt;:&lt;за що&gt;:&lt;нік&gt;</c> і сама вішає ачівку переможцю.
    /// </summary>
    void Award(string room, string nick, int shards, string reason)
    {
        if (!Named(nick) || shards <= 0) return;
        try { events.Raise(new AwardEvent("ad-contest", room, nick, shards, reason)); }
        catch (Exception ex) { log.LogWarning(ex, "не виплатив {Reason} для {Nick}", reason, nick); }
    }

    /// <summary>
    /// Раз на хвилину: дотерміновані конкурси закриваються самі, а щопонеділка о 12:00 за Києвом
    /// відкривається новий, якщо активного нема і сьогодні ще не відкривали.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        // Прострочений конкурс доводимо до кінця навіть із вимкненим Ad:Enabled: люди вже записались і
        // проголосували, і лишити їх без черепків до наступного вмикання гірше, ніж не послухатись прапорця.
        if (store.Active() is { } active)
        {
            if (now >= active.ClosesAt) Close(active.Id);
            return;
        }
        if (!O.Enabled || !O.AutoOpen) return;
        var kyiv = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        if (kyiv.DayOfWeek != DayOfWeek.Monday || kyiv.Hour < Math.Clamp(O.OpenHour, 0, 23)) return;
        // Адмін закрив конкурс у понеділок по обіді — не відкриваємо йому одразу наступний.
        if (store.Latest() is { } last && Days.Of(last.CreatedAt) == Days.Of(now)) return;
        await OpenAsync(ct);
    }

    // =============================================================================================
    // Участь
    // =============================================================================================

    /// <summary>
    /// Конкурс, у якому ще можна щось робити. Одна перевірка на всі три дії, бо «вже дзвінок» —
    /// це не тільки прапорець <c>closed</c>: його ставить хвилинний тікер, і між <c>closes_at</c> і його
    /// кроком минає до хвилини (а як тікер спав — то й більше). Приймати голос після дзвінка не годиться.
    /// </summary>
    (AdContestRow? Contest, string Error) Running(long contestId)
    {
        if (!O.Enabled) return (null, "Конкурс реклами вимкнено");
        if (store.Get(contestId) is not { } contest) return (null, "Такого конкурсу нема");
        if (contest.Closed) return (null, "Цей конкурс уже закрито");
        if (clock.UtcNow >= contest.ClosesAt) return (null, "Конкурс уже скінчився");
        return (contest, "");
    }

    /// <summary>Записати (або перезаписати) свою рекламу. Тіло запиту — сирий запис із мікрофона.</summary>
    public async Task<(bool Ok, string Message)> EnterAsync(long contestId, string nick, Stream body, CancellationToken ct)
    {
        if (!Named(nick)) return (false, "Спершу скажи, як тебе кликати");
        if (!voice.Enabled) return (false, "Голосові вимкнені");
        if (Running(contestId) is { Contest: null, Error: var why }) return (false, why);

        TrackInfo track;
        try { (track, _) = await voice.SaveAsync(body, nick, ct); }
        catch (Exception ex) { return (false, "Не вийшло взяти запис: " + ex.Message); }

        var limit = Math.Max(5, O.MaxSeconds) + Math.Max(0, O.GraceSeconds);
        if (track.DurationSec > limit)
        {
            voice.Delete(track.Id);
            return (false, $"Задовга реклама: {track.DurationSec} с, а треба до {Math.Max(5, O.MaxSeconds)}");
        }

        // ffmpeg жує запис секунди, і за цей час конкурс міг закритись (тікер закриває рівно по closes_at,
        // тобто саме тоді, коли всі дописують). Класти запис у закритий конкурс не можна: виплати вже
        // пораховані, і людина лишилась би з «прийнято» без черепків, а mp3 — сиротою в кеші.
        if (Running(contestId) is { Contest: null })
        {
            voice.Delete(track.Id);
            return (false, "Не встиг: конкурс щойно закрився");
        }

        var old = store.PutEntry(contestId, EconomyStore.Key(nick), nick, track.Id, track.DurationSec, clock.UtcNow);
        // перезапис не має лишати по mp3 у кеші — хіба що стару версію господар уже пустив в ефір
        if (old is not null && !InHouse(old)) voice.Delete(old);
        return (true, old is null
            ? "Запис прийнято, тепер чекай на голоси"
            : "Перезаписав — стара версія пішла в небуття разом із голосами за неї");
    }

    /// <summary>Забрати свій запис із конкурсу.</summary>
    public (bool Ok, string Message) DropEntry(long contestId, string nick)
    {
        if (!Named(nick)) return (false, "Спершу скажи, як тебе кликати");
        if (Running(contestId) is { Contest: null, Error: var why }) return (false, why);
        var old = store.DropEntry(contestId, EconomyStore.Key(nick));
        if (old is null) return (false, "Ти ще нічого не записував");
        if (!InHouse(old)) voice.Delete(old);
        return (true, "Запис забрано");
    }

    /// <summary>Проголосувати. Один голос на ніка, не за себе, змінити можна поки конкурс іде.</summary>
    public (bool Ok, string Message) Vote(long contestId, string nick, long entryId)
    {
        if (!Named(nick)) return (false, "Спершу скажи, як тебе кликати");
        if (Running(contestId) is { Contest: null, Error: var why }) return (false, why);
        if (store.EntryById(contestId, entryId) is not { } entry) return (false, "Такого запису нема");
        var key = EconomyStore.Key(nick);
        if (entry.NickKey == key) return (false, "За себе голосувати не можна");
        store.Vote(contestId, key, nick, entryId, clock.UtcNow);
        return (true, $"Голос за {entry.Nick}");
    }

    // =============================================================================================
    // Ефір
    // =============================================================================================

    /// <summary>Реклама-переможець останнього закритого конкурсу; null — крутити нічого.</summary>
    public AdWinner? Winner()
    {
        lock (_lock)
        {
            if (_winnerKnown) return _winner;
            _winnerKnown = true;
            _winner = null;
            // Холодний старт: беремо найсвіжішого переможця з бази. Конкурси без переможця пропускаємо —
            // так само, як їх пропускає Close, коли не чіпає кеш.
            foreach (var c in store.PastWinners(10))
            {
                if (c.TrackId is null || c.Winner is null) continue;
                _winner = new AdWinner(c.Id, c.TrackId, c.Winner, c.Seconds);
                break;
            }
            return _winner;
        }
    }

    // =============================================================================================
    // Ефір господаря: своя реклама і частота
    // =============================================================================================

    /// <summary>Кеш рядка ad_air: джингл питає його на кожен трек.</summary>
    AdAirRow? _air;

    AdAirRow AirRow()
    {
        lock (_lock) return _air ??= store.Air();
    }

    void Forget()
    {
        lock (_lock) _air = null;
    }

    bool InHouse(string trackId) => AirRow().TrackId == trackId;

    /// <summary>
    /// Що зараз крутити: реклама господаря, якщо вона є і файл живий, інакше — переможець конкурсу.
    /// Зниклий файл господаря не лишає ефір без реклами: переможець підхоплює.
    /// </summary>
    public AdWinner? OnAir()
    {
        var air = AirRow();
        if (air.TrackId is { } id && voice.FilePath(id) is not null)
            return new AdWinner(0, id, air.Nick ?? "господар", air.Seconds, House: true);
        return Winner();
    }

    /// <summary>Раз на скільки треків і не частіше ніж раз на скільки хвилин — з бази, а як не задано, з Ad:*.</summary>
    public (int EveryTracks, int MinMinutes) Frequency()
    {
        var air = AirRow();
        return (Math.Max(1, air.EveryTracks ?? O.EveryTracks), Math.Max(0, air.MinMinutes ?? O.MinMinutes));
    }

    public const int MaxEveryTracks = 100;
    public const int MaxMinMinutes = 600;

    /// <summary>Пустити в ефір запис із конкурсу (будь-якого, хай і відкритого чи закритого).</summary>
    public (bool Ok, string Message) AirEntry(long entryId)
    {
        if (store.EntryAnywhere(entryId) is not { } e) return (false, "Такого запису нема");
        if (voice.FilePath(e.TrackId) is null) return (false, "Файлу цього запису вже нема");
        Replace(e.TrackId, e.Nick, e.Seconds, own: false);
        log.LogInformation("господар пустив в ефір рекламу {Nick} ({Track})", e.Nick, e.TrackId);
        return (true, $"Тепер в ефірі реклама {e.Nick}");
    }

    /// <summary>Записати свою рекламу для ефіру. Ліміт — як у звичайних голосових, не конкурсні 30 с.</summary>
    public async Task<(bool Ok, string Message)> AirRecordAsync(string nick, Stream body, CancellationToken ct)
    {
        if (!voice.Enabled) return (false, "Голосові вимкнені");
        TrackInfo track;
        try { (track, _) = await voice.SaveAsync(body, Named(nick) ? nick : "господар", ct); }
        catch (Exception ex) { return (false, "Не вийшло взяти запис: " + ex.Message); }
        Replace(track.Id, Named(nick) ? nick : "господар", track.DurationSec, own: true);
        log.LogInformation("господар записав свою рекламу {Track}, {Sec} с", track.Id, track.DurationSec);
        return (true, "Твоя реклама тепер в ефірі");
    }

    /// <summary>Прибрати рекламу господаря: далі знову крутиться переможець конкурсу (якщо є).</summary>
    public (bool Ok, string Message) AirClear()
    {
        if (AirRow().TrackId is null) return (false, "Своєї реклами й так нема");
        Replace(null, null, 0, own: false);
        return (true, Winner() is { } w ? $"Прибрав. Тепер знову крутиться переможець — {w.Nick}" : "Прибрав. Реклами в ефірі тепер нема");
    }

    public (bool Ok, string Message) AirEvery(int everyTracks, int minMinutes)
    {
        if (everyTracks is < 1 or > MaxEveryTracks) return (false, $"Треків — від 1 до {MaxEveryTracks}");
        if (minMinutes is < 0 or > MaxMinMinutes) return (false, $"Хвилин — від 0 до {MaxMinMinutes}");
        store.SetAirEvery(everyTracks, minMinutes, clock.UtcNow);
        Forget();
        return (true, minMinutes == 0
            ? $"Реклама — раз на {everyTracks} тр."
            : $"Реклама — раз на {everyTracks} тр., але не частіше ніж раз на {minMinutes} хв");
    }

    void Replace(string? trackId, string? nick, int seconds, bool own)
    {
        var old = AirRow();
        store.SetAirTrack(trackId, nick, seconds, own, clock.UtcNow);
        Forget();
        // Свій окремий запис господаря після заміни нікому не потрібен; запис із конкурсу — не наш, хай живе.
        if (old.Own && old.TrackId is { } was && was != trackId && !store.IsEntryTrack(was))
            voice.Delete(was);
    }

    /// <summary>Що бачить господар у панелі: що в ефірі, звідки воно і як часто.</summary>
    public object AirView(int since)
    {
        var air = AirRow();
        var on = OnAir();
        var (every, minutes) = Frequency();
        return new
        {
            on = on is null ? null : new { trackId = on.TrackId, nick = on.Nick, seconds = on.Seconds, house = on.House, contestId = on.ContestId },
            // своя реклама стоїть, але її mp3 зник — крутиться переможець, і господареві варто про це знати
            houseMissing = air.TrackId is not null && on?.House != true,
            everyTracks = every,
            minMinutes = minutes,
            since,
            jingle = O.Enabled && O.Jingle,
        };
    }
}
