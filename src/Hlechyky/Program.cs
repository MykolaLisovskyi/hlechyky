using Hlechyky;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var root = Paths.Root;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = root,
    WebRootPath = Path.Combine(root, "web"),
});
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var cfg = builder.Configuration;
builder.Services.Configure<SiteOptions>(cfg.GetSection("Site"));
builder.Services.Configure<AuthOptions>(cfg.GetSection("Auth"));
builder.Services.Configure<YtDlpOptions>(cfg.GetSection("YtDlp"));
builder.Services.Configure<VoiceOptions>(cfg.GetSection("Voice"));
builder.Services.Configure<LiquidsoapOptions>(cfg.GetSection("Liquidsoap"));
builder.Services.Configure<IcecastOptions>(cfg.GetSection("Icecast"));
builder.Services.Configure<LastFmOptions>(cfg.GetSection("LastFm"));
builder.Services.Configure<AutoDjOptions>(cfg.GetSection("AutoDj"));
builder.Services.Configure<DjBotOptions>(cfg.GetSection("DjBot"));
builder.Services.Configure<DeployOptions>(cfg.GetSection("Deploy"));

Console.OutputEncoding = System.Text.Encoding.UTF8;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Paths.Resolve("data/keys")));
builder.Services.AddSignalR();
builder.Services.AddSingleton(_ => new Db(Paths.Resolve("data/hlechyky.db")));
builder.Services.AddSingleton<YtMusicClient>();
builder.Services.AddSingleton<YtDlpService>();
builder.Services.AddSingleton<VoiceService>();
builder.Services.AddSingleton<LastFmClient>();
builder.Services.AddSingleton<LiquidsoapClient>();
builder.Services.AddSingleton<RoomTaste>();
builder.Services.AddSingleton<AutoDj>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<RadioEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RadioEngine>());
builder.Services.AddSingleton<TrackCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrackCache>());
builder.Services.AddSingleton<DjBrain>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DjBrain>());
builder.Services.AddHlechykyGames();
builder.Services.AddHlechykyEconomy();
builder.Services.AddHlechykyWords();

var port = cfg.GetValue<int?>("Site:ListenPort") ?? 8080;
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));

var app = builder.Build();
// За Caddy (той самий хост): X-Forwarded-Proto робить Request.IsHttps правдивим, X-Forwarded-For віддає IP слухача
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.Logger.LogInformation("Глечики: root={Root}, port={Port}", root, port);

app.UseHlechykyAuth();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapHlechyky();
app.MapHlechykyGames();
app.MapHlechykyEconomy();
app.MapHub<RadioHub>("/hub");
app.Run();
