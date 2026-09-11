using ProductMvc.Domain;

namespace ProductMvc.Repositories;

public interface IProductRepository
{
    Task<IReadOnlyList<Product>> ListAsync(
        CancellationToken cancellationToken);

    Task<Product?> FindAsync(
        int id,
        CancellationToken cancellationToken);

    Task AddAsync(
        Product product,
        CancellationToken cancellationToken);

    void Remove(Product product);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
