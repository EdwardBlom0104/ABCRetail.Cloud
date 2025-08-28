using ABCRetail.Cloud.Models;
using ABCRetail.Cloud.Models.ViewModels;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ABCRetail.Cloud.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        private readonly AzureStorageService _storageService;
        private readonly ILogger<AdminController> _logger;
        private const int DefaultPageSize = 10;

        public AdminController(AzureStorageService storageService, ILogger<AdminController> logger)
        {
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IActionResult> Index()
        {
            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                if (tableClient == null)
                    throw new Exception("Table client initialization failed");

                var customers = new List<Customer>();
                await foreach (var c in tableClient.QueryAsync<Customer>(c => c.PartitionKey == "Customers"))
                    customers.Add(c);

                var products = new List<Product>();
                await foreach (var p in tableClient.QueryAsync<Product>(p => p.PartitionKey == "Products"))
                    products.Add(p);

                // Get products dictionary for price lookup
                var productDict = products.ToDictionary(p => p.RowKey, p => p);

                var orderEntities = new List<OrderTableEntity>();
                await foreach (var o in tableClient.QueryAsync<OrderTableEntity>(o => o.PartitionKey == "Orders"))
                    orderEntities.Add(o);

                var orders = orderEntities.Select(oe =>
                {
                    var items = new List<OrderItem>();

                    if (!string.IsNullOrEmpty(oe.ItemsJson))
                    {
                        try
                        {
                            items = JsonSerializer.Deserialize<List<OrderItem>>(oe.ItemsJson) ?? new List<OrderItem>();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error deserializing items for order {oe.RowKey}");
                            items = new List<OrderItem>();
                        }
                    }

                    var order = new Order
                    {
                        PartitionKey = oe.PartitionKey,
                        RowKey = oe.RowKey,
                        CustomerId = oe.CustomerId,
                        Items = items,
                        Status = oe.Status ?? "Pending",
                        OrderDate = oe.OrderDate,
                        Timestamp = oe.Timestamp,
                        ETag = oe.ETag,
                        TotalAmount = 0 // Start with 0 and calculate properly
                    };

                    // Calculate total amount with multiple fallback strategies
                    if (items.Any())
                    {
                        double calculatedTotal = 0;

                        foreach (var item in items)
                        {
                            double itemTotal = 0;

                            // Strategy 1: Use UnitPrice from item if it exists and is valid
                            if (item.UnitPrice > 0)
                            {
                                itemTotal = item.UnitPrice * item.Quantity;
                            }
                            // Strategy 2: Look up price from products if UnitPrice is 0 or missing
                            else if (productDict.ContainsKey(item.ProductId))
                            {
                                itemTotal = productDict[item.ProductId].Price * item.Quantity;
                                _logger.LogInformation($"Using product price lookup for item {item.ProductId}: {productDict[item.ProductId].Price}");
                            }
                            // Strategy 3: Default fallback (this shouldn't happen, but just in case)
                            else
                            {
                                _logger.LogWarning($"Could not determine price for item {item.ProductId} in order {oe.RowKey}");
                                itemTotal = 0;
                            }

                            calculatedTotal += itemTotal;
                        }

                        order.TotalAmount = calculatedTotal;
                    }
                    else
                    {
                        // If no items, use stored total (might be a legacy order)
                        order.TotalAmount = oe.TotalAmount;
                    }

                    return order;
                }).ToList();

                return View(new AdminDashboardViewModel
                {
                    CustomerCount = customers.Count,
                    ProductCount = products.Count,
                    RecentOrders = orders.OrderByDescending(o => o.OrderDate).Take(5).ToList(),
                    LowStockProductsCount = products.Count(p => p.StockQuantity < 10),
                    OutOfStockProductsCount = products.Count(p => p.StockQuantity <= 0),
                    PendingOrdersCount = orders.Count(o => o.Status == "Pending"),
                    TotalRevenue = orders.Where(o => o.Status == "Completed").Sum(o => o.TotalAmount)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading admin dashboard");
                TempData["ErrorMessage"] = "Error loading dashboard data";
                return View(new AdminDashboardViewModel());
            }
        }

        // Method to permanently fix order data in database
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RepairOrderData()
        {
            try
            {
                var tableClient = await _storageService.GetTableClientAsync();

                // Get all products for price lookup
                var products = new List<Product>();
                await foreach (var p in tableClient.QueryAsync<Product>(p => p.PartitionKey == "Products"))
                    products.Add(p);
                var productDict = products.ToDictionary(p => p.RowKey, p => p);

                var orderEntities = new List<OrderTableEntity>();
                await foreach (var o in tableClient.QueryAsync<OrderTableEntity>(o => o.PartitionKey == "Orders"))
                    orderEntities.Add(o);

                int repairedCount = 0;

                foreach (var orderEntity in orderEntities)
                {
                    bool needsUpdate = false;
                    double newTotal = 0;

                    if (!string.IsNullOrEmpty(orderEntity.ItemsJson))
                    {
                        try
                        {
                            var items = JsonSerializer.Deserialize<List<OrderItem>>(orderEntity.ItemsJson);
                            if (items != null && items.Any())
                            {
                                foreach (var item in items)
                                {
                                    // If item has no unit price, look it up from products
                                    if (item.UnitPrice == 0 && productDict.ContainsKey(item.ProductId))
                                    {
                                        item.UnitPrice = productDict[item.ProductId].Price;
                                        needsUpdate = true;
                                    }
                                    newTotal += item.UnitPrice * item.Quantity;
                                }

                                // Always update if calculated total differs from stored total
                                if (Math.Abs(newTotal - orderEntity.TotalAmount) > 0.01)
                                {
                                    needsUpdate = true;
                                }

                                if (needsUpdate)
                                {
                                    // Update both the items JSON and total amount
                                    orderEntity.ItemsJson = JsonSerializer.Serialize(items);
                                    orderEntity.TotalAmount = newTotal;

                                    await tableClient.UpdateEntityAsync(orderEntity, ETag.All, TableUpdateMode.Replace);
                                    repairedCount++;

                                    _logger.LogInformation($"Repaired order {orderEntity.RowKey}: Total updated from {orderEntity.TotalAmount} to {newTotal}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error repairing order {orderEntity.RowKey}");
                        }
                    }
                }

                TempData["SuccessMessage"] = $"Successfully repaired {repairedCount} orders!";
                await LogAction($"Repaired {repairedCount} order records");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error repairing order data");
                TempData["ErrorMessage"] = "Error repairing order data";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateOrderStatus(string orderId, string status)
        {
            if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(status))
            {
                TempData["ErrorMessage"] = "Order ID and status are required";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var orderResponse = await tableClient.GetEntityAsync<OrderTableEntity>("Orders", orderId);

                if (!orderResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Order not found";
                    return RedirectToAction(nameof(Index));
                }

                var orderEntity = orderResponse.Value;

                orderEntity.Status = status;
                orderEntity.Timestamp = DateTimeOffset.UtcNow;

                await tableClient.UpdateEntityAsync(orderEntity, ETag.All, TableUpdateMode.Replace);
                await LogAction($"Order {orderId} status updated to: {status}");

                TempData["SuccessMessage"] = $"Order status updated to {status} successfully!";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating order status for order ID {orderId}");
                TempData["ErrorMessage"] = "Error updating order status";
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        public async Task<IActionResult> Orders(int page = 1, int pageSize = DefaultPageSize, string search = "", string status = "", string fromDate = "", string toDate = "")
        {
            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                if (tableClient == null)
                    throw new Exception("Table client initialization failed");

                // Get all products for price lookup
                var products = new List<Product>();
                await foreach (var p in tableClient.QueryAsync<Product>(p => p.PartitionKey == "Products"))
                    products.Add(p);
                var productDict = products.ToDictionary(p => p.RowKey, p => p);

                var allOrderEntities = new List<OrderTableEntity>();
                await foreach (var o in tableClient.QueryAsync<OrderTableEntity>(o => o.PartitionKey == "Orders"))
                    allOrderEntities.Add(o);

                // Convert to Order objects with proper pricing
                var allOrders = allOrderEntities.Select(oe =>
                {
                    var items = new List<OrderItem>();

                    if (!string.IsNullOrEmpty(oe.ItemsJson))
                    {
                        try
                        {
                            items = JsonSerializer.Deserialize<List<OrderItem>>(oe.ItemsJson) ?? new List<OrderItem>();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error deserializing items for order {oe.RowKey}");
                            items = new List<OrderItem>();
                        }
                    }

                    var order = new Order
                    {
                        PartitionKey = oe.PartitionKey,
                        RowKey = oe.RowKey,
                        CustomerId = oe.CustomerId,
                        Items = items,
                        Status = oe.Status ?? "Pending",
                        OrderDate = oe.OrderDate,
                        Timestamp = oe.Timestamp,
                        ETag = oe.ETag,
                        TotalAmount = 0
                    };

                    // Calculate total amount properly
                    if (items.Any())
                    {
                        double calculatedTotal = 0;

                        foreach (var item in items)
                        {
                            double itemTotal = 0;

                            if (item.UnitPrice > 0)
                            {
                                itemTotal = item.UnitPrice * item.Quantity;
                            }
                            else if (productDict.ContainsKey(item.ProductId))
                            {
                                itemTotal = productDict[item.ProductId].Price * item.Quantity;
                            }

                            calculatedTotal += itemTotal;
                        }

                        order.TotalAmount = calculatedTotal;
                    }
                    else
                    {
                        order.TotalAmount = oe.TotalAmount;
                    }

                    return order;
                }).ToList();

                // Apply filters
                var filteredOrders = allOrders.AsEnumerable();

                if (!string.IsNullOrEmpty(search))
                {
                    filteredOrders = filteredOrders.Where(o =>
                        (o.RowKey != null && o.RowKey.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                        (o.CustomerId != null && o.CustomerId.Contains(search, StringComparison.OrdinalIgnoreCase))
                    );
                }

                if (!string.IsNullOrEmpty(status))
                {
                    filteredOrders = filteredOrders.Where(o => o.Status != null && o.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(fromDate) && DateTime.TryParse(fromDate, out var fromDateParsed))
                {
                    filteredOrders = filteredOrders.Where(o => o.OrderDate >= fromDateParsed);
                }

                if (!string.IsNullOrEmpty(toDate) && DateTime.TryParse(toDate, out var toDateParsed))
                {
                    filteredOrders = filteredOrders.Where(o => o.OrderDate <= toDateParsed.AddDays(1)); // Include the entire day
                }

                // Order by date descending
                filteredOrders = filteredOrders.OrderByDescending(o => o.OrderDate);

                var orders = filteredOrders.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                ViewBag.CurrentPage = page;
                ViewBag.PageSize = pageSize;
                ViewBag.TotalCount = filteredOrders.Count();
                ViewBag.TotalPages = (int)Math.Ceiling(filteredOrders.Count() / (double)pageSize);

                return View(orders);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading orders");
                TempData["ErrorMessage"] = "Error loading orders";
                return View(new List<Order>());
            }
        }

        [HttpGet]
        public async Task<IActionResult> Products(int page = 1, int pageSize = DefaultPageSize)
        {
            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                if (tableClient == null)
                    throw new Exception("Table client initialization failed");

                var allProducts = new List<Product>();
                await foreach (var p in tableClient.QueryAsync<Product>(p => p.PartitionKey == "Products"))
                    allProducts.Add(p);

                var products = allProducts.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                ViewBag.CurrentPage = page;
                ViewBag.PageSize = pageSize;
                ViewBag.TotalCount = allProducts.Count;
                ViewBag.TotalPages = (int)Math.Ceiling(allProducts.Count / (double)pageSize);

                return View(products);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading products");
                TempData["ErrorMessage"] = "Error loading products";
                return View(new List<Product>());
            }
        }

        [HttpGet]
        public IActionResult AddProduct() => View(new ProductViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddProduct(ProductViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var blobContainerClient = await _storageService.GetBlobContainerClientAsync();

                string? imageUrl = null;

                if (model.ImageFile != null && model.ImageFile.Length > 0)
                {
                    var fileName = $"{Guid.NewGuid()}{Path.GetExtension(model.ImageFile.FileName)}";
                    var blobClient = blobContainerClient.GetBlobClient(fileName);

                    using var stream = model.ImageFile.OpenReadStream();
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = model.ImageFile.ContentType }
                    });

                    imageUrl = blobClient.Uri.ToString();
                    await LogAction($"Product image uploaded: {fileName}");
                }
                else if (!string.IsNullOrEmpty(model.ImageUrl))
                {
                    imageUrl = model.ImageUrl;
                }

                var product = new Product
                {
                    PartitionKey = "Products",
                    RowKey = Guid.NewGuid().ToString(),
                    Name = model.Name,
                    Description = model.Description,
                    Price = model.Price,
                    StockQuantity = model.StockQuantity,
                    Category = model.Category,
                    ImageUrl = imageUrl ?? string.Empty,
                    CreatedDate = DateTime.UtcNow
                };

                await tableClient.AddEntityAsync(product);

                var queueClient = await _storageService.GetQueueClientAsync();
                await queueClient.SendMessageAsync(Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"New product added: {product.Name} (ID: {product.RowKey})")));

                TempData["SuccessMessage"] = "Product added successfully!";
                return RedirectToAction(nameof(Products));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding product");
                ModelState.AddModelError("", $"Error adding product: {ex.Message}");
                return View(model);
            }
        }

        [HttpGet]
        public async Task<IActionResult> EditProduct(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                TempData["ErrorMessage"] = "Product ID is required";
                return RedirectToAction(nameof(Products));
            }

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var productResponse = await tableClient.GetEntityAsync<Product>("Products", id);

                if (!productResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Product not found";
                    return RedirectToAction(nameof(Products));
                }

                var product = productResponse.Value;
                return View(new ProductViewModel
                {
                    Id = product.RowKey,
                    RowKey = product.RowKey,
                    Name = product.Name,
                    Description = product.Description,
                    Price = product.Price,
                    StockQuantity = product.StockQuantity,
                    Category = product.Category,
                    ExistingImageUrl = product.ImageUrl,
                    ImageUrl = product.ImageUrl,
                    PartitionKey = product.PartitionKey
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading product with ID {id}");
                TempData["ErrorMessage"] = "Error loading product for editing";
                return RedirectToAction(nameof(Products));
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProduct(ProductViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var blobContainerClient = await _storageService.GetBlobContainerClientAsync();
                var existingProductResponse = await tableClient.GetEntityAsync<Product>(model.PartitionKey ?? "Products", model.Id);

                if (!existingProductResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Product not found";
                    return RedirectToAction(nameof(Products));
                }

                var existingProduct = existingProductResponse.Value;
                string? imageUrl = existingProduct.ImageUrl;

                if (model.ImageFile != null && model.ImageFile.Length > 0)
                {
                    if (!string.IsNullOrEmpty(imageUrl))
                        await DeleteBlobAsync(imageUrl);

                    var fileName = $"{Guid.NewGuid()}{Path.GetExtension(model.ImageFile.FileName)}";
                    var blobClient = blobContainerClient.GetBlobClient(fileName);

                    using var stream = model.ImageFile.OpenReadStream();
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = model.ImageFile.ContentType }
                    });

                    imageUrl = blobClient.Uri.ToString();
                    await LogAction($"Product image updated: {fileName}");
                }
                else if (!string.IsNullOrEmpty(model.ImageUrl))
                {
                    imageUrl = model.ImageUrl;
                }
                else if (model.ImageUrl == "")
                {
                    imageUrl = null;
                }

                var updatedProduct = new Product
                {
                    PartitionKey = existingProduct.PartitionKey,
                    RowKey = existingProduct.RowKey,
                    Name = model.Name,
                    Description = model.Description,
                    Price = model.Price,
                    StockQuantity = model.StockQuantity,
                    Category = model.Category,
                    ImageUrl = imageUrl ?? string.Empty,
                    CreatedDate = existingProduct.CreatedDate,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = existingProduct.ETag
                };

                await tableClient.UpdateEntityAsync(updatedProduct, updatedProduct.ETag, TableUpdateMode.Replace);

                TempData["SuccessMessage"] = "Product updated successfully!";
                return RedirectToAction(nameof(Products));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating product with ID {model.Id}");
                ModelState.AddModelError("", $"Error updating product: {ex.Message}");
                return View(model);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteProduct(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                TempData["ErrorMessage"] = "Product ID is required";
                return RedirectToAction(nameof(Products));
            }

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var productResponse = await tableClient.GetEntityAsync<Product>("Products", id);

                if (!productResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Product not found";
                    return RedirectToAction(nameof(Products));
                }

                var product = productResponse.Value;

                if (!string.IsNullOrEmpty(product.ImageUrl))
                    await DeleteBlobAsync(product.ImageUrl);

                await tableClient.DeleteEntityAsync("Products", id);
                await LogAction($"Product deleted: {product.Name} (ID: {id})");

                TempData["SuccessMessage"] = "Product deleted successfully!";
                return RedirectToAction(nameof(Products));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting product with ID {id}");
                TempData["ErrorMessage"] = "Error deleting product";
                return RedirectToAction(nameof(Products));
            }
        }

        [HttpGet]
        public async Task<IActionResult> Customers(int page = 1, int pageSize = DefaultPageSize, string search = "", string country = "", string city = "")
        {
            try
            {
                var tableClient = await _storageService.GetTableClientAsync();

                var allCustomers = new List<Customer>();
                await foreach (var c in tableClient.QueryAsync<Customer>(c => c.PartitionKey == "Customers"))
                    allCustomers.Add(c);

                if (!string.IsNullOrEmpty(search))
                    allCustomers = allCustomers.Where(c =>
                        (c.FirstName != null && c.FirstName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                        (c.LastName != null && c.LastName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                        (c.Email != null && c.Email.Contains(search, StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                if (!string.IsNullOrEmpty(country))
                    allCustomers = allCustomers.Where(c => c.Country != null && c.Country.Equals(country, StringComparison.OrdinalIgnoreCase)).ToList();

                if (!string.IsNullOrEmpty(city))
                    allCustomers = allCustomers.Where(c => c.City != null && c.City.Equals(city, StringComparison.OrdinalIgnoreCase)).ToList();

                var customers = allCustomers.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                ViewBag.CurrentPage = page;
                ViewBag.PageSize = pageSize;
                ViewBag.TotalCount = allCustomers.Count;
                ViewBag.TotalPages = (int)Math.Ceiling(allCustomers.Count / (double)pageSize);

                return View(customers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading customers");
                TempData["ErrorMessage"] = "Error loading customers";
                return View(new List<Customer>());
            }
        }

        [HttpGet]
        public async Task<IActionResult> EditCustomer(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                TempData["ErrorMessage"] = "Customer ID is required";
                return RedirectToAction(nameof(Customers));
            }

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var customerResponse = await tableClient.GetEntityAsync<Customer>("Customers", id);

                if (!customerResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Customer not found";
                    return RedirectToAction(nameof(Customers));
                }

                var customer = customerResponse.Value;
                return View(customer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading customer with ID {id}");
                TempData["ErrorMessage"] = "Error loading customer for editing";
                return RedirectToAction(nameof(Customers));
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditCustomer(Customer model)
        {
            if (!ModelState.IsValid)
                return View(model);

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var existingCustomerResponse = await tableClient.GetEntityAsync<Customer>("Customers", model.RowKey);

                if (!existingCustomerResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Customer not found";
                    return RedirectToAction(nameof(Customers));
                }

                var existingCustomer = existingCustomerResponse.Value;
                var updatedCustomer = new Customer
                {
                    PartitionKey = "Customers",
                    RowKey = model.RowKey,
                    FirstName = model.FirstName,
                    LastName = model.LastName,
                    Email = model.Email,
                    PhoneNumber = model.PhoneNumber,
                    Address = model.Address,
                    City = model.City,
                    State = model.State,
                    Country = string.IsNullOrEmpty(model.Country) ? "South Africa" : model.Country,
                    RegistrationDate = existingCustomer.RegistrationDate,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = existingCustomer.ETag
                };

                await tableClient.UpdateEntityAsync(updatedCustomer, updatedCustomer.ETag, TableUpdateMode.Replace);
                await LogAction($"Customer updated: {model.FirstName} {model.LastName} (ID: {model.RowKey})");

                TempData["SuccessMessage"] = "Customer updated successfully!";
                return RedirectToAction(nameof(Customers));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating customer with ID {model.RowKey}");
                ModelState.AddModelError("", $"Error updating customer: {ex.Message}");
                return View(model);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCustomer(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                TempData["ErrorMessage"] = "Customer ID is required";
                return RedirectToAction(nameof(Customers));
            }

            try
            {
                var tableClient = await _storageService.GetTableClientAsync();
                var customerResponse = await tableClient.GetEntityAsync<Customer>("Customers", id);

                if (!customerResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Customer not found";
                    return RedirectToAction(nameof(Customers));
                }

                var customer = customerResponse.Value;
                await tableClient.DeleteEntityAsync("Customers", id);
                await LogAction($"Customer deleted: {customer.FirstName} {customer.LastName} (ID: {id})");

                TempData["SuccessMessage"] = "Customer deleted successfully!";
                return RedirectToAction(nameof(Customers));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting customer with ID {id}");
                TempData["ErrorMessage"] = "Error deleting customer";
                return RedirectToAction(nameof(Customers));
            }
        }

        private async Task DeleteBlobAsync(string blobUrl)
        {
            try
            {
                var blobContainerClient = await _storageService.GetBlobContainerClientAsync();
                var blobName = new Uri(blobUrl).Segments.Last();
                var blobClient = blobContainerClient.GetBlobClient(blobName);
                await blobClient.DeleteIfExistsAsync();
                await LogAction($"Deleted blob: {blobName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting blob: {blobUrl}");
                throw;
            }
        }

        private async Task LogAction(string message)
        {
            try
            {
                var shareClient = await _storageService.GetFileShareClientAsync();
                var directoryClient = shareClient.GetDirectoryClient("logs");
                await directoryClient.CreateIfNotExistsAsync();

                var fileName = $"log_{DateTime.UtcNow:yyyyMMdd}.txt";
                var fileClient = directoryClient.GetFileClient(fileName);

                var logEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {message}\n";

                if (await fileClient.ExistsAsync())
                {
                    // Download existing content
                    var download = await fileClient.DownloadAsync();
                    using var reader = new StreamReader(download.Value.Content);
                    var existingContent = await reader.ReadToEndAsync();
                    logEntry = existingContent + logEntry;

                    // Delete old file before uploading new content
                    await fileClient.DeleteAsync();
                }

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(logEntry));
                await fileClient.CreateAsync(stream.Length);  // Create file with the correct size
                await fileClient.UploadAsync(stream);         // Upload content
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging action");
            }
        }
    }
}