using UserApi.Contracts;
using UserApi.Domain;
using UserApi.Infrastructure;
using UserApi.Repositories;

namespace UserApi.Services;

public sealed class UserService(
    IUserRepository repository,
    IClock clock) : IUserService
{
    public async Task<UserResponse?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var user = await repository.FindByIdAsync(
            id, cancellationToken);

        return user is null ? null : ToResponse(user);
    }

    public async Task<IReadOnlyList<UserResponse>> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        var users = await repository.GetAllAsync(cancellationToken);

        return users
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(ToResponse)
            .ToList();
    }

    public async Task<UserResponse> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException(
                "Name is required.", nameof(request.Name));
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            PhoneNumber = request.PhoneNumber,
            CreatedAt = clock.UtcNow
        };

        repository.Add(user);
        await repository.SaveChangesAsync(cancellationToken);
        return ToResponse(user);
    }

    private static UserResponse ToResponse(User user)
        => new(user.Id, user.Name, user.CreatedAt);
}
