using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Що стоїть на полиці картинки-перевірки. Шукати треба лише глечики.</summary>
public enum ShelfThing { Jug, Pot, Bowl, Plate, Shard }

/// <summary>
/// Одна річ на полиці: центр і розмір у пікселях картинки, поворот у радіанах, колір із палітри, розпис
/// (0 — без нього, 1 — смуга, 2 — цятки), дзеркало (ручка глечика ліворуч) і зерно для форми черепка.
/// </summary>
public sealed record ShelfItem(ShelfThing Thing, double X, double Y, double Size, double Angle, int Color, int Band, bool Flip, int Form);

/// <summary>
/// Картинка для «Ока майстра»: полиця з глечиками, горщиками, мисками, тарілками й черепками, намальована
/// сервером у PNG. У PNG нема ні DOM, ні підписів, ні координат — скрипт, який читає сторінку, не знає, де
/// там глечики, а мишачий софт, що клацає в одну точку, не влучить у жоден.
///
/// Уся полиця — і речі, і шум — виводиться з 256-бітного ключа (<see cref="KeySize"/>), що живе лише на сервері.
/// Код публічний, тож ключ мусить бути таким, щоб його не перебрати: з <c>new Random(int)</c> 31 біт зерна
/// перебиралися за кілька хвилин на звичайному ПК (досить звірити кілька перших чисел із картинкою), і скрипт
/// відповідав би без жодного розпізнавання.
///
/// Малюємо самі, без System.Drawing: він є лише на Windows, а тести ганяє ubuntu. Без згладжування навмисно:
/// так кольорів мало, палітровий PNG важить кілобайти, а не сотні.
/// </summary>
public static class ClickerPicture
{
    public const int Width = 400, Height = 250;
    public const int MinJugs = 2, MaxJugs = 4;
    /// <summary>Ключ полиці в байтах: 256 біт.</summary>
    public const int KeySize = 32;

    /// <summary>Новий ключ полиці з криптографічного генератора.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Кольори глини — спільні для всіх речей, щоб колір нічого не підказував.</summary>
    static readonly (int R, int G, int B)[] Clay =
    [
        (0xb8, 0x5c, 0x32), (0xc9, 0x8a, 0x4b), (0x8e, 0x4a, 0x2b), (0x6d, 0x6a, 0x72),
        (0xd9, 0xa4, 0x5a), (0x9c, 0x6b, 0x3e), (0x3f, 0x5f, 0x8a),
    ];
    static readonly (int R, int G, int B)[] Decor = [(0xf3, 0xe6, 0xc8), (0x2a, 0x1c, 0x14), (0xc6, 0x2f, 0x25), (0x3d, 0x7a, 0x3a)];
    static readonly double[] Shade = [1.12, 0.98, 0.84, 0.7];
    static readonly (int R, int G, int B) Wall = (0xec, 0xdd, 0xbf), WallDark = (0xdc, 0xc9, 0xa4), Plank = (0x9a, 0x6a, 0x3c), PlankDark = (0x6e, 0x48, 0x26), Ink = (0x3a, 0x26, 0x18);

    // ---------- сцена ----------

    /// <summary>Сцена за ключем: однаковий ключ — однакова полиця (для перевірки відповіді й для кешу PNG).</summary>
    public static IReadOnlyList<ShelfItem> Scene(byte[] key)
    {
        var rng = new ShelfRandom(key, "scene");
        var items = new List<ShelfItem>();
        var jugs = rng.Next(MinJugs, MaxJugs + 1);
        var decoys = rng.Next(5, 8);

        // Спершу глечики: їх мусить бути рівно стільки, скільки назвемо гравцеві. Приманки — скільки влізе.
        for (var i = 0; i < jugs; i++)
            if (!Place(rng, items, ShelfThing.Jug)) break;
        // Горщик хоч один: він такий самий високий, як глечик, тож за пропорціями їх не розрізнити.
        ShelfThing[] others = [ShelfThing.Pot, ShelfThing.Bowl, ShelfThing.Plate, ShelfThing.Shard, ShelfThing.Pot];
        for (var i = 0; i < decoys; i++)
            Place(rng, items, i == 0 ? ShelfThing.Pot : others[rng.Next(others.Length)]);

        // Порядок малювання перемішуємо: інакше глечики завжди лежали б під приманками.
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }

    static bool Place(ShelfRandom rng, List<ShelfItem> items, ShelfThing thing)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var size = 46 + rng.NextDouble() * 16;
            var margin = size * 0.55;
            var x = margin + rng.NextDouble() * (Width - 2 * margin);
            var y = margin + rng.NextDouble() * (Height - 2 * margin);
            if (items.Any(o => Math.Pow(o.X - x, 2) + Math.Pow(o.Y - y, 2) < Math.Pow((o.Size + size) * 0.5, 2))) continue;
            items.Add(new ShelfItem(thing, x, y, size, (rng.NextDouble() - 0.5) * 0.6, rng.Next(Clay.Length),
                rng.Next(3), rng.Next(2) == 0, rng.Next()));
            return true;
        }
        return false;
    }

    public static int Jugs(IReadOnlyList<ShelfItem> scene) => scene.Count(i => i.Thing == ShelfThing.Jug);

    /// <summary>
    /// Потік чисел із ключа: SHA-256(ключ ‖ мітка ‖ лічильник), по чотири байти на число. Мітка розводить сцену й
    /// шум, щоб вони не ділили одних і тих самих чисел.
    /// </summary>
    sealed class ShelfRandom(byte[] key, string label)
    {
        readonly byte[] _input = [.. key, .. Encoding.UTF8.GetBytes(label), 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] _block = [];
        int _at;
        ulong _counter;

        uint NextUInt()
        {
            if (_at + 4 > _block.Length)
            {
                BitConverter.TryWriteBytes(_input.AsSpan(_input.Length - 8), _counter++);
                _block = SHA256.HashData(_input);
                _at = 0;
            }
            var v = BitConverter.ToUInt32(_block, _at);
            _at += 4;
            return v;
        }

        public double NextDouble() => NextUInt() / 4294967296.0;
        public int Next(int maxExclusive) => (int)(NextDouble() * maxExclusive);
        public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);
        public int Next() => (int)(NextUInt() >> 1);
    }

    // ---------- відповідь ----------

    /// <summary>
    /// Чи влучив гравець: торкань рівно стільки, скільки глечиків, і кожне — в інший глечик. Будь-яке торкання
    /// повз глечик (у приманку чи в стіну) — не та відповідь. Поле влучання трохи ширше за сам глечик: палець
    /// на телефоні товстіший за курсор.
    /// </summary>
    public static bool Solve(IReadOnlyList<ShelfItem> scene, IReadOnlyList<(double X, double Y)> taps)
    {
        var jugs = scene.Where(i => i.Thing == ShelfThing.Jug).ToList();
        if (taps.Count != jugs.Count) return false;
        var hit = new HashSet<int>();
        foreach (var (x, y) in taps)
        {
            // Два глечики поруч можуть накривати одне торкання полями влучання — тоді воно належить ближчому.
            var found = -1;
            var best = double.MaxValue;
            for (var j = 0; j < jugs.Count; j++)
            {
                if (hit.Contains(j) || !Near(jugs[j], x, y)) continue;
                var d = Math.Pow(jugs[j].X - x, 2) + Math.Pow(jugs[j].Y - y, 2);
                if (d < best) { best = d; found = j; }
            }
            if (found < 0) return false;
            hit.Add(found);
        }
        return hit.Count == jugs.Count;
    }

    /// <summary>Точка в полі влучання речі: у її власних координатах (без повороту) — прямокутник трохи більший за силует.</summary>
    public static bool Near(ShelfItem it, double x, double y)
    {
        var (u, v) = Local(it, x, y);
        return Math.Abs(u) <= 0.55 && Math.Abs(v) <= 0.6;
    }

    static (double U, double V) Local(ShelfItem it, double x, double y)
    {
        var dx = x - it.X;
        var dy = y - it.Y;
        var cos = Math.Cos(-it.Angle);
        var sin = Math.Sin(-it.Angle);
        var u = (dx * cos - dy * sin) / it.Size;
        var v = (dx * sin + dy * cos) / it.Size;
        return (it.Flip ? -u : u, v);
    }

    // ---------- форми ----------

    /// <summary>
    /// Що в точці (u, v) речі: −1 — повз, інакше номер відтінку 0…3 (об'єм «циліндра»), 4 — розпис, 5 — темне
    /// нутро (тарілка, миска, горло). Координати в розмірах речі, центр — (0, 0), v росте донизу.
    /// </summary>
    static int Probe(ShelfItem it, double u, double v, double[]? shard)
    {
        switch (it.Thing)
        {
            case ShelfThing.Jug:
            {
                // Вінця, вузька шийка, кругле пузо, денце й (не завжди) ручка — глечик.
                var body = Ell(u, v, 0, 0.16, 0.31, 0.3);
                var neck = Math.Abs(u) <= 0.1 && v >= -0.36 && v <= -0.04;
                var rim = Math.Abs(u) <= 0.16 && v >= -0.45 && v <= -0.35;
                var foot = Math.Abs(u) <= 0.17 && v >= 0.4 && v <= 0.48;
                var r = Math.Sqrt(Math.Pow(u - 0.1, 2) + Math.Pow(v + 0.12, 2));
                var handle = it.Form % 5 != 0 && u >= 0.1 && r >= 0.1 && r <= 0.17;
                if (rim && v <= -0.42) return 5;
                if (!(body || neck || rim || foot || handle)) return -1;
                if (body && Bands(it, v, 0.16, u)) return 4;
                return Tone(handle && !body && !neck ? 0.4 : body ? u / 0.31 : u / 0.16);
            }
            case ShelfThing.Pot:
            {
                // Горщик: широка горловина без шийки, стінки звужуються донизу.
                if (v < -0.4 || v > 0.46) return -1;
                if (v <= -0.31) return Math.Abs(u) <= 0.42 ? (v <= -0.37 ? 5 : Tone(u / 0.42)) : -1;
                var half = 0.37 - (v + 0.31) / 0.77 * 0.15;
                if (Math.Abs(u) > half) return -1;
                return Bands(it, v, 0.05, u) ? 4 : Tone(u / half);
            }
            case ShelfThing.Bowl:
            {
                // Миска: половина еліпса з вінцями й ніжкою.
                var bowl = v >= -0.05 && Ell(u, v, 0, -0.05, 0.5, 0.36);
                var rim = Math.Abs(u) <= 0.5 && v >= -0.11 && v <= -0.04;
                var foot = Math.Abs(u) <= 0.16 && v >= 0.3 && v <= 0.38;
                if (rim) return Ell(u, v, 0, -0.075, 0.46, 0.03) ? 5 : 1;
                if (!(bowl || foot)) return -1;
                return Bands(it, v, 0.08, u) && bowl ? 4 : Tone(u / 0.5);
            }
            case ShelfThing.Plate:
            {
                // Тарілка: пласкі еліпси, нутро темніше, розпис — кільцем.
                if (!Ell(u, v, 0, 0, 0.5, 0.17)) return -1;
                if (Ell(u, v, 0, 0, 0.3, 0.09)) return 5;
                return it.Band != 0 && Ell(u, v, 0, 0, 0.42, 0.13) && !Ell(u, v, 0, 0, 0.36, 0.11) ? 4 : Tone(u / 0.5);
            }
            default:
            {
                // Черепок: неправильний многокутник.
                if (shard is null || !InPolygon(shard, u, v)) return -1;
                return Tone(u / 0.45);
            }
        }
    }

    static bool Ell(double u, double v, double cu, double cv, double ru, double rv) =>
        Math.Pow((u - cu) / ru, 2) + Math.Pow((v - cv) / rv, 2) <= 1;

    static int Tone(double across)
    {
        var a = Math.Abs(across);
        return a < 0.3 ? 0 : a < 0.6 ? 1 : a < 0.85 ? 2 : 3;
    }

    static bool Bands(ShelfItem it, double v, double at, double u) => it.Band switch
    {
        1 => Math.Abs(v - at) <= 0.035,
        2 => Math.Abs(v - at) <= 0.04 && ((int)Math.Floor((u + 1) * 14)) % 2 == 0,
        _ => false,
    };

    static double[] ShardShape(int form)
    {
        // Тут System.Random можна: Form — окреме число з потоку ключа, і вгадане з обрису черепка нічого не
        // каже ні про ключ, ні про решту полиці.
        var rng = new Random(form);
        var n = rng.Next(4, 7);
        var pts = new double[n * 2];
        for (var i = 0; i < n; i++)
        {
            var angle = (i + rng.NextDouble() * 0.6) / n * Math.PI * 2;
            var r = 0.22 + rng.NextDouble() * 0.24;
            pts[i * 2] = Math.Cos(angle) * r;
            pts[i * 2 + 1] = Math.Sin(angle) * r * 0.8;
        }
        return pts;
    }

    static bool InPolygon(double[] pts, double u, double v)
    {
        var inside = false;
        var n = pts.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = pts[i * 2], yi = pts[i * 2 + 1], xj = pts[j * 2], yj = pts[j * 2 + 1];
            if ((yi > v) != (yj > v) && u < (xj - xi) * (v - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    // ---------- малювання ----------

    /// <summary>Картинка за зерном як PNG. Шум (цятки, риски, плями) — із того самого зерна.</summary>
    public static byte[] Png(byte[] key)
    {
        var scene = Scene(key);
        var rng = new ShelfRandom(key, "noise");
        var px = new int[Width * Height];

        // Стіна з полицями: дві дошки впоперек.
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                px[y * Width + x] = Rgb((x / 40 + y / 40) % 2 == 0 ? Wall : WallDark);
        foreach (var shelf in new[] { 0.37, 0.74 })
        {
            var top = (int)(Height * shelf + rng.Next(-8, 9));
            Fill(px, 0, top, Width, 7, Plank);
            Fill(px, 0, top + 7, Width, 2, PlankDark);
        }
        // Плями глини на стіні: схожі кольором на речі, тож колір не відділяє річ від тла.
        for (var i = 0; i < 14; i++)
        {
            var c = Clay[rng.Next(Clay.Length)];
            int cx = rng.Next(Width), cy = rng.Next(Height), r = rng.Next(3, 8);
            for (var y = Math.Max(0, cy - r); y < Math.Min(Height, cy + r); y++)
                for (var x = Math.Max(0, cx - r); x < Math.Min(Width, cx + r); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) px[y * Width + x] = Rgb(c);
        }

        foreach (var it in scene) Draw(px, it);

        // Цятки й тонкі риски поверх усього: і тла, і речей.
        for (var i = 0; i < 900; i++)
        {
            int x = rng.Next(Width), y = rng.Next(Height);
            px[y * Width + x] = Rgb(rng.Next(3) switch { 0 => Ink, 1 => Wall, _ => Plank });
        }
        for (var i = 0; i < 7; i++)
            Line(px, rng.Next(Width), rng.Next(Height), rng.Next(Width), rng.Next(Height), rng.Next(2) == 0 ? Ink : PlankDark);

        return Encode(px);
    }

    static void Draw(int[] px, ShelfItem it)
    {
        var shard = it.Thing == ShelfThing.Shard ? ShardShape(it.Form) : null;
        var reach = (int)Math.Ceiling(it.Size * 0.62);
        int x0 = Math.Max(0, (int)it.X - reach), x1 = Math.Min(Width - 1, (int)it.X + reach);
        int y0 = Math.Max(0, (int)it.Y - reach), y1 = Math.Min(Height - 1, (int)it.Y + reach);
        var body = Clay[it.Color];
        var decor = Decor[(it.Color + (it.Form & 0xff)) % Decor.Length];
        var step = 1.3 / it.Size;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var (u, v) = Local(it, x + 0.5, y + 0.5);
                var tone = Probe(it, u, v, shard);
                if (tone < 0) continue;
                // Обведення: сусід на півтора пікселя вже повз — отже, це край.
                var edge = Probe(it, u + step, v, shard) < 0 || Probe(it, u - step, v, shard) < 0
                    || Probe(it, u, v + step, shard) < 0 || Probe(it, u, v - step, shard) < 0;
                px[y * Width + x] = edge ? Rgb(Ink)
                    : tone == 4 ? Rgb(decor)
                    : tone == 5 ? Rgb(Mul(body, 0.5))
                    : Rgb(Mul(body, Shade[tone]));
            }
    }

    static (int R, int G, int B) Mul((int R, int G, int B) c, double k) =>
        (Math.Clamp((int)(c.R * k), 0, 255), Math.Clamp((int)(c.G * k), 0, 255), Math.Clamp((int)(c.B * k), 0, 255));

    static int Rgb((int R, int G, int B) c) => (c.R << 16) | (c.G << 8) | c.B;

    static void Fill(int[] px, int x0, int y0, int w, int h, (int R, int G, int B) c)
    {
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y0 + h); y++)
            for (var x = Math.Max(0, x0); x < Math.Min(Width, x0 + w); x++)
                px[y * Width + x] = Rgb(c);
    }

    static void Line(int[] px, int x0, int y0, int x1, int y1, (int R, int G, int B) c)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            if (x0 >= 0 && x0 < Width && y0 >= 0 && y0 < Height) px[y0 * Width + x0] = Rgb(c);
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ---------- PNG ----------

    /// <summary>Палітровий PNG, якщо кольорів до 256 (так завжди й буває), інакше повноколірний.</summary>
    static byte[] Encode(int[] px)
    {
        var palette = new Dictionary<int, int>();
        foreach (var c in px)
        {
            if (palette.ContainsKey(c)) continue;
            if (palette.Count == 256) { palette = null; break; }
            palette[c] = palette.Count;
        }
        var bpp = palette is null ? 3 : 1;
        var raw = new byte[Height * (Width * bpp + 1)];
        for (var y = 0; y < Height; y++)
        {
            var row = y * (Width * bpp + 1);
            raw[row] = 0;                                          // фільтр «без фільтра»
            for (var x = 0; x < Width; x++)
            {
                var c = px[y * Width + x];
                if (palette is not null) raw[row + 1 + x] = (byte)palette[c];
                else
                {
                    raw[row + 1 + x * 3] = (byte)(c >> 16);
                    raw[row + 2 + x * 3] = (byte)(c >> 8);
                    raw[row + 3 + x * 3] = (byte)c;
                }
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, Width);
        BigEndian(ihdr, 4, Height);
        ihdr[8] = 8;                                              // 8 біт
        ihdr[9] = (byte)(palette is null ? 2 : 3);                // RGB або палітра
        Chunk(png, "IHDR", ihdr);
        if (palette is not null)
        {
            var plte = new byte[palette.Count * 3];
            foreach (var (c, i) in palette)
            {
                plte[i * 3] = (byte)(c >> 16);
                plte[i * 3 + 1] = (byte)(c >> 8);
                plte[i * 3 + 2] = (byte)c;
            }
            Chunk(png, "PLTE", plte);
        }
        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
            Chunk(png, "IDAT", data.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, head.AsSpan(4, 4)), data) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        BigEndian(tail, 0, (int)crc);
        s.Write(tail);
    }

    static void BigEndian(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
