using System.Threading.RateLimiting;

class Throttle
{
    private RateLimiter RateLimiter { get; set; }

    public Throttle(TimeSpan interval)
    {
        RateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions{
            ReplenishmentPeriod = interval,
            TokensPerPeriod = 1,
            AutoReplenishment = true,
            TokenLimit = 1
        });
    }

    public async Task<T> RunThrottled<T>(Func<Task<T>> taskFunc)
    {
        using var rateLimitLease = await RateLimiter.AcquireAsync(0);
        return await taskFunc();
    }
}