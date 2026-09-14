# RedisCacheApi sample

這個 .NET 10 sample 只有在 Development 明確設定 `Redis__AllowMemoryFallback=true` 時才使用 distributed memory cache；Production 沒有 Redis 設定會 fail fast。

```bash
Redis__AllowMemoryFallback=true dotnet run --project examples/RedisCacheApi/RedisCacheApi.csproj --urls http://localhost:5081
curl -H 'X-Tenant-Id: demo' http://localhost:5081/products/42
curl -H 'X-Tenant-Id: demo' http://localhost:5081/products/42
```

使用 Redis：

```bash
docker run --rm --name notebook-redis -p 127.0.0.1:6379:6379 redis:7.4
Redis__Configuration=localhost:6379 Redis__AllowMemoryFallback=false dotnet run --project examples/RedisCacheApi/RedisCacheApi.csproj --urls http://localhost:5081
curl -H 'X-Tenant-Id: demo' http://localhost:5081/products/42
```
