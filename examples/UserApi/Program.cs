using UserApi.Infrastructure;
using UserApi.Repositories;
using UserApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<UserExceptionHandler>();
builder.Services
    .AddAuthentication("DemoHeader")
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DemoHeaderAuthenticationHandler>(
        "DemoHeader",
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("users.read", policy =>
        policy.RequireClaim("permission", "users.read"));
});

// In a production app, replace this with AddDbContext + an EF Core repository.
builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IUserService, UserService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
