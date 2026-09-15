using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Support;

/// <summary>
/// Руки для Гончарного кола: пачки кліків із почерком (<see cref="ClickerGuard"/>) і відповідь майстрові.
/// Людська пачка — нерівні проміжки, різне утримання й трохи різні точки на колі; роботова — рівний крок і
/// миттєве відпускання, як у мишачого софту.
/// </summary>
public static class PotterHands
{
    /// <summary>n людських кліків: проміжок 90–260 мс, утримання 45–140 мс, точка біля центру кола, миша.</summary>
    public static object Human(int n) => new
    {
        c = Enumerable.Range(0, n).Select(_ => new[]
        {
            Random.Shared.Next(90, 261), Random.Shared.Next(45, 141),
            Random.Shared.Next(380, 621), Random.Shared.Next(380, 621), (int)ClickerGuard.Source.Mouse,
        }).ToArray(),
    };

    /// <summary>n тапів по тачпаду: людський ритм, але «кнопку» система відпускає за 0–3 мс — як і мишачий софт.</summary>
    public static object Touchpad(int n) => new
    {
        c = Enumerable.Range(0, n).Select(_ => new[]
        {
            Random.Shared.Next(110, 320), Random.Shared.Next(0, 4), 512, 488, (int)ClickerGuard.Source.Mouse,
        }).ToArray(),
    };

    /// <summary>n кліків мишачого софту: сталий крок, стале утримання, одна й та сама точка.</summary>
    public static object Robot(int n, int dt = 100, int press = 0) => new
    {
        c = Enumerable.Range(0, n).Select(_ => new[] { dt, press, 500, 500, (int)ClickerGuard.Source.Mouse }).ToArray(),
    };

    /// <summary>Ключ полиці для тестів картинки: з числа, щоб полиці були відтворювані.</summary>
    public static byte[] Key(int n)
    {
        var key = new byte[ClickerPicture.KeySize];
        BitConverter.TryWriteBytes(key, n);
        return key;
    }

    /// <summary>Ключ полиці, що чекає відповіді, — зі збереження (у вид він не йде).</summary>
    public static byte[]? Shelf(RoomHarness h)
    {
        lock (h.Room.Sync)
        {
            var guard = JsonNode.Parse(h.Room.Game.Save()!)!["guard"];
            return guard?["shelf"] is JsonValue shelf ? Convert.FromBase64String(shelf.GetValue<string>()) : null;
        }
    }

    /// <summary>Торкання в центри всіх глечиків полиці.</summary>
    public static object RightTaps(byte[] shelf) => new
    {
        taps = ClickerPicture.Scene(shelf).Where(i => i.Thing == ShelfThing.Jug).Select(i => new[] { i.X, i.Y }).ToArray(),
    };

    /// <summary>Торкання в приманки: стільки ж, скільки глечиків, але жодне не в глечик.</summary>
    public static object WrongTaps(byte[] shelf)
    {
        var scene = ClickerPicture.Scene(shelf);
        var jugs = ClickerPicture.Jugs(scene);
        return new
        {
            taps = scene.Where(i => i.Thing != ShelfThing.Jug && !scene.Any(j => j.Thing == ShelfThing.Jug && ClickerPicture.Near(j, i.X, i.Y)))
                .Take(jugs).Select(i => new[] { i.X, i.Y })
                .Concat(Enumerable.Repeat(new[] { -50.0, -50.0 }, jugs)).Take(jugs).ToArray(),
        };
    }

    /// <summary>Відповісти майстрові правильно.</summary>
    public static ActResult Pass(RoomHarness h) =>
        h.Act(0, "answer", RightTaps(Shelf(h) ?? throw new InvalidOperationException("майстер ні про що не питає")));

    /// <summary>Відповісти неправильно.</summary>
    public static ActResult Miss(RoomHarness h) =>
        h.Act(0, "answer", WrongTaps(Shelf(h) ?? throw new InvalidOperationException("майстер ні про що не питає")));
}
