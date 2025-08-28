using Azure;
using Azure.Data.Tables;
using System.ComponentModel.DataAnnotations;

namespace ABCRetail.Cloud.Models
{
    public class CustomerProfile : ITableEntity
    {
        [Required]
        public string PartitionKey { get; set; } = "Customers";
        [Required]
        public string RowKey { get; set; } = Guid.NewGuid().ToString();
        [Required]
        public string FirstName { get; set; } = string.Empty;
        [Required]
        public string LastName { get; set; } = string.Empty;
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;
        [Required]
        public string Password { get; set; } = string.Empty;
        [Phone]
        public string? PhoneNumber { get; set; }
        public string? Address { get; set; }
        public DateTime JoinDate { get; set; } = DateTime.UtcNow;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }
}

