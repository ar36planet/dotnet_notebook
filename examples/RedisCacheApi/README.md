# RedisCacheApi sample

這個 .NET 10 sample 預設使用 distributed memory cache；提供 Redis configuration 後會改用 Redis。

```bash
dotnet run --project examples/RedisCacheApi/RedisCacheApi.csproj --urls http://localhost:5081
curl http://localhost:5081/products/42
curl http://localhost:5081/products/42
```

使用 Redis：

```bash
docker run --rm --name notebook-redis -p 6379:6379 redis:7.4
Redis__Configuration=localhost:6379 dotnet run --project examples/RedisCacheApi/RedisCacheApi.csproj --urls http://localhost:5081
```
