using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Підключення «Своєї гри»: сховище пакетів, вбудовані пакети, API під <c>/api/games/svoya/…</c>. Кличеться двома
/// рядками з <see cref="GamesSetup"/>. Уся логіка — у <see cref="SvoyaPacks"/>, тут лише HTTP.
/// </summary>
public static class SvoyaSetup
{
    public static IServiceCollection AddSvoya(this IServiceCollection services)
    {
        services.AddOptions<SvoyaOptions>().BindConfiguration("Svoya");
        services.AddSingleton(sp => new SvoyaStore(sp.GetRequiredService<Db>()));
        services.AddSingleton(sp => new SvoyaBuiltin(
            Paths.Resolve(sp.GetRequiredService<IOptionsMonitor<SvoyaOptions>>().CurrentValue.BuiltinDir),
            sp.GetService<ILogger<SvoyaBuiltin>>()));
        services.AddSingleton(sp => new SvoyaFiles(Paths.Resolve(sp.GetRequiredService<IOptionsMonitor<SvoyaOptions>>().CurrentValue.MediaDir)));
        // голос ведучого: edge-tts у фоні, кеш cache/tts
        services.AddOptions<TtsOptions>().BindConfiguration("Tts");
        services.TryAddSingleton<ITtsEngine, EdgeTtsEngine>();
        services.AddSingleton<TtsService>();
        services.AddHostedService(sp => sp.GetRequiredService<TtsService>());
        services.AddSingleton<ISvoyaVoice, SvoyaVoice>();
        services.AddSingleton<SvoyaPacks>();
        services.AddSingleton<ISvoyaPackSource>(sp => sp.GetRequiredService<SvoyaPacks>());
        return services;
    }

    public sealed record HideRequest(bool Hidden);

    static SvoyaUser User(HttpContext c) => new(Auth.Nick(c), Auth.IsUser(c), Auth.IsAdmin(c));

    /// <summary>Форма відповіді, як у решти сайту: <c>{ ok, message }</c> плюс дані або список помилок.</summary>
    static IResult Reply(SvoyaReply r)
    {
        if (!r.Ok) return Results.BadRequest(new { ok = false, message = r.Message, errors = r.Errors ?? [] });
        return Results.Json(new { ok = true, message = r.Message, data = r.Data }, SvoyaPack.Json);
    }

    /// <summary>Тіло запиту як пакет. Порожнє тіло — null (для «новий з шаблону»); завелике чи криве — помилка.</summary>
    static async Task<(SvoyaPack? Pack, string? Error)> ReadPack(HttpContext c, int maxKb, CancellationToken ct)
    {
        var max = maxKb * 1024;
        if (c.Request.ContentLength > max) return (null, $"Пакет завеликий (понад {maxKb} КБ)");
        using var reader = new StreamReader(c.Request.Body, Encoding.UTF8);
        var buffer = new char[max + 1];
        var read = 0;
        while (read <= max)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0) break;
            read += n;
        }
        if (read > max) return (null, $"Пакет завеликий (понад {maxKb} КБ)");
        var text = new string(buffer, 0, read);
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        return SvoyaPack.Parse(text) is { } pack ? (pack, null) : (null, "Це не схоже на пакет «Своєї гри»");
    }

    public static WebApplication MapSvoya(this WebApplication app)
    {
        const string Root = "/api/games/svoya/packs";

        app.MapGet(Root, (HttpContext c, SvoyaPacks packs) => Results.Json(packs.List(User(c)), SvoyaPack.Json));

        app.MapGet(Root + "/{id}", (HttpContext c, string id, SvoyaPacks packs) => Reply(packs.Get(id, User(c))));

        app.MapPost(Root, async (HttpContext c, SvoyaPacks packs, IOptionsMonitor<SvoyaOptions> o, CancellationToken ct) =>
        {
            var (pack, error) = await ReadPack(c, o.CurrentValue.BodyMaxKb, ct);
            return error is not null ? Reply(SvoyaReply.Fail(error)) : Reply(packs.Create(User(c), pack));
        });

        app.MapPut(Root + "/{id}", async (HttpContext c, string id, SvoyaPacks packs, IOptionsMonitor<SvoyaOptions> o, CancellationToken ct) =>
        {
            var (pack, error) = await ReadPack(c, o.CurrentValue.BodyMaxKb, ct);
            if (error is not null) return Reply(SvoyaReply.Fail(error));
            return pack is null ? Reply(SvoyaReply.Fail("Порожній пакет")) : Reply(packs.Save(id, User(c), pack));
        });

        app.MapDelete(Root + "/{id}", (HttpContext c, string id, SvoyaPacks packs) => Reply(packs.Delete(id, User(c))));

        app.MapPost(Root + "/{id}/copy", (HttpContext c, string id, SvoyaPacks packs) => Reply(packs.Copy(id, User(c))));

        // «Озвучити»: поставити всі репліки пакета в чергу (POST) і дивитись, скільки вже готово (GET)
        app.MapGet(Root + "/{id}/tts", (HttpContext c, string id, SvoyaPacks packs) => Reply(packs.Voice(id, User(c), start: false)));
        app.MapPost(Root + "/{id}/tts", (HttpContext c, string id, SvoyaPacks packs) => Reply(packs.Voice(id, User(c), start: true)));

        SvoyaVoice.Map(app);                                  // /api/games/svoya/tts/<хеш>.mp3

        app.MapPost(Root + "/{id}/hide", (HttpContext c, string id, HideRequest req, SvoyaPacks packs) =>
            Reply(packs.Hide(id, User(c), req.Hidden)));

        return app;
    }
}
