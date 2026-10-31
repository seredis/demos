using StackExchange.Redis;
using StackExchange.Redis.Availability;

namespace GeoFailoverTui;

// Setup.cs - the complete connection setup, and nothing else: two members, one group, retries
// attached once. Everything after this is plain IDatabaseAsync code - see CoreUsage.cs.
sealed partial class DemoEngine
{
    public const string EastService = "redis-east";
    public const string WestService = "redis-west";
    private const string EastEndpoint = "localhost:16401";
    private const string WestEndpoint = "localhost:16402";

    private async Task<IConnectionGroup> ConnectGroupAsync()
    {
        // higher weight -> the preferred (active) member while healthy
        _east = new ConnectionGroupMember(EastEndpoint, name: EastService) { Weight = 1.0 };
        _west = new ConnectionGroupMember(WestEndpoint, name: WestService) { Weight = 0.5 };

        MultiGroupOptions options = new MultiGroupOptions.Builder
        {
            // the circuit-breaker defaults target production volumes; lower thresholds let it trip
            // within a couple of failed commands in a low-traffic demo
            CircuitBreaker = new CircuitBreaker.Builder { MinimumNumberOfFailures = 2, MetricsWindowSize = TimeSpan.FromSeconds(2) },
            // re-evaluate member health every second (default 5s), so recovery is noticed quickly
            HealthCheckInterval = TimeSpan.FromSeconds(1),
            // return to the recovered highest-weight member once it has been stable for 5s
            FailbackDelay = TimeSpan.FromSeconds(5),
            RetryPolicy = new RetryPolicy.Builder { MaxAttempts = 5, RetryDelay = TimeSpan.FromMilliseconds(200) },
        };

        var group = await ConnectionMultiplexer.ConnectGroupAsync([_east, _west], options);

        // retries are opt-in: wrap the database once and reuse the wrapper (the second wrapper,
        // with MaxAttempts = 1, is what the 'A' key switches to - no retries at all)
        _retrying = group.GetDatabase().WithRetry();
        _retryingOnce = group.GetDatabase().WithRetry(new RetryPolicy.Builder { MaxAttempts = 1 });
        return group;
    }
}
