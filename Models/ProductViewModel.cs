using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ABCRetail.Cloud.Models.ViewModels
{
    public class ProductViewModel
    {
        // Primary Identifier (RowKey alias for UI binding)
        [HiddenInput]
        public string Id { get; set; } = string.Empty;

        // RowKey used in Azure Table Storage
        [HiddenInput]
        public string RowKey { get; set; } = string.Empty;

        [Required(ErrorMessage = "Product name is required")]
        [StringLength(100, ErrorMessage = "Name cannot exceed 100 characters")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Description is required")]
        [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
        public string Description { get; set; } = string.Empty;

        [Required(ErrorMessage = "Price is required")]
        [Range(0.01, 10000, ErrorMessage = "Price must be between R0.01 and R10,000")]
        [DataType(DataType.Currency)]
        public double Price { get; set; }   // ✅ Changed to double

        [Required(ErrorMessage = "Stock quantity is required")]
        [Range(0, 10000, ErrorMessage = "Quantity must be between 0 and 10,000")]
        public int StockQuantity { get; set; }

        [Required(ErrorMessage = "Category is required")]
        [StringLength(50, ErrorMessage = "Category cannot exceed 50 characters")]
        public string Category { get; set; } = string.Empty;

        [Display(Name = "Product Image")]
        public IFormFile? ImageFile { get; set; }

        [HiddenInput]
        public string ExistingImageUrl { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        // Always default PartitionKey for Azure Table Storage
        [HiddenInput]
        public string PartitionKey { get; set; } = "Products";
    }
}
