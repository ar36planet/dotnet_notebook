using ProductMvc.Domain;

namespace ProductMvc.Repositories;

public interface IProductRepository
{
    Task<IReadOnlyList<Product>> ListAsync(
        CancellationToken cancellationToken);

    Task<Product?> FindAsync(
        int id,
        CancellationToken cancellationToken);

    void Add(Product product);

    void Remove(Product product);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
