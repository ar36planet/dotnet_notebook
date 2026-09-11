namespace ProductMvc.Services;

public sealed record CreateProductCommand(string Name, decimal Price);

public sealed record EditProductCommand(int Id, string Name, decimal Price);
