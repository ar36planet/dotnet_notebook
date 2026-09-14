# ContainerApi sample

這是 .NET 10 multi-stage Dockerfile 的最小範例。

```bash
dotnet run --project examples/ContainerApi/ContainerApi.csproj --urls http://localhost:5080
curl http://localhost:5080/health/live
```

如果本機安裝 Docker，從 repo root 使用明確的 build context：

```bash
docker build -f examples/ContainerApi/Dockerfile -t container-api:10.0 examples/ContainerApi
docker run --rm -p 8080:8080 container-api:10.0
curl http://localhost:8080/health/live
```

`/health/live` 不檢查外部服務；`/health/ready` 是 readiness 入口。production 應由 Compose／Kubernetes 設定 probe，並使用不可覆寫的 image digest。
