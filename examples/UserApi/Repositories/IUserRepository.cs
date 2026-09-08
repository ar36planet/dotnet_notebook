using UserApi.Domain;

namespace UserApi.Repositories;

public interface IUserRepository
{
    Task<User?> FindByIdAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> GetAllAsync(
        CancellationToken cancellationToken);

    Task AddAsync(
        User user,
        CancellationToken cancellationToken);
}
