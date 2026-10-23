using System.Diagnostics;

namespace LavaRand.Services;

/// <summary>Feeds the high-resolution timing of every request into the pool.</summary>
public sealed class RequestTimingMiddleware(RequestDelegate next, EntropyPool pool)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        pool.AddEntropy("request-arrival", start);

        await next(context);

        pool.AddEntropy("request-duration", Stopwatch.GetTimestamp() - start);
    }
}
