using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Імпорт і експорт пакетів (specs/svoya.md §7). Два формати на вхід: <c>.siq</c> із SIGame (zip із
/// <c>content.xml</c> версій 4 і 5 і теками <c>Images/Audio/Video</c>) і наш власний zip (<c>pack.json</c> +
/// <c>media/</c>) — той, що віддає експорт, щоб пакети переносились між серверами. Медіа з архіву йде тими самими
/// воротами, що й з конструктора (<see cref="SvoyaUploads"/>): перекодування, ліміти, квоти. Що не влізло —
/// пропускається з рядком у звіті, а не валить увесь імпорт.
/// </summary>
public sealed class SvoyaImport(SvoyaPacks packs, SvoyaUploads uploads, SvoyaFiles files)
{
    /// <summary>Найбільший архів, який ми взагалі розпаковуємо.</summary>
    public const long MaxZipBytes = 250L * 1024 * 1024;

    /// <summary>Посилання на медіа всередині архіву, яке ще треба завантажити й покласти в запитання.</summary>
    sealed record MediaRef(SvoyaQuestion Question, bool Answer, string Entry, string Kind);

    // =========================================================================================
    // Імпорт
    // =========================================================================================

    /// <summary>
    /// Імпортувати архів. Результат — <c>{ id, ready, problems, warnings }</c>. Пакет стає власним пакетом
    /// того, хто імпортує (приватним), тож далі його можна правити в конструкторі.
    /// </summary>
    public async Task<SvoyaReply> ImportAsync(SvoyaUser u, Stream zip, CancellationToken ct)
    {
        if (!packs.CanCreate(u)) return SvoyaReply.Fail(SvoyaPacks.SignIn);
        ZipArchive archive;
        try { archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { return SvoyaReply.Fail("Це не zip і не .siq"); }
        using (archive)
        {
            var warnings = new List<string>();
            var refs = new List<MediaRef>();
            SvoyaPack? pack;
            string source;
            if (Entry(archive, "pack.json") is { } json)
            {
                source = SvoyaPack.User;
                pack = await ReadOwnAsync(json, archive, refs, warnings, ct);
            }
            else if (Entry(archive, "content.xml") is { } xml)
            {
                source = SvoyaPack.Siq;
                pack = await ReadSiqAsync(xml, archive, refs, warnings, ct);
            }
            else return SvoyaReply.Fail("У архіві нема ні content.xml (SIGame), ні pack.json");
            if (pack is null) return SvoyaReply.Fail(warnings.FirstOrDefault() ?? "Пакет не прочитався");

            Fit(pack, warnings);
            pack.Public = false;
            var created = packs.Create(u, pack, source);
            if (!created.Ok) return created;
            var id = ((SvoyaFull)created.Data!).Pack.Id;
            pack = ((SvoyaFull)created.Data!).Pack;

            foreach (var r in refs)
            {
                var entry = archive.GetEntry(r.Entry)!;
                await using var s = entry.Open();
                var up = await uploads.UploadAsync(id, null, entry.Name, s, ct);
                if (!up.Ok) { warnings.Add($"Медіа «{Uri.UnescapeDataString(entry.Name)}» пропущено: {up.Message}"); continue; }
                var d = System.Text.Json.JsonSerializer.SerializeToElement(up.Data);
                var media = new SvoyaMedia { Kind = d.GetProperty("kind").GetString()!, File = d.GetProperty("file").GetString()!, Seconds = d.GetProperty("seconds").GetInt32() };
                if (r.Answer) r.Question.AnswerMedia = media; else r.Question.Media = media;
                if (up.Message.Length > 0) warnings.Add($"«{Uri.UnescapeDataString(entry.Name)}»: {up.Message}");
            }

            var saved = packs.Save(id, u, pack);
            if (!saved.Ok) return saved;
            var d2 = System.Text.Json.JsonSerializer.SerializeToElement(saved.Data);
            var problems = d2.GetProperty("problems").EnumerateArray().Select(x => x.GetString()!).ToList();
            return new SvoyaReply(true, problems.Count == 0 ? "Імпортовано — можна грати" : "Імпортовано як чернетку — подивись зауваження",
                new { id, ready = problems.Count == 0, problems, warnings });
        }
    }

    /// <summary>Запис архіву за іменем без огляду на регістр і на те, чи ім'я закодоване.</summary>
    static ZipArchiveEntry? Entry(ZipArchive zip, string path)
    {
        var want = Norm(path);
        return zip.Entries.FirstOrDefault(e => Norm(e.FullName) == want);
    }

    static string Norm(string p)
    {
        string s;
        try { s = Uri.UnescapeDataString(p); } catch (UriFormatException) { s = p; }
        return s.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    // ---------- наш zip ----------

    static async Task<SvoyaPack?> ReadOwnAsync(ZipArchiveEntry json, ZipArchive zip, List<MediaRef> refs, List<string> warnings, CancellationToken ct)
    {
        string text;
        await using (var s = json.Open())
        using (var r = new StreamReader(s)) text = await r.ReadToEndAsync(ct);
        var pack = SvoyaPack.Parse(text);
        if (pack is null) { warnings.Add("pack.json не прочитався"); return null; }
        pack.Normalize();
        foreach (var q in pack.Rounds.SelectMany(r => r.Themes).SelectMany(t => t.Questions))
        {
            foreach (var answer in new[] { false, true })
            {
                var m = answer ? q.AnswerMedia : q.Media;
                if (m is null) continue;
                if (answer) q.AnswerMedia = null; else q.Media = null;
                if (Entry(zip, "media/" + m.File) is { } e) refs.Add(new MediaRef(q, answer, e.FullName, m.Kind));
                else warnings.Add($"Медіа {m.File} нема в архіві");
            }
        }
        return pack;
    }

    // ---------- .siq ----------

    static async Task<SvoyaPack?> ReadSiqAsync(ZipArchiveEntry xml, ZipArchive zip, List<MediaRef> refs, List<string> warnings, CancellationToken ct)
    {
        XDocument doc;
        try
        {
            await using var s = xml.Open();
            doc = await XDocument.LoadAsync(s, LoadOptions.None, ct);
        }
        catch (System.Xml.XmlException) { warnings.Add("content.xml не прочитався"); return null; }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "package") { warnings.Add("Це не пакет SIGame"); return null; }
        var pack = new SvoyaPack
        {
            Title = Attr(root, "name") ?? "Пакет із SIGame",
            Description = Kids(root, "info").SelectMany(i => Kids(i, "comments")).Select(c => c.Value.Trim()).FirstOrDefault(v => v.Length > 0),
        };
        foreach (var round in Kids(root, "rounds").SelectMany(r => Kids(r, "round")))
        {
            var r = new SvoyaRound
            {
                Name = Attr(round, "name") ?? $"Раунд {pack.Rounds.Count + 1}",
                Type = Attr(round, "type") == "final" ? SvoyaRound.Final : SvoyaRound.Normal,
            };
            foreach (var theme in Kids(round, "themes").SelectMany(t => Kids(t, "theme")))
            {
                var t = new SvoyaTheme { Name = Attr(theme, "name") ?? "" };
                foreach (var question in Kids(theme, "questions").SelectMany(q => Kids(q, "question")))
                {
                    var q = new SvoyaQuestion { Price = Int(Attr(question, "price")) ?? 0 };
                    if (Kids(question, "params").Any()) ReadV5(question, q, zip, refs, warnings, t.Name);
                    else ReadV4(question, q, zip, refs, warnings, t.Name);
                    var right = Kids(question, "right").SelectMany(a => Kids(a, "answer")).Select(a => a.Value.Trim()).Where(a => a.Length > 0).ToList();
                    q.Answer = right.FirstOrDefault() ?? "";
                    q.Accept = [.. right.Skip(1)];
                    if (r.IsFinal) { q.Price = 0; q.Type = SvoyaQuestion.Normal; }
                    t.Questions.Add(q);
                }
                r.Themes.Add(t);
            }
            pack.Rounds.Add(r);
        }
        return pack;
    }

    static void ReadV4(XElement question, SvoyaQuestion q, ZipArchive zip, List<MediaRef> refs, List<string> warnings, string theme)
    {
        if (Kids(question, "type").FirstOrDefault() is { } type)
        {
            var name = Attr(type, "name");
            var ps = Kids(type, "param").ToDictionary(p => Attr(p, "name") ?? "", p => p.Value.Trim());
            q.Type = name switch { "cat" or "bagcat" => SvoyaQuestion.Cat, "auction" => SvoyaQuestion.Auction, _ => SvoyaQuestion.Normal };
            if (q.Type == SvoyaQuestion.Cat && Int(ps.GetValueOrDefault("cost")) is > 0 and var cost) q.CatPrice = cost;
            if (q.Type == SvoyaQuestion.Cat && ps.GetValueOrDefault("theme") is { Length: > 0 } catTheme) q.Comment = $"Тема кота: {catTheme}";
        }
        var text = new List<string>();
        var afterMarker = false;
        foreach (var atom in Kids(question, "scenario").SelectMany(s => Kids(s, "atom")))
        {
            var kind = Attr(atom, "type") ?? "text";
            var value = atom.Value.Trim();
            switch (kind)
            {
                case "marker": afterMarker = true; break;
                case "text": if (value.Length > 0 && !afterMarker) text.Add(value); break;
                case "say": if (value.Length > 0) q.Comment = Join(q.Comment, value); break;
                case "image": case "voice": case "audio": case "video":
                    AddMedia(q, afterMarker, value.TrimStart('@'), kind, zip, refs, warnings, theme);
                    break;
            }
        }
        q.Text = string.Join(" ", text);
    }

    static void ReadV5(XElement question, SvoyaQuestion q, ZipArchive zip, List<MediaRef> refs, List<string> warnings, string theme)
    {
        var type = Attr(question, "type") ?? "simple";
        q.Type = type switch
        {
            "secret" or "secretPublicPrice" or "secretNoQuestion" => SvoyaQuestion.Cat,
            "stake" => SvoyaQuestion.Auction,
            _ => SvoyaQuestion.Normal,
        };
        var ps = Kids(question, "params").SelectMany(p => Kids(p, "param")).ToList();
        foreach (var p in ps)
        {
            var name = Attr(p, "name");
            if (name is "question" or "answer")
            {
                var text = new List<string>();
                foreach (var item in Kids(p, "item"))
                {
                    var kind = Attr(item, "type") ?? "text";
                    var value = item.Value.Trim();
                    if (kind == "text") { if (value.Length > 0 && name == "question") text.Add(value); }
                    else AddMedia(q, name == "answer", value, kind, zip, refs, warnings, theme);
                }
                if (name == "question") q.Text = string.Join(" ", text);
            }
            else if (name == "price" && q.Type == SvoyaQuestion.Cat && Kids(p, "numberSet").FirstOrDefault() is { } set)
            {
                var lo = Int(Attr(set, "minimum"));
                var hi = Int(Attr(set, "maximum"));
                if (lo is > 0 && lo == hi) q.CatPrice = lo;
            }
            else if (name == "theme" && q.Type == SvoyaQuestion.Cat && p.Value.Trim() is { Length: > 0 } catTheme)
                q.Comment = $"Тема кота: {catTheme}";
        }
    }

    /// <summary>Медіа-атом: перший до маркера — у запитання, після маркера — у відповідь, решта — попередження.</summary>
    static void AddMedia(SvoyaQuestion q, bool answer, string name, string kind, ZipArchive zip, List<MediaRef> refs, List<string> warnings, string theme)
    {
        if (refs.Any(r => r.Question == q && r.Answer == answer))
        {
            warnings.Add($"«{theme}», {q.Price}: друге медіа в одному запитанні пропущено («{name}»)");
            return;
        }
        var folder = kind == "image" ? "Images" : kind == "video" ? "Video" : "Audio";
        var entry = Entry(zip, folder + "/" + name) ?? Entry(zip, folder + "/" + Uri.EscapeDataString(name));
        if (entry is null) { warnings.Add($"«{theme}», {q.Price}: медіа «{name}» нема в архіві"); return; }
        refs.Add(new MediaRef(q, answer, entry.FullName, kind));
    }

    /// <summary>
    /// Підрізати те, що не влазить у наші межі (SvoyaPack.Check), щоб пакет узагалі зберігся, — і сказати, що саме
    /// підрізали. Решту (порожні відповіді, дубль цін) покаже звичайна перевірка як зауваження чернетки.
    /// </summary>
    static void Fit(SvoyaPack p, List<string> warnings)
    {
        p.Normalize();
        string Cut(string s, int max, string what)
        {
            if (s.Length <= max) return s;
            warnings.Add($"{what} обрізано до {max} знаків");
            return s[..max].TrimEnd();
        }
        p.Title = Cut(p.Title.Length == 0 ? "Пакет із SIGame" : p.Title, SvoyaPack.NameMax, "Назву пакета");
        if (p.Description is { } d) p.Description = Cut(d, SvoyaPack.DescriptionMax, "Опис");
        if (p.Rounds.Count > SvoyaPack.MaxRounds) { warnings.Add($"Раундів {p.Rounds.Count} — лишено перші {SvoyaPack.MaxRounds}"); p.Rounds = p.Rounds.Take(SvoyaPack.MaxRounds).ToList(); }
        // фінал у нас лише один і лише останнім
        var finals = p.Rounds.Where(r => r.IsFinal).ToList();
        if (finals.Count > 0)
        {
            foreach (var extra in finals.Take(finals.Count - 1)) { extra.Type = SvoyaRound.Normal; warnings.Add($"«{extra.Name}»: зайвий фінал став звичайним раундом"); }
            var last = finals[^1];
            p.Rounds.Remove(last);
            p.Rounds.Add(last);
        }
        foreach (var r in p.Rounds)
        {
            r.Name = Cut(r.Name, SvoyaPack.NameMax, $"Назву раунду «{r.Name[..Math.Min(20, r.Name.Length)]}…»");
            if (r.Themes.Count > SvoyaPack.MaxThemes) { warnings.Add($"«{r.Name}»: тем {r.Themes.Count} — лишено {SvoyaPack.MaxThemes}"); r.Themes = r.Themes.Take(SvoyaPack.MaxThemes).ToList(); }
            foreach (var t in r.Themes)
            {
                t.Name = Cut(t.Name, SvoyaPack.NameMax, "Назву теми");
                var max = r.IsFinal ? 1 : SvoyaPack.MaxQuestions;
                if (t.Questions.Count > max) { warnings.Add($"«{t.Name}»: запитань {t.Questions.Count} — лишено {max}"); t.Questions = t.Questions.Take(max).ToList(); }
                foreach (var q in t.Questions)
                {
                    q.Text = Cut(q.Text, SvoyaPack.TextMax, $"«{t.Name}», {q.Price}: текст");
                    q.Answer = Cut(q.Answer, SvoyaPack.AnswerMax, $"«{t.Name}», {q.Price}: відповідь");
                    q.Accept = [.. q.Accept.Where(a => a.Length <= SvoyaPack.AnswerMax).Take(SvoyaPack.MaxAccept)];
                    if (q.Comment is { } c) q.Comment = Cut(c, SvoyaPack.CommentMax, $"«{t.Name}», {q.Price}: коментар");
                    q.Price = Math.Clamp(q.Price, 0, SvoyaPack.PriceMax);
                    if (q.CatPrice is { } cp) q.CatPrice = Math.Clamp(cp, 1, SvoyaPack.PriceMax);
                }
            }
        }
    }

    // =========================================================================================
    // Експорт
    // =========================================================================================

    /// <summary>Наш zip: <c>pack.json</c> і <c>media/</c>. Лише автору чи адміну — у ньому всі відповіді.</summary>
    public async Task<(byte[]? Zip, string? Error, string Name)> ExportAsync(string id, SvoyaUser u, CancellationToken ct)
    {
        var got = packs.Get(id, u);
        if (!got.Ok) return (null, got.Message, "");
        var pack = ((SvoyaFull)got.Data!).Pack;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("pack.json", CompressionLevel.Optimal);
            await using (var w = new StreamWriter(entry.Open())) await w.WriteAsync(pack.ToJson());
            foreach (var file in pack.MediaFiles())
            {
                if (files.Size(pack.Id, file) is null) continue;
                zip.CreateEntryFromFile(Path.Combine(files.Dir(pack.Id), file), "media/" + file, CompressionLevel.NoCompression);
            }
        }
        var safe = new string([.. pack.Title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')]).Trim('-');
        return (ms.ToArray(), null, (safe.Length == 0 ? pack.Id : safe) + ".zip");
    }

    // ---------- дрібне ----------

    static IEnumerable<XElement> Kids(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);

    static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;
}
