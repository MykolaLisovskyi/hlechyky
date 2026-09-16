using Hlechyky.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Tests.Platform;

/// <summary>Лайки й відповіді в балачках: що лягає в базу і що повертається в історії.</summary>
public class ChatLikesRepliesTests
{
    [Fact]
    public void A_reply_carries_a_quote_of_the_original()
    {
        using var t = new TempDb();
        var hi = t.Db.AddChat("Оля", "хто сьогодні грає в мафію?", "chat");
        var re = t.Db.AddChat("Петро", "я!", "chat", replyTo: hi.Id);

        Assert.Equal(hi.Id, re.ReplyTo);
        Assert.Equal("Оля", re.ReplyNick);
        Assert.Equal("хто сьогодні грає в мафію?", re.ReplyText);

        var fromHistory = t.Db.RecentChat(10, 0).Single(m => m.Id == re.Id);
        Assert.Equal(hi.Id, fromHistory.ReplyTo);
        Assert.Equal("Оля", fromHistory.ReplyNick);
    }

    [Fact]
    public void A_long_original_is_quoted_short()
    {
        using var t = new TempDb();
        var hi = t.Db.AddChat("Оля", new string('а', 400), "chat");
        var re = t.Db.AddChat("Петро", "ого", "chat", replyTo: hi.Id);
        Assert.True(re.ReplyText!.Length <= 121);
        Assert.EndsWith("…", re.ReplyText);
    }

    [Fact]
    public void Replying_to_nothing_or_to_the_journal_is_just_a_message()
    {
        using var t = new TempDb();
        var log = t.Db.AddChat("Глечики", "Оля сіла грати", "system");
        Assert.Null(t.Db.AddChat("Петро", "ок", "chat", replyTo: log.Id).ReplyTo);
        Assert.Null(t.Db.AddChat("Петро", "ок", "chat", replyTo: 99999).ReplyTo);
    }

    [Fact]
    public void Likes_toggle_and_show_up_in_history()
    {
        using var t = new TempDb();
        var m = t.Db.AddChat("Оля", "привіт", "chat");

        Assert.Equal(["Петро"], t.Db.ToggleChatLike(m.Id, "Петро"));
        Assert.Equal(["Петро", "Ганна"], t.Db.ToggleChatLike(m.Id, "Ганна"));
        Assert.Equal(["Петро", "Ганна"], t.Db.RecentChat(10, 0).Single().Likes!);

        Assert.Equal(["Ганна"], t.Db.ToggleChatLike(m.Id, "петро"));   // нік без регістру — та сама людина
        Assert.Equal(["Ганна"], t.Db.RecentChat(10, 0).Single().Likes!);
    }

    [Fact]
    public void Journal_lines_and_missing_messages_cannot_be_liked()
    {
        using var t = new TempDb();
        var log = t.Db.AddChat("Глечики", "Новий стіл", "system");
        Assert.Null(t.Db.ToggleChatLike(log.Id, "Оля"));
        Assert.Null(t.Db.ToggleChatLike(424242, "Оля"));
    }

    [Fact]
    public void Messages_without_likes_have_an_empty_list()
    {
        using var t = new TempDb();
        t.Db.AddChat("Оля", "раз", "chat");
        var liked = t.Db.AddChat("Оля", "два", "chat");
        t.Db.ToggleChatLike(liked.Id, "Петро");
        var history = t.Db.RecentChat(10, 0);
        Assert.Empty(history[0].Likes!);
        Assert.Single(history[1].Likes!);
    }

    [Fact]
    public void An_old_database_gets_the_new_column_and_table()
    {
        var path = Path.Combine(Path.GetTempPath(), "hlechyky-old-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE chat(id INTEGER PRIMARY KEY AUTOINCREMENT, nick TEXT NOT NULL, text TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'chat', created_at TEXT NOT NULL);"
                    + "INSERT INTO chat(nick, text, kind, created_at) VALUES('Оля', 'стара репліка', 'chat', '2026-09-01T10:00:00.0000000+00:00');";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var db = new Db(path);
            var old = db.RecentChat(10, 0).Single();
            Assert.Null(old.ReplyTo);
            Assert.Equal(["Петро"], db.ToggleChatLike(old.Id, "Петро"));
            Assert.Equal(old.Id, db.AddChat("Петро", "відповідаю", "chat", replyTo: old.Id).ReplyTo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }
}
