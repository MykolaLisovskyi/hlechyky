using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Техніка розпису партії: ключ, назва, рідний осередок (розпис, з яким вона дає +10 краси) і що вона відкриває.</summary>
public sealed record ClickerTechnique(string Key, string Name, string Home, int Fired, string Desc, string Unlock);

/// <summary>
/// Модель жару в горні — детермінована, з кроком 100 мс, ОДНАКОВА тут і в <c>web/games/clicker-kiln.js</c> (там —
/// рядок у рядок та сама арифметика). Тому тут лише додавання, множення, ділення, min і max над double: такі операції
/// IEEE дає однаково і в .NET, і в JavaScript, а exp/pow/sin могли б розійтись в останньому знаку.
///
/// Стан: жар <c>T</c> (°C), дрова <c>F</c> і заслінка (відкрита/прикрита). Кожен крок:
/// <code>
/// F -= F × Burn[заслінка] × 0,1
/// T += (F × Heat[заслінка] − (T − 20) × Loss[заслінка] × (порив ? 2 : 1)) × 0,1
/// </code>
/// Відкрита заслінка — жаркіше (рівновага ≈ 1140·F), але дрова згоряють утричі швидше; прикрита — прохолодніше
/// (≈ 500·F: заслінка душить вогонь — так збивають перегрів), зате дрова горять утричі повільніше. Підкинуте поліно — <c>F += 0,4 × розмір</c> (60–140 %, стеля 2,6) і
/// <c>T −= 15</c> (холодне дерево). Пориви вітру й розміри полін — із зерна партії (xorshift32 на цілих, теж
/// однаковий в обох мовах): так «рівний ритм наосліп» не працює, треба дивитись на термометр.
///
/// Зелена смуга: перші 8 с — лише стеля, що росте від 350 до 1010 (швидкий розігрів тріскає глину), далі
/// 830–1010 °C. Рахунок жару <c>H</c> — середнє по кроках після розігріву: у смузі 1, на 100° нижче — лінійно до 0,
/// на 60° вище — лінійно до 0. Перегрів <c>over</c> = ∫(T − стеля)dt у градусо-секундах накопичує ризик тріщини:
/// <c>clamp((over − 300) / 3000; 0; 0,5)</c>.
/// </summary>
public static class KilnHeat
{
    public const int StepMs = 100, Steps = 300, WarmSteps = 80, MaxActs = 80;
    public const int Stoke = 0, Open = 1, Close = 2;
    public const double Amb = 20, Fuel0 = 1.0, Log = 0.4, FuelMax = 2.6, Chill = 15;
    public const double BurnClosed = 0.045, BurnOpen = 0.12, HeatClosed = 60, HeatOpen = 240, LossClosed = 0.12, LossOpen = 0.21, Gust = 2;
    public const double Lo = 830, Hi = 1010, Hi0 = 350, SoftUnder = 100, SoftOver = 60;
    public const double CrackFree = 300, CrackScale = 3000, CrackMax = 0.5;

    /// <summary>Підсумок партії: рахунок жару 0…1, перегрів у градусо-секундах, жар наприкінці, скільки полін згоріло.</summary>
    public readonly record struct Result(double Heat, double Over, double Temp, int Logs);

    static uint Start(uint seed) => seed == 0 ? 0x9E3779B9 : seed;

    static uint Next(ref uint x)
    {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    /// <summary>Пориви вітру: [від кроку; до кроку). Перший — на 4–7 с, далі кожні 5,5–11 с по 1,5–3 с.</summary>
    public static List<(int From, int To)> Gusts(int seed)
    {
        var x = Start((uint)seed);
        var list = new List<(int, int)>();
        var t = 40 + (int)(Next(ref x) % 30);
        while (t < 290)
        {
            var d = 15 + (int)(Next(ref x) % 16);
            list.Add((t, Math.Min(Steps, t + d)));
            t += d + 40 + (int)(Next(ref x) % 50);
        }
        return list;
    }

    /// <summary>Розміри полін у відсотках (60–140), по одному на кожне підкинуте.</summary>
    public static int[] Logs(int seed)
    {
        var y = Start((uint)seed ^ 0x5bd1e995);
        var logs = new int[MaxActs];
        for (var i = 0; i < logs.Length; i++) logs[i] = 60 + (int)(Next(ref y) % 81);
        return logs;
    }

    /// <summary>Прогнати партію: дії <paramref name="acts"/> (мітка в мс від розпалу, дія) уже перевірені — зростають і в межах.</summary>
    public static Result Run(int seed, IReadOnlyList<(int Ms, int Act)> acts, Action<int, double, double, bool>? trace = null)
    {
        var gusts = Gusts(seed);
        var logs = Logs(seed);
        double T = Amb, F = Fuel0, over = 0, score = 0;
        var open = true;
        int j = 0, k = 0, g = 0;
        for (var i = 0; i < Steps; i++)
        {
            while (j < acts.Count && acts[j].Ms / StepMs <= i)
            {
                switch (acts[j].Act)
                {
                    case Stoke:
                        if (F < FuelMax && k < logs.Length)
                        {
                            F = Math.Min(FuelMax, F + Log * logs[k] / 100);
                            k++;
                            T = Math.Max(Amb, T - Chill);
                        }
                        break;
                    case Open: open = true; break;
                    case Close: open = false; break;
                }
                j++;
            }
            while (g < gusts.Count && gusts[g].To <= i) g++;
            var gust = g < gusts.Count && gusts[g].From <= i;
            var loss = (open ? LossOpen : LossClosed) * (gust ? Gust : 1);
            F -= F * (open ? BurnOpen : BurnClosed) * 0.1;
            T += (F * (open ? HeatOpen : HeatClosed) - (T - Amb) * loss) * 0.1;
            var n = i + 1;
            var hi = n < WarmSteps ? Hi0 + (Hi - Hi0) * n / WarmSteps : Hi;
            if (T > hi) over += (T - hi) * 0.1;
            if (n >= WarmSteps)
            {
                if (T > Hi + SoftOver) { }
                else if (T > Hi) score += 1 - (T - Hi) / SoftOver;
                else if (T >= Lo) score += 1;
                else if (T >= Lo - SoftUnder) score += (T - (Lo - SoftUnder)) / SoftUnder;
            }
            trace?.Invoke(i, T, F, open);
        }
        return new Result(score / (Steps - WarmSteps + 1), over, T, k);
    }

    public static double CrackChance(double over) => Math.Clamp((over - CrackFree) / CrackScale, 0, CrackMax);

    /// <summary>
    /// Якість виробу за рахунком жару <paramref name="heat"/> (0…1), красою розпису (0…100) і кидком <paramref name="u"/>.
    /// «Блиск» <c>S = H × (0,6 + 0,4 × краса/100)</c>; дзвінкий — з імовірністю <c>0,3·S²</c>, добрий — <c>0,4·S</c>.
    /// Ідеальний жар без розпису: 11 % дзвінких і 24 % добрих (у середньому ×1,32 до ціни); з красою 100 —
    /// 30 % і 40 % (×1,72). Недогріте горно (H = 0,5) — ×1,13. Підмайстер-палій — рівно ×1.
    /// </summary>
    public static int Quality(double heat, int beauty, double u)
    {
        var s = Math.Clamp(heat, 0, 1) * (0.6 + 0.4 * Math.Clamp(beauty, 0, 100) / 100.0);
        var p3 = 0.3 * s * s;
        var p2 = 0.4 * s;
        return u < p3 ? 3 : u < p3 + p2 ? 2 : 1;
    }
}

/// <summary>
/// Розпис партії: візерунок із зерна (параметри — <see cref="Random"/> із сідом, у вид іде лише те, що треба
/// намалювати) і «краса» 0–100 за траєкторією справжніх торкань. Точки — мс, x, y і «тут почався штрих» у полотні
/// 1000×1000 з центром (500; 500); мітки від початку мінігри на клієнті. Краса рахується лише тут — клієнт її не знає.
/// </summary>
public static class KilnPaint
{
    public const int Canvas = 1000, MinPoints = 12, MaxPoints = 500, MinSpanMs = 1500, MaxSpanMs = 30_000;

    public readonly record struct Pt(double Ms, double X, double Y, bool Down);

    // ---------- візерунки ----------

    public sealed record Rizh(int R0, int Amp, int K, int Phase, int Period, int Dir);
    public sealed record Ryt(int R0, int Amp, int K, int Phase);
    public sealed record Flyand(int[][] Marks, int Bands);
    public sealed record Marble(int[][] Drops);
    public sealed record Losk(int[][] Stripes, int Top, int Bottom);

    /// <summary>Параметри візерунка техніки з зерна: ті самі і для виду, і для підрахунку краси.</summary>
    public static object Pattern(string tech, int seed)
    {
        var r = new Random(seed);
        switch (tech)
        {
            case "rizh":
                return new Rizh(250 + r.Next(70), 45 + r.Next(35), 3 + r.Next(4), r.Next(360), 4200 + r.Next(1400), r.Next(2) == 0 ? 1 : -1);
            case "ryt":
                return new Ryt(260 + r.Next(60), 55 + r.Next(35), 4 + r.Next(4), r.Next(360));
            case "flyand":
            {
                var n = 5 + r.Next(3);
                var first = r.Next(2);
                var marks = new int[n][];
                for (var i = 0; i < n; i++)
                    marks[i] = [(int)(170 + 660.0 * (i + 0.5) / n) + r.Next(41) - 20, (i + first) % 2 == 0 ? 1 : -1];
                return new Flyand(marks, 4 + r.Next(3));
            }
            case "marble":
            {
                var n = 4 + r.Next(3);
                var drops = new List<int[]>();
                for (var tries = 0; drops.Count < n && tries < 400; tries++)
                {
                    var ang = r.Next(360) * Math.PI / 180;
                    var rad = 130 + r.Next(200);
                    var x = (int)Math.Round(500 + rad * Math.Cos(ang));
                    var y = (int)Math.Round(500 + rad * Math.Sin(ang));
                    if (drops.All(d => (d[0] - x) * (d[0] - x) + (d[1] - y) * (d[1] - y) >= 160 * 160)) drops.Add([x, y]);
                }
                return new Marble([.. drops]);
            }
            default:
            {
                var n = 3 + r.Next(3);
                var top = 270 + r.Next(60);
                var bottom = 670 + r.Next(60);
                var stripes = new int[n][];
                for (var i = 0; i < n; i++) stripes[i] = [(int)(500 + (i - (n - 1) / 2.0) * 140), 60 + r.Next(25)];
                return new Losk(stripes, top, bottom);
            }
        }
    }

    // ---------- траєкторія ----------

    /// <summary>
    /// Розібрати <c>path: [dt, x, y, dt, x, y, …]</c> — плоский масив цілих, бо кімната не бере payload понад 8 КБ:
    /// <c>dt</c> — мс від попередньої точки (≥ 0; від'ємне <c>−(dt+1)</c> — тут палець щойно торкнувся, новий штрих),
    /// x і y — у полотні 0…1000. null — зіпсована (не цілі, не кратна трьом, забагато точок).
    /// </summary>
    public static List<Pt>? Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array)
            return null;
        var n = path.GetArrayLength();
        if (n % 3 != 0 || n / 3 > MaxPoints) return null;
        var list = new List<Pt>(n / 3);
        var nums = new int[3];
        var i = 0;
        double ms = 0;
        foreach (var v in path.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var d)) return null;
            nums[i++] = d;
            if (i < 3) continue;
            i = 0;
            var down = nums[0] < 0;
            var dt = down ? -(long)nums[0] - 1 : nums[0];
            if (dt > MaxSpanMs) return null;
            if (list.Count > 0) ms += dt;                                     // час першої точки — нуль мінігри
            list.Add(new Pt(ms, Math.Clamp(nums[1], -50, Canvas + 50), Math.Clamp(nums[2], -50, Canvas + 50), list.Count == 0 || down));
        }
        return list;
    }

    /// <summary>Закодувати точки назад у плоский масив (для тестів і як зразок для клієнта).</summary>
    public static int[] Encode(IEnumerable<Pt> pts)
    {
        var list = new List<int>();
        Pt? prev = null;
        foreach (var p in pts)
        {
            var dt = prev is { } q ? (int)Math.Round(p.Ms - q.Ms) : 0;
            list.Add(prev is null || p.Down ? -dt - 1 : dt);
            list.Add((int)Math.Round(p.X));
            list.Add((int)Math.Round(p.Y));
            prev = p;
        }
        return [.. list];
    }

    /// <summary>
    /// Чи схожа траєкторія на руку: хоч 12 точок, хоч півтори секунди, і не «ідеально рівний крок». Рука на 60 Гц дає
    /// майже рівні проміжки часу (події вирівняні по кадрах), тож робота видає не лише час, а час разом зі сталою
    /// швидкістю: std(Δt) &lt; 0,25 мс і розкид довжин кроків &lt; 3 % — такого пальцем не намалюєш.
    /// </summary>
    public static string? Implausible(List<Pt> pts)
    {
        if (pts.Count < MinPoints) return "Закоротко: проведи довше";
        var span = pts[^1].Ms - pts[0].Ms;
        if (span < MinSpanMs) return "Закоротко: розпис — хоч півтори секунди роботи";
        if (span > MaxSpanMs) return "Розпис затягнувся — фарба підсохла, спробуй ще раз";
        var dts = new List<double>();
        var ds = new List<double>();
        for (var i = 1; i < pts.Count; i++)
        {
            if (pts[i].Down) continue;
            dts.Add(pts[i].Ms - pts[i - 1].Ms);
            ds.Add(Dist(pts[i], pts[i - 1]));
        }
        if (dts.Count >= 20)
        {
            var (mt, st) = MeanStd(dts);
            var (md, sd) = MeanStd(ds);
            if (st < 0.25 && md > 0.5 && sd / md < 0.03) return "Рука так рівно не ходить — спробуй ще раз";
            if (mt <= 0) return "Рука так рівно не ходить — спробуй ще раз";
        }
        return null;
    }

    static (double Mean, double Std) MeanStd(List<double> xs)
    {
        var mean = xs.Average();
        var v = xs.Sum(x => (x - mean) * (x - mean)) / xs.Count;
        return (mean, Math.Sqrt(v));
    }

    static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static List<List<Pt>> Strokes(List<Pt> pts)
    {
        var all = new List<List<Pt>>();
        foreach (var p in pts)
        {
            if (p.Down || all.Count == 0) all.Add([]);
            all[^1].Add(p);
        }
        return all;
    }

    // ---------- краса ----------

    /// <summary>Краса 0–100 за технікою, зерном і траєкторією (до бонусу рідного осередку).</summary>
    public static int Beauty(string tech, int seed, List<Pt> pts)
    {
        var raw = Pattern(tech, seed) switch
        {
            Rizh z => Trace(pts, z.R0, z.Amp, z.K, z.Phase, z.Period, z.Dir, sine: true, tol: 28, blot: 70, blotWeight: 1, accShare: 0.3),
            Ryt z => Trace(pts, z.R0, z.Amp, z.K, z.Phase, 0, 0, sine: false, tol: 18, blot: 45, blotWeight: 1.5, accShare: 0.4),
            Flyand z => Pull(pts, z),
            Marble z => Drip(pts, z),
            Losk z => Rub(pts, z),
            _ => 0,
        };
        return (int)Math.Round(Math.Clamp(raw, 0, 100));
    }

    const int Bins = 72;

    /// <summary>
    /// Ріжкування й ритування: провести по контуру <c>r(θ) = r0 + amp·sin(kθ + φ)</c> (ритування — cos, без обертання).
    /// Коло ріжкування крутиться: точку переводимо в систему кола, <c>θ = кут − dir·2π·мс/period</c>. Коло ділимо на 72
    /// дуги; дуга «пройдена», якщо на ній була точка ближче за допуск. Краса = пройдені дуги × (частка за точність) ×
    /// (кара за клякси — точки далі за <paramref name="blot"/>).
    /// </summary>
    static double Trace(List<Pt> pts, int r0, int amp, int k, int phase, int period, int dir, bool sine, double tol, double blot, double blotWeight, double accShare)
    {
        var bins = new bool[Bins];
        double err = 0;
        int on = 0, blots = 0;
        var ph = phase * Math.PI / 180;
        foreach (var p in pts)
        {
            var dx = p.X - 500;
            var dy = p.Y - 500;
            var rho = Math.Sqrt(dx * dx + dy * dy);
            var theta = Math.Atan2(dy, dx) - (period > 0 ? dir * 2 * Math.PI * p.Ms / period : 0);
            theta %= 2 * Math.PI;
            if (theta < 0) theta += 2 * Math.PI;
            var target = r0 + amp * (sine ? Math.Sin(k * theta + ph) : Math.Cos(k * theta + ph));
            var e = Math.Abs(rho - target);
            if (e <= tol)
            {
                bins[Math.Min(Bins - 1, (int)(theta / (2 * Math.PI) * Bins))] = true;
                err += e;
                on++;
            }
            else if (e > blot) blots++;
        }
        var cov = bins.Count(b => b) / (double)Bins;
        var acc = on > 0 ? 1 - err / on / tol : 0;
        var blotRatio = blots / (double)Math.Max(1, pts.Count);
        return 100 * cov * (1 - accShare + accShare * acc) * (1 - Math.Min(0.6, blotRatio * blotWeight));
    }

    /// <summary>
    /// Фляндрування: смуги ангобу — між y 300 і 700; на позначках треба протягнути гачком упоперек, у бік стрілки.
    /// Кожен штрих: наскільки перетнув смуги, наскільки рівний (відхилення від вертикалі до 25 — ідеально, 90 — ледь),
    /// чи в той бік (ні — ×0,6) і наскільки близько до позначки. Штрих мимо позначок — клякса, −8.
    /// </summary>
    static double Pull(List<Pt> pts, Flyand z)
    {
        var best = new double[z.Marks.Length];
        var stray = 0;
        foreach (var s in Strokes(pts))
        {
            if (s.Count < 3) continue;
            var ymin = s.Min(p => p.Y);
            var ymax = s.Max(p => p.Y);
            if (ymax - ymin < 120) continue;                                  // дотик, а не штрих
            var xmean = s.Average(p => p.X);
            var dev = s.Max(p => Math.Abs(p.X - xmean));
            var span = Math.Clamp((Math.Min(ymax, 700) - Math.Max(ymin, 300)) / 400, 0, 1);
            var straight = dev <= 25 ? 1 : dev >= 90 ? 0.3 : 1 - 0.7 * (dev - 25) / 65;
            var way = s[^1].Y > s[0].Y ? 1 : -1;
            var idx = -1;
            var near = double.MaxValue;
            for (var i = 0; i < z.Marks.Length; i++)
            {
                var d = Math.Abs(z.Marks[i][0] - xmean);
                if (d < near) { near = d; idx = i; }
            }
            if (idx < 0 || near > 45) { stray++; continue; }
            var sc = span * straight * (way == z.Marks[idx][1] ? 1 : 0.6) * (1 - 0.3 * near / 45);
            best[idx] = Math.Max(best[idx], sc);
        }
        return 100 * best.Sum() / Math.Max(1, best.Length) - 8 * stray;
    }

    /// <summary>
    /// Мармурування: накрапати (короткі дотики: до 400 мс і до 50 одиниць руху) у позначки, а тоді різко крутнути —
    /// штрих, що обходить центр хоч на пів оберта. Краплі рахуються лише до закрутки. Краса = 60 % за краплі
    /// (кожна позначка — найближча крапля до 60 одиниць) + 40 % за закрутку (повний оберт за ≤ 0,9 с) − 12 за зайву краплю.
    /// </summary>
    static double Drip(List<Pt> pts, Marble z)
    {
        var strokes = Strokes(pts);
        List<Pt>? spin = null;
        double travel = 0;
        foreach (var s in strokes)
        {
            double tr = 0;
            Pt? prev = null;
            foreach (var p in s)
            {
                if (Math.Sqrt((p.X - 500) * (p.X - 500) + (p.Y - 500) * (p.Y - 500)) < 60) { prev = null; continue; }
                if (prev is { } q)
                {
                    var d = Math.Atan2(p.Y - 500, p.X - 500) - Math.Atan2(q.Y - 500, q.X - 500);
                    if (d > Math.PI) d -= 2 * Math.PI;
                    if (d < -Math.PI) d += 2 * Math.PI;
                    tr += d;
                }
                prev = p;
            }
            if (Math.Abs(tr) >= Math.PI) { spin = s; travel = Math.Abs(tr); }
        }
        var spinStart = spin?[0].Ms ?? double.MaxValue;
        var drops = strokes
            .Where(s => s[0].Ms < spinStart && s[^1].Ms - s[0].Ms <= 400
                && s.Max(p => p.X) - s.Min(p => p.X) <= 50 && s.Max(p => p.Y) - s.Min(p => p.Y) <= 50)
            .Select(s => s[0]).ToList();
        var used = new bool[drops.Count];
        double sum = 0;
        foreach (var t in z.Drops)
        {
            var idx = -1;
            var near = 60.0;
            for (var i = 0; i < drops.Count; i++)
            {
                if (used[i]) continue;
                var d = Math.Sqrt((drops[i].X - t[0]) * (drops[i].X - t[0]) + (drops[i].Y - t[1]) * (drops[i].Y - t[1]));
                if (d <= near) { near = d; idx = i; }
            }
            if (idx < 0) continue;
            used[idx] = true;
            sum += 1 - 0.5 * near / 60;
        }
        var extra = used.Count(u => !u);
        var spinScore = spin is null ? 0 : Math.Min(1, travel / (2 * Math.PI)) * Math.Min(1, 900 / Math.Max(1, spin[^1].Ms - spin[0].Ms));
        return 100 * (0.6 * sum / Math.Max(1, z.Drops.Length) + 0.4 * spinScore) - 12 * extra;
    }

    const int Cell = 40, Grid = Canvas / Cell;
    /// <summary>Стільки шляху камінця (у одиницях полотна) робить клітинку блискучою — приблизно три проходи.</summary>
    public const double PolishLength = 100;

    /// <summary>
    /// Лощіння: сітка 25×25 клітинок по 40; шлях руки розкладаємо по клітинках (кроками ≤ 5, стрибки довші за 200 не
    /// рахуються). Клітинка блищить, коли по ній пройшло ≥ 100 шляху. Краса = частка блискучих клітинок смуг − половина
    /// частки заблищених поза смугами (натер не там — пляма).
    /// </summary>
    static double Rub(List<Pt> pts, Losk z)
    {
        var len = new double[Grid * Grid];
        foreach (var s in Strokes(pts))
            for (var i = 1; i < s.Count; i++)
            {
                var d = Dist(s[i], s[i - 1]);
                if (d <= 0 || d > 200) continue;
                var steps = (int)Math.Ceiling(d / 5);
                for (var k = 0; k < steps; k++)
                {
                    var t = (k + 0.5) / steps;
                    var x = s[i - 1].X + (s[i].X - s[i - 1].X) * t;
                    var y = s[i - 1].Y + (s[i].Y - s[i - 1].Y) * t;
                    var cx = (int)Math.Floor(x / Cell);
                    var cy = (int)Math.Floor(y / Cell);
                    if (cx < 0 || cy < 0 || cx >= Grid || cy >= Grid) continue;
                    len[cy * Grid + cx] += d / steps;
                }
            }
        int zone = 0, inside = 0, outside = 0;
        for (var cy = 0; cy < Grid; cy++)
            for (var cx = 0; cx < Grid; cx++)
            {
                var x = cx * Cell + Cell / 2;
                var y = cy * Cell + Cell / 2;
                var inZone = y >= z.Top && y <= z.Bottom && z.Stripes.Any(st => Math.Abs(x - st[0]) * 2 <= st[1]);
                var shine = len[cy * Grid + cx] >= PolishLength;
                if (inZone) { zone++; if (shine) inside++; }
                else if (shine) outside++;
            }
        return zone == 0 ? 0 : 100.0 * inside / zone - 50.0 * outside / zone;
    }
}

/// <summary>
/// Горно й розпис (пакет B2, docs/games/specs/clicker-v7-kiln.md): висохлі сирці з сушарні — у горно, розпис партії
/// технікою-мінігрою, обпал із мінігрою жару (або підмайстер-палій), відкриття з якістю кожного виробу — і в комору.
/// Правила й кидки — тут, за <see cref="IRoomContext.Clock"/> і <see cref="IRoomContext.Rng"/>; клієнт лише малює
/// й шле на <c>open</c> таймлайн своїх дій, який сервер сам проганяє через <see cref="KilnHeat"/>.
/// </summary>
public sealed partial class Clicker
{
    public const int KilnSlotsBase = 6, KilnSlotsMax = 24, KilnPerLevel = 10;
    public static readonly TimeSpan KilnBurn = TimeSpan.FromMilliseconds(KilnHeat.Steps * KilnHeat.StepMs);
    public static readonly TimeSpan KilnCool = TimeSpan.FromSeconds(60);
    /// <summary>Запас на пінг: клієнт шле open за серверним «зараз» із виду, а воно відстає на дорогу.</summary>
    public static readonly TimeSpan KilnGrace = TimeSpan.FromMilliseconds(500);
    /// <summary>Хто розпалив і пішов: за стільки після кінця горно відкриває підмайстер (якість 1, без тріщин).</summary>
    public static readonly TimeSpan KilnAbandon = TimeSpan.FromMinutes(15);
    /// <summary>Скільки живе виданий візерунок розпису.</summary>
    public static readonly TimeSpan PatternLife = TimeSpan.FromMinutes(5);
    /// <summary>Вага відкритого вручну горна для Ока майстра (спійманий глек — 150): мінігра — пів хвилини живої роботи.</summary>
    public const int KilnGuardWeight = 120;
    /// <summary>Солома: множник шансу тріщини, скільки в'язок уміщає клуня і скільки простих горщиків коштує в'язка.</summary>
    public const double StrawCrack = 0.25;
    public const int StrawMax = 20, StrawPots = 2;
    /// <summary>Тріснутий виріб — черепки на засипку доріжки: частка ціни простого звичайного.</summary>
    public const double ShardShare = 0.15;
    public const int HomeBonus = 10, PerfectMin = 4;

    public static readonly ClickerTechnique[] Techniques =
    [
        new("rizh", "Ріжкування", "", 0,
            "Коло крутиться, а ріжок з рідким ангобом стоїть: веди хвилю по пунктиру, не відриваючи руки. Так розписують миски в Опішні.",
            "завжди"),
        new("flyand", "Фляндрування", "", 10,
            "По мокрих смугах ангобу протягни гачком упоперек — у бік стрілки. Смуги потечуть «ялинкою».",
            "після 10 обпалених виробів"),
        new("marble", "Мармурування", "", 60,
            "Накрапай ангоб у позначки, а тоді різко крутни — фарби розійдуться мармуровими розводами.",
            "після 60 обпалених виробів"),
        new("ryt", "Ритування", "kosiv", 400,
            "Косівська техніка: гострою паличкою продряпай контур крізь білий ангоб — проступить глина. Мимо — подряпина.",
            "з косівським розписом у колекції або після 400 обпалених"),
        new("losk", "Лощіння", "gavarets", 150,
            "Гаварецька техніка: натирай камінцем смуги на сирці до блиску — після чорного димлення вони сяють на матовому.",
            "з гаварецьким розписом у колекції або після 150 обпалених"),
    ];

    sealed record KilnOutRow(string Ware, int Q);

    sealed record KilnLastRow(DateTimeOffset At, bool Helper, int Heat, int Over, int Beauty, string? Style, bool Straw,
        List<KilnOutRow>? Items, long Shards, long Sold);

    sealed record KilnRow(
        List<string>? Batch = null, string? Style = null, string? Tech = null, int Beauty = 0,
        int PaintSeed = 0, DateTimeOffset PaintAt = default,
        DateTimeOffset LitAt = default, bool Helper = false, bool Straw = false, int FireSeed = 0,
        DateTimeOffset CoolUntil = default, int StrawStock = 0, KilnLastRow? Last = null, int Batches = 0);

    /// <summary>Сирці в горні (ключі виробів).</summary>
    readonly List<string> _kiln = [];
    string _kilnStyle = "";
    string _kilnTech = "";
    int _kilnBeauty;
    /// <summary>Зерно виданого візерунка; 0 — розпис не почато або вже здано.</summary>
    int _paintSeed;
    DateTimeOffset _paintAt;
    /// <summary>Коли розпалено; default — горно не палає.</summary>
    DateTimeOffset _litAt;
    bool _litHelper, _litStraw;
    int _fireSeed;
    DateTimeOffset _coolUntil;
    int _straw;
    KilnLastRow? _kilnLast;
    int _kilnBatches;

    /// <summary>Місця горна: піч дає до стелі, ранг майстра цеху — ще два понад неї (ClickerGuild.cs).</summary>
    internal int KilnSlots => Math.Min(KilnSlotsMax, KilnSlotsBase + Level("kiln") / KilnPerLevel) + Math.Max(0, GuildKilnSlots);

    bool TechOpen(ClickerTechnique t) => t.Fired <= 0 || FiredTotal >= t.Fired || (t.Home.Length > 0 && _styles.Contains(t.Home));

    long StrawPrice => Math.Max(10, Mul(ItemValue("pot", "", 1), StrawPots));

    string KilnState(DateTimeOffset now) =>
        _litAt != default ? "burning" : now < _coolUntil ? "cooling" : _kiln.Count > 0 ? "loaded" : "cold";

    // ---------- дії ----------

    ActResult? ActKiln(string action, JsonElement payload)
    {
        if (action != "kiln") return null;
        var now = Ctx.Clock.UtcNow;
        return Str(payload, "op") switch
        {
            "load" => KilnLoad(payload, now),
            "light" => KilnLight(payload, now),
            "open" => KilnOpen(payload, now),
            "paint" => KilnPaintStart(payload, now),
            "decor" => KilnDecor(payload, now),
            "straw" => KilnStraw(payload),
            _ => ActResult.Fail("Біля горна так не роблять"),
        };
    }

    /// <summary>Скласти сухі сирці в горно: <c>{ n }</c> — скільки (типово — скільки влізе). Гаряче горно теж можна завантажувати.</summary>
    ActResult KilnLoad(JsonElement payload, DateTimeOffset now)
    {
        if (_litAt != default) return ActResult.Fail("Горно палає — спершу відкрий його");
        var free = KilnSlots - _kiln.Count;
        if (free <= 0) return ActResult.Fail($"Горно повне: {_kiln.Count} з {KilnSlots}");
        var want = (int)Math.Clamp(Num(payload, "n") ?? free, 1, free);
        var dry = TakeDry(now, want);
        if (dry.Count == 0)
            return ActResult.Fail(_rack.Count > 0 ? "Сирці ще мокрі — зачекай, поки висохнуть" : "Сушарня порожня — спершу виліпи щось на колі");
        _kiln.AddRange(dry.Select(r => r.Ware));
        return ActResult.Accept($"🧱 У горно: {dry.Count} {WaresWord(dry.Count)} · {_kiln.Count} з {KilnSlots}");
    }

    /// <summary>
    /// Розпалити: <c>{ helper, straw }</c>. Порожнє горно само бере сухі з сушарні. Підмайстер палить без мінігри й сам
    /// відкриває горно за 30 с (якість 1, без тріщин); вручну — таймлайн іде на <c>open</c>. Солома — лише для ручного.
    /// </summary>
    ActResult KilnLight(JsonElement payload, DateTimeOffset now)
    {
        if (_litAt != default) return ActResult.Fail("Горно вже палає");
        if (now < _coolUntil) return ActResult.Fail($"Горно ще гаряче — холоне {Wait(_coolUntil - now)}");
        var helper = Flag(payload, "helper");
        var straw = !helper && Flag(payload, "straw");
        if (straw && _straw <= 0) return ActResult.Fail("Соломи нема — купи в'язку або пали без неї");
        if (_kiln.Count == 0)
        {
            var dry = TakeDry(now, KilnSlots);
            if (dry.Count == 0)
                return ActResult.Fail(_rack.Count > 0 ? "Горно порожнє, а сирці ще мокрі — зачекай" : "Горно порожнє — спершу виліпи й висуши щось");
            _kiln.AddRange(dry.Select(r => r.Ware));
        }
        if (straw) _straw--;
        _litAt = now;
        _litHelper = helper;
        _litStraw = straw;
        _fireSeed = Ctx.Rng.Next(1, int.MaxValue);
        _paintSeed = 0;                                   // недомальований розпис лягає як є
        return ActResult.Accept(helper
            ? $"🔥 Підмайстер розпалив горно: {_kiln.Count} {WaresWord(_kiln.Count)} — відкриє сам за {KilnBurn.TotalSeconds:0} с"
            : "🔥 Горно розпалено — тримай жар у зеленій смузі!");
    }

    /// <summary>
    /// Відкрити горно після ручного обпалу: <c>{ t: [[мс, дія], …] }</c> — мітки від розпалу, строго зростають, у межах
    /// 30 с, не більше 80; дія 0 — поліно, 1 — відкрити заслінку, 2 — прикрити. Сервер проганяє модель сам. Око майстра:
    /// якщо хоче глянути — горно лишається палати (нічого не губиться), таймлайн клієнт пришле ще раз.
    /// </summary>
    ActResult KilnOpen(JsonElement payload, DateTimeOffset now)
    {
        if (_litAt == default) return ActResult.Fail("Горно не палає — нема чого відкривати");
        if (_litHelper) return ActResult.Fail("Горно палить підмайстер — сам і відкриє");
        if (Timeline(payload) is not { } acts) return ActResult.Fail("Записи палія зіпсовані — оновися й відкрий ще раз");
        if (acts.Count > KilnHeat.MaxActs) return ActResult.Fail($"Забагато дій: не більше {KilnHeat.MaxActs} за обпал");
        for (var i = 0; i < acts.Count; i++)
        {
            if (acts[i].Ms < 0 || acts[i].Ms >= KilnHeat.Steps * KilnHeat.StepMs || acts[i].Act is < 0 or > 2)
                return ActResult.Fail("Записи палія поза обпалом — оновися й відкрий ще раз");
            if (i > 0 && acts[i].Ms <= acts[i - 1].Ms) return ActResult.Fail("Записи палія переплутані — оновися й відкрий ще раз");
        }
        var left = _litAt + KilnBurn - KilnGrace - now;
        if (left > TimeSpan.Zero) return ActResult.Fail($"Ще палає: {Math.Ceiling(left.TotalSeconds):0} с");
        if (GuardGate(now) is { } gate) return gate;

        var res = KilnHeat.Run(_fireSeed, acts);
        GuardSpend(KilnGuardWeight);
        return ActResult.Accept(KilnFinish(now, res.Heat, res.Over, manual: true));
    }

    /// <summary>
    /// Розпис партії: <c>{ style, tech }</c>. Без техніки — лише обрати розпис (краса 0). З технікою — видати новий
    /// візерунок: клієнт покаже мінігру й пришле траєкторію в <c>decor</c>. Нова спроба скидає красу попередньої.
    /// </summary>
    ActResult KilnPaintStart(JsonElement payload, DateTimeOffset now)
    {
        if (_litAt != default) return ActResult.Fail("Розписують до обпалу — горно вже палає");
        var style = Str(payload, "style");
        if (style.Length > 0 && !_styles.Contains(style))
            return ActResult.Fail(Styles.Any(s => s.Key == style) ? "Цього розпису ще нема в колекції" : "Такого розпису нема");
        var key = Str(payload, "tech");
        if (key.Length == 0)
        {
            if (style == _kilnStyle && _paintSeed == 0) return ActResult.Done;
            _kilnStyle = style;
            _kilnTech = "";
            _kilnBeauty = 0;
            _paintSeed = 0;
            return ActResult.Done;
        }
        if (Techniques.FirstOrDefault(t => t.Key == key) is not { } tech) return ActResult.Fail("Такої техніки розпису нема");
        if (!TechOpen(tech)) return ActResult.Fail($"{tech.Name} відкриється {tech.Unlock}");
        _kilnStyle = style;
        _kilnTech = tech.Key;
        _kilnBeauty = 0;
        _paintSeed = Ctx.Rng.Next(1, int.MaxValue);
        _paintAt = now;
        return ActResult.Done;
    }

    /// <summary>Траєкторія розпису: <c>{ path: [[мс, x, y, 1?], …] }</c>. Сервер перевіряє руку й рахує красу сам.</summary>
    ActResult KilnDecor(JsonElement payload, DateTimeOffset now)
    {
        if (_litAt != default) return ActResult.Fail("Розписують до обпалу — горно вже палає");
        if (_paintSeed == 0 || Techniques.FirstOrDefault(t => t.Key == _kilnTech) is not { } tech)
            return ActResult.Fail("Спершу обери техніку розпису");
        if (now - _paintAt > PatternLife)
        {
            _paintSeed = 0;
            return ActResult.Fail("Візерунок підсох — почни розпис знову");
        }
        if (KilnPaint.Parse(payload) is not { } pts) return ActResult.Fail("Слід пензля прийшов зіпсований — спробуй ще раз");
        if (KilnPaint.Implausible(pts) is { } why) return ActResult.Fail(why);
        // Траєкторію на п'ять секунд не намалюєш за секунду після того, як дістав візерунок.
        if ((now - _paintAt).TotalMilliseconds + 1000 < pts[^1].Ms - pts[0].Ms) return ActResult.Fail("Так швидко не малюють — спробуй ще раз");

        var beauty = KilnPaint.Beauty(tech.Key, _paintSeed, pts);
        var home = tech.Home.Length > 0 && tech.Home == _kilnStyle;
        if (home) beauty = Math.Min(100, beauty + HomeBonus);
        _kilnBeauty = beauty;
        _paintSeed = 0;
        return ActResult.Accept($"🎨 {tech.Name}: краса {beauty}" + (home ? " (рідний осередок +10)" : ""));
    }

    /// <summary>Солома: <c>{ n }</c> в'язок, скільки влізе в клуню й у глеки.</summary>
    ActResult KilnStraw(JsonElement payload)
    {
        var room = StrawMax - _straw;
        if (room <= 0) return ActResult.Fail($"Клуня повна: {StrawMax} в'язок");
        var raw = Num(payload, "n");
        if (raw is <= 0) return ActResult.Fail("Скільки в'язок — хоч одну");
        var want = (int)Math.Clamp(raw ?? 1, 1, room);
        var price = StrawPrice;
        var can = (int)Math.Min(want, _pots / price);
        if (can <= 0) return ActResult.Fail($"Бракує глеків: в'язка коштує {Short(price)}");
        _pots -= Mul(price, can);
        _straw += can;
        return ActResult.Accept($"🌾 Солома: +{can} — тепер {_straw} {Plural(_straw, "в'язка", "в'язки", "в'язок")}");
    }

    /// <summary>
    /// Горно відкрите: кожен виріб — тріщина (лише вручну, від перегріву; солома ділить шанс на чотири) або якість
    /// (<see cref="KilnHeat.Quality"/>). Цілі — у комору (<see cref="PutItems"/>) і в лічильник (<see cref="AddFired"/>);
    /// тріснуті — черепками на засипку. Горно холоне <see cref="KilnCool"/> від <paramref name="at"/>.
    /// </summary>
    string KilnFinish(DateTimeOffset at, double heat, double over, bool manual)
    {
        var style = _kilnStyle.Length > 0 && _styles.Contains(_kilnStyle) ? _kilnStyle : "";
        var beauty = manual ? _kilnBeauty : 0;
        var crack = manual ? KilnHeat.CrackChance(over) * (_litStraw ? StrawCrack : 1) : 0;
        var outs = new List<KilnOutRow>(_kiln.Count);
        foreach (var ware in _kiln)
        {
            if (WareOf(ware) is null) continue;
            if (crack > 0 && Ctx.Rng.NextDouble() < crack) { outs.Add(new KilnOutRow(ware, 0)); continue; }
            outs.Add(new KilnOutRow(ware, manual ? KilnHeat.Quality(heat, beauty, Ctx.Rng.NextDouble()) : 1));
        }
        long sold = 0, shards = 0;
        foreach (var grp in outs.Where(o => o.Q > 0).GroupBy(o => (o.Ware, o.Q)))
        {
            var n = grp.Count();
            sold = Sum(sold, PutItems(grp.Key.Ware, style, grp.Key.Q, n));
            AddFired(new ItemInfo(grp.Key.Ware, style, grp.Key.Q), n);
        }
        foreach (var o in outs.Where(o => o.Q == 0))
            shards = Sum(shards, ToLong(ItemValue(o.Ware, "", 1) * ShardShare));
        Add(shards);

        _kilnLast = new KilnLastRow(at, !manual, (int)Math.Round(heat * 100), (int)Math.Round(over), beauty, style, _litStraw, outs, shards, sold);
        _kilnBatches++;
        var whole = outs.Count(o => o.Q > 0);
        if (manual && outs.Count >= PerfectMin && outs.All(o => o.Q == 3)) Ctx.Award(0, 0, "ach:potter-kiln-perfect");

        _kiln.Clear();
        _litAt = default;
        _litHelper = false;
        _litStraw = false;
        _fireSeed = 0;
        _kilnTech = "";
        _kilnBeauty = 0;
        _paintSeed = 0;
        _coolUntil = at + KilnCool;

        var parts = new List<string>();
        void Part(int q, string one, string few, string many)
        {
            var c = outs.Count(o => o.Q == q);
            if (c > 0) parts.Add($"{c} {Plural(c, one, few, many)}");
        }
        Part(3, "дзвінкий", "дзвінкі", "дзвінких");
        Part(2, "добрий", "добрі", "добрих");
        Part(1, "звичайний", "звичайні", "звичайних");
        Part(0, "тріснув", "тріснули", "тріснуло");
        var text = (manual ? "🔥 Горно відкрите: " : "🔥 Підмайстер відкрив горно: ") + string.Join(" · ", parts);
        if (sold > 0) text += $" · комора повна, на базар +{Short(sold)}";
        if (!manual) AwayNote($"🔥 Підмайстер відкрив горно: {whole} {WaresWord(whole)} у коморі");
        return text;
    }

    static List<(int Ms, int Act)>? Timeline(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<(int, int)>();
        foreach (var e in t.EnumerateArray())
        {
            if (list.Count > KilnHeat.MaxActs) break;                     // далі однаково відмова — не розбираємо тисячі
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2) return null;
            var ms = e[0];
            var act = e[1];
            if (ms.ValueKind != JsonValueKind.Number || !ms.TryGetInt32(out var m)) return null;
            if (act.ValueKind != JsonValueKind.Number || !act.TryGetInt32(out var a)) return null;
            list.Add((m, a));
        }
        return list;
    }

    static bool Flag(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ---------- гачки ----------

    /// <summary>Підмайстер-палій відкриває своє горно сам; покинуте ручне горно за чверть години відкриває теж він (якість 1).</summary>
    void SyncKiln(DateTimeOffset now, TimeSpan paid)
    {
        if (_litAt != default)
        {
            var end = _litAt + KilnBurn;
            if (_litHelper && now >= end) KilnFinish(end, 0, 0, manual: false);
            else if (!_litHelper && now >= end + KilnAbandon) KilnFinish(end + KilnAbandon, 0, 0, manual: false);
        }
        AutoKiln(now);
    }

    /// <summary>
    /// Автогорно челядника цеху: коли горно холодне й порожнє, а сушарня повна й уся суха — підмайстер сам розпалює
    /// (якість 1, без тріщин, як «Хай підмайстер палить»). Лише повна суха сушарня: гравцеві, що палить сам, автогорно
    /// не забирає сирців із-під рук, а за довгий простій крутить по одному обпалу на синхронізацію.
    /// </summary>
    void AutoKiln(DateTimeOffset now)
    {
        if (!GuildAutoKiln || _litAt != default || now < _coolUntil || _kiln.Count > 0) return;
        if (_rack.Count < RackSize || _rack.Any(r => r.DryAt > now)) return;
        var dry = TakeDry(now, KilnSlots);
        if (dry.Count == 0) return;
        _kiln.AddRange(dry.Select(r => r.Ware));
        _litAt = now;
        _litHelper = true;
        _litStraw = false;
        _fireSeed = Ctx.Rng.Next(1, int.MaxValue);
        _paintSeed = 0;
        AwayNote($"🔥 Автогорно: підмайстри самі розпалили {dry.Count} {WaresWord(dry.Count)}");
    }

    void ResetKiln(DateTimeOffset now)
    {
        _kiln.Clear();
        _kilnStyle = "";
        _kilnTech = "";
        _kilnBeauty = 0;
        _paintSeed = 0;
        _paintAt = default;
        _litAt = default;
        _litHelper = false;
        _litStraw = false;
        _fireSeed = 0;
        _coolUntil = default;
        _straw = 0;
        _kilnLast = null;
        _kilnBatches = 0;
    }

    /// <summary>Обпал-престиж: партія в горні (і та, що палає) згорає разом із соломою й недомальованим розписом.</summary>
    void FireKiln(DateTimeOffset now)
    {
        _kiln.Clear();
        _kilnTech = "";
        _kilnBeauty = 0;
        _paintSeed = 0;
        _litAt = default;
        _litHelper = false;
        _litStraw = false;
        _fireSeed = 0;
        _coolUntil = default;
        _straw = 0;
        _kilnLast = null;
    }

    KilnRow? SaveKiln() => new([.. _kiln], _kilnStyle, _kilnTech, _kilnBeauty, _paintSeed, _paintAt, _litAt, _litHelper, _litStraw,
        _fireSeed, _coolUntil, _straw, _kilnLast, _kilnBatches);

    void LoadKiln(KilnRow? row)
    {
        ResetKiln(Ctx.Clock.UtcNow);
        if (row is null) return;
        foreach (var w in row.Batch ?? [])
            if (w is not null && WareOf(w) is not null && _kiln.Count < KilnSlotsMax + 8) _kiln.Add(w);
        _kilnStyle = row.Style is { } s && _styles.Contains(s) ? s : "";
        _kilnTech = row.Tech is { } t && Techniques.Any(x => x.Key == t) ? t : "";
        _kilnBeauty = Math.Clamp(row.Beauty, 0, 100);
        _paintSeed = _kilnTech.Length > 0 ? Math.Max(0, row.PaintSeed) : 0;
        _paintAt = row.PaintAt;
        _straw = Math.Clamp(row.StrawStock, 0, StrawMax);
        _coolUntil = row.CoolUntil;
        _kilnBatches = Math.Max(0, row.Batches);
        if (row.LitAt != default && _kiln.Count > 0 && row.FireSeed > 0)
        {
            _litAt = row.LitAt;
            _litHelper = row.Helper;
            _litStraw = row.Straw && !row.Helper;
            _fireSeed = row.FireSeed;
        }
        if (row.Last is { } l)
            _kilnLast = l with
            {
                Items = (l.Items ?? []).Where(o => o is not null && WareOf(o.Ware) is not null && o.Q is >= 0 and <= 3).Take(KilnSlotsMax + 8).ToList(),
                Style = l.Style is { } ls && Styles.Any(x => x.Key == ls) ? ls : "",
                Heat = Math.Clamp(l.Heat, 0, 100), Beauty = Math.Clamp(l.Beauty, 0, 100),
            };
    }

    object? ViewKiln(DateTimeOffset now)
    {
        var burning = _litAt != default;
        return new
        {
            state = KilnState(now),
            slots = KilnSlots,
            batch = _kiln,
            dry = _rack.Count(r => r.DryAt <= now),
            straw = _straw,
            strawPrice = StrawPrice,
            style = _kilnStyle,
            tech = _kilnTech,
            beauty = _kilnBeauty,
            techs = Techniques.Where(TechOpen).Select(t => t.Key),
            pattern = _paintSeed != 0 && !burning && now - _paintAt <= PatternLife ? PatternView() : null,
            litAt = burning ? _litAt : (DateTimeOffset?)null,
            helper = _litHelper,
            strawOn = _litStraw,
            // Зерно поривів і полін — клієнтові, щоб його модель жару збігалась із серверною крок у крок.
            seed = burning && !_litHelper ? _fireSeed : 0,
            coolUntil = _coolUntil > now ? _coolUntil : (DateTimeOffset?)null,
            batches = _kilnBatches,
            last = _kilnLast is { } l
                ? new
                {
                    at = l.At, helper = l.Helper, heat = l.Heat, over = l.Over, beauty = l.Beauty, style = l.Style ?? "", straw = l.Straw,
                    items = (l.Items ?? []).Select(o => new object[] { o.Ware, o.Q }), shards = l.Shards, sold = l.Sold,
                }
                : null,
        };
    }

    object PatternView()
    {
        object p = KilnPaint.Pattern(_kilnTech, _paintSeed) switch
        {
            KilnPaint.Rizh z => new { r0 = z.R0, amp = z.Amp, k = z.K, phase = z.Phase, period = z.Period, dir = z.Dir },
            KilnPaint.Ryt z => new { r0 = z.R0, amp = z.Amp, k = z.K, phase = z.Phase },
            KilnPaint.Flyand z => new { marks = z.Marks, bands = z.Bands },
            KilnPaint.Marble z => new { drops = z.Drops },
            KilnPaint.Losk z => (object)new { stripes = z.Stripes, top = z.Top, bottom = z.Bottom },
            _ => new { },
        };
        return new { tech = _kilnTech, at = _paintAt, shape = p };
    }

    object? CatalogKiln() => new
    {
        burnMs = KilnBurn.TotalMilliseconds,
        coolMs = KilnCool.TotalMilliseconds,
        graceMs = KilnGrace.TotalMilliseconds,
        patternMs = PatternLife.TotalMilliseconds,
        slotsBase = KilnSlotsBase, slotsMax = KilnSlotsMax, perLevel = KilnPerLevel,
        strawMax = StrawMax, strawCrack = StrawCrack, shardShare = ShardShare, homeBonus = HomeBonus, perfectMin = PerfectMin,
        model = new
        {
            stepMs = KilnHeat.StepMs, steps = KilnHeat.Steps, warm = KilnHeat.WarmSteps, maxActs = KilnHeat.MaxActs,
            amb = KilnHeat.Amb, fuel0 = KilnHeat.Fuel0, log = KilnHeat.Log, fuelMax = KilnHeat.FuelMax, chill = KilnHeat.Chill,
            burn = new[] { KilnHeat.BurnClosed, KilnHeat.BurnOpen }, heat = new[] { KilnHeat.HeatClosed, KilnHeat.HeatOpen },
            loss = new[] { KilnHeat.LossClosed, KilnHeat.LossOpen }, gust = KilnHeat.Gust,
            lo = KilnHeat.Lo, hi = KilnHeat.Hi, hi0 = KilnHeat.Hi0, softUnder = KilnHeat.SoftUnder, softOver = KilnHeat.SoftOver,
            crackFree = KilnHeat.CrackFree, crackScale = KilnHeat.CrackScale, crackMax = KilnHeat.CrackMax,
        },
        techs = Techniques.Select(t => new { key = t.Key, name = t.Name, home = t.Home, desc = t.Desc, unlock = t.Unlock }),
    };

    double KilnAllMult => 1;

    int KilnRackBonus() => 0;
}
