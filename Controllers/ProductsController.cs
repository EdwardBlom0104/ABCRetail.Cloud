using ABCRetail.Cloud.Models;
using ABCRetail.Cloud.Models.ViewModels;
using Azure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ABCRetail.Cloud.Controllers
{
    [Authorize(Roles = "Admin")]
    public class ProductsController : Controller
    {
        private readonly AzureStorageService _storageService;

        public ProductsController(AzureStorageService storageService)
        {
            _storageService = storageService;
        }

        public async Task<IActionResult> Index()
        {
            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var products = await tableClient.QueryAsync<Product>(p => p.PartitionKey == "Products")
                    .ToListAsync();

                return View(products);
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error loading products.";
                return View(new List<Product>());
            }
        }

        [HttpGet]
        public IActionResult Create()
        {
            return View(new ProductViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ProductViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var product = new Product
                {
                    PartitionKey = "Products",
                    RowKey = Guid.NewGuid().ToString(),
                    Name = model.Name ?? string.Empty,
                    Description = model.Description ?? string.Empty,
                    Price = model.Price,
                    StockQuantity = model.StockQuantity,
                    Category = model.Category ?? string.Empty,
                    ImageUrl = model.ExistingImageUrl ?? string.Empty,
                    CreatedDate = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    IsActive = true
                };

                await tableClient.AddEntityAsync(product);

                TempData["SuccessMessage"] = "Product created successfully!";
                return RedirectToAction("Index");
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error creating product. Please try again.";
                return View(model);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Edit(string id)
        {
            if (string.IsNullOrEmpty(id))
                return NotFound();

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var productResponse = await tableClient.GetEntityAsync<Product>("Products", id);

                if (!productResponse.HasValue)
                    return NotFound();

                var product = productResponse.Value;

                var model = new ProductViewModel
                {
                    Id = product.RowKey,
                    RowKey = product.RowKey,
                    Name = product.Name ?? string.Empty,
                    Description = product.Description ?? string.Empty,
                    Price = product.Price,
                    StockQuantity = product.StockQuantity,
                    Category = product.Category ?? string.Empty,
                    ExistingImageUrl = product.ImageUrl ?? string.Empty,
                    ImageUrl = product.ImageUrl ?? string.Empty
                };

                return View(model);
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error loading product for editing.";
                return RedirectToAction("Index");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(ProductViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var productResponse = await tableClient.GetEntityAsync<Product>("Products", model.RowKey);

                if (!productResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Product not found.";
                    return RedirectToAction("Index");
                }

                var existingProduct = productResponse.Value;

                var updatedProduct = new Product
                {
                    PartitionKey = "Products",
                    RowKey = model.RowKey,
                    Name = model.Name ?? string.Empty,
                    Description = model.Description ?? string.Empty,
                    Price = model.Price,
                    StockQuantity = model.StockQuantity,
                    Category = model.Category ?? string.Empty,
                    ImageUrl = model.ExistingImageUrl ?? string.Empty,
                    CreatedDate = existingProduct.CreatedDate,
                    LastUpdated = DateTime.UtcNow,
                    IsActive = existingProduct.IsActive,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = ETag.All
                };

                await tableClient.UpdateEntityAsync(updatedProduct, ETag.All, Azure.Data.Tables.TableUpdateMode.Replace);

                TempData["SuccessMessage"] = "Product updated successfully!";
                return RedirectToAction("Index");
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error updating product. Please try again.";
                return View(model);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (string.IsNullOrEmpty(id))
                return NotFound();

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                await tableClient.DeleteEntityAsync("Products", id);

                TempData["SuccessMessage"] = "Product deleted successfully!";
                return RedirectToAction("Index");
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error deleting product. Please try again.";
                return RedirectToAction("Index");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(string id)
        {
            if (string.IsNullOrEmpty(id))
                return NotFound();

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var productResponse = await tableClient.GetEntityAsync<Product>("Products", id);

                if (!productResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Product not found.";
                    return RedirectToAction("Index");
                }

                var product = productResponse.Value;

                var updatedProduct = new Product
                {
                    PartitionKey = "Products",
                    RowKey = product.RowKey,
                    Name = product.Name ?? string.Empty,
                    Description = product.Description ?? string.Empty,
                    Price = product.Price,
                    StockQuantity = product.StockQuantity,
                    Category = product.Category ?? string.Empty,
                    ImageUrl = product.ImageUrl ?? string.Empty,
                    CreatedDate = product.CreatedDate,
                    LastUpdated = DateTime.UtcNow,
                    IsActive = !product.IsActive,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = ETag.All
                };

                await tableClient.UpdateEntityAsync(updatedProduct, ETag.All, Azure.Data.Tables.TableUpdateMode.Replace);

                TempData["SuccessMessage"] = $"Product {(updatedProduct.IsActive ? "activated" : "deactivated")} successfully!";
                return RedirectToAction("Index");
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error updating product status. Please try again.";
                return RedirectToAction("Index");
            }
        }
    }
}