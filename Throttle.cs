class Throttle
{
    private TimeSpan Interval { get; set; }
    private Task TimerTask { get; set; }

    public Throttle(TimeSpan interval)
    {
        Interval = interval;
        TimerTask = Task.CompletedTask;
    }

    public async Task<T> RunThrottled<T>(Func<Task<T>> taskFunc)
    {
        await TimerTask;
        var result = await taskFunc();
        TimerTask = Task.Run(() => Task.Delay(Interval));
        return result;
    }
}