using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ABCRetail.Cloud.Models
{
    public class Order : ITableEntity
    {
        // Required ITableEntity properties
        public string PartitionKey { get; set; } = "Orders";
        public string RowKey { get; set; } = Guid.NewGuid().ToString();
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        // Order properties
        public string CustomerId { get; set; } = string.Empty;
        public List<OrderItem> Items { get; set; } = new List<OrderItem>();
        public double TotalAmount { get; set; }  // Calculated automatically
        public string Status { get; set; } = "Pending";
        public DateTime OrderDate { get; set; } = DateTime.UtcNow;
        public string ItemsJson { get; set; } = string.Empty; // For Table Storage serialization

        // Constructor for proper initialization
        public Order()
        {
            ETag = new ETag();
            Timestamp = DateTimeOffset.UtcNow;
        }

        // Helper to calculate total
        public void CalculateTotal()
        {
            if (Items != null && Items.Count > 0)
                TotalAmount = Items.Sum(i => i.UnitPrice * i.Quantity);
            else
                TotalAmount = 0;
        }
    }

    public class OrderItem
    {
        public string ProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public double UnitPrice { get; set; }
    }
}