using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Сховище й права пакетів «Своєї гри» (specs/svoya.md §6): хто що бачить, хто що править.</summary>
public sealed class SvoyaPacksTests : IDisposable
{
    readonly TempDb _db = new();
    readonly string _media = Path.Combine(Path.GetTempPath(), "svoya-media-" + Guid.NewGuid().ToString("N"));
    readonly FakeClock _clock = new();
    readonly SvoyaOptions _opts = new() { PackMaxMb = 1, UserMaxMb = 2, MaxPacks = 3 };
    readonly SvoyaPacks _packs;
    readonly SvoyaStore _store;

    static readonly SvoyaUser Olya = new("Оля", Registered: true, Admin: false);
    static readonly SvoyaUser Petro = new("Петро", Registered: true, Admin: false);
    static readonly SvoyaUser Guest = new("гість Вася", Registered: false, Admin: false);
    static readonly SvoyaUser Admin = new("Влад", Registered: true, Admin: true);

    public SvoyaPacksTests()
    {
        var mini = SvoyaPackTests.Mini();
        SvoyaBuiltin.Stamp(mini, "mini");
        _store = new SvoyaStore(_db.Db);
        _packs = new SvoyaPacks(_store, new SvoyaBuiltin([mini]), new SvoyaFiles(_media), _clock, new FixedOptions<SvoyaOptions>(_opts));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_media, true); } catch (IOException) { }
    }

    static JsonElement J(object? o) => JsonDocument.Parse(JsonSerializer.Serialize(o, SvoyaPack.Json)).RootElement;

    SvoyaFull Create(SvoyaUser u, SvoyaPack? body = null)
    {
        var r = _packs.Create(u, body);
        Assert.True(r.Ok, r.Message + " " + string.Join("; ", r.Errors ?? []));
        return (SvoyaFull)r.Data!;
    }

    /// <summary>Готовий до гри пакет ніка, одразу публічний або ні.</summary>
    string Ready(SvoyaUser u, bool pub, string title = "Мій")
    {
        var id = Create(u).Pack.Id;
        var p = SvoyaPackTests.Mini();
        p.Title = title;
        p.Public = pub;
        Assert.True(_packs.Save(id, u, p).Ok);
        return id;
    }

    static string[] Ids(JsonElement list, string group) => [.. list.GetProperty(group).EnumerateArray().Select(x => x.GetProperty("id").GetString()!)];

    // ---------- створення ----------

    [Fact]
    public void Guest_cannot_create_packs()
    {
        var r = _packs.Create(Guest);
        Assert.False(r.Ok);
        Assert.Equal(SvoyaPacks.SignIn, r.Message);
        Assert.False(J(_packs.List(Guest)).GetProperty("canCreate").GetBoolean());
    }

    [Fact]
    public void Guests_can_create_when_the_owner_allows()
    {
        _opts.GuestsCreate = true;
        Assert.True(_packs.Create(Guest).Ok);
    }

    [Fact]
    public void New_pack_is_a_private_draft_from_the_classic_template()
    {
        var full = Create(Olya);
        Assert.StartsWith("p_", full.Pack.Id);
        Assert.Equal(10, full.Pack.Id.Length);
        Assert.Equal("Оля", full.Pack.Author);
        Assert.Equal("оля", full.Pack.AuthorKey);
        Assert.False(full.Ready);
        Assert.NotEmpty(full.Problems);
        Assert.Equal(80, full.Pack.QuestionCount);
        Assert.Equal(_clock.UtcNow, full.Pack.CreatedAt);

        var list = J(_packs.List(Olya));
        Assert.Equal([full.Pack.Id], Ids(list, "mine"));
        Assert.Empty(Ids(list, "public"));
        Assert.Empty(Ids(J(_packs.List(Petro)), "public"));
    }

    [Fact]
    public void Body_cannot_forge_author_or_source()
    {
        var p = SvoyaPackTests.Mini();
        p.Author = "Петро";
        p.AuthorKey = "петро";
        p.Source = SvoyaPack.Builtin;
        p.Id = "b_mini";
        var full = Create(Olya, p);
        Assert.Equal("оля", full.Pack.AuthorKey);
        Assert.Equal(SvoyaPack.User, full.Pack.Source);
        Assert.NotEqual("b_mini", full.Pack.Id);
        Assert.True(full.Ready);
    }

    [Fact]
    public void Pack_count_is_limited_but_not_for_admin()
    {
        for (var i = 0; i < 3; i++) Create(Olya);
        var r = _packs.Create(Olya);
        Assert.False(r.Ok);
        Assert.Contains("3 пакетів", r.Message);
        for (var i = 0; i < 4; i++) Create(Admin);
    }

    [Fact]
    public void Broken_pack_is_not_created()
    {
        var p = SvoyaPackTests.Mini();
        p.Title = new string('я', 100);
        var r = _packs.Create(Olya, p);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors!, e => e.Contains("довша за 60"));
    }

    // ---------- збереження ----------

    [Fact]
    public void Owner_saves_draft_and_then_ready_pack()
    {
        var id = Create(Olya).Pack.Id;
        _clock.Advance(60);
        var draft = SvoyaPack.Classic("Чернетка");
        var r = _packs.Save(id, Olya, draft);
        Assert.True(r.Ok);
        Assert.Equal("Збережено як чернетку", r.Message);
        Assert.False(J(r.Data).GetProperty("ready").GetBoolean());

        r = _packs.Save(id, Olya, SvoyaPackTests.Mini());
        Assert.Equal("Збережено", r.Message);
        var row = _store.Get(id)!;
        Assert.True(row.Ready);
        Assert.Equal("Міні", row.Title);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
        Assert.Equal(new FakeClock().UtcNow, row.CreatedAt);
        Assert.Equal("оля", row.Pack().AuthorKey);
    }

    [Fact]
    public void Stranger_cannot_read_save_or_delete_but_admin_can()
    {
        var id = Ready(Olya, pub: true);
        Assert.Equal(SvoyaPacks.NotYours, _packs.Get(id, Petro).Message);
        Assert.Equal(SvoyaPacks.NotYours, _packs.Save(id, Petro, SvoyaPackTests.Mini()).Message);
        Assert.Equal(SvoyaPacks.NotYours, _packs.Delete(id, Petro).Message);
        Assert.Equal(SvoyaPacks.NotYours, _packs.Get(id, Guest).Message);

        Assert.True(_packs.Get(id, Admin).Ok);
        var p = SvoyaPackTests.Mini();
        p.Title = "Правка адміна";
        Assert.True(_packs.Save(id, Admin, p).Ok);
        Assert.Equal("Оля", _store.Get(id)!.OwnerNick);        // власник той самий
        Assert.True(_packs.Delete(id, Admin).Ok);
        Assert.Null(_store.Get(id));
    }

    [Fact]
    public void Guest_with_the_same_name_is_not_the_owner()
    {
        var id = Ready(Olya, pub: false);
        var impostor = new SvoyaUser("гість Оля", Registered: false, Admin: false);
        Assert.False(_packs.Get(id, impostor).Ok);
        Assert.False(_packs.Save(id, impostor, SvoyaPackTests.Mini()).Ok);
    }

    [Fact]
    public void Nick_case_does_not_matter_for_ownership()
    {
        var id = Ready(Olya, pub: false);
        Assert.True(_packs.Get(id, new SvoyaUser("ОЛЯ", true, false)).Ok);
    }

    [Fact]
    public void Hard_errors_block_saving()
    {
        var id = Create(Olya).Pack.Id;
        var p = SvoyaPackTests.Mini();
        p.Rounds[0].Themes[0].Questions[0].Text = new string('т', 700);
        var r = _packs.Save(id, Olya, p);
        Assert.False(r.Ok);
        Assert.Single(r.Errors!);
        Assert.Equal("Новий пакет", _store.Get(id)!.Title);   // у базі лишилось старе
    }

    [Fact]
    public void Missing_pack_says_so()
    {
        Assert.Equal(SvoyaPacks.NoPack, _packs.Get("p_nothing0", Olya).Message);
        Assert.Equal(SvoyaPacks.NoPack, _packs.Save("p_nothing0", Olya, SvoyaPackTests.Mini()).Message);
        Assert.Equal(SvoyaPacks.NoPack, _packs.Delete("p_nothing0", Olya).Message);
    }

    // ---------- список ----------

    [Fact]
    public void List_has_three_groups_and_no_spoilers()
    {
        var mine = Ready(Olya, pub: true, "Олин");
        var petros = Ready(Petro, pub: true, "Петрів");
        var draft = Create(Petro).Pack.Id;
        _ = Ready(Petro, pub: false, "Приватний");

        var list = J(_packs.List(Olya));
        Assert.Equal(["b_mini"], Ids(list, "builtin"));
        Assert.Equal([petros], Ids(list, "public"));              // своє — лише в «мої», чернетка й приватне — ніде
        Assert.Equal([mine], Ids(list, "mine"));
        Assert.DoesNotContain(draft, Ids(list, "public"));

        var raw = list.GetRawText();
        Assert.DoesNotContain("Котляревський", raw);
        Assert.DoesNotContain("Енеїд", raw);
        Assert.DoesNotContain("\"answer\"", raw);
        Assert.DoesNotContain("\"text\"", raw);

        var row = list.GetProperty("builtin")[0];
        Assert.Equal("Міні", row.GetProperty("title").GetString());
        Assert.Equal("Глечики", row.GetProperty("author").GetString());
        Assert.Equal(6, row.GetProperty("questions").GetInt32());
        Assert.False(row.GetProperty("canEdit").GetBoolean());
        var round = row.GetProperty("rounds")[0];
        Assert.Equal(["Література", "Географія"], round.GetProperty("themes").EnumerateArray().Select(t => t.GetString()));
        Assert.True(row.GetProperty("rounds")[1].GetProperty("final").GetBoolean());
        Assert.True(list.GetProperty("mine")[0].GetProperty("canEdit").GetBoolean());
        Assert.False(list.GetProperty("public")[0].GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public void Plays_are_counted_for_builtin_and_user_packs()
    {
        var id = Ready(Olya, pub: true);
        _packs.NotePlayed("b_mini");
        _packs.NotePlayed("b_mini");
        _packs.NotePlayed(id);
        var list = J(_packs.List(Petro));
        Assert.Equal(2, list.GetProperty("builtin")[0].GetProperty("plays").GetInt32());
        Assert.Equal(1, list.GetProperty("public")[0].GetProperty("plays").GetInt32());
    }

    [Fact]
    public void Admin_hides_a_public_pack()
    {
        var id = Ready(Olya, pub: true);
        Assert.False(_packs.Hide(id, Petro, true).Ok);
        Assert.True(_packs.Hide(id, Admin, true).Ok);
        Assert.Empty(Ids(J(_packs.List(Petro)), "public"));
        var forAdmin = J(_packs.List(Admin));
        Assert.Equal([id], Ids(forAdmin, "public"));
        Assert.True(forAdmin.GetProperty("public")[0].GetProperty("hidden").GetBoolean());
        Assert.Null(_packs.Playable(id, "Петро"));

        var again = SvoyaPackTests.Mini();
        again.Public = true;
        Assert.True(_packs.Save(id, Olya, again).Ok);                 // збереження не знімає «сховано»
        Assert.True(_store.Get(id)!.Hidden);
        Assert.True(_packs.Hide(id, Admin, false).Ok);
        Assert.Equal([id], Ids(J(_packs.List(Petro)), "public"));
    }

    // ---------- вбудовані й копії ----------

    [Fact]
    public void Builtin_pack_is_read_only_and_copies_to_me()
    {
        Assert.False(_packs.Save("b_mini", Olya, SvoyaPackTests.Mini()).Ok);
        Assert.False(_packs.Delete("b_mini", Admin).Ok);
        Assert.False(_packs.Get("b_mini", Olya).Ok);                // відповіді — лише адміну
        Assert.True(_packs.Get("b_mini", Admin).Ok);

        var r = _packs.Copy("b_mini", Olya);
        Assert.True(r.Ok, r.Message);
        var id = J(r.Data).GetProperty("id").GetString()!;
        var row = _store.Get(id)!;
        Assert.Equal("Міні (копія)", row.Title);
        Assert.Equal("оля", row.OwnerKey);
        Assert.False(row.Public);
        Assert.True(row.Ready);
        Assert.Equal(SvoyaPack.User, row.Source);
        Assert.Equal("Котляревський", row.Pack().Rounds[0].Themes[0].Questions[0].Accept[0]);
    }

    [Fact]
    public void Copy_needs_read_rights()
    {
        var priv = Ready(Olya, pub: false);
        Assert.Equal(SvoyaPacks.NoPack, _packs.Copy(priv, Petro).Message);
        Assert.True(_packs.Copy(priv, Olya).Ok);
        var pub = Ready(Olya, pub: true);
        Assert.True(_packs.Copy(pub, Petro).Ok);
        Assert.False(_packs.Copy(pub, Guest).Ok);                    // гість не створює навіть копією
    }

    [Fact]
    public void Long_title_copy_stays_within_the_limit()
    {
        var id = Ready(Olya, pub: true, new string('д', 60));
        var r = _packs.Copy(id, Petro);
        Assert.True(r.Ok);
        var title = _store.Get(J(r.Data).GetProperty("id").GetString()!)!.Title;
        Assert.True(title.Length <= SvoyaPack.NameMax);
        Assert.EndsWith("(копія)", title);
    }

    // ---------- для гри ----------

    [Fact]
    public void Playable_packs()
    {
        Assert.NotNull(_packs.Playable("b_mini", "хто завгодно"));
        var draft = Create(Olya).Pack.Id;
        Assert.Null(_packs.Playable(draft, "Оля"));                 // чернетка — ні навіть авторці
        var priv = Ready(Olya, pub: false);
        Assert.NotNull(_packs.Playable(priv, "оля"));
        Assert.Null(_packs.Playable(priv, "Петро"));
        var pub = Ready(Olya, pub: true);
        Assert.NotNull(_packs.Playable(pub, "Петро"));
        Assert.Null(_packs.Playable("p_nothing0", "Петро"));

        var copy = _packs.Playable("b_mini", "x")!;
        copy.Title = "зіпсував";
        Assert.Equal("Міні", _packs.Playable("b_mini", "x")!.Title);  // гра отримує копію
    }

    // ---------- медіа ----------

    void PutMedia(string packId, string file, int bytes)
    {
        var dir = Path.Combine(_media, packId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), new byte[bytes]);
    }

    static SvoyaPack WithImage(string file)
    {
        var p = SvoyaPackTests.Mini();
        p.Rounds[0].Themes[0].Questions[0].Media = new SvoyaMedia { Kind = "image", File = file };
        return p;
    }

    [Fact]
    public void Missing_media_keeps_pack_a_draft()
    {
        var id = Create(Olya).Pack.Id;
        var r = _packs.Save(id, Olya, WithImage("aaaaaaaaaaaa.jpg"));
        Assert.False(J(r.Data).GetProperty("ready").GetBoolean());
        PutMedia(id, "aaaaaaaaaaaa.jpg", 1000);
        r = _packs.Save(id, Olya, WithImage("aaaaaaaaaaaa.jpg"));
        Assert.True(J(r.Data).GetProperty("ready").GetBoolean());
        Assert.Equal(1000, _store.Get(id)!.MediaBytes);
    }

    [Fact]
    public void Pack_and_user_media_quotas()
    {
        var a = Create(Olya).Pack.Id;
        PutMedia(a, "aaaaaaaaaaaa.jpg", 1_500_000);                  // більше за PackMaxMb = 1
        var r = _packs.Save(a, Olya, WithImage("aaaaaaaaaaaa.jpg"));
        Assert.True(r.Ok);
        Assert.Contains(J(r.Data).GetProperty("problems").EnumerateArray(), e => e.GetString()!.Contains("більше за 1 МБ"));

        var b = Create(Olya).Pack.Id;
        PutMedia(b, "bbbbbbbbbbbb.jpg", 900_000);
        r = _packs.Save(b, Olya, WithImage("bbbbbbbbbbbb.jpg"));    // 1.5 + 0.9 > UserMaxMb = 2
        Assert.False(r.Ok);
        Assert.Contains("2 МБ", r.Message);
    }

    [Fact]
    public void Delete_removes_media_folder_and_copy_takes_media_along()
    {
        var id = Create(Olya).Pack.Id;
        PutMedia(id, "aaaaaaaaaaaa.jpg", 10);
        var p = WithImage("aaaaaaaaaaaa.jpg");
        p.Public = true;
        _packs.Save(id, Olya, p);

        var copy = J(_packs.Copy(id, Petro).Data).GetProperty("id").GetString()!;
        Assert.True(File.Exists(Path.Combine(_media, copy, "aaaaaaaaaaaa.jpg")));
        Assert.True(_store.Get(copy)!.Ready);
        Assert.Equal(10, _store.Get(copy)!.MediaBytes);

        Assert.True(_packs.Delete(id, Olya).Ok);
        Assert.False(Directory.Exists(Path.Combine(_media, id)));
        Assert.True(Directory.Exists(Path.Combine(_media, copy)));
    }

    // ---------- вбудовані з теки ----------

    [Fact]
    public void Builtin_loader_skips_broken_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "svoya-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(SvoyaPackTests.FixturePath, Path.Combine(dir, "a-mini.json"));
            File.WriteAllText(Path.Combine(dir, "b-broken.json"), "{ nope");
            var draft = SvoyaPack.Classic("Недороблений");
            File.WriteAllText(Path.Combine(dir, "c-draft.json"), draft.ToJson());
            var noId = SvoyaPackTests.Mini();
            noId.Id = "";
            noId.Author = "Хтось";
            File.WriteAllText(Path.Combine(dir, "d-other.json"), noId.ToJson());

            var b = new SvoyaBuiltin(dir);
            Assert.Equal(["b_mini", "b_d-other"], b.Packs.Select(p => p.Id));
            Assert.Equal(["b-broken", "c-draft"], b.Problems.Keys.Order());
            Assert.All(b.Packs, p => Assert.Equal(SvoyaBuiltin.Author, p.Author));
            Assert.Empty(new SvoyaBuiltin(Path.Combine(dir, "nope")).Packs);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- справжні вбудовані пакети з репозиторію ----------

    [Fact]
    public void Repo_builtin_packs_are_all_playable_classic_packs()
    {
        var b = new SvoyaBuiltin(Paths.Resolve("data/svoya/builtin"));
        Assert.Empty(b.Problems);
        Assert.True(b.Packs.Count >= 4, $"вбудованих пакетів лише {b.Packs.Count}");
        Assert.Equal(b.Packs.Count, b.Packs.Select(p => p.Id).Distinct().Count());
        foreach (var p in b.Packs)
        {
            Assert.Equal(80, p.QuestionCount);
            Assert.True(p.Rounds[^1].IsFinal, p.Id);
            foreach (var r in p.Rounds.Where(r => !r.IsFinal))
            {
                var qs = r.Themes.SelectMany(t => t.Questions).ToList();
                Assert.Equal(1, qs.Count(q => q.Type == SvoyaQuestion.Cat));
                Assert.Equal(1, qs.Count(q => q.Type == SvoyaQuestion.Auction));
            }
            // кожна відповідь зараховується сама на себе — інакше автомат не прийняв би навіть ідеальну відповідь
            foreach (var q in p.Rounds.SelectMany(r => r.Themes).SelectMany(t => t.Questions))
            {
                Assert.True(SvoyaAnswer.Hits(q.Answer, q.Answers), $"{p.Id}: «{q.Answer}» не зараховується");
                Assert.DoesNotContain(q.Answer, q.Text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
