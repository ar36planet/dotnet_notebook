using UserApi.Infrastructure;
using UserApi.Repositories;
using UserApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();

// In a production app, replace this with AddDbContext + an EF Core repository.
builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IUserService, UserService>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();

app.Run();

// Makes the entry point discoverable by WebApplicationFactory in integration tests.
public partial class Program { }
