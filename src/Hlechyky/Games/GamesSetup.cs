using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hlechyky.Games;

/// <summary>
/// Підключення ігрового каркаса (WP0): кімнати, реєстр, розсилка, тик. Program.cs кличе обидва методи;
/// їхній вміст — власність WP0. Сервіси економіки підключає EconomySetup.
/// </summary>
public static class GamesSetup
{
    public static IServiceCollection AddHlechykyGames(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<GameEvents>();
        // Явна фабрика: у реєстру є ще один, необов'язковий параметр (додаткові збірки для тестів),
        // і контейнер не вміє його вгадати.
        services.AddSingleton(sp => new Registry(sp.GetService<ILogger<Registry>>()));
        services.AddSingleton<Rooms>();
        services.AddSingleton<Broadcaster>();
        services.AddSingleton<RateGate>();
        // Broadcaster знаходиться при першому Post, а не при побудові графа: інакше
        // Rooms → IStakes (економіка) → IOutbox → Broadcaster → Rooms замикає коло (див. DeferredOutbox).
        services.AddSingleton<IOutbox>(sp => new DeferredOutbox(sp.GetRequiredService<Broadcaster>));
        // Заглушки, щоб сервер піднімався без економіки. AddHlechykyGames кличеться раніше за
        // AddHlechykyEconomy, тому TryAdd тут завжди виграє — справжні реалізації WP1 ставить через
        // services.Replace(ServiceDescriptor.Singleton<IStakes, Economy>()).
        services.TryAddSingleton<IStakes, NoStakes>();
        services.TryAddSingleton<IGameStore, MemoryGameStore>();
        services.AddHostedService<TickEngine>();
        services.AddSingleton<Tournament>();
        services.AddHostedService(sp => sp.GetRequiredService<Tournament>());
        Impl.ClickerGuildSetup.AddClickerGuild(services);   // цех Гончарного кола: віз, дарунки, хата друга
        // «Вгадай мелодію»: один на всі столи — щоб пісню з добірки не качали двічі два столи водночас
        services.AddSingleton(sp => new Impl.MelodyLibrary(
            sp.GetService<Db>(), sp.GetService<Microsoft.Extensions.Options.IOptionsMonitor<YtDlpOptions>>(),
            sp.GetService<YtMusicClient>(), sp.GetService<YtDlpService>(), Impl.MelodyClassics.Default,
            sp.GetService<Microsoft.Extensions.Options.IOptionsMonitor<MelodyOptions>>(), sp.GetService<ILogger<Impl.MelodyLibrary>>()));
        services.AddSingleton<Impl.IMelodySource>(sp => sp.GetRequiredService<Impl.MelodyLibrary>());
        return services;
    }

    public static WebApplication MapHlechykyGames(this WebApplication app)
    {
        // Лобі будується з каталогу, а не з хардкоду в JS: додав клас гри — вона з'явилась на сайті.
        app.MapGet("/api/games/catalog", (Registry registry) => new Catalog(registry.Catalog, Rooms.Stakes));
        Impl.ClickerGuildSetup.MapClickerGuild(app);        // /api/games/clicker/guild і /house
        Impl.MelodyClips.Map(app);                          // /api/games/melody/<токен>.mp3 — уривки «Вгадай мелодію»
        return app;
    }
}
