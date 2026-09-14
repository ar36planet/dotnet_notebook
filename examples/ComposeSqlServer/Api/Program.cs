using System.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
var connectionString = OrdersConnectionString.Create(builder.Configuration);

builder.Services.AddSingleton(new OrdersConnection(connectionString));
builder.Services.AddHealthChecks()
    .AddCheck<SqlServerHealthCheck>("sql", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapGet("/api/orders", async (
    int? pageSize,
    OrdersConnection connection,
    CancellationToken cancellationToken) =>
{
    var take = Math.Clamp(pageSize ?? 20, 1, 100);
    await using var sql = new SqlConnection(connection.Value);
    await sql.OpenAsync(cancellationToken);

    await using var command = sql.CreateCommand();
    command.CommandText = """
        SELECT TOP (@PageSize) Id, UserId, Status, TotalAmount, CreatedAt
        FROM dbo.Orders
        ORDER BY CreatedAt DESC, Id DESC;
        """;
    command.Parameters.Add("@PageSize", SqlDbType.Int).Value = take;

    var orders = new List<OrderResponse>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        orders.Add(new OrderResponse(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetDateTimeOffset(4)));
    }

    return Results.Ok(orders);
});

app.Run();

public sealed record OrderResponse(
    long Id,
    Guid UserId,
    string Status,
    decimal TotalAmount,
    DateTimeOffset CreatedAt);

public sealed record OrdersConnection(string Value);

public static class OrdersConnectionString
{
    public static string Create(IConfiguration configuration)
    {
        var host = configuration["DB_HOST"] ?? "localhost,15433";
        var user = configuration["DB_USER"] ?? "orders_app";
        var password = ReadSecret(configuration["DB_PASSWORD_FILE"]);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = host,
            InitialCatalog = configuration["DB_NAME"] ?? "Orders",
            UserID = user,
            Password = password,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 5
        };

        return builder.ConnectionString;
    }

    private static string ReadSecret(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Environment.GetEnvironmentVariable("DB_PASSWORD")
                ?? throw new InvalidOperationException("DB_PASSWORD_FILE is required.");

        return File.ReadAllText(path).Trim();
    }
}

public sealed class SqlServerHealthCheck(OrdersConnection connection)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var sql = new SqlConnection(connection.Value);
            await sql.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("SQL Server is not ready.", exception);
        }
    }
}
