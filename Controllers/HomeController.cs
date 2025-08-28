using Microsoft.AspNetCore.Mvc;
using ABCRetail.Cloud.Models;
using System.Diagnostics;

namespace ABCRetail.Cloud.Controllers
{
    public class HomeController : Controller
    {
        private readonly AzureStorageService _storageService;

        public HomeController(AzureStorageService storageService)
        {
            _storageService = storageService;
        }

        public async Task<IActionResult> Index()
        {
            var tableClient = await _storageService.GetTableClientAsync();
            var products = await tableClient.QueryAsync<Product>(p => p.PartitionKey == "Products")
                .Take(8)
                .ToListAsync();

            return View(products);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}