using System.Text.Json.Nodes;

namespace Hlechyky;

public static class Endpoints
{
    public sealed record AddRequest(string? Input, SearchResult? Pick);
    public sealed record MoveRequest(int ToIndex);
    public sealed record NameRequest(string? Name);
    public sealed record TrackRequest(string? TrackId);
    public sealed record QueueAllRequest(bool Shuffle);

    static IResult Reply((bool Ok, string Message) r) =>
        r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { ok = false, message = r.Message });

    static IResult Fail(string message) => Results.BadRequest(new { ok = false, message });

    public static void MapHlechyky(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/me", (HttpContext c) => new { nick = Auth.Nick(c), role = Auth.Role(c) });

        api.MapGet("/state", (RadioEngine e, Db db) => new { state = e.Snapshot(), chat = db.RecentChat(100) });

        api.MapGet("/search", async (string? q, YtMusicClient ytm, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? [] : await ytm.SearchSongsAsync(q.Trim(), 12, ct));

        api.MapPost("/queue", async (HttpContext c, AddRequest req, RadioEngine e, CancellationToken ct) =>
            Reply(await e.AddAsync(Auth.Nick(c), Auth.IsAdmin(c), req.Input, req.Pick, ct)));

        api.MapPost("/queue/track/{trackId}", async (HttpContext c, string trackId, RadioEngine e, CancellationToken ct) =>
            Reply(await e.AddKnownAsync(trackId, Auth.Nick(c), Auth.IsAdmin(c), ct)));

        api.MapDelete("/queue/{itemId}", (HttpContext c, string itemId, RadioEngine e) =>
            Reply(e.Remove(itemId, Auth.Nick(c), Auth.IsAdmin(c))));

        api.MapPost("/queue/{itemId}/move", (HttpContext c, string itemId, MoveRequest req, RadioEngine e) =>
            Reply(e.Move(itemId, req.ToIndex, Auth.Nick(c), Auth.IsAdmin(c))));

        api.MapPost("/skip", async (HttpContext c, RadioEngine e) =>
            Reply(await e.SkipAsync(Auth.Nick(c))));

        api.MapPost("/like/{trackId}", (HttpContext c, string trackId, RadioEngine e) =>
        {
            var r = e.ToggleLike(trackId, Auth.Nick(c));
            return Results.Ok(new { ok = true, liked = r.Liked, count = r.Count, likers = r.Likers });
        });

        api.MapPost("/ban/{trackId}", async (HttpContext c, string trackId, RadioEngine e) =>
            Auth.IsAdmin(c) ? Reply(await e.BanAsync(trackId, Auth.Nick(c))) : Results.StatusCode(403));

        api.MapPost("/suggest/{itemId}/add", async (HttpContext c, string itemId, RadioEngine e, CancellationToken ct) =>
            Reply(await e.AddSuggestionAsync(itemId, Auth.Nick(c), Auth.IsAdmin(c), ct)));

        api.MapPost("/suggest/{itemId}/skip", (HttpContext c, string itemId, RadioEngine e) =>
            Reply(e.DismissSuggestion(itemId, Auth.Nick(c))));

        api.MapGet("/history", (int? n, Db db) => db.History(Math.Clamp(n ?? 50, 1, 500)));

        api.MapGet("/likes", (Db db) => db.LikedTracksDetailed(200).Select(x => new { track = x.Track, likers = x.Likers, lastLike = x.LastLike }));

        api.MapGet("/top", (int? days, Db db) => new { requesters = db.TopRequesters(Math.Clamp(days ?? 7, 1, 365)) });

        // ---- playlists: shared, anyone can add; only the creator or an admin can delete ----

        api.MapGet("/playlists", (Db db) => db.Playlists());

        api.MapPost("/playlists", (HttpContext c, NameRequest req, Db db) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length is 0 or > 40) return Fail("Назва від 1 до 40 символів");
            var id = db.CreatePlaylist(name, Auth.Nick(c));
            return Results.Ok(new { ok = true, id, message = $"Плейлист «{name}» створено" });
        });

        api.MapDelete("/playlists/{id:long}", (HttpContext c, long id, Db db) =>
        {
            var p = db.GetPlaylist(id);
            if (p is null) return Fail("Нема такого плейлиста");
            if (!Auth.IsAdmin(c) && !p.CreatedBy.Equals(Auth.Nick(c), StringComparison.OrdinalIgnoreCase)) return Fail("Видаляти може тільки той, хто створив");
            db.DeletePlaylist(id);
            return Results.Ok(new { ok = true, message = "Плейлист видалено" });
        });

        api.MapGet("/playlists/{id:long}", (long id, Db db) =>
        {
            var p = db.GetPlaylist(id);
            return p is null ? Results.NotFound() : Results.Ok(new { playlist = p, tracks = db.PlaylistTracks(id).Select(x => new { track = x.Track, addedBy = x.AddedBy }) });
        });

        api.MapPost("/playlists/{id:long}/tracks", (HttpContext c, long id, TrackRequest req, Db db) =>
        {
            if (db.GetPlaylist(id) is null) return Fail("Нема такого плейлиста");
            var t = string.IsNullOrEmpty(req.TrackId) ? null : db.GetTrack(req.TrackId);
            if (t is null) return Fail("Не знаю такого треку");
            return db.AddToPlaylist(id, t.Id, Auth.Nick(c))
                ? Results.Ok(new { ok = true, message = $"Додано в плейлист: {t.Label}" })
                : Fail("Уже є в цьому плейлисті");
        });

        api.MapDelete("/playlists/{id:long}/tracks/{trackId}", (long id, string trackId, Db db) =>
        {
            db.RemoveFromPlaylist(id, trackId);
            return Results.Ok(new { ok = true, message = "Прибрано з плейлиста" });
        });

        api.MapPost("/playlists/{id:long}/queue", async (HttpContext c, long id, QueueAllRequest? req, RadioEngine e, Db db, CancellationToken ct) =>
        {
            var p = db.GetPlaylist(id);
            if (p is null) return Fail("Нема такого плейлиста");
            var ids = db.PlaylistTracks(id).Select(x => x.Track.Id).ToList();
            if (ids.Count == 0) return Fail("Плейлист порожній");
            var n = await e.AddManyAsync(ids, Auth.Nick(c), Auth.IsAdmin(c), req?.Shuffle ?? true, ct);
            return Results.Ok(new { ok = true, count = n, message = n == 0 ? "Усе з цього плейлиста вже в черзі або грає" : $"Закинуто {n} з плейлиста «{p.Name}»" });
        });

        api.MapPost("/liq/track", async (HttpContext c, RadioEngine e) =>
        {
            var node = await JsonNode.ParseAsync(c.Request.Body);
            var dict = new Dictionary<string, string>();
            switch (node)
            {
                case JsonObject o:
                    foreach (var kv in o) dict[kv.Key] = kv.Value?.ToString() ?? "";
                    break;
                case JsonArray a:
                    foreach (var pair in a.OfType<JsonArray>())
                        if (pair.Count == 2) dict[pair[0]?.ToString() ?? ""] = pair[1]?.ToString() ?? "";
                    break;
            }
            await e.OnLiquidsoapTrackAsync(dict);
            return Results.Ok();
        });
    }
}
