using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Око майстра — захист Гончарного кола від автоклікерів і скриптів. Три шари, і всі на сервері:
/// <list type="number">
/// <item><b>Почерк.</b> Кожен клік приходить не числом, а відбитком: проміжок від попереднього, скільки тримали
///   кнопку, де на колі натиснули і чим (миша, палець, перо, пробіл). Мишачий софт клацає з рівним кроком
///   таймера й відпускає кнопку за 0–1 мс. Такий почерк — одразу полиця (див. нижче), без чекання сотень кліків.</item>
/// <item><b>Перевірка картинкою.</b> Раз на кілька сотень зарахованих кліків, за кожні кілька спійманих
///   розписних глеків і за підозрілий почерк коло стає, доки гончар не торкнеться всіх глечиків на полиці.
///   Полицю малює сервер PNG-ом (<see cref="ClickerPicture"/>) із 256-бітного ключа, якого клієнт не бачить,
///   тож відповіді нема ні в DOM, ні у виді, і перебрати її не вийде. Хай скрипт підробить який завгодно
///   почерк — полицю без людини він не пройде, і поки не пройде, кліки не рахуються зовсім.</item>
/// <item><b>Пауза кола.</b> Три помилки поспіль — кліки десять хвилин не рахуються, а по паузі чекає нова полиця.
///   Пасив, покупки й прилавок працюють: карається лише те, що підробляли. Глеків не забираємо.</item>
/// </list>
/// Чому почерк сам не ставить паузу: бувають руки, що виглядають як робот (тап по тачпаду відпускає «кнопку»
/// за 0–5 мс, рівна рука в браузері з загрубленим таймером падає на два вузли сітки). Їм — полиця, яку людина
/// проходить за кілька секунд, а робот не пройде ніколи: для бота без людини це та сама зупинка, тільки безстрокова.
/// </summary>
public sealed class ClickerGuard
{
    /// <summary>Скільки останніх кліків пам'ятаємо для почерку і з якої кількості вже судимо.</summary>
    public const int Window = 40, MinJudge = 32;
    /// <summary>Скільки зарахованих кліків між перевірками: випадково, щоб бот не підлаштувався під число.</summary>
    public const int CheckMin = 600, CheckMax = 1000;
    /// <summary>Скільки кліків «коштує» спійманий розписний глек: бот, що лише ловить глеки, теж зустріне майстра.</summary>
    public const int CatchWeight = 150;
    public const int MaxMisses = 3;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(10);
    /// <summary>Скільки кліків у пачці приймаємо — стільки ж, скільки відро дозволяє за секунду.</summary>
    public const int MaxBatch = 12;
    public const int MaxTaps = 8;

    public enum Source { Mouse, Touch, Pen, Key }

    /// <summary>Відбиток одного кліка: мс від попереднього натиску, мс утримання, точка на колі 0…1000 (−1 — пробіл), чим.</summary>
    public readonly record struct Hand(int Dt, int Press, int X, int Y, Source Src);

    readonly List<Hand> _window = [];

    /// <summary>Кліків до наступної перевірки.</summary>
    public int Left { get; private set; }
    /// <summary>Ключ полиці, що чекає відповіді; null — майстер ні про що не питає. Клієнту — ніколи.</summary>
    public byte[]? Shelf { get; private set; }
    /// <summary>Номер перевірки для клієнта: за ним він кешує картинку, не знаючи ключа.</summary>
    public int Serial { get; private set; }
    public int Misses { get; private set; }
    public DateTimeOffset LockUntil { get; private set; }
    /// <summary>Що показати гравцеві: «rhythm», «press» — почерк, «misses» — пауза за три помилки, порожньо — звичайна перевірка.</summary>
    public string Why { get; private set; } = "";
    /// <summary>Через який почерк майстер питає зараз. На відміну від <see cref="Why"/>, пауза його не стирає — лише пройдена полиця.</summary>
    public string Doubt { get; private set; } = "";
    public int Passed { get; private set; }
    /// <summary>
    /// Полицю через миттєве відпускання пройшла людина — отже, це її тачпад, а не софт. Далі за утриманням не
    /// судимо ніколи: тачпад лишиться тачпадом. Ритм і перевірки раз на кілька сотень кліків лишаються.
    /// </summary>
    public bool PressTrusted { get; private set; }
    /// <summary>
    /// Полицю через рівний ритм пройшла людина. Довіряємо лише до наступної звичайної перевірки: ритм частіше
    /// за тачпад виявляється таки автоклікером, увімкненим при господарі.
    /// </summary>
    public bool RhythmTrusted { get; private set; }

    string _pngFor = "";
    string _png = "";

    public bool Pending => Shelf is not null;
    public bool Locked(DateTimeOffset now) => now < LockUntil;

    public void Reset(Random rng)
    {
        _window.Clear();
        Left = rng.Next(CheckMin, CheckMax + 1);
        Shelf = null;
        Serial = 0;
        Misses = 0;
        LockUntil = default;
        Why = "";
        Doubt = "";
        Passed = 0;
        PressTrusted = false;
        RhythmTrusted = false;
    }

    // ---------- почерк ----------

    /// <summary>
    /// Розібрати пачку кліків: <c>c: [[dt, press, x, y, src], …]</c>. null — пачки нема або вона зіпсована;
    /// тоді не рахуємо жодного кліка (стара вкладка без відбитків — теж сюди: кліки без почерку не приймаємо).
    /// </summary>
    public static List<Hand>? Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("c", out var c)
            || c.ValueKind != JsonValueKind.Array) return null;
        var n = c.GetArrayLength();
        if (n < 1 || n > MaxBatch) return null;
        var list = new List<Hand>(n);
        foreach (var e in c.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 5) return null;
            var v = new int[5];
            var i = 0;
            foreach (var f in e.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Number || !f.TryGetInt32(out v[i])) return null;
                i++;
            }
            if (v[0] < 0 || v[1] < 0 || v[1] > 10_000 || v[2] < -1 || v[2] > 1000 || v[3] < -1 || v[3] > 1000
                || v[4] < 0 || v[4] > (int)Source.Key) return null;
            list.Add(new Hand(Math.Min(v[0], 60_000), v[1], v[2], v[3], (Source)v[4]));
        }
        return list;
    }

    /// <summary>Дописати пачку в пам'ять почерку і розсудити: «rhythm» чи «press» — підозра, null — рука.</summary>
    public string? Judge(IEnumerable<Hand> batch)
    {
        _window.AddRange(batch);
        if (_window.Count > Window) _window.RemoveRange(0, _window.Count - Window);
        return Robot(_window, PressTrusted, RhythmTrusted);
    }

    /// <summary>
    /// Почерк робота. Пороги з великим запасом від людини:
    /// <list type="bullet">
    /// <item><b>ритм</b> — ≥ 90 % проміжків безперервного клацання вміщаються у два вікна по 5 мс. Людина за
    ///   ~150 мс між кліками тримає розкид щонайменше 8–15 мс; навіть на миші з опитуванням 125 Гц (кліки
    ///   падають на сітку 8 мс) два найчастіші значення рідко беруть понад 80 %. Автоклікер на таймері
    ///   Windows дає одне-два значення (напр. 94 і 109 мс) — це 100 %;</item>
    /// <item><b>утримання</b> — ≥ 90 % кліків відпущено за ≤ 6 мс (софт шле «натиснув-відпустив» підряд), або ≥ 95 %
    ///   утримань у двох вікнах по 5 мс (софт зі сталим «тримати 50 мс»). Тап по тачпаду виглядає так само.</item>
    /// </list>
    /// Браузер із загрубленим часом (Firefox із resistFingerprinting кругляє до ~16,7 мс) із розкидом хоч на три
    /// вузли сітки не судимо: там і людина виглядала б роботом. Рівна рука на двох вузлах однаково може сюди
    /// потрапити — тому за почерк лише полиця, а не пауза.
    /// </summary>
    public static string? Robot(IReadOnlyList<Hand> hands, bool pressTrusted = false, bool rhythmTrusted = false)
    {
        var dts = hands.Select(h => h.Dt).Where(d => d is >= 20 and <= 1500).ToList();
        var presses = hands.Select(h => h.Press).ToList();
        if (!rhythmTrusted && dts.Count >= MinJudge && !Coarse(dts) && TwoWindows(dts) >= 0.9) return "rhythm";
        if (!pressTrusted && presses.Count >= MinJudge && !Coarse(presses))
        {
            if (presses.Count(p => p <= 6) >= 0.9 * presses.Count) return "press";
            if (TwoWindows(presses) >= 0.95) return "press";
        }
        return null;
    }

    /// <summary>Яку частку значень накривають два найщільніші вікна по 5 мс (друге — з того, що не влізло в перше).</summary>
    static double TwoWindows(List<int> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        var (from1, count1) = Densest(sorted, _ => true);
        var to1 = sorted[from1] + 4;
        var (_, count2) = Densest(sorted, v => v < sorted[from1] || v > to1);
        return (double)(count1 + count2) / values.Count;
    }

    static (int From, int Count) Densest(List<int> sorted, Func<int, bool> allowed)
    {
        int best = 0, at = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!allowed(sorted[i])) continue;
            var n = 0;
            for (var j = i; j < sorted.Count && sorted[j] <= sorted[i] + 4; j++)
                if (allowed(sorted[j])) n++;
            if (n > best) { best = n; at = i; }
        }
        return (at, best);
    }

    /// <summary>
    /// Час загрублено до кадру (~16,7 мс): майже всі значення лежать на сітці кадрів і розкидані хоч по трьох її
    /// вузлах. Без другої умови сюди втік би автоклікер рівно на 100 мс (це теж вузол сітки), а нульове
    /// утримання — саме той робот, якого шукаємо.
    /// </summary>
    static bool Coarse(List<int> values)
    {
        const double frame = 1000.0 / 60;
        var nodes = new Dictionary<long, int>();
        var onGrid = 0;
        foreach (var v in values)
        {
            var node = (long)Math.Round(v / frame);
            if (Math.Abs(v - node * frame) > 1.01) continue;
            onGrid++;
            nodes[node] = nodes.GetValueOrDefault(node) + 1;
        }
        return onGrid >= 0.9 * values.Count && nodes.Count(kv => kv.Key > 0 && kv.Value >= 2) >= 3;
    }

    // ---------- перевірка й пауза ----------

    /// <summary>Порахувати зараховані кліки (чи спійманий глек) до наступної перевірки.</summary>
    public void Spend(int clicks) => Left = Math.Max(int.MinValue / 2, Left - clicks);

    public bool Due => Left <= 0;

    /// <summary>Звичайна перевірка раз на кілька сотень кліків. Довіра до ритму на ній і кінчається.</summary>
    public void Check()
    {
        Why = "";
        RhythmTrusted = false;
        Ask();
    }

    /// <summary>Підозрілий почерк: полиця одразу, без паузи. Людина проходить її за кілька секунд, робот — ніколи.</summary>
    public void Suspect(string why)
    {
        Why = why;
        Doubt = why;
        _window.Clear();
        Ask();
    }

    /// <summary>Нова полиця: ключ — 256 біт із криптографічного генератора, номер — для кешу картинки на клієнті.</summary>
    void Ask()
    {
        Shelf = ClickerPicture.NewKey();
        Serial++;
    }

    /// <summary>Коло стає на <see cref="LockFor"/>; після паузи однаково чекає відповіді на нову полицю.</summary>
    void Lock(DateTimeOffset now)
    {
        LockUntil = now + LockFor;
        Why = "misses";
        Misses = 0;
        _window.Clear();
        Ask();
    }

    public enum Verdict { Passed, Wrong, Locked }

    /// <summary>Відповідь на полицю: влучив — наступна перевірка через кілька сотень кліків, ні — нова полиця.</summary>
    public Verdict Answer(IReadOnlyList<(double X, double Y)> taps, DateTimeOffset now, Random rng)
    {
        if (Shelf is not { } shelf) return Verdict.Wrong;
        if (ClickerPicture.Solve(ClickerPicture.Scene(shelf), taps))
        {
            if (Doubt == "press") PressTrusted = true;
            if (Doubt == "rhythm") RhythmTrusted = true;
            Doubt = "";
            Shelf = null;
            Misses = 0;
            Why = "";
            Passed++;
            // Почерк до перевірки вже нічого не доводить: після неї судимо з чистого аркуша.
            _window.Clear();
            Left = rng.Next(CheckMin, CheckMax + 1);
            return Verdict.Passed;
        }
        Misses++;
        if (Misses >= MaxMisses)
        {
            Lock(now);
            return Verdict.Locked;
        }
        Ask();
        return Verdict.Wrong;
    }

    /// <summary>Торкання з відповіді: <c>taps: [[x, y], …]</c> у пікселях картинки. null — зіпсовані.</summary>
    public static List<(double X, double Y)>? Taps(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("taps", out var t)
            || t.ValueKind != JsonValueKind.Array || t.GetArrayLength() > MaxTaps) return null;
        var list = new List<(double, double)>();
        foreach (var e in t.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2) return null;
            var x = e[0];
            var y = e[1];
            // TryGet, а не Get: «1e999» від скрипта кинуло б виняток, а виняток у дії каркас вважає поломкою гри.
            if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number
                || !x.TryGetDouble(out var px) || !y.TryGetDouble(out var py)) return null;
            if (!double.IsFinite(px) || !double.IsFinite(py)) return null;
            list.Add((px, py));
        }
        return list;
    }

    // ---------- вид ----------

    /// <summary>Що бачить клієнт: картинку (без ключа), скільки глечиків шукати, спроби й паузу.</summary>
    public object? View(DateTimeOffset now)
    {
        if (!Pending && !Locked(now)) return null;
        return new
        {
            serial = Serial,
            count = Shelf is { } s ? ClickerPicture.Jugs(ClickerPicture.Scene(s)) : 0,
            png = Shelf is { } shelf ? Picture(shelf) : "",
            width = ClickerPicture.Width,
            height = ClickerPicture.Height,
            misses = Misses,
            maxMisses = MaxMisses,
            lockUntil = Locked(now) ? LockUntil : (DateTimeOffset?)null,
            why = Why,
        };
    }

    /// <summary>PNG малюється раз на полицю: вид летить на кожну дію, а картинка між ними та сама.</summary>
    string Picture(byte[] shelf)
    {
        var id = Convert.ToBase64String(shelf);
        if (_pngFor != id)
        {
            _png = "data:image/png;base64," + Convert.ToBase64String(ClickerPicture.Png(shelf));
            _pngFor = id;
        }
        return _png;
    }

    // ---------- збереження ----------

    public sealed record Row(int Left, string? Shelf, int Serial, int Misses, DateTimeOffset LockUntil, string? Why, int Passed,
        List<int[]>? Hands, bool PressTrusted = false, bool RhythmTrusted = false, string? Doubt = null);

    public Row Save() => new(Left, Shelf is null ? null : Convert.ToBase64String(Shelf), Serial, Misses, LockUntil, Why, Passed,
        _window.Select(h => new[] { h.Dt, h.Press, h.X, h.Y, (int)h.Src }).ToList(), PressTrusted, RhythmTrusted, Doubt);

    /// <summary>
    /// Відновити з бази. Старе збереження (до Ока майстра) — чистий аркуш із повним лічильником. Пауза й
    /// недороблена перевірка переживають F5 так само, як і відро дозволів: інакше перезавантаження їх знімало б.
    /// </summary>
    public void Load(Row? row, Random rng)
    {
        if (row is null)
        {
            Reset(rng);
            return;
        }
        _window.Clear();
        Left = Math.Clamp(row.Left, int.MinValue / 2, CheckMax);
        Shelf = KeyOf(row.Shelf);
        Serial = Math.Max(0, row.Serial);
        Misses = Math.Clamp(row.Misses, 0, MaxMisses - 1);
        LockUntil = row.LockUntil;
        Why = row.Why is "rhythm" or "press" or "misses" ? row.Why : "";
        Doubt = row.Doubt is "rhythm" or "press" ? row.Doubt : "";
        Passed = Math.Max(0, row.Passed);
        PressTrusted = row.PressTrusted;
        RhythmTrusted = row.RhythmTrusted;
        foreach (var h in row.Hands ?? [])
            if (h is { Length: 5 } && h[4] is >= 0 and <= (int)Source.Key)
                _window.Add(new Hand(h[0], h[1], h[2], h[3], (Source)h[4]));
        if (_window.Count > Window) _window.RemoveRange(0, _window.Count - Window);
    }

    /// <summary>Ключ зі збереження: рівно 256 біт у base64, інакше полиці нема.</summary>
    static byte[]? KeyOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var buffer = new byte[ClickerPicture.KeySize + 3];
        return Convert.TryFromBase64String(text, buffer, out var n) && n == ClickerPicture.KeySize ? buffer[..n] : null;
    }
}
