using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>Talks to liquidsoap over its telnet server. One short connection per command.</summary>
public sealed class LiquidsoapClient(IOptionsMonitor<LiquidsoapOptions> options, ILogger<LiquidsoapClient> log)
{
    static readonly char[] Ws = [' ', '\r', '\n', '\t'];
    LiquidsoapOptions O => options.CurrentValue;

    readonly SemaphoreSlim _gate = new(1, 1);
    TcpClient? _tcp;
    NetworkStream? _stream;

    /// <summary>One persistent telnet session, reconnected on any failure. Serialised: liquidsoap answers one command at a time.</summary>
    public async Task<string> CommandAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                return await SendAsync(command, cts.Token);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                Disconnect();
                return await SendAsync(command, cts.Token); // one retry on a fresh connection
            }
        }
        catch
        {
            Disconnect();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<string> SendAsync(string command, CancellationToken ct)
    {
        if (_tcp is null || !_tcp.Connected || _stream is null)
        {
            Disconnect();
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(O.Host, O.Port, ct);
            _stream = _tcp.GetStream();
        }
        await _stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), ct);
        var sb = new StringBuilder();
        var buf = new byte[8192];
        while (true)
        {
            var n = await _stream.ReadAsync(buf, ct);
            if (n == 0) throw new IOException("liquidsoap closed the connection");
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            var t = sb.ToString();
            if (t.EndsWith("END\r\n", StringComparison.Ordinal) || t.EndsWith("END\n", StringComparison.Ordinal)) break;
        }
        var text = sb.ToString().TrimEnd();
        if (text.EndsWith("END", StringComparison.Ordinal)) text = text[..^3].TrimEnd();
        return text;
    }

    void Disconnect()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await CommandAsync("uptime", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string ContainerPath(string hostPath) => O.CacheMount.TrimEnd('/') + "/" + Path.GetFileName(hostPath);

    public static string Annotate(IEnumerable<(string Key, string Value)> meta, string uri)
    {
        static string Esc(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        return "annotate:" + string.Join(",", meta.Select(m => $"{m.Key}=\"{Esc(m.Value)}\"")) + ":" + uri;
    }

    public async Task<string?> PushAsync(string queue, string uri, CancellationToken ct = default)
    {
        var r = await CommandAsync($"{queue}.push {uri}", ct);
        var rid = r.Split(Ws, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (rid is not null && int.TryParse(rid, out _)) return rid;
        log.LogWarning("{Queue}.push returned: {Resp}", queue, r);
        return null;
    }

    public async Task<List<string>> QueueAsync(string queue, CancellationToken ct = default)
    {
        var r = await CommandAsync($"{queue}.queue", ct);
        return r.Split(Ws, StringSplitOptions.RemoveEmptyEntries).Where(x => int.TryParse(x, out _)).ToList();
    }

    public Task RemoveAsync(string queue, string rid, CancellationToken ct = default) => CommandAsync($"{queue}.remove {rid}", ct);

    /// <summary>Seconds left in the current request, or null when a live stream / silence is on air.</summary>
    public async Task<double?> RemainingAsync(CancellationToken ct = default)
    {
        var r = (await CommandAsync("radio.remaining", ct)).Trim();
        return double.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>
    /// Metadata of what is on air now. "radio.metadata" prints "--- n ---" blocks from the oldest
    /// (highest n) down to "--- 1 ---", which is the newest, so the last printed block wins.
    /// </summary>
    public async Task<Dictionary<string, string>> CurrentMetadataAsync(CancellationToken ct = default)
    {
        var r = await CommandAsync("radio.metadata", ct);
        var current = new Dictionary<string, string>();
        foreach (var line in r.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                current = new Dictionary<string, string>();
                continue;
            }
            ParseLine(line, current);
        }
        return current;
    }

    /// <summary>Skip the current request of a specific queue (userq / autoq), or whatever is on air.</summary>
    public Task SkipAsync(string? queue, CancellationToken ct = default) =>
        CommandAsync(queue is "userq" or "autoq" ? $"{queue}.skip" : "radio.skip", ct);

    /// <summary>liquidsoap uptime in seconds ("0d 01h 04m 15s"); a drop means it restarted.</summary>
    public async Task<long> UptimeSecondsAsync(CancellationToken ct = default)
    {
        var r = await CommandAsync("uptime", ct);
        long total = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(r, @"(\d+)([dhms])"))
        {
            var v = long.Parse(m.Groups[1].Value);
            total += m.Groups[2].Value switch { "d" => v * 86400, "h" => v * 3600, "m" => v * 60, _ => v };
        }
        return total;
    }

    public async Task<List<string>> AllRequestsAsync(CancellationToken ct = default)
    {
        var r = await CommandAsync("request.all", ct);
        return r.Split(Ws, StringSplitOptions.RemoveEmptyEntries).Where(x => int.TryParse(x, out _)).ToList();
    }

    static void ParseLine(string line, Dictionary<string, string> into)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line[..eq];
        var val = line[(eq + 1)..].Trim();
        if (val.Length >= 2 && val[0] == '"' && val[^1] == '"') val = val[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        into[key] = System.Net.WebUtility.HtmlDecode(val);
    }

    public async Task<Dictionary<string, string>> MetadataAsync(string rid, CancellationToken ct = default)
    {
        var r = await CommandAsync($"request.metadata {rid}", ct);
        var d = new Dictionary<string, string>();
        foreach (var line in r.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ParseLine(line, d);
        return d;
    }
}
