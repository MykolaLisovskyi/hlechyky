using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Шлях, який рушій дає liquidsoap. У контейнер змонтована вся тека кешу, тож підтеки в шляху мусять
/// вижити: «Вгадай мелодію» тримає свої пісні в <c>cache/melody</c>, і поки їхній шлях складався з
/// одного імені файла, liquidsoap не знаходив пісню, викидав запит — а ефір годинами висів на
/// запасній спотіфай-трансляції.
/// </summary>
public class LiquidsoapPathTests
{
    static LiquidsoapClient Client() => new(
        new FixedOptions<LiquidsoapOptions>(new LiquidsoapOptions { CacheMount = "/cache" }),
        new FixedOptions<YtDlpOptions>(new YtDlpOptions { CacheDir = "cache" }),
        NullLogger<LiquidsoapClient>.Instance);

    static string InCache(params string[] parts) => Path.Combine([Paths.Resolve("cache"), .. parts]);

    [Fact]
    public void Track_from_the_cache_root_is_mounted_by_name() =>
        Assert.Equal("/cache/zgIfcF3WPA4.m4a", Client().ContainerPath(InCache("zgIfcF3WPA4.m4a")));

    [Fact]
    public void Song_from_a_subfolder_keeps_that_subfolder() =>
        Assert.Equal("/cache/melody/zgIfcF3WPA4.m4a", Client().ContainerPath(InCache("melody", "zgIfcF3WPA4.m4a")));

    [Fact]
    public void File_outside_the_cache_has_no_container_path() =>
        Assert.Null(Client().ContainerPath(Path.Combine(Paths.Root, "data", "zgIfcF3WPA4.m4a")));
}
