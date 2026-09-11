using Microsoft.AspNetCore.Mvc;
using ProductMvc.Models;
using ProductMvc.Services;

namespace ProductMvc.Controllers;

public sealed class ProductController(IProductService service)
    : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
        => View(await service.ListAsync(cancellationToken));

    [HttpGet]
    public async Task<IActionResult> Details(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var product = await service.GetAsync(id, cancellationToken);
        return product is null ? NotFound() : View(product);
    }

    [HttpGet]
    public IActionResult Create() => View(new ProductCreateViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ProductCreateViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await service.CreateAsync(
            new CreateProductCommand(model.Name, model.Price),
            cancellationToken);

        TempData["Message"] = "商品已建立。";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var model = await service.GetForEditAsync(id, cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        [FromRoute] int id,
        ProductEditViewModel model,
        CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var updated = await service.UpdateAsync(
            new EditProductCommand(model.Id, model.Name, model.Price),
            cancellationToken);

        if (!updated)
        {
            return NotFound();
        }

        TempData["Message"] = "商品已更新。";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Delete(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var product = await service.GetAsync(id, cancellationToken);
        return product is null ? NotFound() : View(product);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteAsync(id, cancellationToken);

        if (!deleted)
        {
            return NotFound();
        }

        TempData["Message"] = "商品已刪除。";
        return RedirectToAction(nameof(Index));
    }
}
