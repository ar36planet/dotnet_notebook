---
title: 37 RabbitMQ 與 ASP.NET Core 非同步訊息
tags: [aspnet-core, rabbitmq, messaging, microservices, async]
---

# 37 RabbitMQ 與 ASP.NET Core 非同步訊息

## 學習目標

- 分辨 synchronous HTTP call 和 asynchronous message。
- 看懂 producer、exchange、queue、consumer、ack 和 retry。
- 用 RabbitMQ.Client 7 建立長生命週期 connection、channel 和 hosted service。
- 分辨 publisher confirm 與 consumer acknowledgement 解決的不同問題。
- 在 at-least-once delivery 下設計 retry、dead-letter 和 idempotency。

## 1. 一句話理解

RabbitMQ 把 producer 和 consumer 解耦：producer 將 message 發到 broker，consumer 之後從 queue 取出處理；這讓短暫的 consumer 中斷不必阻塞 producer，但也把重試、重複訊息、順序和關機排空交給 application 設計。

先看會出事的場景：訂單 API 收到付款請求後，同步呼叫付款、寄信和庫存三個服務。只要寄信服務慢，整個 HTTP request 就跟著 timeout。改成發布「訂單已建立」事件後，付款、通知和庫存可以各自消費；但 broker 可能在 consumer ack 前重新送出同一個 event，consumer 必須能安全地處理重複 delivery。

## 2. RabbitMQ 與 Java 對照

### 元件關係

~~~text
Producer
   ↓ publish
Exchange
   ↓ binding
Queue
   ↓ delivery
Consumer
   ↓ ack / nack
RabbitMQ broker
~~~

- Exchange 決定 message 如何依 routing key 路由到 queue。
- 一個下游服務應有自己的 queue 和 binding；同一 queue 的多個 consumer 是 competing consumers，不是不同的 consumer group。
- Queue durable 只表示 queue 定義在 broker restart 後可恢復；message 也要設為 persistent，兩者都不能取代 quorum、backup 和監控。

Java 的 Spring AMQP 通常用 `RabbitTemplate` 發布、`@RabbitListener` 或 listener container 消費；`ackMode=MANUAL`、prefetch、retry queue、DLQ 和 outbox 的責任與 .NET 相同。Java client 的 channel 也不應被多個 concurrent request 無互斥共用；語言不同，不會改變 broker 的 delivery semantics。

## 3. C# 語法

### Connection 與 channel ownership

`IConnection` 適合由 application 或 hosted service 長期持有；不要每個 HTTP request 建立 connection。`IChannel` 不可讓多個 concurrent publish 無互斥共用，因為 publish sequence、confirm 和 protocol frame 需要有明確的 owner。

常見配置是：

- 一個 process 共用長生命週期 `IConnection`。
- 每個 publisher worker 使用自己的 channel；若多個 request 共用 publisher，使用單一序列化 publisher、有限 channel pool，或明確的 semaphore。
- 每個 consumer worker 使用自己的 consumer channel；retry publisher 使用另一個 channel，不能在 consumer callback 中同時對同一 channel publish 和 ack。
- connection recovery 可以協助重連，但 publish 發生 connection failure 時，broker 是否已接受 message 仍可能不明；recovery 不是 exactly-once。

RabbitMQ.Client 7 開啟 publisher confirmations 的 channel：

~~~csharp
await using var connection =
    await factory.CreateConnectionAsync(cancellationToken);

await using var channel = await connection.CreateChannelAsync(
    new CreateChannelOptions(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true,
        outstandingPublisherConfirmationsRateLimiter: null,
        consumerDispatchConcurrency: null),
    cancellationToken);
~~~

這個 constructor 的四個參數是 RabbitMQ.Client 7 的 API；本章範例使用相同寫法。

### Publisher confirm、mandatory 與穩定的 MessageId

`BasicPublishAsync` 要同時使用 `mandatory: true` 和 publisher confirmations：

~~~csharp
var properties = new BasicProperties
{
    Persistent = true,
    ContentType = "application/json",
    Type = "ProductCreated",
    MessageId = eventId
};

await channel.BasicPublishAsync(
    exchange: "notebook.product-events",
    routingKey: "product.created",
    mandatory: true,
    basicProperties: properties,
    body: body,
    cancellationToken: cancellationToken);
~~~

confirmation 代表 broker 回覆 publish 已被接受；`mandatory` 讓 unroutable message 回傳。RabbitMQ.Client 7 的 `BasicPublishAsync` 會把 nack 或 basic.return 反映成 `PublishException`；timeout、socket 關閉或 cancellation 則可能只得到「結果不明」。這三種情況要分開記錄：可確定失敗的 publish 可以重試，結果不明的 publish 要保留相同 `MessageId`／`EventId` 再重試，consumer 以 inbox 或 business key 去重。

`MessageId` 不能在每次 retry 時重新產生。outbox publisher 應把 event ID 存在資料庫，只有收到 broker confirm 後才把 outbox row 標記為已發布；若 confirm 後、資料庫 mark 前 process crash，重啟時會再次發布相同 event ID，這是可接受的 at-least-once 流程。

### Consumer acknowledgement、prefetch 與 retry

consumer 要在 business side effect 完成後才 ack：

~~~csharp
await channel.BasicQosAsync(
    prefetchSize: 0,
    prefetchCount: 8,
    global: false,
    cancellationToken: cancellationToken);

try
{
    await handler.HandleAsync(message, cancellationToken);
    await channel.BasicAckAsync(
        delivery.DeliveryTag,
        multiple: false,
        cancellationToken: CancellationToken.None);
}
catch (TransientDependencyException)
{
    // 先把 message 發到已確認的 retry queue，再 ack 原 delivery。
    // retry queue 以 TTL 到期後 dead-letter 回 main exchange。
}
catch (JsonException)
{
    await channel.BasicNackAsync(
        delivery.DeliveryTag,
        multiple: false,
        requeue: false,
        cancellationToken: CancellationToken.None);
}
~~~

prefetch 是「尚未 ack 的 in-flight delivery 上限」；數值不是越大越快，應用 handler latency、記憶體和 broker metrics 實測。retry 不要立即無限 `requeue: true`，否則 poison message 會形成 hot loop。可用有上限的 retry queue：

~~~text
main queue
  ↓ transient failure
retry queue (TTL 5 seconds, persistent)
  ↓ dead-letter after TTL
main queue
  ↓ attempts exceeded or invalid message
dead-letter exchange → final DLQ
~~~

每次 retry 保留 event ID、attempt、原因和時間。超過上限時 `BasicNack(requeue: false)`，讓 broker 將 delivery 送到 final DLQ；DLQ 必須有 owner、保留期限、告警和人工處理流程。

### Hosted service 與 scoped DI

consumer 是 long-running worker，不要把 scoped `DbContext` 或 request service 直接注入成 singleton。每筆 delivery 建立 scope，完整 await handler 和 side effect，再 ack：

~~~csharp
await using var scope = scopeFactory.CreateAsyncScope();
var handler = scope.ServiceProvider
    .GetRequiredService<IProductCreatedHandler>();

await handler.HandleAsync(message, stoppingToken);
await consumerChannel.BasicAckAsync(
    delivery.DeliveryTag,
    multiple: false,
    CancellationToken.None);
~~~

graceful shutdown 的順序是：cancel consumer、停止接收新 delivery、等待 in-flight handler 完成或明確 nack、關閉 consumer channel、最後關閉 connection。`Task.Delay(Timeout.InfiniteTimeSpan)` 只負責保持 worker 存活，不能取代這個 drain 流程。

## 4. 實務範例：ProductCreated publisher、retry 與 DLQ

`examples/RabbitMqDemo` 是 .NET 10 + RabbitMQ.Client 7 範例，包含：

- durable direct exchange、quorum main queue、quorum retry queue 和 final DLQ。
- publisher confirmation tracking、`mandatory: true`、persistent message 和穩定 `MessageId`。
- prefetch 8、每筆 delivery 的 async DI scope、handler 完成後才 ack。
- 5 秒 TTL retry、最多 3 次處理、invalid message 與超限 transient failure 都進 DLQ。
- retry publish 確認後才 ack 原 delivery；確認 timeout 時保留 stable event ID，接受可能重複。
- worker 關機時先 `BasicCancelAsync`，再 drain in-flight delivery。

啟動 RabbitMQ 和範例的完整命令在 [examples/RabbitMqDemo/README.md](examples/RabbitMqDemo/README.md)。先以 `dotnet build` 驗證程式，再啟動 consumer 和 publisher：

~~~bash
dotnet build examples/RabbitMqDemo/RabbitMqDemo.csproj \
  --configuration Release --warnaserror -p:NuGetAudit=false
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj -- consume
dotnet run --project examples/RabbitMqDemo/RabbitMqDemo.csproj
~~~

`RABBITMQ_FAIL_EVENT_ID=<event-id>` 可以讓範例 handler 模擬 transient dependency failure；同一 event 會經過 TTL retry，第三次失敗後進入 `notebook.product-created.dlq`。這個 failure injection 只用於本機示範，不是正式錯誤處理策略。

### Outbox 與 inbox 的邊界

直接在 controller 先 commit Product、再 publish event 有 race：database commit 成功而 process 在 publish 前停止，商品存在但下游永遠收不到 event。outbox 把兩筆資料放在同一個 database transaction：

~~~text
HTTP request
  ↓
one database transaction
  ├── Product row
  └── OutboxMessage(EventId, Payload, PublishedAt = null)
        ↓ publisher worker
      confirmed RabbitMQ publish
        ↓
      set PublishedAt
~~~

confirm 後 mark 前 crash 仍可能重複發布，所以每個下游要用唯一的 EventId 建立 inbox／deduplication record，並把「去重記錄」和 business side effect 放在同一個 transaction。這是 at-least-once + idempotency，不是跨 SQL Server 和 RabbitMQ 的 exactly-once transaction。

### 正式環境的安全與監控

本機範例使用專用的 `notebook_app` user 和 `/notebook` vhost；正式環境還要限制 user 只能存取需要的 exchange／queue，禁止共用 `guest`，並使用 secret provider，不把 password 放在 repo。跨主機連線使用 TLS，啟用 certificate validation，不用 `AcceptablePolicyErrors` 放寬驗證來掩蓋設定問題。

至少要觀察：publish confirm failure、nack、mandatory return、connection／topology recovery、queue ready depth、unacked depth、message age、consumer capacity、retry 次數和 DLQ depth／age。只監控 process 是否 alive，無法看出 consumer 已經卡住或 poison message 正在堆積。

## 5. 常見誤解

- publisher confirm 不等於 consumer ack；前者是 producer 到 broker，後者是 broker delivery 到 consumer 的兩個方向。
- `await BasicPublishAsync` 不等於 database transaction 已經和 RabbitMQ 一致；outbox 只把遺失窗口改成可恢復的重試流程。
- durable queue 加 persistent message 不等於 message 永遠不會遺失；仍要選擇 queue type、replication、backup、retention 和 recovery 策略。
- 同一 queue 的多個 consumer 是 competing consumers；要讓付款、通知和庫存各收一次，必須各自有 queue 和 binding。
- `requeue: true` 不是 retry policy；沒有 delay 和上限的 requeue 會造成 poison message hot loop。
- consumer 收到 message 不代表 side effect 成功；ack 要放在 side effect 完成後。
- connection recovery 不會消除 publish outcome ambiguity，也不會替 consumer 做 idempotency。
- 不要在 ASP.NET Core request 裡每次建立 connection，也不要把同一個 `IChannel` 無互斥注入多個 concurrent request。
- `async void` 不適合 publisher 或 consumer handler；使用 `Task`，讓 hosted service 能 await、記錄錯誤和排空工作。

## 6. 面試怎麼回答

> 我會把 RabbitMQ 描述成 producer 和 consumer 之間的 broker。Producer 發到 exchange，exchange 依 binding 路由到各下游自己的 queue；consumer 用 manual acknowledgement 表示 business side effect 已經完成。Producer 端用 persistent message、mandatory 和 publisher confirms，consumer 端設定 prefetch，失敗走有延遲和上限的 retry queue，超限進 DLQ。因為常見語意是 at-least-once，我會用穩定的 EventId 和 inbox 或 business key 做 idempotency。若 database write 和 publish 不能有遺失窗口，我會把資料和 outbox row 放在同一個 database transaction，收到 broker confirm 後才標記 outbox，而不會假設 RabbitMQ 和 SQL Server 自動共用 transaction。

## 7. 小練習

1. 為 ProductCreated event 設計 EventId、OccurredAt、AggregateId 和 SchemaVersion，並說明哪些欄位在 retry 時不能改變。
2. 為 database timeout 設計 5 秒、30 秒、5 分鐘的 retry queue 與 final DLQ；指出每個 queue 的 TTL 和 dead-letter routing key。
3. 說明 publisher confirm、mandatory return 和 consumer ack 的方向差異，以及各自失敗時要保留的資訊。
4. 設計 inbox 的唯一鍵和 transaction 邊界，讓同一 EventId 重送時不會重複扣款。
5. 說明 process 在 broker confirm 後、更新 `OutboxMessage.PublishedAt` 前 crash 時，下一次 publisher 要怎麼安全恢復。
