using System.Text.Json;

namespace HitlessDemo;

/// <summary>One database the demo can connect to. <see cref="BdbId"/> is only needed to drive faults through the fault injector.</summary>
sealed record DatabaseTarget(string Name, string Endpoint, string? User, string? Password, bool Tls, bool TrustAnyCertificate, int? BdbId);

/// <summary>
/// Where the demo connects and how faults are triggered. This demo brings no server of its own: a Redis
/// Enterprise cluster (or Redis Cloud database) with the maintenance-notification flags on is required,
/// and the fault injector is optional - without it the fault keys show the rladmin command to run by hand.
/// </summary>
sealed record DemoSettings(IReadOnlyList<DatabaseTarget> Databases, Uri? FaultInjector, int ClusterIndex, string? InitialMode,
    ConsoleKey? Fire, int FireAfter, int? QuitAfter, int Workers = 8, int TimeoutMs = 5000, int Rate = 0)
{
    public const string LocalFile = "hitless.local.json";
    private static readonly Uri DefaultFaultInjector = new("http://127.0.0.1:20324");

    /// <summary>
    /// Precedence: command line, then HITLESS_* environment variables, then hitless.local.json in the current
    /// directory. The fault injector's own endpoints.json is accepted directly (--endpoints), so nothing has to
    /// be copied out of it.
    /// </summary>
    public static DemoSettings Load(string[] args)
    {
        string? endpointsFile = Env("HITLESS_ENDPOINTS"), settingsFile = null, single = Env("HITLESS_ENDPOINT"),
            password = Env("HITLESS_PASSWORD"), user = Env("HITLESS_USER"), name = null, injector = Env("HITLESS_FAULT_INJECTOR"), bdb = Env("HITLESS_BDB"),
            mode = Env("HITLESS_MODE");
        bool tls = Env("HITLESS_TLS") is "1" or "true", trustAny = false, noInjector = false;
        int clusterIndex = 0, fireAfter = 10, workers = 8, timeoutMs = 5000, rate = 0;
        int? quitAfter = null;
        ConsoleKey? fire = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--endpoints": endpointsFile = Next(); break;
                case "--settings": settingsFile = Next(); break;
                case "--endpoint": single = Next(); break;
                case "--password": password = Next(); break;
                case "--user": user = Next(); break;
                case "--name": name = Next(); break;
                case "--bdb": bdb = Next(); break;
                case "--tls": tls = true; break;
                case "--tls-insecure": tls = true; trustAny = true; break;
                case "--fault-injector": injector = Next(); break;
                case "--no-fault-injector": noInjector = true; break;
                case "--cluster-index": clusterIndex = int.Parse(Next()); break;
                case "--mode": mode = Next(); break; // Auto, Enabled or Disabled to start in
                // hands-free: press one fault key after a delay, and quit after a while (for a scripted run or a smoke test)
                case "--fire": fire = Enum.Parse<ConsoleKey>(Next().ToUpperInvariant()); break;
                case "--fire-after": fireAfter = int.Parse(Next()); break;
                case "--quit-after": quitAfter = int.Parse(Next()); break;
                case "--workers": workers = int.Parse(Next()); break; // concurrent callers
                case "--rate": rate = int.Parse(Next()); break; // open loop: operations issued per second whether or not earlier ones have completed
                case "--timeout": timeoutMs = int.Parse(Next()); break; // sync/async command timeout, ms
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }

        var databases = new List<DatabaseTarget>();
        Uri? injectorUri = null;
        if (single is not null)
        {
            databases.Add(new DatabaseTarget(name ?? "database", single, user, password, tls, trustAny, int.TryParse(bdb, out var id) ? id : null));
        }
        else if (endpointsFile is not null)
        {
            databases.AddRange(FromFaultInjectorEndpoints(endpointsFile, trustAny));
            injectorUri = DefaultFaultInjector; // the file is written by the injector, so it is normally running alongside
        }
        else
        {
            settingsFile ??= File.Exists(LocalFile) ? LocalFile : null;
            if (settingsFile is null)
            {
                throw new ArgumentException(
                    $"no target: pass --endpoints <endpoints.json> (fault injector), --endpoint host:port [--password ...], or create {LocalFile} (see hitless.local.example.json)");
            }
            injectorUri = FromLocalFile(settingsFile, databases, ref clusterIndex);
        }
        if (injector is not null) injectorUri = new Uri(injector);
        if (noInjector) injectorUri = null;
        if (databases.Count == 0) throw new ArgumentException("no databases configured");
        if (mode is not null && !HandoffEngine.Modes.Any(m => m.Name.Equals(mode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"--mode must be one of {string.Join(", ", HandoffEngine.Modes.Select(m => m.Name))}");
        }
        return new DemoSettings(databases, injectorUri, clusterIndex, mode, fire, fireAfter, quitAfter, workers, timeoutMs, rate);
    }

    /// <summary>A leading "~/" is expanded, so HITLESS_ENDPOINTS=~/aws/endpoints.json works from a shell profile.</summary>
    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("~/", StringComparison.Ordinal)) value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
        return value;
    }

    /// <summary>The fault injector's endpoints.json: one object per database, keyed by name.</summary>
    private static IEnumerable<DatabaseTarget> FromFaultInjectorEndpoints(string path, bool trustAny)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var db in doc.RootElement.EnumerateObject())
        {
            var v = db.Value;
            string? endpoint = null;
            if (v.TryGetProperty("endpoints", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            {
                endpoint = list[0].GetString();
            }
            else if (v.TryGetProperty("raw_endpoints", out var raw) && raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0)
            {
                var first = raw[0];
                endpoint = $"{first.GetProperty("dns_name").GetString()}:{first.GetProperty("port").GetInt32()}";
            }
            if (endpoint is null) continue;
            int? bdbId = v.TryGetProperty("bdb_id", out var idProp) && int.TryParse(idProp.ToString(), out var id) ? id : null;
            bool tls = v.TryGetProperty("tls", out var tlsProp) && tlsProp.ValueKind == JsonValueKind.True;
            yield return new DatabaseTarget(db.Name, endpoint,
                v.TryGetProperty("username", out var u) ? u.GetString() : null,
                v.TryGetProperty("password", out var p) ? p.GetString() : null,
                tls, tls && trustAny, bdbId);
        }
    }

    /// <summary>The demo's own file; see hitless.local.example.json.</summary>
    private static Uri? FromLocalFile(string path, List<DatabaseTarget> databases, ref int clusterIndex)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Uri? injector = root.TryGetProperty("faultInjector", out var fi) && fi.GetString() is { Length: > 0 } s ? new Uri(s) : null;
        if (root.TryGetProperty("clusterIndex", out var ci)) clusterIndex = ci.GetInt32();
        foreach (var d in root.GetProperty("databases").EnumerateArray())
        {
            databases.Add(new DatabaseTarget(
                d.GetProperty("name").GetString() ?? "database",
                d.GetProperty("endpoint").GetString() ?? throw new ArgumentException("database without endpoint"),
                d.TryGetProperty("user", out var u) ? u.GetString() : null,
                d.TryGetProperty("password", out var p) ? p.GetString() : null,
                d.TryGetProperty("tls", out var t) && t.ValueKind == JsonValueKind.True,
                d.TryGetProperty("trustAnyCertificate", out var ta) && ta.ValueKind == JsonValueKind.True,
                d.TryGetProperty("bdbId", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null));
        }
        return injector;
    }
}
