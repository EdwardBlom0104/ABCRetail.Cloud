using ABCRetail.Cloud.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;
using Azure.Data.Tables;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Azure;

namespace ABCRetail.Cloud.Controllers
{
    [AllowAnonymous]
    public class AccountController : Controller
    {
        private readonly AzureStorageService _storageService;
        private readonly IConfiguration _configuration;

        public AccountController(AzureStorageService storageService, IConfiguration configuration)
        {
            _storageService = storageService;
            _configuration = configuration;
        }

        [HttpGet]
        public IActionResult Login() => View();

        [HttpPost]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Admin login
            var adminEmail = _configuration["AdminCredentials:Email"];
            var adminPassword = _configuration["AdminCredentials:Password"];

            if (model.Email == adminEmail && model.Password == adminPassword)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, "Admin"),
                    new Claim(ClaimTypes.Email, adminEmail),
                    new Claim(ClaimTypes.Role, "Admin")
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

                return RedirectToAction("Index", "Admin");
            }

            // Customer login
            var tableClient = await _storageService.GetTableClientAsync();
            var customers = tableClient.QueryAsync<Customer>(c =>
                c.PartitionKey == "Customers" && c.Email == model.Email);

            Customer? customer = null;
            await foreach (var c in customers)
            {
                customer = c;
                break;
            }

            if (customer != null && customer.Password == model.Password)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, $"{customer.FirstName} {customer.LastName}"),
                    new Claim(ClaimTypes.Email, customer.Email),
                    new Claim(ClaimTypes.Role, "Customer"),
                    new Claim("CustomerId", customer.RowKey)
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

                return RedirectToAction("Index", "Customer");
            }

            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        [HttpGet]
        public IActionResult Register() => View();

        [HttpPost]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var tableClient = await _storageService.GetTableClientAsync();

            // Check existing
            var existing = tableClient.QueryAsync<Customer>(c =>
                c.PartitionKey == "Customers" && c.Email == model.Email);

            Customer? existingCustomer = null;
            await foreach (var c in existing)
            {
                existingCustomer = c;
                break;
            }

            if (existingCustomer != null)
            {
                ModelState.AddModelError("Email", "Email already registered.");
                return View(model);
            }

            // Create new customer
            var customer = new Customer
            {
                PartitionKey = "Customers",
                RowKey = Guid.NewGuid().ToString(),
                FirstName = model.FirstName,
                LastName = model.LastName,
                Email = model.Email,
                Password = model.Password,
                Address = model.Address,
                PhoneNumber = model.PhoneNumber
            };

            await tableClient.AddEntityAsync(customer);

            await LogAction($"New customer registered: {model.Email}");

            return RedirectToAction("Login");
        }

        [Authorize]
        [HttpPost] // ✅ Fixed to use POST only
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        private async Task LogAction(string message)
        {
            try
            {
                var shareClient = await _storageService.GetFileShareClientAsync();
                var directoryClient = shareClient.GetDirectoryClient("logs");
                await directoryClient.CreateIfNotExistsAsync();

                var fileClient = directoryClient.GetFileClient($"log_{DateTime.UtcNow:yyyyMMdd}.txt");
                string logEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {message}\n";

                string newContent = logEntry;

                if (await fileClient.ExistsAsync())
                {
                    var download = await fileClient.DownloadAsync();
                    using var reader = new StreamReader(download.Value.Content);
                    var existingContent = await reader.ReadToEndAsync();
                    newContent = existingContent + logEntry;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(newContent);
                using var stream = new MemoryStream(bytes);

                // ✅ Correct way for File Shares: create (or overwrite) then upload
                await fileClient.CreateAsync(bytes.Length);
                await fileClient.UploadRangeAsync(
                    new HttpRange(0, bytes.Length),
                    stream
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error logging action: {ex.Message}");
            }
        }
    }
}
