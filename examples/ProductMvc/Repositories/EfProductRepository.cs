using Microsoft.EntityFrameworkCore;
using ProductMvc.Data;
using ProductMvc.Domain;

namespace ProductMvc.Repositories;

public sealed class EfProductRepository(ProductDbContext db)
    : IProductRepository
{
    public async Task<IReadOnlyList<Product>> ListAsync(
        CancellationToken cancellationToken)
        => await db.Products
            .AsNoTracking()
            .OrderBy(product => product.Id)
            .ToListAsync(cancellationToken);

    public Task<Product?> FindAsync(
        int id,
        CancellationToken cancellationToken)
        => db.Products.SingleOrDefaultAsync(
            product => product.Id == id,
            cancellationToken);

    public void Add(Product product) => db.Products.Add(product);

    public void Remove(Product product) => db.Products.Remove(product);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
        => db.SaveChangesAsync(cancellationToken);
}
