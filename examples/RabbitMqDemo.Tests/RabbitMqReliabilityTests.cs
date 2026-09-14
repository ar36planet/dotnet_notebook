using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;
using Xunit.Sdk;

namespace RabbitMqDemo.Tests;

public sealed class RabbitMqReliabilityTests
{
    [RabbitMqFact]
    public async Task Confirmed_publish_reaches_bound_queue()
    {
        await using var connection = await TestBroker.ConnectAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var exchange = $"notebook.test.{suffix}";
        var queue = $"notebook.test.queue.{suffix}";
        const string routingKey = "product.created";
        var eventId = $"event-{suffix}";

        await using var publisher = await connection.CreateChannelAsync(
            new CreateChannelOptions(true, true, null, null));
        await using var consumer = await connection.CreateChannelAsync();
        await DeclareBoundQueueAsync(consumer, exchange, queue, routingKey);

        await publisher.BasicPublishAsync(
            exchange,
            routingKey,
            mandatory: true,
            basicProperties: new BasicProperties
            {
                Persistent = true,
                MessageId = eventId
            },
            body: "{}"u8.ToArray());

        var delivery = await GetEventuallyAsync(consumer, queue);

        Assert.Equal(eventId, delivery.BasicProperties.MessageId);
        await consumer.BasicAckAsync(
            delivery.DeliveryTag,
            multiple: false);
    }

    [RabbitMqFact]
    public async Task Mandatory_unroutable_publish_is_rejected()
    {
        await using var connection = await TestBroker.ConnectAsync();
        var exchange = $"notebook.test.{Guid.NewGuid():N}";

        await using var publisher = await connection.CreateChannelAsync(
            new CreateChannelOptions(true, true, null, null));
        await publisher.ExchangeDeclareAsync(
            exchange,
            ExchangeType.Direct,
            durable: false,
            autoDelete: true);

        await Assert.ThrowsAsync<PublishException>(async () =>
        {
            await publisher.BasicPublishAsync(
                exchange,
                routingKey: "no.binding",
                mandatory: true,
                basicProperties: new BasicProperties
                {
                    Persistent = true,
                    MessageId = Guid.NewGuid().ToString("N")
                },
                body: "{}"u8.ToArray());
        });
    }

    [RabbitMqFact]
    public async Task Nack_without_requeue_moves_delivery_to_dlq()
    {
        await using var connection = await TestBroker.ConnectAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var exchange = $"notebook.test.{suffix}";
        var deadLetterExchange = $"notebook.test.dlx.{suffix}";
        var queue = $"notebook.test.queue.{suffix}";
        var deadLetterQueue = $"notebook.test.dlq.{suffix}";
        const string routingKey = "product.created";
        var eventId = $"event-{suffix}";

        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(
            exchange,
            ExchangeType.Direct,
            durable: false,
            autoDelete: true);
        await channel.ExchangeDeclareAsync(
            deadLetterExchange,
            ExchangeType.Direct,
            durable: false,
            autoDelete: true);
        await channel.QueueDeclareAsync(
            queue,
            durable: false,
            exclusive: false,
            autoDelete: true,
            arguments: new Dictionary<string, object?>
            {
                [Headers.XDeadLetterExchange] = deadLetterExchange,
                [Headers.XDeadLetterRoutingKey] = routingKey
            });
        await channel.QueueDeclareAsync(
            deadLetterQueue,
            durable: false,
            exclusive: false,
            autoDelete: true);
        await channel.QueueBindAsync(queue, exchange, routingKey);
        await channel.QueueBindAsync(deadLetterQueue, deadLetterExchange, routingKey);

        await channel.BasicPublishAsync(
            exchange,
            routingKey,
            mandatory: true,
            basicProperties: new BasicProperties { MessageId = eventId },
            body: "{}"u8.ToArray());
        var delivery = await GetEventuallyAsync(channel, queue);

        await channel.BasicNackAsync(
            delivery.DeliveryTag,
            multiple: false,
            requeue: false);
        var deadLetter = await GetEventuallyAsync(channel, deadLetterQueue);

        Assert.Equal(eventId, deadLetter.BasicProperties.MessageId);
        await channel.BasicAckAsync(deadLetter.DeliveryTag, multiple: false);
    }

    [RabbitMqFact]
    public async Task Requeue_can_redeliver_the_same_message_id()
    {
        await using var connection = await TestBroker.ConnectAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var exchange = $"notebook.test.{suffix}";
        var queue = $"notebook.test.queue.{suffix}";
        const string routingKey = "product.created";
        var eventId = $"event-{suffix}";

        await using var channel = await connection.CreateChannelAsync();
        await DeclareBoundQueueAsync(channel, exchange, queue, routingKey);
        await channel.BasicPublishAsync(
            exchange,
            routingKey,
            mandatory: true,
            basicProperties: new BasicProperties { MessageId = eventId },
            body: "{}"u8.ToArray());

        var first = await GetEventuallyAsync(channel, queue);
        await channel.BasicNackAsync(
            first.DeliveryTag,
            multiple: false,
            requeue: true);
        var second = await GetEventuallyAsync(channel, queue);

        Assert.Equal(eventId, first.BasicProperties.MessageId);
        Assert.Equal(eventId, second.BasicProperties.MessageId);
        await channel.BasicAckAsync(second.DeliveryTag, multiple: false);
    }

    private static async Task DeclareBoundQueueAsync(
        IChannel channel,
        string exchange,
        string queue,
        string routingKey)
    {
        await channel.ExchangeDeclareAsync(
            exchange,
            ExchangeType.Direct,
            durable: false,
            autoDelete: true);
        await channel.QueueDeclareAsync(
            queue,
            durable: false,
            exclusive: false,
            autoDelete: true);
        await channel.QueueBindAsync(queue, exchange, routingKey);
    }

    private static async Task<BasicGetResult> GetEventuallyAsync(
        IChannel channel,
        string queue)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: false);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new XunitException($"No message arrived in queue '{queue}'.");
    }
}

internal static class TestBroker
{
    public static async Task<IConnection> ConnectAsync()
    {
        var host = Environment.GetEnvironmentVariable("RABBITMQ_TEST_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException(
                "RabbitMqFact should skip when RABBITMQ_TEST_HOST is absent.");
        }

        var factory = new ConnectionFactory
        {
            HostName = host,
            UserName = Environment.GetEnvironmentVariable("RABBITMQ_TEST_USER")
                ?? "notebook_app",
            Password = Environment.GetEnvironmentVariable("RABBITMQ_TEST_PASSWORD")
                ?? "notebook_password",
            VirtualHost = Environment.GetEnvironmentVariable("RABBITMQ_TEST_VHOST")
                ?? "/notebook",
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            ClientProvidedName = "notebook-rabbitmq-tests"
        };

        return await factory.CreateConnectionAsync();
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("RABBITMQ_TEST_HOST")))
        {
            Skip = "Set RABBITMQ_TEST_HOST to run RabbitMQ integration tests.";
        }
    }
}
