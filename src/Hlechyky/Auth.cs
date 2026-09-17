using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Акаунти й гості. Нік займають один раз разом із паролем, і далі він належить лише своєму хазяїну:
/// сесія — підписана кука, тож під чужим ім'ям не напишеш. Хто не зайшов — гість: нік бере який хоче,
/// але сервер ставить перед ним «гість », і з зареєстрованим він не зіллється. Робить гість усе те ж,
/// що й решта, і його глеки лежать під «гість Вася», доки він не зареєструється.
/// Усе добро тримається за ніком, як і було: зареєстрував «Влад» — і глеки, ачівки та статистика, що
/// вже лежали під «влад», твої без жодного переносу (ключ той самий, що й у гаманців — <see cref="NickKey"/>).
/// Адмінка як була: одне відкриття ?k=&lt;AdminKey&gt; кладе окрему куку на рік; якщо в цей момент
/// ти в акаунті, роль адміна дописується й самому акаунту, тож на іншому пристрої досить просто зайти.
/// </summary>
public static class Auth
{
    /// <summary>Кука адміна. Ім'я старе навмисно: у діючих адмінів вона не протухає від появи акаунтів.</summary>
    public const string CookieName = "hlechyky_auth";
    public const string SessionCookie = "hlechyky_session";
    public const string Guest = "гість";
    /// <summary>Приставка гостя. З пробілом: «гість Вася» читається як людина, а не як логін.</summary>
    public const string GuestPrefix = "гість ";
    public const int NickMin = 2, NickMax = 24, PasswordMin = 6;

    public static Account? Me(HttpContext c) => c.Items["account"] as Account;
    public static bool IsUser(HttpContext c) => Me(c) is not null;
    public static string Role(HttpContext c) => c.Items["role"] as string ?? "member";
    public static bool IsAdmin(HttpContext c) => Role(c) == "admin";
    public static string Nick(HttpContext c) => c.Items["nick"] as string ?? Guest;

    /// <summary>Ключ ніка — без регістру й країв, як у гаманців (Store.Key): «Оля» і «оля» — одна людина.</summary>
    public static string NickKey(string? nick) => (nick ?? "").Trim().ToLowerInvariant();

    /// <summary>Нік із зовнішнього рядка: без керівних символів і подвійних пробілів, не довший за <see cref="NickMax"/>.</summary>
    public static string CleanNick(string? raw)
    {
        var printable = new string((raw ?? "").Where(ch => !char.IsControl(ch)).ToArray());
        var s = string.Join(' ', printable.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length > NickMax ? s[..NickMax].TrimEnd() : s;
    }

    public static string SanitizeNick(string? raw)
    {
        var s = CleanNick(raw);
        return s.Length == 0 ? Guest : s;
    }

    /// <summary>Чи це гість — за приставкою, бо нічого іншого гість із собою не носить.</summary>
    public static bool IsGuestNick(string? nick) =>
        string.IsNullOrEmpty(nick) || NickKey(nick) == Guest || NickKey(nick).StartsWith(GuestPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Гостьовий нік: що прислав браузер, з «гість » попереду. Порожній — просто «гість». Той, хто приніс
    /// приставку сам (нік із localStorage минулого разу), другої не отримує.
    /// </summary>
    public static string GuestNick(string? raw)
    {
        var s = CleanNick(raw);
        if (s.StartsWith(GuestPrefix, StringComparison.OrdinalIgnoreCase)) s = s[GuestPrefix.Length..].Trim();
        else if (NickKey(s) == Guest) s = "";
        if (s.Length == 0) return Guest;
        var room = NickMax - GuestPrefix.Length;
        return GuestPrefix + (s.Length > room ? s[..room].TrimEnd() : s);
    }

    /// <summary>Чому такий нік не зареєструвати; null — годиться. Регістр не рахується: «оля» і «Оля» — один нік.</summary>
    public static string? NickProblem(string nick, SiteOptions site)
    {
        if (nick.Length < NickMin) return $"Нік — хоча б {NickMin} символи";
        if (IsGuestNick(nick)) return "«гість» — це для тих, хто без пароля. Вигадай своє";
        var key = NickKey(nick);
        foreach (var taken in new[] { site.Name, site.DjName, site.DjNameGen })
            if (key == NickKey(taken)) return "Це ім'я вже зайняте — воно тут господар";
        return null;
    }

    // ---------- пароль ----------
    // PBKDF2-SHA256: своя сіль на кожного, ітерацій стільки, щоб перебір був дорогим, а вхід — миттєвим.
    const int PbkdfIterations = 120_000;

    /// <summary>Сіль потрібна й акаунту без пароля: вона — ключ сесії в куці, і зміна пароля її оновлює.</summary>
    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string HashPassword(string password, out string salt)
    {
        salt = NewSalt();
        return Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), PbkdfIterations, HashAlgorithmName.SHA256, 32));
    }

    public static bool VerifyPassword(string password, string hash, string salt)
    {
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), PbkdfIterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Підбір пароля: п'ять промахів з однієї адреси — і чекай п'ять хвилин.</summary>
    static readonly ConcurrentDictionary<string, (int Count, DateTime Since)> Misses = new();
    const int MaxMisses = 5;
    static readonly TimeSpan MissWindow = TimeSpan.FromMinutes(5);
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";

    public static bool TooManyTries(HttpContext c) =>
        Misses.TryGetValue(Ip(c), out var m) && m.Count >= MaxMisses && DateTime.UtcNow - m.Since < MissWindow;

    public static void CountMiss(HttpContext c) =>
        Misses.AddOrUpdate(Ip(c), _ => (1, DateTime.UtcNow),
            (_, m) => DateTime.UtcNow - m.Since < MissWindow ? (m.Count + 1, m.Since) : (1, DateTime.UtcNow));

    public static void ForgetMisses(HttpContext c) => Misses.TryRemove(Ip(c), out _);

    // ---------- сесія ----------
    // У куці — ключ ніка й сіль пароля: зміна пароля (зокрема адміном через /пароль) валить усі старі сесії.

    static IDataProtector Protector(HttpContext c) =>
        c.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("hlechyky.auth");

    static CookieOptions Year(HttpContext c) => new()
    {
        HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true,
        Expires = DateTimeOffset.UtcNow.AddDays(365),
    };

    public static void SignIn(HttpContext c, Account a)
    {
        c.Response.Cookies.Append(SessionCookie, Protector(c).Protect($"session|{NickKey(a.Nick)}|{a.PassSalt}"), Year(c));
        c.Items["account"] = a;
        c.Items["nick"] = a.Nick;
        if (a.Role == "admin") c.Items["role"] = "admin";
    }

    public static void SignOut(HttpContext c)
    {
        c.Response.Cookies.Delete(SessionCookie);
        c.Items.Remove("account");
        c.Items["nick"] = Guest;
    }

    static Account? SessionAccount(HttpContext c, IDataProtector protector, Db db)
    {
        if (!c.Request.Cookies.TryGetValue(SessionCookie, out var cookie)) return null;
        string payload;
        try { payload = protector.Unprotect(cookie); }
        catch { return null; } // чужа чи протухла кука — гість
        var parts = payload.Split('|');
        if (parts.Length != 3 || parts[0] != "session") return null;
        var a = db.FindAccount(parts[1]);
        return a is not null && a.PassSalt == parts[2] ? a : null;
    }

    public static IApplicationBuilder UseHlechykyAuth(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api/liq"))
        {
            var expected = ctx.RequestServices.GetRequiredService<IOptionsMonitor<LiquidsoapOptions>>().CurrentValue.ApiKey;
            if (string.IsNullOrEmpty(expected) || ctx.Request.Headers["X-Api-Key"] != expected)
            {
                ctx.Response.StatusCode = 401;
                return;
            }
            await next();
            return;
        }
        if (path.StartsWithSegments("/static"))
        {
            await next();
            return;
        }

        var protector = Protector(ctx);
        var db = ctx.RequestServices.GetRequiredService<Db>();
        var account = SessionAccount(ctx, protector, db);

        if (ctx.Request.Query.TryGetValue("k", out var k))
        {
            var adminKey = ctx.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue.AdminKey;
            if (!string.IsNullOrEmpty(adminKey) && k.ToString() == adminKey)
            {
                ctx.Response.Cookies.Append(CookieName, protector.Protect("admin"), Year(ctx));
                if (account is not null) db.SetAccountRole(account.Nick, "admin");
            }
            ctx.Response.Redirect("/");
            return;
        }

        var role = "member";
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            try { if (protector.Unprotect(cookie) == "admin") role = "admin"; }
            catch { /* stale or foreign cookie: plain member */ }
        }
        if (account?.Role == "admin") role = "admin";
        ctx.Items["role"] = role;

        if (account is not null)
        {
            ctx.Items["account"] = account;
            ctx.Items["nick"] = account.Nick;
        }
        else
        {
            // Гість: нік — із заголовка (API) або з рядка запиту (підключення до хабу), з приставкою.
            var nick = ctx.Request.Headers["X-Nick"].ToString();
            if (string.IsNullOrEmpty(nick)) nick = ctx.Request.Query["nick"].ToString();
            var raw = Uri.UnescapeDataString(nick);
            if (path.StartsWithSegments("/mcp"))
            {
                // Агент — не людина з паролем, але й не «гість Вася»: ім'я як назвався, аби не чуже зареєстроване.
                var clean = SanitizeNick(raw);
                ctx.Items["nick"] = db.FindAccount(clean) is null ? clean : Guest;
            }
            else ctx.Items["nick"] = GuestNick(raw);
        }
        await next();
    });
}
