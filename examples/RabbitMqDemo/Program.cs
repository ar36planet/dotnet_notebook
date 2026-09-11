using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var factory = new ConnectionFactory
{
    HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST")
        ?? "localhost"
};

await using var connection =
    await factory.CreateConnectionAsync();
await using var channel =
    await connection.CreateChannelAsync();

const string queueName = "product-created";

await channel.QueueDeclareAsync(
    queue: queueName,
    durable: true,
    exclusive: false,
    autoDelete: false,
    arguments: new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum"
    });

if (args.Contains("consume", StringComparer.OrdinalIgnoreCase))
{
    var consumer = new AsyncEventingBasicConsumer(channel);
    consumer.ReceivedAsync += async (_, delivery) =>
    {
        var message = Encoding.UTF8.GetString(delivery.Body.ToArray());
        Console.WriteLine($"received: {message}");

        await channel.BasicAckAsync(
            delivery.DeliveryTag,
            multiple: false);
    };

    await channel.BasicConsumeAsync(
        queue: queueName,
        autoAck: false,
        consumer: consumer);

    Console.WriteLine("consumer is running; press Ctrl+C to stop");
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
else
{
    var body = Encoding.UTF8.GetBytes(
        "{\"eventId\":\"demo\",\"productId\":42}");

    await channel.BasicPublishAsync(
        exchange: string.Empty,
        routingKey: queueName,
        body: body);

    Console.WriteLine("published product-created");
}
