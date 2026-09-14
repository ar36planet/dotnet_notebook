using UserApi.Domain;

namespace UserApi.Repositories;

public interface IUserRepository
{
    Task<User?> FindByIdAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> GetAllAsync(
        CancellationToken cancellationToken);

    void Add(User user);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
