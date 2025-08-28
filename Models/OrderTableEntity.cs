using Azure;
using Azure.Data.Tables;
using System;

namespace ABCRetail.Cloud.Models
{
    public class OrderTableEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = "Orders";
        public string RowKey { get; set; } = Guid.NewGuid().ToString();
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public string CustomerId { get; set; } = string.Empty;
        public string ItemsJson { get; set; } = string.Empty;
        public double TotalAmount { get; set; }  // Store calculated total
        public string Status { get; set; } = "Pending";
        public DateTime OrderDate { get; set; } = DateTime.UtcNow;

        public OrderTableEntity()
        {
            ETag = new ETag();
            Timestamp = DateTimeOffset.UtcNow;
        }
    }
}