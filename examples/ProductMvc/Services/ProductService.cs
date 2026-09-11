using ProductMvc.Domain;
using ProductMvc.Models;
using ProductMvc.Repositories;

namespace ProductMvc.Services;

public sealed class ProductService(
    IProductRepository repository) : IProductService
{
    public async Task<IReadOnlyList<ProductViewModel>> ListAsync(
        CancellationToken cancellationToken)
    {
        var products = await repository.ListAsync(cancellationToken);

        return products
            .Select(ToViewModel)
            .ToList();
    }

    public async Task<ProductViewModel?> GetAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(id, cancellationToken);
        return product is null ? null : ToViewModel(product);
    }

    public async Task<int> CreateAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = new Product
        {
            Name = command.Name.Trim(),
            Price = command.Price,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await repository.AddAsync(product, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return product.Id;
    }

    public async Task<ProductEditViewModel?> GetForEditAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(id, cancellationToken);

        return product is null
            ? null
            : new ProductEditViewModel
            {
                Id = product.Id,
                Name = product.Name,
                Price = product.Price
            };
    }

    public async Task<bool> UpdateAsync(
        EditProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(
            command.Id,
            cancellationToken);

        if (product is null)
        {
            return false;
        }

        product.Name = command.Name.Trim();
        product.Price = command.Price;

        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(id, cancellationToken);

        if (product is null)
        {
            return false;
        }

        repository.Remove(product);
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ProductViewModel ToViewModel(Product product)
        => new()
        {
            Id = product.Id,
            Name = product.Name,
            Price = product.Price
        };
}
