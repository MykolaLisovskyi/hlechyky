using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Один малюнок на логічному полотні 1000 × 750 — спільне для ігор, де малюють (Піктіонарі, Зіпсований телефон).
/// Малюнок — список операцій, кожна — масив цілих: <c>[вид, штрих, колір, товщина, x0, y0, x1, y1, …]</c>;
/// вид 0 — лінія (шматок штриха), 1 — заливка (одна точка). Клієнт шле штрих шматками, тож один штрих — це
/// кілька операцій з однаковим номером; «скасувати» прибирає їх усі.
/// <para>
/// Усе, що приходить із дроту, тут перевіряється: колір і товщина в межах, координати обрізаються до полотна,
/// кількість операцій і точок обмежена, щоб один художник не роздув пам'ять і розсилку.
/// </para>
/// </summary>
public sealed class Sketch(int maxOps = Sketch.DefaultMaxOps, int maxPoints = Sketch.DefaultMaxPoints)
{
    public const int CanvasW = 1000, CanvasH = 750;
    /// <summary>Кольорів у палітрі клієнта (PALETTE у web/games/pictionary.js і telephone.js).</summary>
    public const int Colors = 20;
    public const int MinWidth = 1, MaxWidth = 60;
    /// <summary>Точок в одному шматку штриха (пар x,y). 8 КБ payload вистачає з запасом.</summary>
    public const int MaxChunkPoints = 300;
    public const int DefaultMaxOps = 6_000, DefaultMaxPoints = 120_000;

    public const string Crooked = "Кривий штрих";
    public const string Full = "Полотно переповнене — очисть його";

    readonly List<int[]> _ops = [];

    public int Count => _ops.Count;
    public int Points { get; private set; }

    /// <summary>Шматок лінії <c>{ s, c, w, p: [x, y, …] }</c>. null — додано, інакше текст відмови.</summary>
    public string? Line(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return Crooked;
        var stroke = Int(payload, "s", -1);
        var color = Int(payload, "c", -1);
        var width = Int(payload, "w", 0);
        if (stroke < 0 || color is < 0 or >= Colors || width is < MinWidth or > MaxWidth) return Crooked;
        if (!payload.TryGetProperty("p", out var p) || p.ValueKind != JsonValueKind.Array) return Crooked;

        var n = p.GetArrayLength();
        if (n < 2 || n % 2 != 0 || n / 2 > MaxChunkPoints) return Crooked;
        if (_ops.Count >= maxOps || Points + n / 2 > maxPoints) return Full;

        var op = new int[4 + n];
        op[0] = 0; op[1] = stroke; op[2] = color; op[3] = width;
        var k = 0;
        foreach (var v in p.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || double.IsNaN(d)) return Crooked;
            var limit = k % 2 == 0 ? CanvasW : CanvasH;
            op[4 + k] = (int)Math.Clamp(Math.Round(d), 0, limit);
            k++;
        }
        _ops.Add(op);
        Points += n / 2;
        return null;
    }

    /// <summary>Заливка <c>{ s, c, x, y }</c>. null — додано, інакше текст відмови.</summary>
    public string? Fill(JsonElement payload)
    {
        var stroke = Int(payload, "s", -1);
        var color = Int(payload, "c", -1);
        var x = Int(payload, "x", -1);
        var y = Int(payload, "y", -1);
        if (stroke < 0 || color is < 0 or >= Colors || x is < 0 or > CanvasW || y is < 0 or > CanvasH) return "Крива заливка";
        if (_ops.Count >= maxOps || Points + 1 > maxPoints) return Full;
        _ops.Add([1, stroke, color, 0, x, y]);
        Points++;
        return null;
    }

    /// <summary>Прибрати останній штрих цілком (усі його шматки). false — нічого було прибирати.</summary>
    public bool Undo()
    {
        if (_ops.Count == 0) return false;
        var stroke = _ops[^1][1];
        while (_ops.Count > 0 && _ops[^1][1] == stroke)
        {
            Points -= Math.Max(1, (_ops[^1].Length - 4) / 2);
            _ops.RemoveAt(_ops.Count - 1);
        }
        return true;
    }

    public void Clear()
    {
        _ops.Clear();
        Points = 0;
    }

    /// <summary>Копія операцій, починаючи з <paramref name="from"/> — те, що йде на дріт.</summary>
    public int[][] Ops(int from = 0) => [.. _ops.Skip(from).Select(o => (int[])o.Clone())];

    static int Int(JsonElement payload, string field, int fallback) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && !double.IsNaN(d) && Math.Abs(d) < int.MaxValue
            ? (int)Math.Round(d) : fallback;
}
