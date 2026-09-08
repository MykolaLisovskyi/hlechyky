namespace Hlechyky;

/// <summary>Repo root discovery: tools, cache, data and web are all addressed relative to it.</summary>
public static class Paths
{
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("HLECHYKY_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
}

public sealed class SiteOptions
{
    public string Name { get; set; } = "Глечики";
    /// <summary>The auto-DJ's persona, nominative ("Дядько Глек радить").</summary>
    public string DjName { get; set; } = "Дядько Глек";
    /// <summary>Genitive form ("порада Дядька Глека").</summary>
    public string DjNameGen { get; set; } = "Дядька Глека";
    public string PublicStreamUrl { get; set; } = "";
    public int StreamDelaySeconds { get; set; } = 6;
    public int ListenPort { get; set; } = 8080;
}

public sealed class AuthOptions
{
    public string AdminKey { get; set; } = "";
}

public sealed class YtDlpOptions
{
    public string BinaryPath { get; set; } = "tools/yt-dlp/yt-dlp.exe";
    public string FfmpegDir { get; set; } = "tools/yt-dlp";
    public string CacheDir { get; set; } = "cache";
    /// <summary>Used only when YouTube demands a login (age-gated 18+ videos): a Netscape cookies.txt exported from a logged-in browser…</summary>
    public string CookiesFile { get; set; } = "";
    /// <summary>…or the browser to read cookies from (firefox works; chrome/edge lock their database while running).</summary>
    public string CookiesFromBrowser { get; set; } = "";
    public int MaxDurationSeconds { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 240;
}

public sealed class LiquidsoapOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1234;
    public string ApiKey { get; set; } = "";
    public string CacheMount { get; set; } = "/cache";
}

public sealed class IcecastOptions
{
    public string StatusUrl { get; set; } = "http://127.0.0.1:8000/status-json.xsl";
    public string RadioMount { get; set; } = "/radio.mp3";
    public string SpotifyMount { get; set; } = "/spotify.mp3";
}

public sealed class LastFmOptions
{
    public string ApiKey { get; set; } = "";
    public string SharedSecret { get; set; } = "";
}

public sealed class AutoDjOptions
{
    public bool Enabled { get; set; } = true;
    public int RecentSeeds { get; set; } = 10;
    public int LikeSeeds { get; set; } = 4;
    public int NoRepeatHours { get; set; } = 6;
    public int MaxDurationSeconds { get; set; } = 720;
    public string SeedQuery { get; set; } = "";
}
