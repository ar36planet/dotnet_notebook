using System.Collections.Concurrent;
using UserApi.Domain;

namespace UserApi.Repositories;

public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<Guid, User> _users = new(
        new[]
        {
            new User
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Name = "Ada",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2)
            },
            new User
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Name = "Grace",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            }
        }.ToDictionary(x => x.Id));

    public Task<User?> FindByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _users.TryGetValue(id, out var user);
        return Task.FromResult(user);
    }

    public Task<IReadOnlyList<User>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<User> snapshot = _users.Values.ToList();
        return Task.FromResult(snapshot);
    }

    public Task AddAsync(
        User user,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_users.TryAdd(user.Id, user))
        {
            throw new InvalidOperationException(
                $"A user with id {user.Id} already exists.");
        }

        return Task.CompletedTask;
    }
}
