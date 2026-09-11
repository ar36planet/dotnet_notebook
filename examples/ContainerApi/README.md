# ContainerApi sample

這是 .NET 10 multi-stage Dockerfile 的最小範例。

```bash
dotnet run --project examples/ContainerApi/ContainerApi.csproj --urls http://localhost:5080
curl http://localhost:5080/health
```

如果本機安裝 Docker：

```bash
cd examples/ContainerApi
docker build -t container-api:10.0 .
docker run --rm -p 8080:8080 container-api:10.0
curl http://localhost:8080/health
```
