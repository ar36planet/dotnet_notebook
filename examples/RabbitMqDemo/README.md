# RabbitMqDemo sample

這是 .NET 10 + RabbitMQ.Client 7 的最小 producer / consumer 範例。需要先啟動 RabbitMQ：

```bash
docker run --rm --name notebook-rabbitmq \
  -p 5672:5672 -p 15672:15672 \
  rabbitmq:4-management
```

先啟動 consumer：

```bash
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj -- consume
```

另一個 terminal 發送 message：

```bash
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj
```
