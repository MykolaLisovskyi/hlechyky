using System.Net;
using System.Text;
using System.Text.Json;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Підстраховка автодеплою. 15.09.2026 GitHub двічі поспіль не дочекався відповіді на «збірка завершена»
/// (його стеля — 10 с, а до домашнього сайту він часом їде 6–9 с), і зелений коміт так і не викотився.
/// </summary>
public sealed class DeployWatchTests : IDisposable
{
    const string Built = "29d9899fb417eb3caf67c96c88dda561fb24b7c5";
    const string Fresh = "264702cfbbbb02e0ee708427d74e0f6fde711c3b";
    const string Older = "d6ebba9b2103375afd4516a8410e06640a7fa904";

    readonly string _root = Path.Combine(Path.GetTempPath(), "hlechyky-deploy-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> _launched = [];
    readonly List<string> _urls = [];

    public DeployWatchTests() => Directory.CreateDirectory(Path.Combine(_root, "data"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    static DeployOptions Prod() => new()
    {
        Enabled = true, WebhookSecret = "секрет", Repo = "kartatyi/hlechyky", Branch = "main", Workflow = "build", PollMinutes = 3,
    };

    static object Run(string sha, string name = "build", string branch = "main", string evt = "push", string conclusion = "success") =>
        new { id = 1, name, head_sha = sha, head_branch = branch, @event = evt, status = "completed", conclusion };

    static JsonElement Runs(params object[] runs) =>
        JsonSerializer.SerializeToElement(new { total_count = runs.Length, workflow_runs = runs });

    void Mark(string file, string text) => File.WriteAllText(Path.Combine(_root, file), text);

    DeployWatch Watch(DeployOptions o, HttpStatusCode status, object body, Action<HttpResponseMessage>? tweak = null)
    {
        var handler = new Answer(req =>
        {
            _urls.Add(req.RequestUri!.ToString());
            var res = new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            tweak?.Invoke(res);
            return res;
        });
        return new DeployWatch(new FixedOptions<DeployOptions>(o), NullLogger.Instance, new HttpClient(handler), _root, (_, sha) => _launched.Add(sha));
    }

    sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }

    // ---------- що вважати «пора викотити» ----------

    [Fact]
    public void The_newest_green_build_that_is_not_built_yet_is_pending()
    {
        Assert.Equal(Fresh, DeployWatch.Pending(Runs(Run(Fresh), Run(Built)), Prod(), Built, ""));
    }

    [Fact]
    public void Nothing_is_pending_when_build_already_has_the_newest_green_commit()
    {
        Assert.Null(DeployWatch.Pending(Runs(Run(Built), Run(Older)), Prod(), Built, ""));
        // start.ps1 пише sha з git як є, а регістр — не привід перезапускати сайт
        Assert.Null(DeployWatch.Pending(Runs(Run(Built.ToUpperInvariant())), Prod(), Built, ""));
    }

    [Fact]
    public void A_commit_is_tried_only_once()
    {
        // Деплой цього коміту вже запускали (і він, скажімо, відкотився): по колу сайт не перезапускаємо.
        Assert.Null(DeployWatch.Pending(Runs(Run(Fresh), Run(Built)), Prod(), Built, Fresh));
    }

    [Fact]
    public void An_older_green_build_does_not_count_when_the_newest_one_is_already_tried()
    {
        // Старіші зелені не рахуються: deploy.ps1 однаково тягне верхівку origin.
        Assert.Null(DeployWatch.Pending(Runs(Run(Fresh), Run(Older)), Prod(), Built, Fresh));
    }

    [Theory]
    [InlineData("codeql", "main", "push", "success")]
    [InlineData("build", "feature", "push", "success")]
    [InlineData("build", "main", "pull_request", "success")]
    [InlineData("build", "main", "push", "failure")]
    public void Other_workflows_branches_pull_requests_and_red_builds_are_ignored(string name, string branch, string evt, string conclusion)
    {
        Assert.Null(DeployWatch.Pending(Runs(Run(Fresh, name, branch, evt, conclusion)), Prod(), Built, ""));
        // …але зелену збірку main за ними все одно знайдемо
        Assert.Equal(Older, DeployWatch.Pending(Runs(Run(Fresh, name, branch, evt, conclusion), Run(Older)), Prod(), Built, ""));
    }

    [Fact]
    public void A_strange_answer_means_nothing_to_do()
    {
        Assert.Null(DeployWatch.Pending(JsonSerializer.SerializeToElement(new { message = "Not Found" }), Prod(), Built, ""));
        Assert.Null(DeployWatch.Pending(Runs(), Prod(), Built, ""));
    }

    // ---------- перевірка цілком ----------

    [Fact]
    public async Task A_lost_webhook_is_picked_up_and_launched_once()
    {
        Mark(Deploy.BuiltFile, Built + "\r\n");          // так пише start.ps1: Set-Content додає перенос
        var watch = Watch(Prod(), HttpStatusCode.OK, new { workflow_runs = new[] { Run(Fresh), Run(Built) } });

        Assert.Equal(Fresh, await watch.CheckAsync());
        Assert.Equal([Fresh], _launched);
        Assert.Equal(Fresh, Deploy.ReadMark(_root, Deploy.TriedFile));
        var url = Assert.Single(_urls);
        Assert.StartsWith("https://api.github.com/repos/kartatyi/hlechyky/actions/runs?", url);
        Assert.Contains("branch=main", url);
        Assert.Contains("status=success", url);

        // Наступна перевірка (деплой, скажімо, впав і build\ лишився старим) — вже нічого.
        Assert.Null(await watch.CheckAsync());
        Assert.Equal([Fresh], _launched);
    }

    [Fact]
    public async Task Nothing_happens_while_a_deploy_is_running()
    {
        Mark(Deploy.BuiltFile, Built);
        Mark(Deploy.LockFile, "");
        var watch = Watch(Prod(), HttpStatusCode.OK, new { workflow_runs = new[] { Run(Fresh) } });

        Assert.Null(await watch.CheckAsync());
        Assert.Empty(_urls);                             // навіть GitHub не смикали
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task A_copy_that_start_ps1_never_built_is_left_alone()
    {
        var watch = Watch(Prod(), HttpStatusCode.OK, new { workflow_runs = new[] { Run(Fresh) } });

        Assert.Null(await watch.CheckAsync());
        Assert.Empty(_urls);
        Assert.Empty(_launched);
    }

    public static TheoryData<string> Switches => new() { "disabled", "no-secret", "no-repo", "no-poll" };

    [Theory]
    [MemberData(nameof(Switches))]
    public async Task Without_a_webhook_secret_repo_or_poll_interval_nothing_is_checked(string what)
    {
        Mark(Deploy.BuiltFile, Built);
        var o = Prod();
        switch (what)
        {
            case "disabled": o.Enabled = false; break;
            case "no-secret": o.WebhookSecret = ""; break;
            case "no-repo": o.Repo = " "; break;
            case "no-poll": o.PollMinutes = 0; break;
        }
        var watch = Watch(o, HttpStatusCode.OK, new { workflow_runs = new[] { Run(Fresh) } });

        Assert.Null(await watch.CheckAsync());
        Assert.Empty(_urls);
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task A_rate_limit_is_an_error_not_a_deploy()
    {
        Mark(Deploy.BuiltFile, Built);
        var reset = DateTimeOffset.UtcNow.AddMinutes(40).ToUnixTimeSeconds();
        var watch = Watch(Prod(), HttpStatusCode.Forbidden, new { message = "API rate limit exceeded" },
            res => res.Headers.Add("X-RateLimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => watch.CheckAsync());
        Assert.Contains("ліміт", ex.Message);
        Assert.Empty(_launched);
    }
}
