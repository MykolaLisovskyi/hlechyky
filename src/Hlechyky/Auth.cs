using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Open site: everyone is a member without any key. A visit with ?k=&lt;admin key&gt; sets a protected
/// "admin" cookie; any other ?k= (old invite links) just redirects home. Nick comes from the client (X-Nick header / ?nick=).
/// </summary>
public static class Auth
{
    public const string CookieName = "hlechyky_auth";

    public static string Role(HttpContext c) => c.Items["role"] as string ?? "member";
    public static bool IsAdmin(HttpContext c) => Role(c) == "admin";
    public static string Nick(HttpContext c) => c.Items["nick"] as string ?? "гість";

    public static string SanitizeNick(string? raw)
    {
        var s = new string((raw ?? "").Trim().Where(ch => !char.IsControl(ch)).ToArray());
        if (s.Length > 24) s = s[..24];
        return s.Length == 0 ? "гість" : s;
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

        var protector = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("hlechyky.auth");

        if (ctx.Request.Query.TryGetValue("k", out var k))
        {
            var adminKey = ctx.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue.AdminKey;
            if (!string.IsNullOrEmpty(adminKey) && k.ToString() == adminKey)
                ctx.Response.Cookies.Append(CookieName, protector.Protect("admin"), new CookieOptions
                {
                    HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true, Expires = DateTimeOffset.UtcNow.AddDays(365),
                });
            ctx.Response.Redirect("/");
            return;
        }

        var role = "member";
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            try { if (protector.Unprotect(cookie) == "admin") role = "admin"; }
            catch { /* stale or foreign cookie: plain member */ }
        }

        ctx.Items["role"] = role;
        var nick = ctx.Request.Headers["X-Nick"].ToString();
        if (string.IsNullOrEmpty(nick)) nick = ctx.Request.Query["nick"].ToString();
        ctx.Items["nick"] = SanitizeNick(Uri.UnescapeDataString(nick));
        await next();
    });
}
