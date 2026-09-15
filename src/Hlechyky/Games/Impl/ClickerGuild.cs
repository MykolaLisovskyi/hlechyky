using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Цех: тижневий віз для всіх гончарів, дарунки, хата друга, похвала в Журнал, цехові ранги.
/// Контракт гачків — docs/games/specs/clicker-v7.md §2.7. ЗАГЛУШКА: пакет B5 ще не реалізовано, усе нейтральне.
/// </summary>
public sealed partial class Clicker
{
    sealed record GuildRow();

    GuildRow? SaveGuild() => null;

    void LoadGuild(GuildRow? row) { }

    void ResetGuild(DateTimeOffset now) { }

    void FireGuild(DateTimeOffset now) { }

    void SyncGuild(DateTimeOffset now, TimeSpan paid) { }

    ActResult? ActGuild(string action, JsonElement payload) => null;

    object? ViewGuild(DateTimeOffset now) => null;

    object? CatalogGuild() => null;

    double GuildAllMult => 1;

    void GuildOnFired(ItemInfo item, int n) { }

    bool GuildWareOpen(string ware) => false;
}
