using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var redisConfiguration = builder.Configuration["Redis:Configuration"];
var allowMemoryFallback = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Redis:AllowMemoryFallback");

if (string.IsNullOrWhiteSpace(redisConfiguration) && allowMemoryFallback)
{
    builder.Services.AddDistributedMemoryCache();
}
else if (!string.IsNullOrWhiteSpace(redisConfiguration))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConfiguration;
        options.InstanceName = "dotnet-notebook:";
    });
}
else
{
    throw new InvalidOperationException(
        "Redis:Configuration is required outside an explicit Development memory-cache opt-in.");
}

builder.Services.AddSingleton<IProductReader, FakeProductReader>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet(
    "/products/{id:int}",
    async (
        int id,
        HttpContext httpContext,
        IDistributedCache cache,
        IProductReader reader,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var tenantId = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantId))
            return Results.BadRequest("X-Tenant-Id is required.");

        var key = $"v1:tenant:{Uri.EscapeDataString(tenantId)}:product:{id}";
        string? cached = null;

        try
        {
            cached = await cache.GetStringAsync(key, cancellationToken);
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "Redis read failed for {CacheKey}", key);
        }

        if (cached is not null)
        {
            try
            {
                var product = JsonSerializer.Deserialize<ProductResponse>(cached);
                if (product is not null)
                    return Results.Ok(new { source = "cache", product });
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Invalid cache payload for {CacheKey}", key);
            }

            try
            {
                await cache.RemoveAsync(key, cancellationToken);
            }
            catch (RedisException exception)
            {
                logger.LogWarning(exception, "Redis delete failed for {CacheKey}", key);
            }
        }

        var result = await reader.FindAsync(
            id,
            cancellationToken);

        if (result is null)
        {
            return Results.NotFound();
        }

        try
        {
            await cache.SetStringAsync(
                key,
                JsonSerializer.Serialize(result),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                    SlidingExpiration = TimeSpan.FromMinutes(1)
                },
                cancellationToken);
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "Redis write failed for {CacheKey}", key);
        }

        return Results.Ok(new { source = "reader", product = result });
    });

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();

public sealed record ProductResponse(
    int Id,
    string Name,
    decimal Price);

public interface IProductReader
{
    Task<ProductResponse?> FindAsync(
        int id,
        CancellationToken cancellationToken);
}

public sealed class FakeProductReader : IProductReader
{
    public Task<ProductResponse?> FindAsync(
        int id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ProductResponse? result = id == 42
            ? new ProductResponse(42, "USB-C 充電器", 890m)
            : null;

        return Task.FromResult(result);
    }
}
