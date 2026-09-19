using System.Text;
using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>ffmpeg без ffmpeg: копіює вхід у вихід і каже, скільки звучить.</summary>
sealed class FakeTranscoder(double seconds = 12, bool trimmed = false, bool ok = true) : ISvoyaTranscoder
{
    public int Calls;

    public Task<SvoyaTranscoded> AudioAsync(string input, string output, int maxSeconds, CancellationToken ct) => Run(input, output);
    public Task<SvoyaTranscoded> VideoAsync(string input, string output, int maxSeconds, CancellationToken ct) => Run(input, output);

    Task<SvoyaTranscoded> Run(string input, string output)
    {
        Calls++;
        if (!ok) return Task.FromResult(new SvoyaTranscoded(false, 0, false, "не звук"));
        File.Copy(input, output, true);
        return Task.FromResult(new SvoyaTranscoded(true, seconds, trimmed));
    }
}

/// <summary>Медіа пакетів (specs/svoya.md §5): що приймаємо, як називаємо, квоти, прибирання, роздача.</summary>
public sealed class SvoyaMediaTests : IDisposable
{
    readonly TempDb _db = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "svoya-up-" + Guid.NewGuid().ToString("N"));
    readonly FakeClock _clock = new();
    readonly SvoyaOptions _opts = new() { PackMaxMb = 1, UserMaxMb = 2 };
    readonly SvoyaStore _store;
    readonly SvoyaFiles _files;
    readonly FakeTranscoder _ff = new();
    readonly SvoyaUploads _up;
    readonly SvoyaPacks _packs;

    static readonly SvoyaUser Olya = new("Оля", true, false);
    static readonly SvoyaUser Petro = new("Петро", true, false);
    static readonly SvoyaUser Admin = new("Влад", true, true);

    public SvoyaMediaTests()
    {
        _store = new SvoyaStore(_db.Db);
        _files = new SvoyaFiles(_dir);
        var o = new FixedOptions<SvoyaOptions>(_opts);
        _up = new SvoyaUploads(_store, _files, _ff, _clock, o);
        _packs = new SvoyaPacks(_store, new SvoyaBuiltin([]), _files, _clock, o, uploads: _up);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string NewPack(SvoyaUser u) => ((SvoyaFull)_packs.Create(u).Data!).Pack.Id;

    static byte[] Png(int size = 100)
    {
        var b = new byte[size];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        b[size - 1] = (byte)size;
        return b;
    }

    Task<SvoyaReply> Up(string pack, SvoyaUser? u, string name, byte[] data) => _up.UploadAsync(pack, u, name, new MemoryStream(data), CancellationToken.None);

    static JsonElement J(SvoyaReply r) => JsonSerializer.SerializeToElement(r.Data);

    [Fact]
    public async Task Image_is_named_by_content_and_typed_by_signature()
    {
        var id = NewPack(Olya);
        var r = await Up(id, Olya, "фото.JPEG", Png());         // розширення бреше — вірить вмісту
        Assert.True(r.Ok, r.Message);
        var d = J(r);
        Assert.Equal("image", d.GetProperty("kind").GetString());
        Assert.Matches("^[0-9a-f]{24}\\.png$", d.GetProperty("file").GetString());
        Assert.Equal(0, d.GetProperty("seconds").GetInt32());
        Assert.Equal(0, _ff.Calls);
        var again = J(await Up(id, Olya, "копія.png", Png()));
        Assert.Equal(d.GetProperty("file").GetString(), again.GetProperty("file").GetString());   // той самий файл — одне ім'я
        Assert.Single(Directory.GetFiles(_files.Dir(id)));
    }

    [Fact]
    public async Task Not_an_image_or_unknown_type_is_refused()
    {
        var id = NewPack(Olya);
        Assert.Equal("Це не схоже на картинку", (await Up(id, Olya, "x.jpg", Encoding.UTF8.GetBytes("<html>привіт</html>"))).Message);
        Assert.StartsWith("Такий файл не візьму", (await Up(id, Olya, "virus.exe", [1, 2, 3])).Message);
        Assert.Equal("Порожній файл", (await Up(id, Olya, "x.png", [])).Message);
        Assert.Empty(Directory.GetFiles(_files.Dir(id)));        // жодного .part не лишилось
    }

    [Fact]
    public async Task Audio_and_video_go_through_ffmpeg()
    {
        var id = NewPack(Olya);
        var r = J(await Up(id, Olya, "пісня.ogg", [1, 2, 3, 4]));
        Assert.Equal("audio", r.GetProperty("kind").GetString());
        Assert.EndsWith(".mp3", r.GetProperty("file").GetString());
        Assert.Equal(12, r.GetProperty("seconds").GetInt32());
        var v = J(await Up(id, Olya, "кліп.webm", [5, 6, 7]));
        Assert.EndsWith(".mp4", v.GetProperty("file").GetString());
        Assert.Equal(2, _ff.Calls);
    }

    [Fact]
    public async Task Long_audio_is_trimmed_with_a_warning_and_broken_is_refused()
    {
        var id = NewPack(Olya);
        var trim = new SvoyaUploads(_store, _files, new FakeTranscoder(90, trimmed: true), _clock, new FixedOptions<SvoyaOptions>(_opts));
        var r = await trim.UploadAsync(id, Olya, "довга.mp3", new MemoryStream([1, 2]), CancellationToken.None);
        Assert.Equal("Обрізано до 90 с", r.Message);
        var bad = new SvoyaUploads(_store, _files, new FakeTranscoder(ok: false), _clock, new FixedOptions<SvoyaOptions>(_opts));
        Assert.False((await bad.UploadAsync(id, Olya, "x.mp3", new MemoryStream([1]), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Size_limits_per_kind()
    {
        var id = NewPack(Admin);
        var big = new byte[SvoyaUploads.ImageMax + 1];
        Png().CopyTo(big, 0);
        Assert.StartsWith("Завеликий файл: для картинки — до 5 МБ", (await Up(id, Admin, "x.png", big)).Message);
    }

    [Fact]
    public async Task Only_the_owner_or_admin_uploads()
    {
        var id = NewPack(Olya);
        Assert.Equal(SvoyaPacks.NotYours, (await Up(id, Petro, "x.png", Png())).Message);
        Assert.True((await Up(id, Admin, "x.png", Png())).Ok);
        Assert.Equal(SvoyaPacks.NoPack, (await Up("p_nothing0", Olya, "x.png", Png())).Message);
    }

    [Fact]
    public async Task Pack_and_user_quotas()
    {
        var a = NewPack(Olya);
        Assert.True((await Up(a, Olya, "a.png", Png(700_000))).Ok);
        Assert.StartsWith("Медіа пакета більше за 1 МБ", (await Up(a, Olya, "b.png", Png(500_000))).Message);

        // перший пакет зберігаємо з медіа — тепер він рахується в квоту ніка
        var file = Directory.GetFiles(_files.Dir(a)).Select(Path.GetFileName).Single()!;
        var p = SvoyaPackTests.Mini();
        p.Rounds[0].Themes[0].Questions[0].Media = new SvoyaMedia { Kind = "image", File = file };
        Assert.True(_packs.Save(a, Olya, p).Ok);
        var b = NewPack(Olya);
        Assert.True((await Up(b, Olya, "c.png", Png(900_000))).Ok);
        var c = NewPack(Olya);
        Assert.StartsWith("Усі твої пакети разом більші за 2 МБ", (await Up(c, Olya, "d.png", Png(900_001))).Message);
        Assert.True((await Up(NewPack(Admin), Admin, "e.png", Png(1_000_000))).Ok);
    }

    [Fact]
    public async Task Saving_sweeps_files_the_pack_forgot_but_not_fresh_ones()
    {
        var id = NewPack(Olya);
        var keep = J(await Up(id, Olya, "a.png", Png(100))).GetProperty("file").GetString()!;
        var gone = J(await Up(id, Olya, "b.png", Png(200))).GetProperty("file").GetString()!;
        var p = SvoyaPackTests.Mini();
        p.Rounds[0].Themes[0].Questions[0].Media = new SvoyaMedia { Kind = "image", File = keep };

        _packs.Save(id, Olya, p);
        Assert.True(File.Exists(Path.Combine(_files.Dir(id), gone)));       // щойно завантажений — ще живе

        foreach (var f in Directory.GetFiles(_files.Dir(id))) File.SetLastWriteTimeUtc(f, _clock.UtcNow.UtcDateTime);
        _clock.Advance(SvoyaUploads.OrphanGrace + TimeSpan.FromMinutes(1));
        _packs.Save(id, Olya, p);
        Assert.False(File.Exists(Path.Combine(_files.Dir(id), gone)));
        Assert.True(File.Exists(Path.Combine(_files.Dir(id), keep)));
        Assert.True(_store.Get(id)!.Ready);
        Assert.Equal(100, _store.Get(id)!.MediaBytes);
    }

    [Fact]
    public async Task Files_are_served_only_by_exact_name()
    {
        var id = NewPack(Olya);
        var name = J(await Up(id, Olya, "a.png", Png())).GetProperty("file").GetString()!;
        Assert.NotNull(_up.FileOf(id, name));
        Assert.Null(_up.FileOf(id, "../" + name));
        Assert.Null(_up.FileOf(id, name.ToUpperInvariant()));
        Assert.Null(_up.FileOf("..", name));
        Assert.Null(_up.FileOf(id, "nothing12345.png"));
        Assert.Equal("image/png", SvoyaUploads.ContentType(name));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, null)]
    public void Image_signatures(byte[] head, string? type) => Assert.Equal(type, SvoyaUploads.ImageType(head));
}
