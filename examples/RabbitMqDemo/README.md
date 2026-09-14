# RabbitMqDemo sample

這是 .NET 10 + RabbitMQ.Client 7 的 publisher／consumer 範例。它使用專用 local user/vhost；正式環境要從 secret provider 注入密碼，並使用 TLS certificate validation。

## 啟動本機 broker

Docker daemon 可用時：

```bash
docker run --rm --name notebook-rabbitmq \
  --publish 127.0.0.1:5672:5672 \
  --publish 127.0.0.1:15672:15672 \
  --env RABBITMQ_DEFAULT_USER=notebook_app \
  --env RABBITMQ_DEFAULT_PASS=notebook_password \
  --env RABBITMQ_DEFAULT_VHOST=/notebook \
  rabbitmq:4-management
```

management UI 是 `http://localhost:15672`，只在本機使用。範例預設連到 `localhost`、`notebook_app`、`notebook_password` 和 `/notebook`；其他環境請設定 `RABBITMQ_HOST`、`RABBITMQ_USER`、`RABBITMQ_PASSWORD`、`RABBITMQ_VHOST`。

## 編譯與執行

先確認範例可編譯：

```bash
dotnet build examples/RabbitMqDemo/RabbitMqDemo.csproj \
  --configuration Release --warnaserror -p:NuGetAudit=false
```

integration tests 需要另外設定 `RABBITMQ_TEST_HOST`；沒有 broker 時會明確 skip：

```bash
RABBITMQ_TEST_HOST=localhost \
RABBITMQ_TEST_USER=notebook_app \
RABBITMQ_TEST_PASSWORD=notebook_password \
RABBITMQ_TEST_VHOST=/notebook \
dotnet test examples/RabbitMqDemo.Tests/RabbitMqDemo.Tests.csproj \
  --configuration Release --warnaserror -p:NuGetAudit=false
```

測試使用每個 test 自己的 exchange／queue，涵蓋 confirmed publish、mandatory unroutable publish、nack 到 DLQ 和 duplicate delivery；它不會共用正式 sample 的 queue，也不會把 local broker 的狀態帶進下一個 test。

Terminal 1 啟動 consumer：

```bash
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj -- consume
```

Terminal 2 發送一筆 `ProductCreated`：

```bash
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj
```

每次 publisher run 都會產生一個 EventId；要讓重試或人工重送保留同一個 ID：

```bash
RABBITMQ_EVENT_ID=product-created-20260915-001 \
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj
```

設定 `RABBITMQ_FAIL_EVENT_ID` 等於該 EventId，會讓 sample handler 模擬 transient dependency failure。message 會進入 5 秒 TTL retry queue，最多處理 3 次；仍失敗就進 `notebook.product-created.dlq`：

```bash
RABBITMQ_FAIL_EVENT_ID=product-created-20260915-001 \
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj -- consume
```

程式碼中的 retry publish 取得 broker confirm 後才 ack 原 delivery；confirm timeout 或 connection failure 時不保證結果，stable EventId 可能造成重複，這正是下游 inbox／idempotency 要處理的情況。按 Ctrl+C 時 worker 先取消 consumer，再等待 in-flight handler，最後關閉 channel 和 connection。

這個 sample 需要可連線的 RabbitMQ 才能執行 broker integration path；沒有 broker 時仍可用上面的 `dotnet build` 驗證程式碼。
