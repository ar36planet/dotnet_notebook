using System.ComponentModel.DataAnnotations;

namespace ProductMvc.Models;

public sealed class ProductViewModel
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public decimal Price { get; init; }
}

public sealed class ProductCreateViewModel
{
    [Required(ErrorMessage = "請輸入商品名稱。")]
    [StringLength(120, ErrorMessage = "商品名稱不能超過 120 個字。")]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, 100000, ErrorMessage = "價格必須介於 0.01 到 100,000。")]
    public decimal Price { get; set; }
}

public sealed class ProductEditViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "請輸入商品名稱。")]
    [StringLength(120, ErrorMessage = "商品名稱不能超過 120 個字。")]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, 100000, ErrorMessage = "價格必須介於 0.01 到 100,000。")]
    public decimal Price { get; set; }
}
