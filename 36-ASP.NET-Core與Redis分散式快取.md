---
title: 36 ASP.NET Core 與 Redis 分散式快取
tags: [aspnet-core, redis, cache, distributed-cache, docker]
---

# 36 ASP.NET Core 與 Redis 分散式快取

## 學習目標

- 分辨 cache、database 和 message broker 的責任。
- 使用 IDistributedCache 實作 cache-aside。
- 設定 Redis 的 key、TTL、序列化和失效策略。
- 知道分散式快取在多個 API replica 下解決什麼問題。
- 避免把 Redis 當成不加設計的 primary database。

## 1. 一句話理解

Redis cache 是 application 和主要資料來源之間的快速暫存層；cache miss 時讀 database，成功後把結果寫入 Redis，下一次 request 可以直接讀 cache。

先看會出事的場景：API 有三個 replica，每個 instance 都用自己的記憶體 dictionary 快取商品。使用者第一次打到 replica A，第二次打到 replica B，兩台看到的 cache 完全不同；更新商品後，舊資料也可能留在其中一台。需要跨 instance 共用的 cache 應使用 Redis 或其他 distributed cache。

## 2. ASP.NET Core 語法

### 註冊 Redis 或本機 fallback

~~~csharp
var builder = WebApplication.CreateBuilder(args);

var redisConfiguration =
    builder.Configuration["Redis:Configuration"];

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
~~~

正式環境由 configuration 注入：

~~~json
{
  "Redis": {
    "Configuration": "redis:6379"
  }
}
~~~

在 Compose 中使用 redis:6379；API 從 host 直接測試時，才可能使用 localhost:6379 或映射後的 host port。不要把兩者混在同一份 production connection string。

### Cache-aside

~~~csharp
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
            return Results.Ok(new { source = "redis", product });
        }

        var result = await reader.FindAsync(
            id,
            cancellationToken);

        if (result is null)
        {
            return Results.NotFound();
        }

        var json = JsonSerializer.Serialize(result);
        await cache.SetStringAsync(
            key,
            json,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow =
                    TimeSpan.FromMinutes(5)
            },
            cancellationToken);

        return Results.Ok(new { source = "database", product = result });
    });
~~~

讀取順序是：

~~~text
request
  ↓
Redis hit → 回 cache
  ↓ miss
database → mapping → 寫 Redis → 回 response
~~~

### Update 時失效 cache

~~~csharp
await productWriter.UpdateAsync(
    command,
    cancellationToken);

await cache.RemoveAsync(
    $"product:{command.Id}",
    cancellationToken);
~~~

先更新 database，再刪除 cache 是常見的 cache-aside 做法。若刪除 cache 失敗，仍要有 logging、retry 或 background repair 策略；不要假設 cache 永遠可用。

### TTL 和 key 設計

~~~text
product:42
product:list:page=1:size=20:keyword=keyboard
customer:guid:orders:2026-09
~~~

key 要包含足夠的 query identity，避免不同使用者、租戶或查詢條件共用錯誤資料。TTL 應根據資料變動速度和可接受的 stale window 決定；TTL 不是資料正確性的替代品。

## 3. 實務範例：Redis container + .NET 10

本 repo 的 examples/RedisCacheApi 可以在沒有 Redis 時使用 distributed memory cache 執行；設定 Redis connection 後才切換到 Redis。

~~~bash
dotnet run --project examples/RedisCacheApi/RedisCacheApi.csproj
curl http://localhost:5081/products/42
~~~

啟動本機 Redis：

~~~bash
docker run --rm \
  --name notebook-redis \
  --publish 6379:6379 \
  redis:7.4
~~~

再以 environment variable 啟動 API：

~~~bash
Redis__Configuration=localhost:6379 \
dotnet run --project examples/RedisCacheApi/RedisCacheApi.csproj
~~~

第一次查詢會讀取 fake database 並寫入 cache；後續查詢會回 cache。這個 sample 用固定 reader 讓 cache 行為可以獨立觀察，真實專案把 reader 換成 EF Core projection。

## 4. 常見誤解

- Redis cache 不是 database backup；cache 被清空後，application 必須能從主要資料來源重建。
- IDistributedCache 只提供 key/value cache contract，不替你設計 key、serialization、invalidation 或 stampede protection。
- 加 Redis 不會自動解決 cache stampede；熱門 key 過期時仍可能有大量 request 同時查 database。
- 只在單一 instance 使用 IMemoryCache 沒問題，但多 replica 時要清楚它不是共用 cache。
- cache 內容通常是 DTO 或 response snapshot，不要直接序列化含 EF Core tracking state 的 entity graph。
- 使用者、租戶和權限相關資料要避免 key collision；錯誤 key 可能造成資料跨使用者洩漏。
- Redis container 需要 persistence、memory limit、eviction policy、network security 和 monitoring，不能只寫一行 docker run 就當 production 完成。

## 5. 面試怎麼回答

> 分散式快取是多個 application instance 共用的暫存層。常見 cache-aside 流程是先用有版本和查詢條件的 key 查 Redis，命中就回傳；miss 才查 database，映射成 DTO 後寫入 Redis，並設定 TTL。更新 database 後要失效相關 key。Redis 不能取代主要資料來源，快取失效、stale data、stampede、serialization 和 eviction policy 都要納入設計。

## 6. 小練習

1. 為 Product list 設計包含 page、size、keyword 的 cache key。
2. 為商品 Edit 和 Delete 寫出需要刪除的 cache keys。
3. 比較 IMemoryCache 和 IDistributedCache 在兩個 API replica 下的行為。
4. 設計五分鐘 TTL 過期時的 cache stampede 防護。
5. 把 Redis Compose service 加進第 35 章的 API stack，讓 API 使用 service name 連線。
