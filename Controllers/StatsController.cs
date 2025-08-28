using ABCRetail.Cloud.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABCRetail.Cloud.Controllers
{
    [Route("api/stats")]
    [ApiController]
    public class StatsController : ControllerBase
    {
        private readonly AzureStorageService _storageService;

        public StatsController(AzureStorageService storageService)
        {
            _storageService = storageService;
        }

        [HttpGet("customers")]
        public async Task<IActionResult> GetCustomerCount()
        {
            return Ok(new { count = await _storageService.GetCustomerCountAsync() });
        }

        [HttpGet("products")]
        public async Task<IActionResult> GetProductCount()
        {
            return Ok(new { count = await _storageService.GetProductCountAsync() });
        }

        [HttpGet("orders")]
        public async Task<IActionResult> GetOrderCount()
        {
            return Ok(new { count = await _storageService.GetQueueMessageCountAsync() });
        }
    }
}