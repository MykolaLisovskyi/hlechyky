using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Імпорт .siq (SIGame v4/v5) і наш zip туди-назад (specs/svoya.md §7).</summary>
public sealed class SvoyaImportTests : IDisposable
{
    readonly TempDb _db = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "svoya-imp-" + Guid.NewGuid().ToString("N"));
    readonly SvoyaFiles _files;
    readonly SvoyaPacks _packs;
    readonly SvoyaImport _import;
    readonly SvoyaStore _store;

    static readonly SvoyaUser Olya = new("Оля", true, false);
    static readonly SvoyaUser Guest = new("гість Вася", false, false);

    public SvoyaImportTests()
    {
        _store = new SvoyaStore(_db.Db);
        _files = new SvoyaFiles(_dir);
        var clock = new FakeClock();
        var o = new FixedOptions<SvoyaOptions>(new SvoyaOptions());
        var up = new SvoyaUploads(_store, _files, new FakeTranscoder(7), clock, o);
        _packs = new SvoyaPacks(_store, new SvoyaBuiltin([]), _files, clock, o, uploads: up);
        _import = new SvoyaImport(_packs, up, _files);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    static MemoryStream Zip(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
        ms.Position = 0;
        return ms;
    }

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    const string V4 = """
        <?xml version="1.0" encoding="utf-8"?>
        <package name="Пакет із SIGame" version="4" xmlns="http://vladimirkhil.com/ygpackage3.0.xsd">
          <info><comments>Тестовий пакет</comments></info>
          <rounds>
            <round name="1-й раунд">
              <themes>
                <theme name="Картини">
                  <questions>
                    <question price="100">
                      <scenario><atom>Що на картинці?</atom><atom type="image">@картинка.png</atom></scenario>
                      <right><answer>Глечик</answer><answer>глек</answer></right>
                    </question>
                    <question price="200">
                      <type name="bagcat"><param name="theme">Коти</param><param name="cost">500</param></type>
                      <scenario><atom>Хто нявкає?</atom><atom type="marker" /><atom type="voice">@tone.mp3</atom></scenario>
                      <right><answer>Кіт</answer></right>
                    </question>
                    <question price="300">
                      <type name="auction" />
                      <scenario><atom>Два медіа</atom><atom type="image">@картинка.png</atom><atom type="image">@друга.png</atom></scenario>
                      <right><answer>Так</answer></right>
                    </question>
                    <question price="400">
                      <scenario><atom type="image">@нема.png</atom><atom>Файла нема</atom></scenario>
                      <right><answer>Ні</answer></right>
                    </question>
                  </questions>
                </theme>
              </themes>
            </round>
            <round name="Фінал" type="final">
              <themes>
                <theme name="Ф1"><questions><question price="0"><scenario><atom>Фінал один</atom></scenario><right><answer>1991</answer></right></question></questions></theme>
                <theme name="Ф2"><questions>
                  <question price="0"><scenario><atom>Фінал два</atom></scenario><right><answer>два</answer></right></question>
                  <question price="0"><scenario><atom>Зайве</atom></scenario><right><answer>зайве</answer></right></question>
                </questions></theme>
              </themes>
            </round>
          </rounds>
        </package>
        """;

    MemoryStream Siq4() => Zip(
        ("content.xml", U(V4)),
        ("Images/" + Uri.EscapeDataString("картинка.png"), Png),
        ("Audio/tone.mp3", [9, 9, 9]));

    async Task<(SvoyaReply R, JsonElement D, SvoyaPack P)> Import(Stream zip, SvoyaUser? u = null)
    {
        var r = await _import.ImportAsync(u ?? Olya, zip, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        var d = JsonSerializer.SerializeToElement(r.Data);
        var p = _store.Get(d.GetProperty("id").GetString()!)!.Pack();
        return (r, d, p);
    }

    static string[] Warnings(JsonElement d) => [.. d.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!)];

    [Fact]
    public async Task Siq_v4_maps_rounds_types_media_and_answers()
    {
        var (_, d, p) = await Import(Siq4());
        Assert.Equal("Пакет із SIGame", p.Title);
        Assert.Equal("Тестовий пакет", p.Description);
        Assert.Equal(SvoyaPack.Siq, p.Source);
        Assert.False(p.Public);
        Assert.Equal(2, p.Rounds.Count);
        var qs = p.Rounds[0].Themes[0].Questions;
        Assert.Equal("Що на картинці?", qs[0].Text);
        Assert.Equal("image", qs[0].Media!.Kind);
        Assert.True(File.Exists(Path.Combine(_files.Dir(p.Id), qs[0].Media!.File)));
        Assert.Equal("Глечик", qs[0].Answer);
        Assert.Equal(["глек"], qs[0].Accept);

        Assert.Equal(SvoyaQuestion.Cat, qs[1].Type);
        Assert.Equal(500, qs[1].CatPrice);
        Assert.Null(qs[1].Media);                                  // звук після маркера — до відповіді
        Assert.Equal("audio", qs[1].AnswerMedia!.Kind);
        Assert.Equal(7, qs[1].AnswerMedia!.Seconds);
        Assert.Contains("Коти", qs[1].Comment);

        Assert.Equal(SvoyaQuestion.Auction, qs[2].Type);
        Assert.Contains(Warnings(d), w => w.Contains("друге медіа"));
        Assert.Contains(Warnings(d), w => w.Contains("нема.png") && w.Contains("нема в архіві"));

        Assert.True(p.Rounds[1].IsFinal);
        Assert.Single(p.Rounds[1].Themes[1].Questions);            // зайве фінальне запитання відрізано
        Assert.Contains(Warnings(d), w => w.Contains("лишено 1"));
        Assert.Equal(0, p.Rounds[1].Themes[0].Questions[0].Price);
        Assert.True(d.GetProperty("ready").GetBoolean(), string.Join("; ", d.GetProperty("problems").EnumerateArray()));
    }

    [Fact]
    public async Task Siq_v5_reads_params_and_secret_prices()
    {
        const string v5 = """
            <package name="Пʼятий" version="5" xmlns="https://github.com/VladimirKhil/SI/blob/master/assets/siq_5.xsd">
              <rounds><round name="Раунд">
                <themes><theme name="Тема">
                  <questions>
                    <question price="100">
                      <params>
                        <param name="question" type="content"><item>Текст запитання</item><item type="image" isRef="True">pic.png</item></param>
                        <param name="answer" type="content"><item type="audio" isRef="True">a.mp3</item></param>
                      </params>
                      <right><answer>Відповідь</answer></right>
                    </question>
                    <question price="200" type="secret">
                      <params>
                        <param name="theme">Секрет</param>
                        <param name="price" type="numberSet"><numberSet minimum="300" maximum="300" step="0" /></param>
                        <param name="question" type="content"><item>Кіт v5</item></param>
                      </params>
                      <right><answer>Так</answer></right>
                    </question>
                    <question price="300" type="stake">
                      <params><param name="question" type="content"><item>Ставки</item></param></params>
                      <right><answer>Так</answer></right>
                    </question>
                    <question price="400" type="secret">
                      <params>
                        <param name="price" type="numberSet"><numberSet minimum="100" maximum="500" step="100" /></param>
                        <param name="question" type="content"><item>Кіт на вибір</item></param>
                      </params>
                      <right><answer>Так</answer></right>
                    </question>
                  </questions>
                </theme></themes>
              </round></rounds>
            </package>
            """;
        var (_, _, p) = await Import(Zip(("content.xml", U(v5)), ("Images/pic.png", Png), ("Audio/a.mp3", [1, 2])));
        var qs = p.Rounds[0].Themes[0].Questions;
        Assert.Equal("Текст запитання", qs[0].Text);
        Assert.Equal("image", qs[0].Media!.Kind);
        Assert.Equal("audio", qs[0].AnswerMedia!.Kind);
        Assert.Equal(SvoyaQuestion.Cat, qs[1].Type);
        Assert.Equal(300, qs[1].CatPrice);
        Assert.Equal(SvoyaQuestion.Auction, qs[2].Type);
        Assert.Null(qs[3].CatPrice);                               // діапазон — ціну обирає одержувач
    }

    [Fact]
    public async Task Too_long_texts_are_cut_with_a_warning()
    {
        var xml = V4.Replace("Що на картинці?", new string('я', 700));
        var (_, d, p) = await Import(Zip(("content.xml", U(xml)), ("Images/" + Uri.EscapeDataString("картинка.png"), Png), ("Audio/tone.mp3", [1])));
        Assert.Equal(600, p.Rounds[0].Themes[0].Questions[0].Text.Length);
        Assert.Contains(Warnings(d), w => w.Contains("обрізано до 600"));
    }

    [Fact]
    public async Task Own_zip_round_trips_with_media()
    {
        var (_, _, first) = await Import(Siq4());
        var (zip, error, name) = await _import.ExportAsync(first.Id, Olya, CancellationToken.None);
        Assert.Null(error);
        Assert.EndsWith(".zip", name);
        using (var z = new ZipArchive(new MemoryStream(zip!)))
        {
            Assert.NotNull(z.GetEntry("pack.json"));
            Assert.Equal(2, z.Entries.Count(e => e.FullName.StartsWith("media/")));
        }
        var (r, d, copy) = await Import(new MemoryStream(zip!));
        Assert.NotEqual(first.Id, copy.Id);
        Assert.Equal(SvoyaPack.User, copy.Source);
        Assert.Equal(first.QuestionCount, copy.QuestionCount);
        Assert.Equal(first.Rounds[0].Themes[0].Questions[0].Media!.File, copy.Rounds[0].Themes[0].Questions[0].Media!.File);
        Assert.True(File.Exists(Path.Combine(_files.Dir(copy.Id), copy.Rounds[0].Themes[0].Questions[1].AnswerMedia!.File)));
        Assert.True(d.GetProperty("ready").GetBoolean());
        Assert.Equal("Імпортовано — можна грати", r.Message);
    }

    [Fact]
    public async Task Export_is_for_the_owner_only()
    {
        var (_, _, p) = await Import(Siq4());
        var (zip, error, _) = await _import.ExportAsync(p.Id, new SvoyaUser("Петро", true, false), CancellationToken.None);
        Assert.Null(zip);
        Assert.Equal(SvoyaPacks.NotYours, error);
    }

    [Fact]
    public async Task Bad_archives_and_guests_are_refused()
    {
        Assert.Equal(SvoyaPacks.SignIn, (await _import.ImportAsync(Guest, Siq4(), CancellationToken.None)).Message);
        Assert.Equal("Це не zip і не .siq", (await _import.ImportAsync(Olya, new MemoryStream(U("привіт")), CancellationToken.None)).Message);
        Assert.StartsWith("У архіві нема", (await _import.ImportAsync(Olya, Zip(("readme.txt", U("x"))), CancellationToken.None)).Message);
        Assert.Equal("content.xml не прочитався", (await _import.ImportAsync(Olya, Zip(("content.xml", U("<package"))), CancellationToken.None)).Message);
        Assert.Empty(_store.Mine("оля"));
    }

    [Fact]
    public async Task Draft_import_says_what_is_missing()
    {
        var xml = V4.Replace("<answer>Ні</answer>", "");
        var (r, d, _) = await Import(Zip(("content.xml", U(xml)), ("Images/" + Uri.EscapeDataString("картинка.png"), Png), ("Audio/tone.mp3", [1])));
        Assert.False(d.GetProperty("ready").GetBoolean());
        Assert.Contains(d.GetProperty("problems").EnumerateArray(), x => x.GetString()!.Contains("нема відповіді"));
        Assert.Equal("Імпортовано як чернетку — подивись зауваження", r.Message);
    }
}
