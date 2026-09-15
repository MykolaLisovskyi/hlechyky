using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Альбом майстра: сітка «виріб × розпис», майстерність, кахляна піч, трипільський музей.
/// Контракт гачків — docs/games/specs/clicker-v7.md §2.7. ЗАГЛУШКА: пакет B3 ще не реалізовано, усе нейтральне.
/// </summary>
public sealed partial class Clicker
{
    sealed record AlbumRow();

    AlbumRow? SaveAlbum() => null;

    void LoadAlbum(AlbumRow? row) { }

    void ResetAlbum(DateTimeOffset now) { }

    void FireAlbum(DateTimeOffset now) { }

    void SyncAlbum(DateTimeOffset now, TimeSpan paid) { }

    ActResult? ActAlbum(string action, JsonElement payload) => null;

    object? ViewAlbum(DateTimeOffset now) => null;

    object? CatalogAlbum() => null;

    double AlbumAllMult => 1;

    double AlbumWorkMult(string ware) => 1;

    double AlbumValueMult(string ware) => 1;

    void AlbumOnFormed(string ware) { }

    void AlbumOnFired(ItemInfo item, int n) { }
}
