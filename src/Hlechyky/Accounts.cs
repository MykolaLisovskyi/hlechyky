using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>Хто прийшов від Google: стале <c>sub</c> (пошту можна змінити, sub — ні), пошта й ім'я з профілю.</summary>
public sealed record GoogleIdentity(string Sub, string? Email, string? Name);

/// <summary>Перевірка ID-токена від кнопки Google. Інтерфейс — щоб тести не ходили до Google.</summary>
public interface IGoogleVerifier
{
    /// <summary>Без Client ID кнопки Google на сайті просто нема.</summary>
    bool Enabled { get; }
    string ClientId { get; }
    Task<GoogleIdentity?> VerifyAsync(string credential);
}

/// <summary>
/// Справжня перевірка: підпис по ключах Google (бібліотека сама їх кешує), видавець і аудиторія — наш Client ID.
/// Секрет для цього не потрібен: токен підписує Google, нам лишається перевірити.
/// </summary>
public sealed class GoogleVerifier(IOptionsMonitor<GoogleOptions> options, ILogger<GoogleVerifier> log) : IGoogleVerifier
{
    public string ClientId => options.CurrentValue.ClientId;
    public bool Enabled => !string.IsNullOrEmpty(ClientId);

    public async Task<GoogleIdentity?> VerifyAsync(string credential)
    {
        if (!Enabled || string.IsNullOrEmpty(credential)) return null;
        try
        {
            var p = await GoogleJsonWebSignature.ValidateAsync(credential, new GoogleJsonWebSignature.ValidationSettings { Audience = [ClientId] });
            return new(p.Subject, p.EmailVerified ? p.Email : null, p.Name);
        }
        catch (InvalidJwtException e)
        {
            log.LogWarning("Google-токен не пройшов перевірку: {Why}", e.Message);
            return null;
        }
    }
}

/// <summary>
/// Вхід і реєстрація: паролем, через Google, прив'язка Google до наявного акаунта, зміна пароля.
/// Тут лише база й правила; куку кладе Endpoints. Відповідь — або акаунт, або текст помилки з HTTP-кодом.
/// </summary>
public sealed class Accounts(Db db, IOptionsMonitor<SiteOptions> site)
{
    /// <summary>
    /// <see cref="NeedNick"/> — Google підтвердив людину, якої тут ще нема: хай назветься (<see cref="Suggest"/> —
    /// ім'я з профілю, якщо воно годиться за нік і вільне).
    /// </summary>
    public sealed record Outcome(Account? Account = null, string? Error = null, int Status = 400, bool NeedNick = false, string Suggest = "");

    static Outcome Ok(Account a) => new(a);
    static Outcome Fail(string why, int status = 400) => new(Error: why, Status: status);

    public Outcome Register(string? rawNick, string? password)
    {
        var nick = Auth.CleanNick(rawNick);
        if (Auth.NickProblem(nick, site.CurrentValue) is { } why) return Fail(why);
        if ((password ?? "").Length < Auth.PasswordMin) return Fail($"Пароль — хоча б {Auth.PasswordMin} символів");
        var hash = Auth.HashPassword(password!, out var salt);
        if (!db.AddAccount(nick, hash, salt)) return Fail($"Нік «{nick}» уже зайнятий", 409);
        return Ok(db.FindAccount(nick)!);
    }

    /// <summary>null в Account і null в Error не буває: хибний пароль — це Error зі статусом 401.</summary>
    public Outcome Login(string? rawNick, string? password)
    {
        var a = db.FindAccount(Auth.CleanNick(rawNick));
        if (a is not null && !a.HasPassword)
            return Fail($"У «{a.Nick}» нема пароля — заходь через Google, а пароль поставиш у картці «Ти — {a.Nick}»", 401);
        if (a is null || !Auth.VerifyPassword(password ?? "", a.PassHash, a.PassSalt)) return Fail("Не той нік або пароль", 401);
        db.TouchAccount(a.Nick);
        return Ok(a);
    }

    /// <summary>
    /// Кнопка Google на картці входу. Свій Google — одразу вхід. Новий — треба нік: без нього <c>NeedNick</c>,
    /// з ніком — реєстрація без пароля. Наявний нік із паролем через це не забрати: зайди паролем і прив'яжи.
    /// </summary>
    public Outcome Google(GoogleIdentity g, string? rawNick)
    {
        if (db.FindAccountByGoogle(g.Sub) is { } known)
        {
            db.TouchAccount(known.Nick);
            return Ok(known);
        }
        var nick = Auth.CleanNick(rawNick);
        if (nick.Length == 0)
        {
            var suggest = Auth.CleanNick(g.Name);
            if (Auth.NickProblem(suggest, site.CurrentValue) is not null || db.FindAccount(suggest) is not null) suggest = "";
            return new(NeedNick: true, Suggest: suggest, Status: 200);
        }
        if (Auth.NickProblem(nick, site.CurrentValue) is { } why) return Fail(why);
        if (!db.AddAccount(nick, "", Auth.NewSalt(), g.Sub, g.Email))
            return Fail($"Нік «{nick}» уже зайнятий. Якщо це ти — зайди з паролем і прив'яжи Google у картці «Ти — {nick}»", 409);
        return Ok(db.FindAccount(nick)!);
    }

    /// <summary>Кнопка Google у картці «Ти — …»: щоб заходити без пароля й мати куди повернутись, коли його забув.</summary>
    public Outcome LinkGoogle(Account me, GoogleIdentity g)
    {
        if (db.FindAccountByGoogle(g.Sub) is { } other)
            return Fail(Auth.NickKey(other.Nick) == Auth.NickKey(me.Nick) ? "Цей Google уже прив'язано" : $"Цей Google уже прив'язаний до «{other.Nick}»", 409);
        if (!db.SetAccountGoogle(me.Nick, g.Sub, g.Email)) return Fail("Цей Google уже чийсь", 409);
        return Ok(db.FindAccount(me.Nick)!);
    }

    /// <summary>Поставити чи змінити пароль. Теперішній питаємо лише в того, у кого він є (Google-акаунт ставить перший).</summary>
    public Outcome SetPassword(Account me, string? current, string? password)
    {
        if ((password ?? "").Length < Auth.PasswordMin) return Fail($"Пароль — хоча б {Auth.PasswordMin} символів");
        if (me.HasPassword && !Auth.VerifyPassword(current ?? "", me.PassHash, me.PassSalt)) return Fail("Не той теперішній пароль", 401);
        db.SetAccountPassword(me.Nick, Auth.HashPassword(password!, out var salt), salt);
        return Ok(db.FindAccount(me.Nick)!);
    }
}
