using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

namespace ABCRetail.Cloud.Models
{
    public class AzureStorageService
    {
        private readonly IConfiguration _configuration;

        public AzureStorageService(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<TableClient> GetTableClientAsync(string? tableName = null)
        {
            var connectionString = _configuration["AzureStorage:ConnectionString"] ?? string.Empty;
            var serviceClient = new TableServiceClient(connectionString);
            var tableClient = serviceClient.GetTableClient(tableName ?? _configuration["AzureStorage:TableName"] ?? string.Empty);
            await tableClient.CreateIfNotExistsAsync();
            return tableClient;
        }

        public async Task<BlobContainerClient> GetBlobContainerClientAsync(string? containerName = null)
        {
            var connectionString = _configuration["AzureStorage:ConnectionString"] ?? string.Empty;
            var blobServiceClient = new BlobServiceClient(connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName ?? _configuration["AzureStorage:BlobContainerName"] ?? string.Empty);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);
            return containerClient;
        }

        public async Task<QueueClient> GetQueueClientAsync(string? queueName = null)
        {
            var connectionString = _configuration["AzureStorage:ConnectionString"] ?? string.Empty;
            var queueClient = new QueueClient(connectionString, queueName ?? _configuration["AzureStorage:QueueName"] ?? string.Empty);
            await queueClient.CreateIfNotExistsAsync();
            return queueClient;
        }

        public async Task<ShareClient> GetFileShareClientAsync(string? shareName = null)
        {
            var connectionString = _configuration["AzureStorage:ConnectionString"] ?? string.Empty;
            var shareClient = new ShareClient(connectionString, shareName ?? _configuration["AzureStorage:FileShareName"] ?? string.Empty);
            await shareClient.CreateIfNotExistsAsync();
            return shareClient;
        }

        public async Task<List<Product>> GetAllProductsAsync()
        {
            var tableClient = await GetTableClientAsync();
            var products = new List<Product>();
            await foreach (var product in tableClient.QueryAsync<Product>("PartitionKey eq 'Products'"))
                products.Add(product);
            return products;
        }

        public async Task<Product?> GetProductByIdAsync(string productId)
        {
            var tableClient = await GetTableClientAsync();
            var response = await tableClient.GetEntityAsync<Product>("Products", productId);
            return response.HasValue ? response.Value : null;
        }

        public async Task AddProductAsync(Product product)
        {
            var tableClient = await GetTableClientAsync();
            await tableClient.AddEntityAsync(product);
        }

        public async Task UpdateProductAsync(Product product)
        {
            var tableClient = await GetTableClientAsync();
            var existing = await tableClient.GetEntityAsync<Product>("Products", product.RowKey);
            product.ETag = existing.Value.ETag;
            product.LastUpdated = DateTime.UtcNow;
            await tableClient.UpdateEntityAsync(product, product.ETag, TableUpdateMode.Replace);
        }

        public async Task<List<Product>> GetFeaturedProductsAsync()
        {
            var tableClient = await GetTableClientAsync();
            var products = new List<Product>();
            await foreach (var product in tableClient.QueryAsync<Product>("PartitionKey eq 'Products' and IsFeatured eq true"))
                products.Add(product);
            return products;
        }

        public async Task<List<Product>> GetProductsByCategoryAsync(string category)
        {
            var tableClient = await GetTableClientAsync();
            var products = new List<Product>();
            await foreach (var product in tableClient.QueryAsync<Product>($"PartitionKey eq 'Products' and Category eq '{category}'"))
                products.Add(product);
            return products;
        }

        public async Task AddOrderAsync(Order order)
        {
            var tableClient = await GetTableClientAsync();

            order.CalculateTotal(); // Calculate total before saving

            var orderEntity = new OrderTableEntity
            {
                PartitionKey = order.PartitionKey,
                RowKey = order.RowKey,
                CustomerId = order.CustomerId,
                ItemsJson = JsonSerializer.Serialize(order.Items),
                TotalAmount = order.TotalAmount,
                Status = order.Status,
                OrderDate = order.OrderDate,
                Timestamp = order.Timestamp ?? DateTimeOffset.UtcNow,
                ETag = order.ETag
            };

            await tableClient.AddEntityAsync(orderEntity);
        }

        public async Task<List<Order>> GetRecentOrdersAsync(int top = 10)
        {
            var tableClient = await GetTableClientAsync();
            var orders = new List<Order>();

            await foreach (var entity in tableClient.QueryAsync<OrderTableEntity>("PartitionKey eq 'Orders'"))
            {
                var order = new Order
                {
                    PartitionKey = entity.PartitionKey,
                    RowKey = entity.RowKey,
                    CustomerId = entity.CustomerId,
                    Status = entity.Status ?? "Pending",
                    OrderDate = entity.OrderDate,
                    Timestamp = entity.Timestamp,
                    ETag = entity.ETag,
                    TotalAmount = entity.TotalAmount
                };

                if (!string.IsNullOrEmpty(entity.ItemsJson))
                {
                    try
                    {
                        order.Items = JsonSerializer.Deserialize<List<OrderItem>>(entity.ItemsJson) ?? new List<OrderItem>();
                    }
                    catch
                    {
                        order.Items = new List<OrderItem>();
                    }
                }

                // Only recalculate if stored TotalAmount is 0 and we have items
                if (order.TotalAmount == 0 && order.Items.Any())
                {
                    order.CalculateTotal();
                }

                orders.Add(order);
            }

            orders.Sort((a, b) => b.OrderDate.CompareTo(a.OrderDate));
            return orders.Count > top ? orders.GetRange(0, top) : orders;
        }

        public async Task AddOrderMessageAsync(OrderMessage message)
        {
            var queueClient = await GetQueueClientAsync();
            await queueClient.SendMessageAsync(message.Content ?? string.Empty);
        }

        public async Task LogToFileAsync(string message)
        {
            var shareClient = await GetFileShareClientAsync();
            var directoryClient = shareClient.GetDirectoryClient("logs");
            await directoryClient.CreateIfNotExistsAsync();

            var fileClient = directoryClient.GetFileClient($"log_{DateTime.UtcNow:yyyyMMdd}.txt");
            string logEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";

            string newContent = logEntry;

            if (await fileClient.ExistsAsync())
            {
                var download = await fileClient.DownloadAsync();
                using var reader = new StreamReader(download.Value.Content);
                var existingContent = await reader.ReadToEndAsync();
                newContent = existingContent + logEntry;
            }

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(newContent);
            using var stream = new MemoryStream(bytes);

            await fileClient.CreateAsync(bytes.Length);
            await fileClient.UploadRangeAsync(new HttpRange(0, bytes.Length), stream);
        }

        public async Task<string> UploadImageAsync(Stream imageStream, string fileName)
        {
            var containerClient = await GetBlobContainerClientAsync("product-images");
            var blobClient = containerClient.GetBlobClient(fileName);

            await blobClient.UploadAsync(imageStream, overwrite: true);

            return blobClient.Uri.ToString();
        }

        private string GetContentType(string fileExtension)
        {
            return fileExtension.ToLower() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };
        }

        public async Task<int> GetCustomerCountAsync()
        {
            var tableClient = await GetTableClientAsync("Customers");
            int count = 0;
            await foreach (var _ in tableClient.QueryAsync<TableEntity>("PartitionKey eq 'Customers'"))
                count++;
            return count;
        }

        public async Task<int> GetProductCountAsync()
        {
            var tableClient = await GetTableClientAsync();
            int count = 0;
            await foreach (var _ in tableClient.QueryAsync<Product>("PartitionKey eq 'Products'"))
                count++;
            return count;
        }

        public async Task<int> GetActiveProductCountAsync()
        {
            var tableClient = await GetTableClientAsync();
            int count = 0;
            await foreach (var _ in tableClient.QueryAsync<Product>("PartitionKey eq 'Products' and IsActive eq true"))
                count++;
            return count;
        }

        public async Task<int> GetQueueMessageCountAsync()
        {
            var queueClient = await GetQueueClientAsync();
            var properties = await queueClient.GetPropertiesAsync();
            return properties.Value.ApproximateMessagesCount;
        }
    }
}