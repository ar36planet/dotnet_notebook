namespace UserApi.Domain;

public sealed class User
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; private set; } = true;

    public void Deactivate() => IsActive = false;
}
