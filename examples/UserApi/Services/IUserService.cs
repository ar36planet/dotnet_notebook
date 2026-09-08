using UserApi.Contracts;

namespace UserApi.Services;

public interface IUserService
{
    Task<UserResponse?> GetAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserResponse>> GetActiveAsync(
        CancellationToken cancellationToken);

    Task<UserResponse> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken);
}
