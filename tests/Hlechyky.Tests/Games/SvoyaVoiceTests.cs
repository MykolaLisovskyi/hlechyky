using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Рушій озвучки без edge-tts: пише кілька байтів і каже, що звучить 1,5 с. Може «зламатись» на тексті.</summary>
sealed class FakeTtsEngine : ITtsEngine
{
    public readonly List<string> Said = [];
    public readonly HashSet<string> Broken = [];
    /// <summary>Що зробити посеред озвучки — щоб перевірити чергу, поки воркер зайнятий.</summary>
    public Action<string>? During;

    public Task<bool> SynthesizeAsync(string voice, string text, string rate, int pauseMs, string outPath, CancellationToken ct)
    {
        Said.Add(text);
        During?.Invoke(text);
        if (Broken.Contains(text)) return Task.FromResult(false);
        File.WriteAllBytes(outPath, [1, 2, 3]);
        return Task.FromResult(true);
    }

    public Task<double> DurationAsync(string path, CancellationToken ct) => Task.FromResult(1.5);
}

/// <summary>Голос ведучого (specs/svoya.md §4): черга, кеш, числа словами, репліки, тумблер у live.</summary>
public sealed class SvoyaVoiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "svoya-tts-" + Guid.NewGuid().ToString("N"));
    readonly FakeTtsEngine _engine = new();
    readonly TtsOptions _opts;
    readonly TtsService _tts;

    public SvoyaVoiceTests()
    {
        _opts = new TtsOptions { CacheDir = _dir };
        _tts = new TtsService(_engine, new FixedOptions<TtsOptions>(_opts), NullLogger<TtsService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    async Task Drain() { while (await _tts.StepAsync(CancellationToken.None)) { } }

    // ---------- черга й кеш ----------

    [Fact]
    public async Task Line_is_voiced_once_and_then_served_from_cache()
    {
        Assert.Null(_tts.TryGet("ostap", "Привіт"));
        _tts.Enqueue("ostap", ["Привіт", "Привіт", "  "]);
        Assert.Equal(1, _tts.Queued);
        await Drain();
        var clip = _tts.TryGet("ostap", "Привіт");
        Assert.NotNull(clip);
        Assert.Equal(1.5, clip!.Seconds);
        Assert.True(File.Exists(clip.FilePath));
        _tts.Enqueue("ostap", ["Привіт"]);
        Assert.Equal(0, _tts.Queued);
        Assert.Single(_engine.Said);
    }

    [Fact]
    public async Task Cache_survives_a_restart()
    {
        _tts.Enqueue("ostap", ["Раунд перший"]);
        await Drain();
        var fresh = new TtsService(new FakeTtsEngine(), new FixedOptions<TtsOptions>(_opts), NullLogger<TtsService>.Instance);
        Assert.NotNull(fresh.TryGet("ostap", "Раунд перший"));
        Assert.Null(fresh.TryGet("polina", "Раунд перший"));        // інший голос — інший файл
    }

    [Fact]
    public async Task Urgent_line_jumps_the_queue()
    {
        _tts.Enqueue("ostap", ["один", "два", "три"]);
        _tts.Enqueue("ostap", ["три"], urgent: true);
        _tts.Enqueue("ostap", ["терміново"], urgent: true);
        await Drain();
        Assert.Equal(["терміново", "три", "один", "два"], _engine.Said);
    }

    [Fact]
    public async Task Urgent_request_for_a_line_being_voiced_is_harmless()
    {
        _engine.During = t => { if (t == "зараз") _tts.Enqueue("ostap", ["зараз", "потім"], urgent: true); };
        _tts.Enqueue("ostap", ["зараз"]);
        await Drain();
        Assert.Equal(["зараз", "потім"], _engine.Said);
        Assert.NotNull(_tts.TryGet("ostap", "зараз"));
    }

    [Fact]
    public async Task Failed_line_is_not_retried_and_leaves_no_file()
    {
        _engine.Broken.Add("зламана");
        _tts.Enqueue("ostap", ["зламана"]);
        await Drain();
        Assert.Null(_tts.TryGet("ostap", "зламана"));
        _tts.Enqueue("ostap", ["зламана"]);
        await Drain();
        Assert.Single(_engine.Said);
        Assert.Empty(Directory.GetFiles(_dir, "*.part.mp3"));
    }

    [Fact]
    public async Task Disabled_voice_does_nothing()
    {
        _opts.Enabled = false;
        _tts.Enqueue("ostap", ["тиша"]);
        await Drain();
        Assert.Empty(_engine.Said);
        Assert.False(new SvoyaVoice(_tts).Enabled);
    }

    [Fact]
    public async Task Rate_is_part_of_the_cache_key()
    {
        _tts.Enqueue("ostap", ["швидко"]);
        await Drain();
        _opts.Rate = "-4%";
        Assert.Null(_tts.TryGet("ostap", "швидко"));
    }

    [Fact]
    public async Task Files_are_served_only_by_exact_hash_name()
    {
        _tts.Enqueue("ostap", ["файл"]);
        await Drain();
        var hash = _tts.TryGet("ostap", "файл")!.Hash;
        Assert.NotNull(_tts.FileOf(hash + ".mp3"));
        Assert.Null(_tts.FileOf("../../hlechyky.db"));
        Assert.Null(_tts.FileOf(hash.ToUpperInvariant() + ".mp3"));
        Assert.Null(_tts.FileOf(hash + ".sec"));
    }

    [Fact]
    public async Task Game_voice_gives_a_url_and_the_length()
    {
        var voice = new SvoyaVoice(_tts);
        Assert.Null(voice.Ready("ostap", "Хто написав «Енеїду»?"));
        voice.Prepare("ostap", ["Хто написав «Енеїду»?"]);
        await Drain();
        var clip = voice.Ready("ostap", "Хто написав «Енеїду»?")!;
        Assert.Matches("^/api/games/svoya/tts/[0-9a-f]{40}\\.mp3$", clip.Url);
        Assert.Equal(1.5, clip.Seconds);
    }

    // ---------- числа й репліки ----------

    [Theory]
    [InlineData(100, "сто")]
    [InlineData(300, "триста")]
    [InlineData(25, "двадцять п'ять")]
    [InlineData(1000, "тисяча")]
    [InlineData(1500, "тисяча п'ятсот")]
    [InlineData(2000, "дві тисячі")]
    [InlineData(5000, "п'ять тисяч")]
    [InlineData(12_000, "дванадцять тисяч")]
    [InlineData(21_000, "двадцять одна тисяча")]
    [InlineData(22_400, "двадцять дві тисячі чотириста")]
    [InlineData(0, "нуль")]
    [InlineData(-200, "мінус двісті")]
    public void Numbers_in_words(int n, string words) => Assert.Equal(words, NumberWords.Say(n));

    [Fact]
    public void Lines_read_numbers_in_words()
    {
        var q = SvoyaPackTests.Mini().Rounds[0].Themes[0].Questions[0];
        Assert.Equal("Правильно, Оля! Плюс триста. Перша книга сучасною українською", SvoyaLines.Right("Оля", 300, q));
        Assert.Equal("Ні. Мінус тисяча двісті.", SvoyaLines.Wrong(1200));
        Assert.StartsWith("Правильна відповідь — Іван Котляревський.", SvoyaLines.Nobody(q));
    }

    [Fact]
    public void Pack_lines_cover_intros_questions_and_answers()
    {
        var lines = SvoyaLines.All(SvoyaPackTests.Mini()).ToList();
        Assert.Contains("Перший раунд. Теми: Література, Географія.", lines);
        Assert.Contains("Столиця України?", lines);
        Assert.Contains("Правильна відповідь — Київ.", lines);
        Assert.Contains("Фінал. Теми: Історія, Музика.", lines);
    }

    // ---------- у грі ----------

    [Fact]
    public void Answering_prepares_both_verdicts_urgently()
    {
        var voice = new FakeSvoyaVoice();
        var h = SvoyaTests.Table(voice: voice);
        var c = SvoyaTests.Open(h);
        h.Act(c, "buzz");
        Assert.Contains(SvoyaLines.Wrong(100), voice.Urgent);
        Assert.Contains(voice.Urgent, l => l.StartsWith($"Правильно, {h.NickOf(c)}! Плюс сто."));
    }

    sealed class ThrowingVoice : Hlechyky.Games.Impl.ISvoyaVoice
    {
        public bool Enabled => true;
        public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false) => throw new InvalidOperationException("зламався");
        public SvoyaClip? Ready(string voice, string text) => throw new InvalidOperationException("зламався");
    }

    [Fact]
    public void Broken_voice_does_not_break_the_game()
    {
        var h = SvoyaTests.Table(voice: new ThrowingVoice());
        Assert.Equal(Hlechyky.Games.RoomStatus.Playing, h.Room.Status);
        SvoyaTests.Until(h, Hlechyky.Games.Impl.Svoya.Board);
        var c = SvoyaTests.Open(h);
        Assert.True(h.Act(c, "buzz").Ok);
    }

    [Fact]
    public void Live_host_can_let_the_voice_read()
    {
        var voice = new FakeSvoyaVoice(seconds: 2);
        var h = SvoyaTests.Table(new { host = "live" }, ["Ведучий", "Оля"], voice: voice);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("say").ValueKind);
        Assert.False(h.View(0).GetProperty("voice").GetProperty("on").GetBoolean());
        Assert.True(h.Act(0, "voice", new { on = true }).Ok);
        Assert.True(h.View(0).GetProperty("voice").GetProperty("on").GetBoolean());
        h.Act(0, "next");
        h.Act(1, "pick", new { theme = 0, q = 0 });
        Assert.Equal("Хто написав «Енеїду»?", h.View(null).GetProperty("say").GetProperty("text").GetString());
        SvoyaTests.Until(h, Hlechyky.Games.Impl.Svoya.Buzz, 20);    // кнопка відкрилась сама, щойно голос дочитав
        Assert.False(h.Act(1, "voice", new { on = false }).Ok);     // тумблер — лише ведучому
    }

    [Fact]
    public void Voice_toggle_needs_a_voice()
    {
        var h = SvoyaTests.Table(new { host = "live", voice = "none" }, ["Ведучий", "Оля"], voice: new FakeSvoyaVoice());
        Assert.Equal("Голосу на цьому столі нема", h.Act(0, "voice", new { on = true }).Message);
        var silent = SvoyaTests.Table(new { host = "live" }, ["Ведучий", "Оля"]);
        Assert.False(silent.View(0).GetProperty("voice").GetProperty("available").GetBoolean());
    }

    [Fact]
    public void Saving_a_ready_pack_voices_it_ahead()
    {
        using var db = new TempDb();
        var voice = new FakeSvoyaVoice();
        var packs = new SvoyaPacks(new SvoyaStore(db.Db), new SvoyaBuiltin([]), new SvoyaFiles(_dir), new FakeClock(), voice: voice);
        var u = new SvoyaUser("Оля", true, false);
        var id = ((SvoyaFull)packs.Create(u).Data!).Pack.Id;
        Assert.Empty(voice.Prepared);                                // чернетку не озвучуємо
        packs.Save(id, u, SvoyaPackTests.Mini());
        Assert.Contains("Столиця України?", voice.Prepared);
        var progress = JsonSerializer.SerializeToElement(packs.Voice(id, u, start: false).Data);
        Assert.Equal(progress.GetProperty("total").GetInt32(), progress.GetProperty("ready").GetInt32());   // підробка «готова» завжди
        Assert.False(packs.Voice(id, new SvoyaUser("Петро", true, false), start: true).Ok);
    }
}
