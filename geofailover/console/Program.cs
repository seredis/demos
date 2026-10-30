// E2E geo failover/failback showcase for SE.Redis connection groups.
//
// Runs GET/SET traffic against two local Redis containers (see docker-compose.yml),
// stops the highest-weight ("active") one to trigger a failover, then restarts it to
// observe the automatic failback. The whole scenario is automated, including
// stopping/starting the docker container.
//
// Prerequisites: `docker compose up -d --wait` (see README.md).
//
// This is a port of the redis-py example at
// https://github.com/redis-developer/redis-py-geo-failover-example

using System.Diagnostics;
using StackExchange.Redis;
using StackExchange.Redis.Availability;

const string ActiveService = "redis-east"; // docker-compose service of the highest-weight member

const string EastConfiguration = "localhost:16401";
const string WestConfiguration = "localhost:16402";

// --max-attempts N (default 5). With 5 no error surfaces; with 1 (no retries at all) the operations in
// flight when redis-east is stopped fail, and the traffic loop prints what the fault looked like.
int maxAttempts = 5;
for (int a = 0; a < args.Length; a++)
{
    string arg = args[a], value;
    if (arg.StartsWith("--max-attempts=", StringComparison.Ordinal)) value = arg["--max-attempts=".Length..];
    else if (arg == "--max-attempts" && a + 1 < args.Length) value = args[++a];
    else
    {
        Console.Error.WriteLine($"Unknown argument '{arg}'. Usage: dotnet run [-- --max-attempts N]");
        return 2;
    }
    if (!int.TryParse(value, out maxAttempts) || maxAttempts < 1)
    {
        Console.Error.WriteLine($"--max-attempts must be a positive integer, got '{value}'");
        return 2;
    }
}

ConnectionGroupMember[] members =
[
    // Higher weight -> preferred (active) member while healthy.
    new(EastConfiguration, name: "redis-east") { Weight = 1.0 },
    new(WestConfiguration, name: "redis-west") { Weight = 0.5 },
];

MultiGroupOptions options = new MultiGroupOptions.Builder
{
    // The circuit breaker defaults (MinimumNumberOfFailures = 1000) target production traffic
    // volumes; lower thresholds let the breaker trip within a couple of failed commands in this
    // low-traffic demo, so the group fails over promptly.
    CircuitBreaker = new CircuitBreaker.Builder
    {
        MinimumNumberOfFailures = 2,
        MetricsWindowSize = TimeSpan.FromSeconds(2),
    },

    // Re-evaluate member health every second (default: 5s), so recovery is noticed quickly.
    HealthCheckInterval = TimeSpan.FromSeconds(1),

    // Return to the recovered highest-weight member once it has gone 5s without a failure.
    // The default is zero (fail back as soon as a health check passes); a positive delay
    // keeps a flapping member out of rotation until it has been stable for the whole period.
    FailbackDelay = TimeSpan.FromSeconds(5),

    // At the default of 5, enough retries to bridge the failover window, so no error ever surfaces
    // to the traffic loop. The first retry waits (up to FailoverDelay, 5s) for the group to move to
    // another member and is then sent there; subsequent retries pause RetryDelay (+ jitter).
    RetryPolicy = new RetryPolicy.Builder
    {
        MaxAttempts = maxAttempts,
        RetryDelay = TimeSpan.FromMilliseconds(200),
    },
};

Console.WriteLine($"=== Connecting group to {ActiveService}:16401 (weight=1.0) and redis-west:16402 (weight=0.5); MaxAttempts={maxAttempts} ===");

await using IConnectionGroup group = await ConnectionMultiplexer.ConnectGroupAsync(members, options);

// The library reports member and active-member changes through one event;
// this listener makes the failover/failback moment explicit in the output.
group.ConnectionChanged += (_, e) =>
{
    if (e.Type == GroupConnectionChangedEventArgs.ChangeType.ActiveChanged)
    {
        Console.WriteLine($">>> ACTIVE MEMBER SWITCHED: {Describe(e.PreviousGroup)} -> {Describe(e.Group)}");
    }
    else
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{e.Type}] {e.Group.Name}");
    }
};

// Retries are opt-in: wrap the database once and reuse the wrapper. SET is a "last wins" write
// and GET is a read, so both are within the default retry category and are replayed after an
// ambiguous fault (a timeout, or a connection lost after the command was sent). An INCR would
// not be, because a replay could count twice.
IDatabaseAsync db = group.GetDatabase().WithRetry();

try
{
    await db.StringSetAsync("bootstrap", "ok");
}
catch (RedisException ex)
{
    Console.Error.WriteLine($"Could not connect to both Redis endpoints: {ex.Message}");
    Console.Error.WriteLine("Start the infrastructure first: docker compose up -d --wait");
    return 1;
}

foreach (var member in group.GetMembers())
{
    if (!member.IsConnected)
    {
        Console.Error.WriteLine($"Member '{member.Name}' is not connected.");
        Console.Error.WriteLine("Start the infrastructure first: docker compose up -d --wait");
        return 1;
    }
}

var traffic = new TrafficGenerator(db);
traffic.Start();

try
{
    await RunTrafficAsync(10, $"Phase 1: traffic flows to the active member ({ActiveService})");

    Console.WriteLine();
    Console.WriteLine($"!!! Stopping the ACTIVE member's container '{ActiveService}' - expect a failover");
    Console.WriteLine();
    DockerCompose("stop", ActiveService);

    await RunTrafficAsync(15, "Phase 2: traffic continues on redis-west, no exceptions");

    Console.WriteLine();
    Console.WriteLine($"!!! Restarting '{ActiveService}' - expect it to reconnect, then automatic failback within ~5s");
    Console.WriteLine();
    DockerCompose("start", ActiveService);

    await RunTrafficAsync(15, $"Phase 3: waiting for failback to {ActiveService}");
    await RunTrafficAsync(5, "Phase 4: traffic flows on the restored active member");
}
finally
{
    await traffic.StopAsync();
}

Console.WriteLine();
Console.WriteLine($"=== Scenario finished: {traffic.Operations} operations, {traffic.Errors.Count} errors ===");
return traffic.Errors.Count == 0 ? 0 : 1;

async Task RunTrafficAsync(int seconds, string label)
{
    Console.WriteLine($"--- {label} (running traffic for {seconds}s) ---");
    var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
    while (Stopwatch.GetTimestamp() < deadline)
    {
        await Task.Delay(2000);
        Console.WriteLine($"    traffic ok: {traffic.Operations} ops, active={Describe(group.ActiveMember)}");
    }
}

static string Describe(ConnectionGroupMember? member)
    => member is null ? "(none)" : $"{member.Name} (weight={member.Weight})";

static void DockerCompose(params string[] args)
{
    var psi = new ProcessStartInfo("docker")
    {
        WorkingDirectory = FindRepoRoot(),
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };
    psi.ArgumentList.Add("compose");
    foreach (var arg in args) psi.ArgumentList.Add(arg);

    using var process = Process.Start(psi)!;
    var stderr = process.StandardError.ReadToEnd();
    process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"'docker compose {string.Join(' ', args)}' failed:");
        Console.Error.WriteLine(stderr);
        Environment.Exit(1);
    }
}

// docker-compose.yml sits in the demo folder (geofailover/); walk up from the build output to find it.
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
        dir = dir.Parent;
    }
    return Environment.CurrentDirectory;
}

/// <summary>Background GET/SET traffic. Any surfaced exception fails the demo.</summary>
sealed class TrafficGenerator(IDatabaseAsync db)
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _operations;

    public int Operations => Volatile.Read(ref _operations);
    public List<Exception> Errors { get; } = [];

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        int i = 0;
        while (!_stop.IsCancellationRequested)
        {
            i++;
            try
            {
                await db.StringSetAsync($"key:{i}", $"value:{i}");
                await db.StringGetAsync($"key:{i}");
                Interlocked.Add(ref _operations, 2);
            }
            catch (Exception ex) // any surfaced error fails the demo
            {
                lock (Errors) Errors.Add(ex);
                Console.WriteLine($"!!! Traffic error: {ex.GetType().Name}: {ex.Message}");

                // FaultContext is what the retry controller itself uses to classify a fault; it normalises
                // RedisConnectionException / RedisTimeoutException / RedisServerException. NotApplied is the
                // interesting bit: true means the client knows the server never ran the command (still queued,
                // or a pre-execution server error such as LOADING), so a replay could not double-apply.
                var fault = new FaultContext(ex);
                var category = fault.Flags & CommandFlags.CommandRetryNever; // the retry-category bit region
                Console.WriteLine($"    kind={fault.ErrorKind} connection={fault.ConnectionFailureType} " +
                                  $"notApplied={fault.NotApplied} category={category}");
            }

            try
            {
                await Task.Delay(200, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        // allow an in-flight retrying operation to finish; the group is disposed after this
        await _loop.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
