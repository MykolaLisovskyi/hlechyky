using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Горно й розпис: висохлі вироби з сушарні — у горно (мінігра жару), розпис партії технікою-мінігрою, якість.
/// Контракт гачків — docs/games/specs/clicker-v7.md §2.7. ЗАГЛУШКА: пакет B2 ще не реалізовано, усе нейтральне.
/// </summary>
public sealed partial class Clicker
{
    sealed record KilnRow();

    KilnRow? SaveKiln() => null;

    void LoadKiln(KilnRow? row) { }

    void ResetKiln(DateTimeOffset now) { }

    void FireKiln(DateTimeOffset now) { }

    void SyncKiln(DateTimeOffset now, TimeSpan paid) { }

    ActResult? ActKiln(string action, JsonElement payload) => null;

    object? ViewKiln(DateTimeOffset now) => null;

    object? CatalogKiln() => null;

    double KilnAllMult => 1;

    int KilnRackBonus() => 0;
}
