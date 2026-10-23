using System.Threading.RateLimiting;
using LavaRand.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<EntropyPool>();
builder.Services.AddHostedService<EntropyCollector>();
builder.Services.AddHttpClient();

// The frame endpoint is public, so cap how fast one client can push data.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("frames", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromSeconds(10)
        }));
});

var app = builder.Build();

app.UseRateLimiter();
app.UseMiddleware<RequestTimingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// GET /api/random/bytes?length=32
api.MapGet("/random/bytes", (EntropyPool pool, int length = 32) =>
{
    if (length is < 1 or > 1024)
        return Results.BadRequest(new { error = "length must be between 1 and 1024" });

    var bytes = pool.GetBytes(length);
    return Results.Ok(new
    {
        length,
        hex = Convert.ToHexString(bytes).ToLowerInvariant(),
        base64 = Convert.ToBase64String(bytes)
    });
});

// GET /api/random/int?min=1&max=7   (max is exclusive, like Random.Next)
api.MapGet("/random/int", (EntropyPool pool, int min = 0, int max = 100) =>
{
    if (min >= max)
        return Results.BadRequest(new { error = "min must be less than max" });

    return Results.Ok(new { min, max, value = pool.GetInt32(min, max) });
});

// GET /api/random/token?length=32  (URL-safe token for API keys, reset links, etc.)
api.MapGet("/random/token", (EntropyPool pool, int length = 32) =>
{
    if (length is < 8 or > 256)
        return Results.BadRequest(new { error = "length must be between 8 and 256" });

    var token = Convert.ToBase64String(pool.GetBytes(length))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    return Results.Ok(new { token });
});

// POST /api/entropy/frame  (raw bytes: a webcam frame, canvas pixels, anything messy)
api.MapPost("/entropy/frame", async (HttpRequest request, EntropyPool pool) =>
{
    const int maxBytes = 2 * 1024 * 1024;

    if (request.ContentLength is > maxBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    if (ms.Length is 0 or > maxBytes)
        return Results.BadRequest(new { error = "body must be 1 byte to 2 MB" });

    pool.AddEntropy("browser-frame", ms.ToArray());
    return Results.Ok(new { accepted = ms.Length });
}).RequireRateLimiting("frames");

// GET /api/entropy/stats
api.MapGet("/entropy/stats", (EntropyPool pool) => Results.Ok(pool.GetStats()));

app.Run();
