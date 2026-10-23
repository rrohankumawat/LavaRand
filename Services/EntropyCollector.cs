using System.Diagnostics;

namespace LavaRand.Services;

/// <summary>
/// Background service that keeps topping up the pool with "chaotic" server-side data.
/// Browser webcam / lava-lamp frames arrive separately via POST /api/entropy/frame.
/// </summary>
public sealed class EntropyCollector(
    EntropyPool pool,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<EntropyCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Entropy:IntervalSeconds", 2)));
        var cameraUrl = config["Entropy:CameraSnapshotUrl"];
        var useDrand = config.GetValue("Entropy:UseDrandBeacon", false);

        logger.LogInformation("Entropy collector started (camera: {Cam}, drand: {Drand})",
            string.IsNullOrWhiteSpace(cameraUrl) ? "off" : cameraUrl, useDrand);

        while (!ct.IsCancellationRequested)
        {
            // 1) Timing jitter: how long does a tiny busy loop really take?
            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < 1000; i++) Thread.SpinWait(1);
            pool.AddEntropy("timing-jitter", Stopwatch.GetTimestamp() - t0);

            // 2) Process / runtime noise
            pool.AddEntropy("gc", GC.GetTotalAllocatedBytes());
            pool.AddEntropy("threadpool", ThreadPool.CompletedWorkItemCount);
            pool.AddEntropy("clock", DateTime.UtcNow.Ticks);

            // 3) Optional: IP camera pointed at a real lava lamp (or anything that moves)
            if (!string.IsNullOrWhiteSpace(cameraUrl))
                await TryFetch("camera", cameraUrl, ct);

            // 4) Optional: public randomness beacon (mix-in only; everyone sees the same value)
            if (useDrand)
                await TryFetch("drand", "https://api.drand.sh/public/latest", ct);

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TryFetch(string source, string url, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var data = await client.GetByteArrayAsync(url, ct);
            pool.AddEntropy(source, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Entropy source {Source} unavailable", source);
        }
    }
}
