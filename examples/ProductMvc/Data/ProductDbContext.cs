using Microsoft.EntityFrameworkCore;
using ProductMvc.Domain;

namespace ProductMvc.Data;

public sealed class ProductDbContext(DbContextOptions<ProductDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name)
                .HasMaxLength(120)
                .IsRequired();
            entity.Property(product => product.Price)
                .HasPrecision(18, 2);
        });
    }
}

public static class ProductDatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await db.Database.EnsureCreatedAsync();

        if (await db.Products.AnyAsync())
        {
            return;
        }

        db.Products.AddRange(
            new Product
            {
                Name = "USB-C 充電器",
                Price = 890m,
                CreatedAt = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero)
            },
            new Product
            {
                Name = "人體工學鍵盤",
                Price = 2_490m,
                CreatedAt = new DateTimeOffset(2026, 2, 3, 9, 0, 0, TimeSpan.Zero)
            });

        await db.SaveChangesAsync();
    }
}
