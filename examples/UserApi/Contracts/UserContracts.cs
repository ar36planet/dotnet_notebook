namespace UserApi.Contracts;

public sealed record CreateUserRequest(
    string Name,
    string? PhoneNumber);

public sealed record UserResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt);
