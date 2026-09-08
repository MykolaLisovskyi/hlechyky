using Hlechyky;
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
builder.Services.Configure<LiquidsoapOptions>(cfg.GetSection("Liquidsoap"));
builder.Services.Configure<IcecastOptions>(cfg.GetSection("Icecast"));
builder.Services.Configure<LastFmOptions>(cfg.GetSection("LastFm"));
builder.Services.Configure<AutoDjOptions>(cfg.GetSection("AutoDj"));

Console.OutputEncoding = System.Text.Encoding.UTF8;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Paths.Resolve("data/keys")));
builder.Services.AddSignalR();
builder.Services.AddSingleton(_ => new Db(Paths.Resolve("data/hlechyky.db")));
builder.Services.AddSingleton<YtMusicClient>();
builder.Services.AddSingleton<YtDlpService>();
builder.Services.AddSingleton<LastFmClient>();
builder.Services.AddSingleton<LiquidsoapClient>();
builder.Services.AddSingleton<AutoDj>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<Games>();
builder.Services.AddSingleton<RadioEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RadioEngine>());
builder.Services.AddHostedService<SnakeEngine>();

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
app.MapHub<RadioHub>("/hub");
app.Run();
