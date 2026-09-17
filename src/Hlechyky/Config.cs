namespace Hlechyky;

/// <summary>Repo root discovery: tools, cache, data and web are all addressed relative to it.</summary>
public static class Paths
{
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("HLECHYKY_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
}

public sealed class SiteOptions
{
    public string Name { get; set; } = "Глечики";
    /// <summary>The auto-DJ's persona, nominative ("Дядько Глек радить").</summary>
    public string DjName { get; set; } = "Дядько Глек";
    /// <summary>Genitive form ("порада Дядька Глека").</summary>
    public string DjNameGen { get; set; } = "Дядька Глека";
    public string PublicStreamUrl { get; set; } = "";
    public int StreamDelaySeconds { get; set; } = 6;
    public int ListenPort { get; set; } = 8080;
}

public sealed class AuthOptions
{
    public string AdminKey { get; set; } = "";
}

/// <summary>Вхід через Google. Client ID публічний (він і так у сторінці), секрет для ID-токенів не потрібен. Порожній — кнопки нема.</summary>
public sealed class GoogleOptions
{
    public string ClientId { get; set; } = "";
}

public sealed class YtDlpOptions
{
    public string BinaryPath { get; set; } = "tools/yt-dlp/yt-dlp.exe";
    public string FfmpegDir { get; set; } = "tools/yt-dlp";
    public string CacheDir { get; set; } = "cache";
    /// <summary>Скільки гігабайт треків тримати в CacheDir; старе й рідке видаляється (TrackCache). 0 — без ліміту.</summary>
    public double CacheMaxGb { get; set; } = 5;
    /// <summary>Used only when YouTube demands a login (age-gated 18+ videos): a Netscape cookies.txt exported from a logged-in browser…</summary>
    public string CookiesFile { get; set; } = "";
    /// <summary>…or the browser to read cookies from (firefox works; chrome/edge lock their database while running).</summary>
    public string CookiesFromBrowser { get; set; } = "";
    public int MaxDurationSeconds { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 240;
}

public sealed class VoiceOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Довші записи ріжуться при перегонці; браузер зупиняє запис сам на цій межі.</summary>
    public int MaxSeconds { get; set; } = 120;
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>До якої гучності (LUFS) підтягнути голос, щоб він не тонув між треками.</summary>
    public double LoudnessLufs { get; set; } = -14;
}

public sealed class LiquidsoapOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1234;
    public string ApiKey { get; set; } = "";
    public string CacheMount { get; set; } = "/cache";
}

public sealed class IcecastOptions
{
    public string StatusUrl { get; set; } = "http://127.0.0.1:8000/status-json.xsl";
    public string RadioMount { get; set; } = "/radio.mp3";
    public string SpotifyMount { get; set; } = "/spotify.mp3";
}

public sealed class LastFmOptions
{
    public string ApiKey { get; set; } = "";
    public string SharedSecret { get; set; } = "";
}

public sealed class AutoDjOptions
{
    public bool Enabled { get; set; } = true;
    public int NoRepeatHours { get; set; } = 6;
    public int MaxDurationSeconds { get; set; } = 720;
    public string SeedQuery { get; set; } = "";
    /// <summary>
    /// Скільки треків Глек може поставити «від себе» поспіль, перш ніж сід візьметься не з ефіру,
    /// а з людського замовлення. Без цієї стелі він блукає від власних виборів і не вертається. 0 — без стелі.
    /// </summary>
    public int MaxSelfChain { get; set; } = 3;
    /// <summary>Кожен N-й авто-трек — із власного архіву кімнати, а не з ютубного радіо. 0 — не витягувати.</summary>
    public int ArchiveEvery { get; set; } = 5;
    /// <summary>Наскільки тиснути смаком кімнати (свої артисти, звична мова). 0 — не тиснути зовсім.</summary>
    public double TasteBias { get; set; } = 1;
    /// <summary>Перебити мовний нахил вручну: <c>cyr</c>, <c>lat</c>. Порожньо — рахувати з того, що замовляють люди.</summary>
    public string PreferScript { get; set; } = "";
    /// <summary>
    /// Вага власного вибору Глека в ефірі, коли поради вже будуються від людських замовлень. Трохи —
    /// щоб перехід не рвався; більше — і він знову сідиться від себе. 0 — не зважати на нього зовсім.
    /// </summary>
    public double SelfSeedWeight { get; set; } = 0.3;
    /// <summary>Вага радіо YouTube Music від треку. Саме туди лізуть контент-ферми, тож основа — схожі артисти.</summary>
    public double RadioWeight { get; set; } = 0.6;
    /// <summary>Фільтр якості: артист мусить бути відомий Last.fm чи MusicBrainz або вже звучати в кімнаті.</summary>
    public bool QualityGate { get; set; } = true;
    /// <summary>Скільки слухачів на Last.fm достатньо, щоб артист пройшов без MusicBrainz.</summary>
    public int MinListeners { get; set; } = 5000;
    /// <summary>Те саме для кириличних треків: своя сцена на Last.fm сильно недорахована.</summary>
    public int MinListenersCyr { get; set; } = 1000;
    /// <summary>Скільки годин пам'ятати «Не те» і швидкі скіпи авто-треків.</summary>
    public int FeedbackHours { get; set; } = 48;
    /// <summary>Скіп авто-треку раніше за стільки секунд — сигнал «не те», а не «вже набридло».</summary>
    public int QuickSkipSeconds { get; set; } = 45;
}

public sealed class DjBotOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Ключ з console.anthropic.com. Порожній — Глек просто мовчить, сайт працює як і працював.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-haiku-4-5";
    /// <summary>Жорстка стеля на календарний місяць; коли впирається — Глек мовчить до першого числа.</summary>
    public double MonthlyBudgetUsd { get; set; } = 5;
    /// <summary>Скільки разів одна людина може смикнути Глека за годину.</summary>
    public int PerNickPerHour { get; set; } = 12;
    /// <summary>Спонтанні репліки: не частіше ніж раз на стільки хвилин.</summary>
    public int SpontaneousCooldownMinutes { get; set; } = 20;
    /// <summary>Шанс кинути слівце, коли заграв новий трек.</summary>
    public double ChanceOnTrackChange { get; set; } = 0.15;
    /// <summary>Шанс встряти в жваву балачку.</summary>
    public double ChanceOnLivelyChat { get; set; } = 0.25;
    /// <summary>Скільки повідомлень за 3 хвилини вважати жвавою балачкою.</summary>
    public int LivelyChatMessages { get; set; } = 4;
    public int MaxToolRounds { get; set; } = 4;
    public int MaxTokens { get; set; } = 400;
    public int MaxReplyChars { get; set; } = 400;
    public int TimeoutSeconds { get; set; } = 45;
    // Ціни Haiku 4.5 станом на вересень 2026, $ за мільйон токенів — лише щоб рахувати стелю
    public double InputUsdPerMTok { get; set; } = 1.0;
    public double OutputUsdPerMTok { get; set; } = 5.0;
    public double CacheReadUsdPerMTok { get; set; } = 0.10;
    public double CacheWriteUsdPerMTok { get; set; } = 1.25;
}

/// <summary>
/// Черепки: скільки і за що. Числа з ARCHITECTURE §6.2; секція <c>Economy</c> в appsettings.json,
/// підхоплюється без рестарту. Ставки (0/5/10/25) сюди не входять — це константа каркаса кімнат.
/// </summary>
public sealed class EconomyOptions
{
    /// <summary>Черепок за кожні стільки хвилин на сайті.</summary>
    public int ListenEveryMinutes { get; set; } = 10;
    public int ListenReward { get; set; } = 1;
    /// <summary>Скільки черепків за день можна набути самим лише сидінням на сайті.</summary>
    public int ListenDailyCap { get; set; } = 12;

    public int WinReward { get; set; } = 5;
    public int DrawReward { get; set; } = 2;
    /// <summary>Програв, але дограв до кінця.</summary>
    public int PlayReward { get; set; } = 1;
    /// <summary>Скільки партій на день у ту саму гру ще нагороджуються (перемоги, нічиї й участь разом).</summary>
    public int RewardedGamesPerDay { get; set; } = 10;
    /// <summary>Партія, коротша і за стільки ходів, і за <see cref="MinRewardSeconds"/>, — без нагороди.</summary>
    public int MinRewardMoves { get; set; } = 6;
    public int MinRewardSeconds { get; set; } = 20;

    /// <summary>Типова нагорода за розв'язану головоломку дня.</summary>
    public int DailyReward { get; set; } = 5;
    /// <summary>Скільки черепків на день можна виміняти в гончарному колі.</summary>
    public int ClickerDailyCap { get; set; } = 20;
    /// <summary>Вище цього клейма майстра стелю обміну не піднімуть, хоч скільки їх набери.</summary>
    public int ClickerDailyCapMax { get; set; } = 40;
    /// <summary>Скільки черепків на день можуть принести всі позастандартні нагороди разом.</summary>
    public int AwardDailyCap { get; set; } = 30;

    /// <summary>Бан треку, що грає, за черепки (адміну безкоштовно). 0 — за черепки не можна.</summary>
    public int BanPrice { get; set; } = 100;
    /// <summary>Викупити трек із бану. 0 — за черепки не можна, розбанює лише адмін.</summary>
    public int UnbanPrice { get; set; } = 100;
}

public sealed class DeployOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Спільний секрет із вебхуком на GitHub. Порожній — ендпоінт /api/github/deploy просто не існує.</summary>
    public string WebhookSecret { get; set; } = "";
    public string Branch { get; set; } = "main";
    /// <summary>Назва workflow в Actions, зеленої збірки якого чекаємо (порожньо — будь-якого).</summary>
    public string Workflow { get; set; } = "build";
    public string Script { get; set; } = "deploy.ps1";
    /// <summary>Репозиторій на GitHub («власник/назва»), чиї збірки перевіряє <see cref="DeployWatch"/>. Порожньо — не перевіряє.</summary>
    public string Repo { get; set; } = "";
    /// <summary>Раз на скільки хвилин перевіряти, чи не загубився вебхук про зелену збірку. 0 — не перевіряти.</summary>
    public int PollMinutes { get; set; } = 3;
}
