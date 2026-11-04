using System.Text;
using System.Text.Json;

namespace HitlessDemo;

/// <summary>
/// A thin client for the Redis fault injector's HTTP API (an internal Redis tool; see the README). Every
/// POST is asynchronous and returns an action id; the job is then polled until its status leaves the
/// pending set. Nothing here is needed to run the demo: without it, faults are triggered with rladmin by hand.
/// </summary>
sealed class FaultInjector(Uri baseAddress) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(120) };

    public Uri BaseAddress { get; } = baseAddress;

    /// <summary>The job passes through "running" after "pending", so a loop that only waits on "pending" exits early.</summary>
    public static readonly HashSet<string> PendingStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "pending", "running", "queued", "started", "deferred" };

    public async Task<bool> PingAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync("/action", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>The topology-change route against an existing database: no setup or teardown step.</summary>
    public Task<string> TopologyChangeAsync(string effect, string trigger, int bdbId, int clusterIndex, string? extraQuery)
        => PostAsync($"/topology-change-standalone?effect={effect}&trigger={trigger}&bdb_id={bdbId}&cluster_index={clusterIndex}{extraQuery}", null);

    /// <summary>A generic action: POST /action with a type and its parameters.</summary>
    public Task<string> ActionAsync(string type, object parameters)
        => PostAsync("/action", JsonSerializer.Serialize(new { type, parameters }));

    public async Task<(string Status, string? Detail)> StatusAsync(string actionId)
    {
        using var response = await _http.GetAsync($"/action/{actionId}");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string status = root.TryGetProperty("status", out var s) ? s.ToString() : "unknown";
        string? detail = null;
        foreach (var field in new[] { "error", "output", "result" })
        {
            if (root.TryGetProperty(field, out var d) && d.ValueKind is not JsonValueKind.Null) { detail = Trim(d.ToString()); break; }
        }
        return (status, detail);
    }

    private async Task<string> PostAsync(string url, string? json)
    {
        using var content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("action_id", out var id) && id.GetString() is { Length: > 0 } actionId
            ? actionId
            : throw new InvalidOperationException($"no action_id in response: {Trim(body)}");
    }

    private static string Trim(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 160 ? text : text[..160] + "...";
    }

    public void Dispose() => _http.Dispose();
}
