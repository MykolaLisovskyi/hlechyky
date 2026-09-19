using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування «Своєї гри» (секція <c>Svoya</c> в appsettings).</summary>
public sealed class SvoyaOptions
{
    /// <summary>Вбудовані пакети (JSON-файли, їдуть у git).</summary>
    public string BuiltinDir { get; set; } = "data/svoya/builtin";
    /// <summary>Медіа пакетів користувачів: <c>&lt;MediaDir&gt;/&lt;packId&gt;/&lt;sha1&gt;.&lt;ext&gt;</c>.</summary>
    public string MediaDir { get; set; } = "data/svoya/media";
    /// <summary>Книга фраз ведучого (specs/svoya.md §11); нема — ведучий сухий.</summary>
    public string HostFile { get; set; } = "data/svoya/host.json";
    public int PackMaxMb { get; set; } = 150;
    public int UserMaxMb { get; set; } = 500;
    /// <summary>Скільки пакетів може мати один нік (адмін — без меж).</summary>
    public int MaxPacks { get; set; } = 50;
    /// <summary>Чи можуть гості («гість Вася») робити пакети. Рішення власника (19.09.2026) — ні.</summary>
    public bool GuestsCreate { get; set; }
    /// <summary>Найбільше тіло пакета в PUT/POST, КБ.</summary>
    public int BodyMaxKb { get; set; } = 2048;
}

/// <summary>Хто питає: нік із куки, чи він зареєстрований, чи він адмін.</summary>
public sealed record SvoyaUser(string Nick, bool Registered, bool Admin)
{
    public string Key => Auth.NickKey(Nick);
}

/// <summary>Пакет цілком для редактора: сам пакет, чи можна правити, і чому в нього ще не можна грати.</summary>
public sealed record SvoyaFull(SvoyaPack Pack, bool CanEdit, bool Ready, IReadOnlyList<string> Problems);

/// <summary>Відповідь сервісу пакетів: те, що ендпоінт віддасть як <c>{ ok, message, … }</c>.</summary>
public sealed record SvoyaReply(bool Ok, string Message = "", object? Data = null, IReadOnlyList<string>? Errors = null)
{
    public static SvoyaReply Fail(string message, IReadOnlyList<string>? errors = null) => new(false, message, null, errors);
}

/// <summary>
/// Теки з медіа пакетів. Тут — лише те, що треба сховищу (розмір файла, прибрати теку); завантаження й
/// перекодування додає конструктор (етап 5).
/// </summary>
public sealed class SvoyaFiles(string root)
{
    public string Root { get; } = root;

    public string Dir(string packId) => Path.Combine(Root, Safe(packId));

    /// <summary>Розмір файла в теці пакета або null, якщо його нема.</summary>
    public long? Size(string packId, string file)
    {
        if (Path.GetFileName(file) != file || file.Contains("..")) return null;
        var info = new FileInfo(Path.Combine(Dir(packId), file));
        return info.Exists ? info.Length : null;
    }

    public void DeleteAll(string packId)
    {
        try { if (Directory.Exists(Dir(packId))) Directory.Delete(Dir(packId), recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Id пакета — лише літери, цифри й «_»: з нього складається шлях.</summary>
    static string Safe(string id) => new([.. id.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')]);
}

/// <summary>
/// Вбудовані пакети: <c>data/svoya/builtin/*.json</c>, читаються один раз на старті (як банк «Скільки?»).
/// Редагувати їх не можна — лише скопіювати до себе. Пакет, у який не можна грати, у список не йде, а
/// причина лишається в <see cref="Problems"/> (і в лозі).
/// </summary>
public sealed class SvoyaBuiltin
{
    public const string Author = "Глечики";

    public IReadOnlyList<SvoyaPack> Packs { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Problems { get; }

    public SvoyaBuiltin(IEnumerable<SvoyaPack> packs)
    {
        Packs = [.. packs];
        Problems = new Dictionary<string, IReadOnlyList<string>>();
    }

    public SvoyaBuiltin(string dir, ILogger? log = null)
    {
        var packs = new List<SvoyaPack>();
        var problems = new Dictionary<string, IReadOnlyList<string>>();
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                SvoyaPack? pack;
                try { pack = SvoyaPack.Parse(File.ReadAllText(file)); }
                catch (IOException) { pack = null; }
                if (pack is null) { problems[name] = ["кривий JSON"]; continue; }
                Stamp(pack, name);
                var errors = pack.Validate();
                if (errors.Count > 0) { problems[name] = errors; continue; }
                if (packs.Any(p => p.Id == pack.Id)) { problems[name] = [$"id {pack.Id} уже зайнятий"]; continue; }
                packs.Add(pack);
            }
        }
        foreach (var (name, errors) in problems)
            log?.LogWarning("своя гра: вбудований пакет {Name} пропущено: {Errors}", name, string.Join("; ", errors.Take(5)));
        Packs = packs;
        Problems = problems;
    }

    /// <summary>Те, що у вбудованого пакета завжди однакове, хоч би що написали у файлі.</summary>
    public static void Stamp(SvoyaPack pack, string fileName)
    {
        pack.Normalize();
        if (!pack.Id.StartsWith("b_", StringComparison.Ordinal)) pack.Id = "b_" + fileName;
        pack.Source = SvoyaPack.Builtin;
        pack.Author = Author;
        pack.AuthorKey = "";
        pack.Public = true;
    }

    public SvoyaPack? Get(string id) => Packs.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// Пакети «Своєї гри»: список, права, створення, збереження, копія (specs/svoya.md §6). Уся логіка API — тут,
/// ендпоінти в <see cref="SvoyaSetup"/> лише перекладають HTTP. Правило власності: правити й видаляти може
/// автор (<c>owner_key == nick_key</c>) або адмін; створювати — лише зареєстровані (рішення власника сайту),
/// бо гостя «Вася» підробить будь-хто, назвавшись так само.
/// </summary>
public sealed class SvoyaPacks(SvoyaStore store, SvoyaBuiltin builtin, SvoyaFiles files, IClock clock,
    IOptionsMonitor<SvoyaOptions>? options = null, ILogger<SvoyaPacks>? log = null, ISvoyaVoice? voice = null,
    SvoyaUploads? uploads = null) : ISvoyaPackSource
{
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    SvoyaOptions O => options?.CurrentValue ?? new SvoyaOptions();

    public const string NoPack = "Такого пакета нема";
    public const string NotYours = "Це не твій пакет";
    public const string SignIn = "Увійди, щоб робити пакети";

    // ---------- читання ----------

    /// <summary>Список для лобі й конструктора: три групи, без жодного запитання чи відповіді (спойлери).</summary>
    public object List(SvoyaUser u)
    {
        var plays = store.Plays();
        var key = u.Key;
        var mine = store.Mine(key);
        return new
        {
            builtin = builtin.Packs.Select(p => Summary(p, null, plays, u)).ToArray(),
            @public = store.Public(withHidden: u.Admin).Where(r => r.OwnerKey != key).Select(r => Summary(r.Pack(), r, plays, u)).ToArray(),
            mine = mine.Select(r => Summary(r.Pack(), r, plays, u)).ToArray(),
            canCreate = CanCreate(u),
        };
    }

    /// <summary>Рядок списку. Лише назви: раунди й теми, щоб гравці знали, на що йдуть, — але не що всередині.</summary>
    static object Summary(SvoyaPack p, SvoyaRow? row, IReadOnlyDictionary<string, int> plays, SvoyaUser u) => new
    {
        id = p.Id,
        title = p.Title,
        description = p.Description,
        author = row?.OwnerNick ?? p.Author,
        source = p.Source,
        rounds = p.Rounds.Select(r => new { name = r.Name, final = r.IsFinal, themes = r.Themes.Select(t => t.Name).ToArray() }).ToArray(),
        questions = p.QuestionCount,
        mediaMb = Math.Round((row?.MediaBytes ?? 0) / (1024.0 * 1024.0), 1),
        plays = plays.GetValueOrDefault(p.Id),
        updatedAt = row?.UpdatedAt ?? p.UpdatedAt,
        @public = row?.Public ?? true,
        ready = row?.Ready ?? true,
        hidden = row?.Hidden == true ? true : (bool?)null,
        canEdit = row is not null && CanEdit(u, row),
    };

    /// <summary>Пакет цілком — для редактора. Лише автору або адміну: у ньому всі відповіді.</summary>
    public SvoyaReply Get(string id, SvoyaUser u)
    {
        if (builtin.Get(id) is { } b)
            return u.Admin ? new SvoyaReply(true, "", Full(b, canEdit: false, [])) : SvoyaReply.Fail(NotYours);
        if (store.Get(id) is not { } row) return SvoyaReply.Fail(NoPack);
        if (!CanEdit(u, row)) return SvoyaReply.Fail(NotYours);
        var pack = row.Pack();
        return new SvoyaReply(true, "", Full(pack, canEdit: true, Problems(pack)));
    }

    static SvoyaFull Full(SvoyaPack pack, bool canEdit, IReadOnlyList<string> problems) =>
        new(pack, canEdit, problems.Count == 0, problems);

    // ---------- зміни ----------

    public bool CanCreate(SvoyaUser u) => u.Admin || u.Registered || (O.GuestsCreate && !string.IsNullOrWhiteSpace(u.Nick));

    public static bool CanEdit(SvoyaUser u, SvoyaRow row) => u.Admin || row.OwnerKey == u.Key;

    /// <summary>Новий пакет: з тіла (імпорт, «вставити JSON») або класичний шаблон.</summary>
    public SvoyaReply Create(SvoyaUser u, SvoyaPack? body = null, string source = SvoyaPack.User)
    {
        if (!CanCreate(u)) return SvoyaReply.Fail(SignIn);
        if (!u.Admin && store.CountOf(u.Key) >= O.MaxPacks) return SvoyaReply.Fail($"У тебе вже {O.MaxPacks} пакетів — прибери старі");
        var pack = (body ?? SvoyaPack.Classic()).Normalize();
        var errors = pack.Check();
        if (errors.Count > 0) return SvoyaReply.Fail("Пакет не зберігся", errors);

        var now = clock.UtcNow;
        pack.Id = NewId();
        pack.Source = source;
        pack.Author = u.Nick;
        pack.AuthorKey = u.Key;
        pack.CreatedAt = pack.UpdatedAt = now;
        var problems = Problems(pack);
        store.Insert(Row(pack, u.Key, u.Nick, problems.Count == 0, 0, now));
        _log.LogInformation("своя гра: {Nick} створив пакет {Id} «{Title}»", u.Nick, pack.Id, pack.Title);
        return new SvoyaReply(true, "Пакет створено", Full(pack, canEdit: true, problems));
    }

    /// <summary>
    /// Зберегти пакет цілком (автозбереження конструктора). Незмінне — id, автор, джерело й дата створення —
    /// береться з бази, а не з тіла: інакше пакет можна було б «подарувати» чужому ніку.
    /// </summary>
    public SvoyaReply Save(string id, SvoyaUser u, SvoyaPack body)
    {
        if (store.Get(id) is not { } row) return SvoyaReply.Fail(builtin.Get(id) is null ? NoPack : "Вбудований пакет не правиться — скопіюй його до себе");
        if (!CanEdit(u, row)) return SvoyaReply.Fail(NotYours);
        var pack = body.Normalize();
        var errors = pack.Check();
        if (errors.Count > 0) return SvoyaReply.Fail("Пакет не зберігся", errors);

        var old = row.Pack();
        pack.Id = row.Id;
        pack.Author = row.OwnerNick;
        pack.AuthorKey = row.OwnerKey;
        pack.Source = row.Source;
        pack.CreatedAt = row.CreatedAt;
        pack.UpdatedAt = clock.UtcNow;
        var bytes = MediaBytes(pack);
        if (!u.Admin && bytes + store.MediaBytesOf(row.OwnerKey, row.Id) > (long)O.UserMaxMb * 1024 * 1024)
            return SvoyaReply.Fail($"Усі твої пакети разом більші за {O.UserMaxMb} МБ медіа");
        var problems = Problems(pack);
        store.Update(Row(pack, row.OwnerKey, row.OwnerNick, problems.Count == 0, bytes, pack.CreatedAt) with { Hidden = row.Hidden });
        uploads?.Sweep(pack);                                      // медіа, яке пакет уже не згадує, — геть (зі запасом часу)
        // готовий пакет озвучуємо наперед, у фоні: до першої партії більшість реплік уже лежатиме в кеші
        if (problems.Count == 0) voice?.Prepare(VoiceName, SvoyaLines.All(pack));
        return new SvoyaReply(true, problems.Count == 0 ? "Збережено" : "Збережено як чернетку", new { ready = problems.Count == 0, problems, updatedAt = pack.UpdatedAt });
    }

    public SvoyaReply Delete(string id, SvoyaUser u)
    {
        if (store.Get(id) is not { } row) return SvoyaReply.Fail(builtin.Get(id) is null ? NoPack : "Вбудований пакет не видаляється");
        if (!CanEdit(u, row)) return SvoyaReply.Fail(NotYours);
        store.Delete(id);
        files.DeleteAll(id);
        _log.LogInformation("своя гра: {Nick} видалив пакет {Id} «{Title}»", u.Nick, id, row.Title);
        return new SvoyaReply(true, "Пакет видалено");
    }

    /// <summary>
    /// Скопіювати до себе: вбудований, публічний, свій (адмін — будь-який). Копія приватна, медіа
    /// копіюються разом із теками.
    /// </summary>
    public SvoyaReply Copy(string id, SvoyaUser u)
    {
        SvoyaPack source;
        if (builtin.Get(id) is { } b) source = b.Clone();
        else if (store.Get(id) is { } row && (u.Admin || row.OwnerKey == u.Key || row is { Public: true, Hidden: false, Ready: true }))
            source = row.Pack();
        else return SvoyaReply.Fail(NoPack);

        var title = source.Title + " (копія)";
        source.Title = title.Length > SvoyaPack.NameMax ? source.Title[..(SvoyaPack.NameMax - 8)].TrimEnd() + " (копія)" : title;
        source.Public = false;
        var r = Create(u, source);
        if (!r.Ok) return r;
        var copy = ((SvoyaFull)r.Data!).Pack;
        CopyMedia(id, copy.Id);
        if (copy.MediaFiles().Any()) Save(copy.Id, u, copy.Clone());   // перерахувати розмір медіа й готовність уже з файлами
        return new SvoyaReply(true, "Скопійовано до тебе", new { id = copy.Id });
    }

    void CopyMedia(string fromId, string toId)
    {
        var from = files.Dir(fromId);
        if (!Directory.Exists(from)) return;
        var to = files.Dir(toId);
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }

    /// <summary>Адмін ховає публічний пакет зі списку (або повертає).</summary>
    public SvoyaReply Hide(string id, SvoyaUser u, bool hidden)
    {
        if (!u.Admin) return SvoyaReply.Fail("Це вміє тільки господар");
        return store.SetHidden(id, hidden) ? new SvoyaReply(true, hidden ? "Сховано" : "Знову видно") : SvoyaReply.Fail(NoPack);
    }

    /// <summary>Голос, яким озвучуємо наперед (типовий у лобі).</summary>
    public const string VoiceName = "ostap";

    /// <summary>
    /// «Озвучити»: скільки реплік пакета вже готово; <paramref name="start"/> — ще й поставити решту в чергу.
    /// Лише автору чи адміну (у репліках — відповіді).
    /// </summary>
    public SvoyaReply Voice(string id, SvoyaUser u, bool start)
    {
        SvoyaPack pack;
        if (builtin.Get(id) is { } b) { if (!u.Admin) return SvoyaReply.Fail(NotYours); pack = b; }
        else if (store.Get(id) is { } row) { if (!CanEdit(u, row)) return SvoyaReply.Fail(NotYours); pack = row.Pack(); }
        else return SvoyaReply.Fail(NoPack);
        if (voice is null || !voice.Enabled) return SvoyaReply.Fail("Голосу на сервері нема");
        var lines = SvoyaLines.All(pack).Distinct().ToList();
        if (start) voice.Prepare(VoiceName, lines);
        return new SvoyaReply(true, "", new { ready = lines.Count(l => voice.Ready(VoiceName, l) is not null), total = lines.Count });
    }

    // ---------- для гри ----------

    /// <summary>
    /// Пакет для партії: копія, яку гра може тримати в собі. Грати можна у вбудований, публічний і свій; у
    /// чернетку — ні (у ній бувають запитання без відповіді).
    /// </summary>
    public SvoyaPack? Playable(string id, string hostNick)
    {
        if (builtin.Get(id) is { } b) return b.Clone();
        if (store.Get(id) is not { } row || !row.Ready) return null;
        if (row.OwnerKey != Auth.NickKey(hostNick) && !(row.Public && !row.Hidden)) return null;
        return row.Pack();
    }

    public void NotePlayed(string id) => store.AddPlay(id);

    /// <summary>Чому в пакет ще не можна грати (порожньо — можна). Медіа перевіряються по теці пакета.</summary>
    public List<string> Problems(SvoyaPack pack) =>
        pack.Validate(file => files.Size(pack.Id, file), (long)O.PackMaxMb * 1024 * 1024);

    long MediaBytes(SvoyaPack pack) => pack.MediaFiles().Sum(f => files.Size(pack.Id, f) ?? 0);

    static SvoyaRow Row(SvoyaPack p, string ownerKey, string ownerNick, bool ready, long bytes, DateTimeOffset created) =>
        new(p.Id, ownerKey, ownerNick, p.Title, p.Public, false, ready, p.Source, p.ToJson(), bytes, created, p.UpdatedAt);

    string NewId()
    {
        while (true)
        {
            var id = "p_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            if (store.Get(id) is null) return id;
        }
    }
}
