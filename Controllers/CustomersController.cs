using ABCRetail.Cloud.Models;
using ABCRetail.Cloud.Models.ViewModels;
using Azure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;

namespace ABCRetail.Cloud.Controllers
{
    [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        private readonly AzureStorageService _storageService;
        private readonly string _customerId;

        public CustomerController(AzureStorageService storageService, IHttpContextAccessor httpContextAccessor)
        {
            _storageService = storageService;

            // Fix nullable reference issues
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext?.User == null)
                throw new InvalidOperationException("HTTP context or user not available");

            var customerIdClaim = httpContext.User.FindFirst("CustomerId");
            _customerId = customerIdClaim?.Value ?? throw new InvalidOperationException("Customer ID not found in claims");
        }

        public async Task<IActionResult> Index()
        {
            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                // Get customer details
                var customerResponse = await tableClient.GetEntityAsync<Customer>("Customers", _customerId);

                if (!customerResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Customer profile not found.";
                    return RedirectToAction("Login", "Account");
                }

                // Get recent orders
                var orders = await tableClient.QueryAsync<Order>(o =>
                    o.PartitionKey == "Orders" && o.CustomerId == _customerId)
                    .Take(5)
                    .ToListAsync();

                // Fixed: No conversion needed now that both use double
                double totalSpent = 0;
                if (orders.Any())
                {
                    totalSpent = orders.Sum(o => o.TotalAmount);
                }

                var viewModel = new CustomerDashboardViewModel
                {
                    Customer = customerResponse.Value,
                    RecentOrders = orders,
                    OrderCount = orders.Count,
                    TotalSpent = totalSpent,
                    PendingOrdersCount = orders.Count(o => o.Status == "Pending"),
                    WishlistItemsCount = 0 // Initialize or fetch actual wishlist count
                };

                return View(viewModel);
            }
            catch (Exception ex)
            {
                // Use the exception variable or remove it
                TempData["ErrorMessage"] = $"Error loading dashboard data: {ex.Message}";
                return View(new CustomerDashboardViewModel
                {
                    Customer = new Customer
                    {
                        FirstName = "Unknown",
                        LastName = "User",
                        Email = "unknown@example.com",
                        RegistrationDate = DateTime.Now
                    }
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Products()
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
        public async Task<IActionResult> ProductDetails(string id)
        {
            if (string.IsNullOrEmpty(id))
                return NotFound();

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var productResponse = await tableClient.GetEntityAsync<Product>("Products", id);

                if (!productResponse.HasValue)
                    return NotFound();

                return View(productResponse.Value);
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error loading product details.";
                return RedirectToAction("Products");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PlaceOrder(string productId, int quantity)
        {
            if (string.IsNullOrEmpty(productId) || quantity <= 0)
            {
                TempData["ErrorMessage"] = "Invalid product or quantity.";
                return RedirectToAction("Products");
            }

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                // Get product
                var productResponse = await tableClient.GetEntityAsync<Product>("Products", productId);

                if (!productResponse.HasValue || productResponse.Value.StockQuantity < quantity)
                {
                    TempData["ErrorMessage"] = "Product not available or insufficient stock.";
                    return RedirectToAction("ProductDetails", new { id = productId });
                }

                var product = productResponse.Value;

                // Create order with serialized items
                var order = new Order
                {
                    PartitionKey = "Orders",
                    RowKey = Guid.NewGuid().ToString(),
                    CustomerId = _customerId,
                    Items = new List<OrderItem>
                    {
                        new OrderItem
                        {
                            ProductId = productId,
                            ProductName = product.Name ?? "Unknown Product",
                            Quantity = quantity,
                            UnitPrice = product.Price
                        }
                    },
                    TotalAmount = product.Price * quantity,
                    Status = "Pending",
                    OrderDate = DateTime.UtcNow
                };

                // Convert the Order to a TableEntity for storage
                var orderEntity = new OrderTableEntity
                {
                    PartitionKey = "Orders",
                    RowKey = order.RowKey,
                    CustomerId = order.CustomerId,
                    ItemsJson = JsonSerializer.Serialize(order.Items),
                    TotalAmount = order.TotalAmount,
                    Status = order.Status ?? "Pending",
                    OrderDate = order.OrderDate,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = ETag.All
                };

                await tableClient.AddEntityAsync(orderEntity);

                // Update product stock - fix nullable assignments
                var updatedProduct = new Product
                {
                    PartitionKey = "Products",
                    RowKey = productId,
                    Name = product.Name ?? string.Empty,
                    Description = product.Description ?? string.Empty,
                    Price = product.Price,
                    StockQuantity = product.StockQuantity - quantity,
                    Category = product.Category ?? string.Empty,
                    ImageUrl = product.ImageUrl ?? string.Empty,
                    CreatedDate = product.CreatedDate,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = ETag.All
                };

                await tableClient.UpdateEntityAsync(updatedProduct, ETag.All, Azure.Data.Tables.TableUpdateMode.Replace);

                // Add to queue for order processing
                var queueClient = await _storageService.GetQueueClientAsync();
                await queueClient.SendMessageAsync($"New order placed: {order.RowKey} by customer {_customerId}");

                TempData["SuccessMessage"] = "Order placed successfully!";
                return RedirectToAction("Orders");
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error placing order. Please try again.";
                return RedirectToAction("ProductDetails", new { id = productId });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Orders()
        {
            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var orderEntities = await tableClient.QueryAsync<OrderTableEntity>(o =>
                    o.PartitionKey == "Orders" && o.CustomerId == _customerId)
                    .ToListAsync();

                // Convert back to Order objects
                var orders = orderEntities.Select(oe => new Order
                {
                    PartitionKey = oe.PartitionKey,
                    RowKey = oe.RowKey,
                    CustomerId = oe.CustomerId,
                    Items = string.IsNullOrEmpty(oe.ItemsJson) ?
                        new List<OrderItem>() :
                        JsonSerializer.Deserialize<List<OrderItem>>(oe.ItemsJson) ?? new List<OrderItem>(),
                    TotalAmount = oe.TotalAmount,
                    Status = oe.Status ?? string.Empty,
                    OrderDate = oe.OrderDate,
                    Timestamp = oe.Timestamp,
                    ETag = oe.ETag
                }).ToList();

                return View(orders);
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error loading orders.";
                return View(new List<Order>());
            }
        }

        [HttpGet]
        public async Task<IActionResult> OrderDetails(string id)
        {
            if (string.IsNullOrEmpty(id))
                return NotFound();

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var orderEntityResponse = await tableClient.GetEntityAsync<OrderTableEntity>("Orders", id);

                if (!orderEntityResponse.HasValue || orderEntityResponse.Value.CustomerId != _customerId)
                    return NotFound();

                var orderEntity = orderEntityResponse.Value;

                // Convert back to Order object
                var order = new Order
                {
                    PartitionKey = orderEntity.PartitionKey,
                    RowKey = orderEntity.RowKey,
                    CustomerId = orderEntity.CustomerId,
                    Items = string.IsNullOrEmpty(orderEntity.ItemsJson) ?
                        new List<OrderItem>() :
                        JsonSerializer.Deserialize<List<OrderItem>>(orderEntity.ItemsJson) ?? new List<OrderItem>(),
                    TotalAmount = orderEntity.TotalAmount,
                    Status = orderEntity.Status ?? string.Empty,
                    OrderDate = orderEntity.OrderDate,
                    Timestamp = orderEntity.Timestamp,
                    ETag = orderEntity.ETag
                };

                return View(order);
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error loading order details.";
                return RedirectToAction("Orders");
            }
        }

        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var customerResponse = await tableClient.GetEntityAsync<Customer>("Customers", _customerId);

                if (!customerResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Customer profile not found.";
                    return RedirectToAction("Index");
                }

                var customer = customerResponse.Value;
                var model = new ProfileViewModel
                {
                    FirstName = customer.FirstName ?? string.Empty,
                    LastName = customer.LastName ?? string.Empty,
                    Email = customer.Email ?? string.Empty,
                    Address = customer.Address ?? string.Empty,
                    PhoneNumber = customer.PhoneNumber ?? string.Empty
                };

                return View(model);
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error loading profile.";
                return RedirectToAction("Index");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateProfile(ProfileViewModel model)
        {
            if (!ModelState.IsValid)
                return View("Profile", model);

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var customerResponse = await tableClient.GetEntityAsync<Customer>("Customers", _customerId);

                if (!customerResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Customer profile not found.";
                    return RedirectToAction("Index");
                }

                var existingCustomer = customerResponse.Value;

                var updatedCustomer = new Customer
                {
                    PartitionKey = "Customers",
                    RowKey = _customerId,
                    FirstName = model.FirstName ?? string.Empty,
                    LastName = model.LastName ?? string.Empty,
                    Email = existingCustomer.Email ?? string.Empty,
                    Password = existingCustomer.Password ?? string.Empty,
                    Address = model.Address ?? string.Empty,
                    PhoneNumber = model.PhoneNumber ?? string.Empty,
                    City = existingCustomer.City ?? string.Empty,
                    State = existingCustomer.State ?? string.Empty,
                    Country = existingCustomer.Country ?? string.Empty,
                    RegistrationDate = existingCustomer.RegistrationDate,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = ETag.All
                };

                await tableClient.UpdateEntityAsync(updatedCustomer, ETag.All, Azure.Data.Tables.TableUpdateMode.Replace);

                TempData["SuccessMessage"] = "Profile updated successfully!";
                return RedirectToAction("Profile");
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error updating profile. Please try again.";
                return View("Profile", model);
            }
        }

        [HttpGet]
        public IActionResult ChangePassword()
        {
            return View(new ChangePasswordViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var tableClient = await _storageService.GetTableClientAsync();

            try
            {
                var customerResponse = await tableClient.GetEntityAsync<Customer>("Customers", _customerId);

                if (!customerResponse.HasValue)
                {
                    TempData["ErrorMessage"] = "Customer profile not found.";
                    return RedirectToAction("Profile");
                }

                var customer = customerResponse.Value;

                if (customer.Password != model.CurrentPassword)
                {
                    ModelState.AddModelError("CurrentPassword", "Current password is incorrect.");
                    return View(model);
                }

                var updatedCustomer = new Customer
                {
                    PartitionKey = "Customers",
                    RowKey = _customerId,
                    FirstName = customer.FirstName ?? string.Empty,
                    LastName = customer.LastName ?? string.Empty,
                    Email = customer.Email ?? string.Empty,
                    Password = model.NewPassword ?? string.Empty,
                    Address = customer.Address ?? string.Empty,
                    PhoneNumber = customer.PhoneNumber ?? string.Empty,
                    City = customer.City ?? string.Empty,
                    State = customer.State ?? string.Empty,
                    Country = customer.Country ?? string.Empty,
                    RegistrationDate = customer.RegistrationDate,
                    Timestamp = DateTimeOffset.UtcNow,
                    ETag = ETag.All
                };

                await tableClient.UpdateEntityAsync(updatedCustomer, ETag.All, Azure.Data.Tables.TableUpdateMode.Replace);

                TempData["SuccessMessage"] = "Password changed successfully!";
                return RedirectToAction("Profile");
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "Error changing password. Please try again.";
                return View(model);
            }
        }
    }
}