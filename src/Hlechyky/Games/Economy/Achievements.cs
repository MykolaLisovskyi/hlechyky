using System.Collections.Concurrent;

namespace Hlechyky.Games.Economy;

/// <summary>Одна ачівка з каталогу. <see cref="Hidden"/> — не показувати, поки не здобув.</summary>
public sealed record Achievement(string Key, string Title, string Text, string Icon, int Reward, bool Hidden = false);

/// <summary>Каталог ачівок (ARCHITECTURE §8). Ключі стабільні: вони лежать у базі.</summary>
public static class AchievementCatalog
{
    public static readonly IReadOnlyList<Achievement> All =
    [
        new("first-game",   "Перший крок",     "Дограв першу партію з людиною", "🚪", 5),
        new("first-win",    "Перша перемога",  "Виграв уперше", "🥇", 10),
        new("ten-wins",     "Десятка",         "Десять перемог", "🔟", 25),
        new("chess-5",      "Шахіст",          "П'ять перемог у шахах", "♟", 30),
        new("checkers-5",   "Шашист",          "П'ять перемог у шашках", "⛀", 30),
        new("all-boards",   "Настільний",      "Перемога в кожній настільній грі", "🎲", 50),
        new("streak-3",     "Серія",           "Три перемоги поспіль", "🔥", 15),
        new("scrabble-30",  "Ерудит",          "Слово на 30 очок і більше", "🔤", 20),
        new("mines-fast",   "Сапер",           "Сапер дня швидше за хвилину", "💣", 20),
        new("wordle-2",     "З двох спроб",    "Глек-слово з двох спроб", "🎯", 20),
        new("wordle-7",     "Тиждень слів",    "Сім днів Глек-слова поспіль", "📅", 30),
        new("duel-fast",    "Швидка рука",     "Постріл швидше за 200 мс", "🤠", 15),
        new("duel-10",      "Ковбой",          "Десять перемог у дуелі", "🔫", 20),
        new("mafia-win",    "Мафіозі",         "Перемога за мафію", "🕶", 15),
        new("sheriff",      "Комісар",         "Знайшов мафію перевіркою", "🔎", 15),
        new("mafia-maniac", "Маньяк",          "Пересидів у мафії і село, і мафію", "🔪", 25),
        new("ad-winner",    "Голос села",      "Виграв конкурс реклами", "📢", 25),
        new("potter-1k",    "Гончар",          "Тисяча глеків у гончарному колі", "🏺", 10),
        new("potter-100k",  "Майстер-гончар",  "Сто тисяч глеків", "🏆", 30),
        new("potter-1m",    "Мільйон глеків",  "Мільйон глеків за весь час", "🧺", 15),
        new("potter-1b",    "Глечаний магнат", "Мільярд глеків за весь час", "💎", 40),
        new("potter-1t",    "Цар-гончар",      "Трильйон глеків за весь час", "👑", 60),
        new("potter-fire",  "Перший обпал",    "Обпалив майстерню за клейма майстра", "🔥", 20),
        new("potter-golden","Ловець розписних","Впіймав 50 розписних глеків", "🎨", 25),
        new("potter-museum","Музей у хаті",    "Зібрав усі розписи глеків", "🖼", 50),
        new("potter-grab",  "Спритні руки",    "Спіймав 100 глеків, що падали з полиці", "🤲", 30),
        new("potter-streak","Жодного розбитого","Десять глеків з полиці поспіль", "🧤", 25),
        // Сьоме оновлення «Ремесло» (docs/games/specs/clicker-v7.md): гра сама видає їх через Ctx.Award(0, 0, "ach:<key>").
        new("potter-ware-1",      "Перший виріб",      "Виліпив на колі перший виріб", "🏺", 10),
        new("potter-ware-1k",     "Тисяча виробів",    "Виліпив тисячу виробів", "🧱", 30),
        new("potter-kiln-perfect","Дзвінке горно",     "Усі вироби в горні вийшли дзвінкими", "🔔", 25),
        new("potter-album-row",   "Уся колекція виробу","Один виріб у всіх розписах", "📒", 25),
        new("potter-album-all",   "Альбом майстра",    "Заповнив увесь альбом виробів", "📚", 80),
        new("potter-museum-shards","Трипільський музей","Знайшов у глинищі всі трипільські речі", "🏺", 40),
        new("potter-stove",       "Кахляна піч",       "Склав кахляну піч у хаті", "🧱", 40),
        new("potter-mastery",     "Золоті руки",       "Майстерність десятого рівня в одному виробі", "🖐", 30),
        new("potter-rep",         "Шана в селі",       "Найвища репутація в одному з гончарних сіл", "🤝", 30),
        new("potter-guest",       "Гостинна хата",     "Прийняв двадцять гостей", "🚪", 20),
        new("potter-wagon",       "Віз на ярмарок",    "Разом із цехом відправив денний віз", "🛒", 30),
        new("potter-gift",        "Щедра душа",        "Подарував друзям десять виробів", "🎁", 20),
        new("potter-rank",        "Цехмістр",          "Дійшов до найвищого цехового рангу", "🎖", 60),
        new("high-roller",  "Ставка",          "Виграв партію зі ставкою 25", "💰", 15),
        new("listener-10h", "Слухач",          "Десять годин на сайті", "🎧", 15),
        new("listener-100h","Меломан",         "Сто годин на сайті", "📻", 50),
        new("rich-100",     "Сотня",           "Сто черепків на балансі", "🏦", 10),
    ];

    static readonly Dictionary<string, Achievement> ByKey = All.ToDictionary(a => a.Key, StringComparer.Ordinal);

    public static Achievement? Get(string key) => ByKey.GetValueOrDefault(key);
}

/// <summary>
/// Видача ачівок. Перевірки — на подіях, які приносить <see cref="Rewards"/> (партія, соло, нагорода),
/// плюс онлайн-хвилини з тікера і зміна балансу з <see cref="Economy"/>. Кожна ачівка видається один раз:
/// сторож — <c>INSERT OR IGNORE</c> у таблицю, а не перевірка перед вставкою.
/// </summary>
public sealed class Achievements
{
    /// <summary>«Виграй N партій у грі X» — усе, що рахується однаково.</summary>
    static readonly (string Key, string Game, int Wins)[] WinRules =
    [
        ("chess-5", "chess", 5),
        ("checkers-5", "checkers", 5),
        ("duel-10", "duel", 10),
    ];

    readonly EconomyStore _store;
    readonly Economy _economy;
    readonly GameNames _names;
    readonly IClock _clock;
    readonly IOutbox _outbox;
    /// <summary>Що вже видано цьому серверу: інакше «Сотня» і «Меломан» довбали б базу на кожен рух грошей
    /// і на кожну хвилину онлайну — довічно, бо давно видана ачівка все одно щоразу йшла б у INSERT.</summary>
    readonly ConcurrentDictionary<string, byte> _granted = new(StringComparer.Ordinal);

    public Achievements(EconomyStore store, Economy economy, GameNames names, IClock clock, IOutbox outbox)
    {
        _store = store; _economy = economy; _names = names; _clock = clock; _outbox = outbox;
        _economy.Changed += OnBalance;
    }

    /// <summary>Видати ачівку, якщо її ще не було. true — щойно видали.</summary>
    public bool Unlock(string nick, string key)
    {
        var a = AchievementCatalog.Get(key);
        if (a is null) return false;
        var nickKey = Economy.Key(nick);
        if (nickKey.Length == 0) return false;
        // у пам'яті — і ті, що ми щойно видали, і ті, які база відбила як наявні: обидва випадки означають
        // «більше сюди не ходимо». А от якщо база впала — мітку знімаємо, щоб наступна подія спробувала ще раз
        var mark = $"{nickKey}|{key}";
        if (!_granted.TryAdd(mark, 0)) return false;
        bool granted;
        try { granted = _store.Unlock(nickKey, nick, key, _clock.UtcNow); }
        catch (Exception) { _granted.TryRemove(mark, out _); throw; }
        if (!granted) return false;

        if (a.Reward > 0)
            _economy.Grant(nick, a.Reward, $"ach:{key}", $"ach:{key}:{nickKey}",
                $"+{a.Reward} {Economy.Shards(a.Reward)}: ачівка «{a.Title}»");
        _outbox.Post(new AchievementUnlocked(nick, a.Key, a.Title, a.Text, a.Icon, a.Reward));
        _outbox.Post(new Journal($"🏅 {nick}: ачівка «{a.Title}»" +
            (a.Reward > 0 ? $" (+{a.Reward} {Economy.Shards(a.Reward)})" : "")));
        return true;
    }

    public bool Has(string nick, string key) => _store.HasAchievement(Economy.Key(nick), key);

    /// <summary>Що людина вже здобула — для профілю.</summary>
    public List<(Achievement Info, DateTimeOffset At)> Of(string nick) =>
        _store.AchievementsOf(Economy.Key(nick))
            .Select(u => (Info: AchievementCatalog.Get(u.Key), u.At))
            .Where(x => x.Info is not null)
            .Select(x => (x.Info!, x.At))
            .ToList();

    // ---------- перевірки на подіях ----------

    /// <summary>Кличе <see cref="Rewards"/> уже після запису результатів у базу.</summary>
    public void OnRoomFinished(RoomFinishedEvent e, string nick, string outcome)
    {
        var nickKey = Economy.Key(nick);
        var all = DateTimeOffset.MinValue;

        Unlock(nick, "first-game");
        if (outcome != "win") return;

        var wins = _store.CountResults(nickKey, null, "win", all);
        if (wins >= 1) Unlock(nick, "first-win");
        if (wins >= 10) Unlock(nick, "ten-wins");

        foreach (var (key, game, need) in WinRules)
            if (game == e.GameId && _store.CountResults(nickKey, game, "win", all) >= need)
                Unlock(nick, key);

        // «Настільний» — перемога в КОЖНІЙ настільній грі, яку знає платформа (реєстр збірки, ARCHITECTURE §8):
        // планка однакова для всіх і не залежить від того, у що встигли пограти інші. Приховані настільні
        // (морський бій, доміно, дурень) — теж настільні. На порожньому списку ігор (ранні гілки, тести)
        // ачівку не даємо: інакше перша ж перемога зачиняла б її за 50 черепків.
        var boards = _names.All.Where(i => i.Group == GameGroup.Board && !i.Solo).Select(i => i.Id).ToList();
        if (boards.Count >= 3)
        {
            var won = _store.GamesWon(nickKey);
            if (boards.All(won.Contains)) Unlock(nick, "all-boards");
        }

        var recent = _store.RecentResults(nickKey, 3, multiplayerOnly: true);
        if (recent.Count >= 3 && recent.All(r => r.Outcome == "win")) Unlock(nick, "streak-3");

        if (e.Stake >= 25) Unlock(nick, "high-roller");
    }

    /// <summary>
    /// Соло-результат: гончарне коло рахує глеки за весь час, а дуель кладе сюди найкращу реакцію в
    /// мілісекундах (specs/duel.md — «Швидка рука» перевіряється саме тут, а не в самій грі).
    /// </summary>
    public void OnSolo(SoloScoreEvent e)
    {
        if (e.GameId == "clicker")
        {
            if (e.Score >= 1_000) Unlock(e.Nick, "potter-1k");
            if (e.Score >= 100_000) Unlock(e.Nick, "potter-100k");
            if (e.Score >= 1_000_000) Unlock(e.Nick, "potter-1m");
            if (e.Score >= 1_000_000_000) Unlock(e.Nick, "potter-1b");
            if (e.Score >= 1_000_000_000_000) Unlock(e.Nick, "potter-1t");
        }
        if (e.GameId == "duel" && e.Score is > 0 and < 200) Unlock(e.Nick, "duel-fast");
    }

    /// <summary>Результат щоденної головоломки (уже записаний), <paramref name="streak"/> — серія разом із сьогодні.</summary>
    public void OnDaily(string nick, DailyRow row, int streak)
    {
        if (!row.Solved) return;
        if (row.Game == "wordle")
        {
            if (row.Attempts is > 0 and <= 2) Unlock(nick, "wordle-2");
            if (streak >= 7) Unlock(nick, "wordle-7");
        }
        if (row.Game.StartsWith("mines", StringComparison.Ordinal) && row.Ms is > 0 and < 60_000)
            Unlock(nick, "mines-fast");
    }

    /// <summary>
    /// Позастандартна нагорода. Гра, якій треба ачівка, якої платформа сама не побачить (роль у мафії,
    /// слово на 30 очок, реакція швидша за 200 мс), кличе <c>Ctx.Award(seat, 0, "ach:&lt;key&gt;")</c>.
    /// </summary>
    public void OnAward(AwardEvent e)
    {
        if (e.Reason.StartsWith("ach:", StringComparison.Ordinal))
        {
            Unlock(e.Nick, e.Reason[4..]);
            return;
        }
        if (e.Reason is "ad:winner") Unlock(e.Nick, "ad-winner");
    }

    /// <summary>Хвилини на сайті всього (не за день) — кличе EconomyTicker.</summary>
    public void OnOnlineMinutes(string nick, int totalMinutes)
    {
        if (totalMinutes >= 600) Unlock(nick, "listener-10h");
        if (totalMinutes >= 6_000) Unlock(nick, "listener-100h");
    }

    void OnBalance(string nick, int balance)
    {
        if (balance >= 100) Unlock(nick, "rich-100");
    }
}
