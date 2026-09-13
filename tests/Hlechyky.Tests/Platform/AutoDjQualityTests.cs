using System.Text.Json.Nodes;
using Hlechyky.Tests.Support;
using V = Hlechyky.ArtistQuality.Verdict;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Фільтр якості й пам'ять про відкинуте. Числа — з реальної ночі 13.09, коли Глек заїхав від
/// «ДК Енергетик» в AI-deep-house і іспанські пісні про One Piece.
/// </summary>
public class AutoDjQualityTests
{
    [Theory]
    // справжні: хтось своє, хтось нішеве, але живе
    [InlineData(false, 66460, null, 1000, V.Popular)]      // SadSvit
    [InlineData(false, 7643, null, 1000, V.Popular)]       // Small Depo, кириличний поріг
    [InlineData(false, 876, true, 1000, V.Catalogued)]     // слухачів мало, але в MusicBrainz є
    [InlineData(true, 0, false, 5000, V.Known)]            // кімната вже ставила — без питань
    // контент-ферми: на Last.fm жменька слухачів, у MusicBrainz нема
    [InlineData(false, 4288, false, 5000, V.Rejected)]     // DannyHO
    [InlineData(false, 365, false, 5000, V.Rejected)]      // Velvet Smoke
    [InlineData(false, 0, false, 5000, V.Rejected)]        // Last.fm такого не знає зовсім
    // збої мережі
    [InlineData(false, null, null, 5000, V.Unknown)]       // обидва мовчать — не караємо
    [InlineData(false, 486, null, 5000, V.Rejected)]       // Last.fm каже «мало», MusicBrainz не відповів — не ризикуємо
    public void Judge_tells_real_artists_from_content_farms(bool known, int? listeners, bool? catalogued, int threshold, V expected)
    {
        var v = ArtistQuality.Judge(known, listeners, catalogued, threshold);
        Assert.Equal(expected, v);
        Assert.Equal(expected != V.Rejected, ArtistQuality.Passes(v));
    }

    [Fact]
    public void Feedback_is_counted_per_artist_and_per_seed_and_forgotten_in_time()
    {
        using var db = new TempDb();
        db.Db.AddDjFeedback("dannyho", "uSeedTrack1", "dismiss", "владік");
        db.Db.AddDjFeedback("dannyho", "uSeedTrack1", "skip", "микола");
        db.Db.AddDjFeedback("velvet smoke", null, "dismiss", "владік");
        db.Db.Exec("INSERT INTO dj_feedback(artist_key, seed_id, kind, created_at) VALUES('sadsvit', 'uOld', 'dismiss', $t)",
            ("$t", DateTimeOffset.UtcNow.AddDays(-5).ToString("o")));

        var (artists, seeds) = AutoDj.CountStrikes(db.Db.DjFeedbackSince(DateTimeOffset.UtcNow.AddHours(-48)));

        Assert.Equal(2, artists["dannyho"]);
        Assert.Equal(1, artists["velvet smoke"]);
        Assert.False(artists.ContainsKey("sadsvit"), "відмова п'ятиденної давнини вже не має тиснути");
        Assert.Equal(2, seeds["uSeedTrack1"]);
        Assert.Single(seeds);
    }

    /// <summary>
    /// Обрізана справжня відповідь InnerTube (hl=uk). Заголовки полиць локалізовані, тож парсер мусить
    /// розпізнати пісні й схожих артистів за вмістом, а не взяти альбоми чи відео за «схожих».
    /// </summary>
    [Fact]
    public void Artist_page_yields_top_songs_and_related_artists_not_albums()
    {
        static JsonObject Artist(string id) => new()
        {
            ["browseEndpoint"] = new JsonObject
            {
                ["browseId"] = id,
                ["browseEndpointContextSupportedConfigs"] = new JsonObject
                {
                    ["browseEndpointContextMusicConfig"] = new JsonObject { ["pageType"] = "MUSIC_PAGE_TYPE_ARTIST" },
                },
            },
        };
        static JsonObject Album(string id) => new()
        {
            ["browseEndpoint"] = new JsonObject
            {
                ["browseId"] = id,
                ["browseEndpointContextSupportedConfigs"] = new JsonObject
                {
                    ["browseEndpointContextMusicConfig"] = new JsonObject { ["pageType"] = "MUSIC_PAGE_TYPE_ALBUM" },
                },
            },
        };
        static JsonObject Runs(string text) => new() { ["runs"] = new JsonArray(new JsonObject { ["text"] = text }) };
        static JsonObject Col(string text) => new() { ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject { ["text"] = Runs(text) } };
        static JsonObject Song(string id, string title, string artist, string plays, string album) => new()
        {
            ["musicResponsiveListItemRenderer"] = new JsonObject
            {
                ["playlistItemData"] = new JsonObject { ["videoId"] = id },
                ["flexColumns"] = new JsonArray(Col(title), Col(artist), Col(plays), Col(album)),
            },
        };
        static JsonObject Card(string title, JsonObject nav) => new()
        {
            ["musicTwoRowItemRenderer"] = new JsonObject { ["title"] = Runs(title), ["navigationEndpoint"] = nav },
        };
        static JsonObject Carousel(string header, params JsonObject[] cards) => new()
        {
            ["musicCarouselShelfRenderer"] = new JsonObject
            {
                ["header"] = new JsonObject { ["musicCarouselShelfBasicHeaderRenderer"] = new JsonObject { ["title"] = Runs(header) } },
                ["contents"] = new JsonArray(cards.Select(c => (JsonNode)c).ToArray()),
            },
        };

        var root = new JsonObject
        {
            ["header"] = new JsonObject { ["musicImmersiveHeaderRenderer"] = new JsonObject { ["title"] = Runs("SadSvit") } },
            ["contents"] = new JsonObject
            {
                ["singleColumnBrowseResultsRenderer"] = new JsonObject
                {
                    ["tabs"] = new JsonArray(new JsonObject
                    {
                        ["tabRenderer"] = new JsonObject
                        {
                            ["content"] = new JsonObject
                            {
                                ["sectionListRenderer"] = new JsonObject
                                {
                                    ["contents"] = new JsonArray(
                                        new JsonObject
                                        {
                                            ["musicShelfRenderer"] = new JsonObject
                                            {
                                                ["title"] = Runs("Найпопулярніші пісні"),
                                                ["contents"] = new JsonArray(
                                                    Song("BKdXNhY1oyY", "Касета", "SadSvit", "52 млн відтворень", "Cassette"),
                                                    Song("5JfYOxleges", "Персонажі", "SadSvit", "38 млн відтворень", "Персонажі")),
                                            },
                                        },
                                        Carousel("Альбоми", Card("Цвіт магнолії", Album("MPREb_1"))),
                                        Carousel("Відео", Card("Силуети", new JsonObject { ["watchEndpoint"] = new JsonObject { ["videoId"] = "x" } })),
                                        Carousel("Схожі виконавці", Card("Electrobirds", Artist("UCelectro")), Card("Sudno", Artist("UCsudno"))),
                                        new JsonObject { ["musicDescriptionShelfRenderer"] = new JsonObject() }),
                                },
                            },
                        },
                    }),
                },
            },
        };

        var page = YtMusicClient.ParseArtist("UCsadsvit", root)!;

        Assert.Equal("SadSvit", page.Name);
        Assert.Equal(["Касета", "Персонажі"], page.TopSongs.Select(s => s.Title));
        Assert.Equal("SadSvit", page.TopSongs[0].Artist);
        Assert.Equal("Cassette", page.TopSongs[0].Album);
        Assert.Equal(["Electrobirds", "Sudno"], page.Related.Select(r => r.Name));
        Assert.Equal("UCelectro", page.Related[0].Id);
    }
}
