var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "container-api",
    framework = ".NET 10"
}));

app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
