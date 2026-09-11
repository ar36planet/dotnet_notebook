using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

var builder = WebApplication.CreateBuilder(args);
var redisConfiguration = builder.Configuration["Redis:Configuration"];

if (string.IsNullOrWhiteSpace(redisConfiguration))
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConfiguration;
        options.InstanceName = "dotnet-notebook:";
    });
}

builder.Services.AddSingleton<IProductReader, FakeProductReader>();

var app = builder.Build();

app.MapGet(
    "/products/{id:int}",
    async (
        int id,
        IDistributedCache cache,
        IProductReader reader,
        CancellationToken cancellationToken) =>
    {
        var key = $"product:{id}";
        var cached = await cache.GetStringAsync(
            key,
            cancellationToken);

        if (cached is not null)
        {
            var product = JsonSerializer.Deserialize<ProductResponse>(
                cached);
            return Results.Ok(new { source = "cache", product });
        }

        var result = await reader.FindAsync(
            id,
            cancellationToken);

        if (result is null)
        {
            return Results.NotFound();
        }

        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(result),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow =
                    TimeSpan.FromMinutes(5)
            },
            cancellationToken);

        return Results.Ok(new { source = "reader", product = result });
    });

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
