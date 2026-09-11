using ProductMvc.Models;

namespace ProductMvc.Services;

public interface IProductService
{
    Task<IReadOnlyList<ProductViewModel>> ListAsync(
        CancellationToken cancellationToken);

    Task<ProductViewModel?> GetAsync(
        int id,
        CancellationToken cancellationToken);

    Task<int> CreateAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken);

    Task<ProductEditViewModel?> GetForEditAsync(
        int id,
        CancellationToken cancellationToken);

    Task<bool> UpdateAsync(
        EditProductCommand command,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken);
}
