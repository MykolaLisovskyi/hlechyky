using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Hlechyky;

public static class Endpoints
{
    public sealed record AddRequest(string? Input, SearchResult? Pick);
    public sealed record MoveRequest(int ToIndex);
    public sealed record NameRequest(string? Name);
    public sealed record TrackRequest(string? TrackId);
    public sealed record SayRequest(string? Text);
    public sealed record QueueAllRequest(bool Shuffle);

    static IResult Reply((bool Ok, string Message) r) =>
        r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { ok = false, message = r.Message });

    static IResult Fail(string message) => Results.BadRequest(new { ok = false, message });

    public static void MapHlechyky(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/me", (HttpContext c, TrackBans bans) => new { nick = Auth.Nick(c), role = Auth.Role(c), banPrice = bans.BanPrice });

        api.MapGet("/state", (RadioEngine e, Db db) => new { state = e.Snapshot(), chat = db.RecentChat(100) });

        api.MapGet("/search", async (string? q, YtMusicClient ytm, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? [] : await ytm.SearchSongsAsync(q.Trim(), 12, ct));

        api.MapPost("/queue", async (HttpContext c, AddRequest req, RadioEngine e, CancellationToken ct) =>
            Reply(await e.AddAsync(Auth.Nick(c), Auth.IsAdmin(c), req.Input, req.Pick, ct)));

        api.MapPost("/queue/track/{trackId}", async (HttpContext c, string trackId, RadioEngine e, CancellationToken ct) =>
            Reply(await e.AddKnownAsync(trackId, Auth.Nick(c), Auth.IsAdmin(c), ct)));

        // Голосове: тіло запиту — сирий запис із мікрофона, ffmpeg робить із нього mp3 у кеші,
        // далі воно стає в чергу як звичайний трек (файл уже є, качати нема чого).
        api.MapPost("/voice", async (HttpContext c, RadioEngine e, VoiceService voice, CancellationToken ct) =>
        {
            if (!voice.Enabled) return Fail("Голосові вимкнені");
            if (c.Request.ContentLength > voice.MaxUploadBytes) return Fail($"Задовгий запис, ліміт {voice.MaxUploadBytes / (1024 * 1024)} МБ");
            TrackInfo track;
            string path;
            try { (track, path) = await voice.SaveAsync(c.Request.Body, Auth.Nick(c), ct); }
            catch (Exception ex) { return Fail("Не вийшло взяти голосове: " + ex.Message); }
            return Reply(e.AddVoice(track, path, Auth.Nick(c)));
        });

        api.MapGet("/voice/{name}", (string name, VoiceService voice) =>
        {
            var path = voice.FilePath(name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
            return path is null ? Results.NotFound() : Results.File(path, "audio/mpeg", enableRangeProcessing: true);
        });

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

        // ---- бан-лист: адмін — будь-що і безкоштовно, решта — трек в ефірі за черепки, викуп теж за черепки ----

        api.MapGet("/bans", (HttpContext c, TrackBans bans) => new
        {
            banPrice = bans.BanPrice,
            unbanPrice = bans.UnbanPrice,
            balance = bans.Balance(Auth.Nick(c)),
            items = bans.List().Select(b => new { track = b.Track, by = b.By, price = b.Price, createdAt = b.CreatedAt }),
        });

        api.MapPost("/ban/{trackId}", async (HttpContext c, string trackId, TrackBans bans) =>
            Reply(await bans.BanAsync(trackId, Auth.Nick(c), Auth.IsAdmin(c))));

        api.MapDelete("/ban/{trackId}", (HttpContext c, string trackId, TrackBans bans) =>
            Reply(bans.Unban(trackId, Auth.Nick(c), Auth.IsAdmin(c))));

        api.MapPost("/suggest/{itemId}/add", async (HttpContext c, string itemId, RadioEngine e, CancellationToken ct) =>
            Reply(await e.AddSuggestionAsync(itemId, Auth.Nick(c), Auth.IsAdmin(c), ct)));

        api.MapPost("/suggest/{itemId}/skip", (HttpContext c, string itemId, RadioEngine e) =>
            Reply(e.DismissSuggestion(itemId, Auth.Nick(c))));

        api.MapGet("/history", (int? n, Db db) => db.History(Math.Clamp(n ?? 50, 1, 500)));

        api.MapGet("/likes", (Db db) => db.LikedTracksDetailed(200).Select(x => new { track = x.Track, likers = x.Likers, lastLike = x.LastLike }));

        api.MapGet("/top", (int? days, Db db) => new { requesters = db.TopRequesters(Math.Clamp(days ?? 7, 1, 365)) });

        // Рейтинг треків: програвання, скільки дослуховують, хто слухав; заразом — скільки займає кеш
        api.MapGet("/rating", (int? days, string? sort, Db db, TrackCache cache) =>
        {
            var (bytes, files) = cache.Usage();
            return new
            {
                tracks = db.TrackRatings(Math.Clamp(days ?? 7, 1, 3650), sort ?? "plays", 100),
                cache = new { bytes, files, limitBytes = cache.LimitBytes },
            };
        });

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

        // Голос Дядька Глека ззовні: адмін (або мозок бота під адмінським ключем) каже щось у чат від його імені.
        api.MapPost("/dj/say", async (HttpContext c, SayRequest req, RadioEngine e) =>
        {
            if (!Auth.IsAdmin(c)) return Results.StatusCode(403);
            var text = (req.Text ?? "").Trim();
            if (text.Length == 0) return Fail("Порожнє повідомлення");
            if (text.Length > 500) text = text[..500];
            await e.SayAsync(text);
            return Results.Ok(new { ok = true });
        });

        // Автодеплой: сюди стукає GitHub, коли збірка на main позеленіла (Deploy.cs → deploy.ps1)
        api.MapPost("/github/deploy", (HttpContext c, IOptionsMonitor<DeployOptions> opt, ILoggerFactory lf) =>
            Deploy.HandleAsync(c, opt.CurrentValue, lf.CreateLogger("Hlechyky.Deploy")));
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
