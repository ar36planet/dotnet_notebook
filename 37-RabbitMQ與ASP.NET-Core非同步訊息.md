---
title: 37 RabbitMQ 與 ASP.NET Core 非同步訊息
tags: [aspnet-core, rabbitmq, messaging, microservices, async]
---

# 37 RabbitMQ 與 ASP.NET Core 非同步訊息

## 學習目標

- 分辨 synchronous HTTP call 和 asynchronous message。
- 看懂 producer、exchange、queue、consumer、ack 和 retry。
- 用 RabbitMQ .NET client 7 建立長生命週期 connection / channel。
- 知道 publisher confirms 和 consumer acknowledgements 解決的問題不同。
- 設計 at-least-once delivery 下的 idempotency。

## 1. 一句話理解

RabbitMQ 把 producer 和 consumer 解耦：producer 將 message 發到 broker，consumer 之後從 queue 取出處理；這讓短暫的 consumer 中斷不必阻塞 producer，但也把重試、重複訊息和順序問題交給 application 設計。

先看會出事的場景：訂單 API 收到付款請求後，同步呼叫付款、寄信和庫存三個服務。只要寄信服務慢，整個 HTTP request 就跟著 timeout。把「訂單已建立」發布成 message，讓付款、通知和庫存各自消費，可以縮短前端等待時間；但每個 consumer 必須能安全地處理重複 delivery。

## 2. RabbitMQ 語法

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

- Producer 不應假設某個 consumer 一定在線。
- Exchange 決定 message 如何路由到 queue。
- Queue 保存尚未被成功處理的 delivery。
- Consumer 處理完成後才送 manual acknowledgement。
- 一個 message 被送到多個 queue，才會由多個 consumer group 各自收到；同一 queue 的多個 consumer 通常是競爭消費。

### .NET 10 producer

RabbitMQ .NET client 7 的 connection 和 channel 都應該長生命週期，不要每個 request 建立一條 connection。最小 producer：

~~~csharp
var factory = new ConnectionFactory
{
    HostName = configuration["RabbitMQ:Host"] ?? "localhost"
};

await using var connection =
    await factory.CreateConnectionAsync();
await using var channel =
    await connection.CreateChannelAsync();

await channel.QueueDeclareAsync(
    queue: "product-created",
    durable: true,
    exclusive: false,
    autoDelete: false,
    arguments: new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum"
    });

var body = JsonSerializer.SerializeToUtf8Bytes(message);

await channel.BasicPublishAsync(
    exchange: string.Empty,
    routingKey: "product-created",
    body: body);
~~~

範例專案在 examples/RabbitMqDemo；它可以用 consume argument 啟動 consumer，也可以不帶 argument 發送一筆測試 message。

### Consumer acknowledgement

Consumer 不應在收到 message 的第一行就 ack：

~~~csharp
var consumer = new AsyncEventingBasicConsumer(channel);

consumer.ReceivedAsync += async (_, delivery) =>
{
    try
    {
        var message = JsonSerializer.Deserialize<ProductCreated>(
            delivery.Body.Span);

        if (message is null)
        {
            await channel.BasicNackAsync(
                delivery.DeliveryTag,
                multiple: false,
                requeue: false);
            return;
        }

        await handler.HandleAsync(
            message,
            cancellationToken);

        await channel.BasicAckAsync(
            delivery.DeliveryTag,
            multiple: false);
    }
    catch (TransientDependencyException)
    {
        await channel.BasicNackAsync(
            delivery.DeliveryTag,
            multiple: false,
            requeue: true);
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Message handling failed");

        await channel.BasicNackAsync(
            delivery.DeliveryTag,
            multiple: false,
            requeue: false);
    }
};
~~~

ack 的時機是「business side effect 已經成功」，不是「bytes 已經讀到」。如果 process 在 ack 前中斷，RabbitMQ 可能再次 delivery；這正是 at-least-once delivery 的預期行為。

### Publisher confirms

Consumer ack 是 broker 對 consumer delivery 的確認；publisher confirm 是 broker 對 producer publish 的確認。兩者方向不同，不能互相取代。不能接受 message 遺失的 producer 應啟用 publisher confirms，並處理 nack、timeout 和 connection failure。

### Prefetch、retry、dead-letter

Consumer 可以限制未 ack 的 in-flight delivery 數量，避免單一 consumer 被大量工作壓垮。失敗訊息不要無限 requeue，常見做法是：

~~~text
main queue
  ↓ transient failure
retry queue with delay
  ↓ retry limit exceeded
dead-letter queue
~~~

每次重試都要保留 event id、attempt count、原因和時間；dead-letter queue 要有可觀測性和人工處理流程。

## 3. 實務範例：Order API 發布 ProductCreated

Controller 只負責建立 application command：

~~~csharp
[HttpPost]
public async Task<IActionResult> Create(
    CreateProductRequest request,
    CancellationToken cancellationToken)
{
    var product = await service.CreateAsync(
        request,
        cancellationToken);

    await publisher.PublishAsync(
        new ProductCreated(
            Guid.NewGuid(),
            product.Id,
            product.Name,
            DateTimeOffset.UtcNow),
        cancellationToken);

    return CreatedAtAction(
        nameof(Get),
        new { id = product.Id },
        product);
}
~~~

這個簡化流程仍有一致性問題：database 已 commit，但 publish 失敗時，商品已存在而 event 不見。正式 microservice 通常使用 outbox pattern：

~~~text
HTTP request
  ↓
同一個 database transaction
  ├── Product row
  └── OutboxMessage row
        ↓ background publisher
      RabbitMQ
~~~

Outbox publisher 成功後標記 outbox row；如果 process 重啟，可以從未發布的 outbox rows 繼續。Consumer 仍需 idempotency，因為 producer retry 可能送出同一個 event 多次。

## 4. 常見誤解

- RabbitMQ 不會自動讓同步 business operation 變成一致的 transaction。
- consumer 收到 message 不等於 business side effect 成功；ack 要放在成功處理後。
- publisher confirm 不等於 consumer ack；一個確認 producer 到 broker，另一個確認 broker 到 consumer。
- at-least-once delivery 代表可能重複，consumer 要用 EventId、business key 或 inbox table 去重。
- async void 不適合 producer service；使用 Task，讓 caller 能 await、捕捉例外和取消。
- 不要每個 HTTP request 都建立 RabbitMQ connection；connection / channel 應由 hosted service 或 publisher abstraction 管理。
- 無限 requeue 會形成 poison message loop；要有 retry 上限和 dead-letter。
- queue durable 和 message persistent 不能取代 backup、replication、quorum queue 或 broker monitoring。

## 5. 面試怎麼回答

> RabbitMQ 用 broker 把 producer 和 consumer 解耦。Producer 把 event 發到 exchange，依 binding 路由到 queue；consumer 以 manual acknowledgement 表示 message 已成功處理。Publisher confirms 和 consumer ack 是兩個方向的可靠性機制。因為常見語意是 at-least-once，所以 consumer 必須 idempotent，錯誤訊息要有 retry / dead-letter 策略；如果 database write 和 message publish 要有一致性，會考慮 outbox pattern，而不是假設 RabbitMQ 和 SQL Server 自動共用 transaction。

## 6. 小練習

1. 為 ProductCreated event 設計 EventId、OccurredAt、AggregateId 和 SchemaVersion。
2. 設計一個 consumer 在 database timeout 時的 retry / nack 流程。
3. 說明 publisher confirm 和 consumer ack 的方向差異。
4. 為重複 delivery 設計 inbox table 或 idempotency key。
5. 把同步呼叫 NotificationService 的 controller 改成 outbox + RabbitMQ 流程。
