using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using ProductMvc.Data;
using ProductMvc.Repositories;
using ProductMvc.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddDbContext<ProductDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("Products")
        ?? "Data Source=products.db"));
builder.Services.AddScoped<IProductRepository, EfProductRepository>();
builder.Services.AddScoped<IProductService, ProductService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

await app.Services.InitializeDatabaseAsync();


app.Run();
