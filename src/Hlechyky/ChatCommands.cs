namespace Hlechyky;

/// <summary>
/// Команди чату — те, що починається зі скісної. Кидає сервер, а не браузер, щоб результат був
/// один для всіх і його не можна було підкрутити в консолі. Нова команда — гілка в Run і рядок
/// у COMMANDS на фронті.
/// </summary>
public static class ChatCommands
{
    /// <summary>Error бачить лише той, хто набрав; Text іде в чат усім.</summary>
    public sealed record Result(string? Error = null, string? Text = null, string Kind = "chat");

    public static Result Run(string text)
    {
        var space = text.IndexOf(' ');
        var name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var args = space < 0 ? "" : text[(space + 1)..].Trim();
        return name switch
        {
            "/roll" or "/кубик" => Roll(args),
            _ => new(Error: $"Команди {name} нема. Є /roll — кинути кубик"),
        };
    }

    /// <summary>/roll — 1–6, /roll 100 — 1–100, /roll 2-12 — свої межі (включно).</summary>
    static Result Roll(string args)
    {
        var (min, max) = (1, 6);
        if (args.Length > 0)
        {
            var parts = args.Split(['-', '–', '—', ' ', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out var n)) (min, max) = (1, n);
            else if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b)) (min, max) = (a, b);
            else return new(Error: "Не зрозумів межі. Кидай так: /roll, /roll 100 або /roll 2-12");
        }
        if (min > max) (min, max) = (max, min);
        if (min < 0 || max > 1_000_000) return new(Error: "Тримайся в межах від 0 до мільйона");
        if (min == max) return new(Error: "З таких меж кубик нецікавий");
        return new(Text: $"🎲 {Random.Shared.Next(min, max + 1)} ({min}–{max})", Kind: "dice");
    }
}
