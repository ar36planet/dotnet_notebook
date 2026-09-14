using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

if (args.Contains("consume", StringComparer.OrdinalIgnoreCase))
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<IProductCreatedHandler, ProductCreatedHandler>();
    builder.Services.AddHostedService<RabbitMqConsumerWorker>();

    await builder.Build().RunAsync();
    return;
}

await PublishOnceAsync(CancellationToken.None);

static async Task PublishOnceAsync(CancellationToken cancellationToken)
{
    await using var connection =
        await RabbitMqConnection.CreateAsync("notebook-producer", cancellationToken);
    await using var channel = await connection.CreateChannelAsync(
        new CreateChannelOptions(true, true, null, null),
        cancellationToken);

    await RabbitMqTopology.DeclareAsync(channel, cancellationToken);

    var eventId = Environment.GetEnvironmentVariable("RABBITMQ_EVENT_ID")
        ?? Guid.NewGuid().ToString("N");
    var message = new ProductCreated(
        eventId,
        ProductId: 42,
        ProductName: "機械式鍵盤",
        DateTimeOffset.UtcNow);

    var body = JsonSerializer.SerializeToUtf8Bytes(message);
    var properties = RabbitMqTopology.CreateProperties(
        message.EventId,
        attempt: 0);

    try
    {
        await RabbitMqPublisher.PublishConfirmedAsync(
            channel,
            RabbitMqTopology.MainRoutingKey,
            properties,
            body,
            cancellationToken);

        Console.WriteLine(
            $"published eventId={message.EventId} productId={message.ProductId}");
    }
    catch (PublishException exception)
    {
        Console.Error.WriteLine(
            $"publish rejected by broker; eventId={message.EventId}; " +
            $"nack or mandatory return: {exception.Message}");
        throw;
    }
    catch (PublishOutcomeUnknownException exception)
    {
        Console.Error.WriteLine(
            $"publish outcome is unknown; keep eventId={message.EventId} " +
            $"stable before retrying: {exception.Message}");
        throw;
    }
}

public sealed record ProductCreated(
    string EventId,
    int ProductId,
    string ProductName,
    DateTimeOffset OccurredAt);

public interface IProductCreatedHandler
{
    Task HandleAsync(ProductCreated message, CancellationToken cancellationToken);
}

public sealed class ProductCreatedHandler : IProductCreatedHandler
{
    public async Task HandleAsync(
        ProductCreated message,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);

        var failureEventId = Environment.GetEnvironmentVariable(
            "RABBITMQ_FAIL_EVENT_ID");
        if (string.Equals(
                failureEventId,
                message.EventId,
                StringComparison.Ordinal))
        {
            throw new TransientDependencyException(
                $"demo dependency failure for event {message.EventId}");
        }

        Console.WriteLine(
            $"handled eventId={message.EventId} productId={message.ProductId}");
    }
}

public sealed class RabbitMqConsumerWorker(
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection =
            await RabbitMqConnection.CreateAsync("notebook-consumer", stoppingToken);
        await using var consumerChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(false, false, null, 1),
            stoppingToken);
        await using var publisherChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(true, true, null, null),
            stoppingToken);

        await RabbitMqTopology.DeclareAsync(consumerChannel, stoppingToken);
        await consumerChannel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: RabbitMqTopology.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken);

        using var retryPublishGate = new SemaphoreSlim(1, 1);
        var inFlight = new ConcurrentDictionary<ulong, Task>();
        var consumer = new AsyncEventingBasicConsumer(consumerChannel);

        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var processing = ProcessDeliveryAsync(
                delivery,
                consumerChannel,
                publisherChannel,
                retryPublishGate,
                stoppingToken);
            inFlight[delivery.DeliveryTag] = processing;

            try
            {
                await processing;
            }
            finally
            {
                inFlight.TryRemove(delivery.DeliveryTag, out Task? removed);
            }
        };

        var consumerTag = await consumerChannel.BasicConsumeAsync(
            queue: RabbitMqTopology.MainQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        Console.WriteLine(
            $"consumer is running queue={RabbitMqTopology.MainQueue} " +
            $"prefetch={RabbitMqTopology.PrefetchCount}; press Ctrl+C to stop");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown is the expected exit path.
        }
        finally
        {
            try
            {
                await consumerChannel.BasicCancelAsync(
                    consumerTag,
                    noWait: false,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"consumer cancellation failed: {exception.Message}");
            }

            await DrainInFlightAsync(inFlight);
        }
    }

    private async Task ProcessDeliveryAsync(
        BasicDeliverEventArgs delivery,
        IChannel consumerChannel,
        IChannel publisherChannel,
        SemaphoreSlim retryPublishGate,
        CancellationToken stoppingToken)
    {
        var messageId = delivery.BasicProperties.MessageId ?? "missing";
        var attempt = RabbitMqTopology.ReadAttempt(delivery.BasicProperties.Headers);

        try
        {
            var message = JsonSerializer.Deserialize<ProductCreated>(
                delivery.Body.Span);
            if (message is null || string.IsNullOrWhiteSpace(message.EventId))
            {
                throw new JsonException("ProductCreated.EventId is required.");
            }
            messageId = message.EventId;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider
                .GetRequiredService<IProductCreatedHandler>();
            await handler.HandleAsync(message, stoppingToken);

            await consumerChannel.BasicAckAsync(
                delivery.DeliveryTag,
                multiple: false,
                cancellationToken: CancellationToken.None);
        }
        catch (TransientDependencyException exception)
        {
            if (attempt + 1 >= RabbitMqTopology.MaxAttempts)
            {
                await SafeNackAsync(consumerChannel, delivery.DeliveryTag, requeue: false);
                Console.Error.WriteLine(
                    $"dead-lettered eventId={messageId} attempts={attempt + 1}: " +
                    exception.Message);
                return;
            }

            try
            {
                await retryPublishGate.WaitAsync(stoppingToken);
                try
                {
                    var retryProperties = RabbitMqTopology.CreateProperties(
                        messageId,
                        attempt + 1);
                    await RabbitMqPublisher.PublishConfirmedAsync(
                        publisherChannel,
                        RabbitMqTopology.RetryRoutingKey,
                        retryProperties,
                        delivery.Body.ToArray(),
                        stoppingToken);
                }
                finally
                {
                    retryPublishGate.Release();
                }

                // Ack only after the retry publish has a broker confirmation.
                await consumerChannel.BasicAckAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    cancellationToken: CancellationToken.None);
                Console.WriteLine(
                    $"scheduled retry eventId={messageId} " +
                    $"attempt={attempt + 1}/{RabbitMqTopology.MaxAttempts}");
            }
            catch (Exception retryException)
            {
                // Requeue is safe only as a recovery path here; the retry queue
                // normally absorbs transient failures and prevents a hot loop.
                await SafeNackAsync(consumerChannel, delivery.DeliveryTag, requeue: true);
                Console.Error.WriteLine(
                    $"retry publish failed; requeued eventId={messageId}: " +
                    retryException.Message);
            }
        }
        catch (JsonException exception)
        {
            await SafeNackAsync(consumerChannel, delivery.DeliveryTag, requeue: false);
            Console.Error.WriteLine(
                $"dead-lettered invalid event messageId={messageId}: " +
                exception.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Do not ack work interrupted by shutdown. The broker will redeliver.
            await SafeNackAsync(consumerChannel, delivery.DeliveryTag, requeue: true);
        }
        catch (Exception exception)
        {
            await SafeNackAsync(consumerChannel, delivery.DeliveryTag, requeue: false);
            Console.Error.WriteLine(
                $"dead-lettered event messageId={messageId}: {exception.Message}");
        }
    }

    private static async Task DrainInFlightAsync(
        ConcurrentDictionary<ulong, Task> inFlight)
    {
        while (true)
        {
            var pending = inFlight.Values.ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending);
        }
    }

    private static async Task SafeNackAsync(
        IChannel channel,
        ulong deliveryTag,
        bool requeue)
    {
        try
        {
            await channel.BasicNackAsync(
                deliveryTag,
                multiple: false,
                requeue,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"nack failed deliveryTag={deliveryTag}: {exception.Message}");
        }
    }
}

public static class RabbitMqConnection
{
    public static async Task<IConnection> CreateAsync(
        string clientName,
        CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST")
                ?? "localhost",
            UserName = Environment.GetEnvironmentVariable("RABBITMQ_USER")
                ?? "notebook_app",
            Password = Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD")
                ?? "notebook_password",
            VirtualHost = Environment.GetEnvironmentVariable("RABBITMQ_VHOST")
                ?? "/notebook",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            ClientProvidedName = clientName
        };

        return await factory.CreateConnectionAsync(cancellationToken);
    }
}

public static class RabbitMqTopology
{
    public const string MainExchange = "notebook.product-events";
    public const string DeadLetterExchange = "notebook.product-events.dlx";
    public const string MainQueue = "notebook.product-created";
    public const string RetryQueue = "notebook.product-created.retry.5s";
    public const string DeadLetterQueue = "notebook.product-created.dlq";
    public const string MainRoutingKey = "product.created";
    public const string RetryRoutingKey = "product.created.retry";
    public const string DeadLetterRoutingKey = "product.created.dead";
    public const string AttemptHeader = "x-notebook-attempt";
    public const ushort PrefetchCount = 8;
    public const int MaxAttempts = 3;

    public static async Task DeclareAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: MainExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            exchange: DeadLetterExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: MainQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                [Headers.XQueueType] = "quorum",
                [Headers.XDeadLetterExchange] = DeadLetterExchange,
                [Headers.XDeadLetterRoutingKey] = DeadLetterRoutingKey
            },
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: RetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                [Headers.XQueueType] = "quorum",
                [Headers.XMessageTTL] = 5_000,
                [Headers.XDeadLetterExchange] = MainExchange,
                [Headers.XDeadLetterRoutingKey] = MainRoutingKey
            },
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                [Headers.XQueueType] = "quorum"
            },
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: MainQueue,
            exchange: MainExchange,
            routingKey: MainRoutingKey,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: RetryQueue,
            exchange: MainExchange,
            routingKey: RetryRoutingKey,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: DeadLetterQueue,
            exchange: DeadLetterExchange,
            routingKey: DeadLetterRoutingKey,
            cancellationToken: cancellationToken);
    }

    public static BasicProperties CreateProperties(string eventId, int attempt) =>
        new()
        {
            Persistent = true,
            ContentType = "application/json",
            Type = "ProductCreated",
            MessageId = eventId,
            Headers = new Dictionary<string, object?>
            {
                [AttemptHeader] = attempt,
                ["event-id"] = eventId
            }
        };

    public static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue(AttemptHeader, out var value))
        {
            return 0;
        }

        return value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number when number <= int.MaxValue => (int)number,
            _ when int.TryParse(value?.ToString(), out var number) => number,
            _ => 0
        };
    }
}

public static class RabbitMqPublisher
{
    public static async Task PublishConfirmedAsync(
        IChannel channel,
        string routingKey,
        BasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await channel.BasicPublishAsync(
                exchange: RabbitMqTopology.MainExchange,
                routingKey,
                mandatory: true,
                basicProperties: properties,
                body,
                cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PublishOutcomeUnknownException(
                "publisher confirmation timed out; the broker may have accepted the message.");
        }
    }
}

public sealed class TransientDependencyException(string message) : Exception(message);

public sealed class PublishOutcomeUnknownException(string message) : Exception(message);
