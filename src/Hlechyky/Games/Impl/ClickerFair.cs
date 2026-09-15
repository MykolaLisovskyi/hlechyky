using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ярмарок і люди: замовлення з вимогами й репутація сіл, торг, гості, події з вибором, пори року, свята й погода.
/// Контракт гачків — docs/games/specs/clicker-v7.md §2.7. ЗАГЛУШКА: пакет B4 ще не реалізовано, усе нейтральне.
/// </summary>
public sealed partial class Clicker
{
    sealed record FairRow();

    FairRow? SaveFair() => null;

    void LoadFair(FairRow? row) { }

    void ResetFair(DateTimeOffset now) { }

    void FireFair(DateTimeOffset now) { }

    void SyncFair(DateTimeOffset now, TimeSpan paid) { }

    ActResult? ActFair(string action, JsonElement payload) => null;

    object? ViewFair(DateTimeOffset now) => null;

    object? CatalogFair() => null;

    double FairAllMult => 1;

    double FairWorkMult(string ware) => 1;

    double FairValueMult(string ware) => 1;

    double FairDryMult() => 1;
}
