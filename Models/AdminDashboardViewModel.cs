using ABCRetail.Cloud.Models;
using System.Collections.Generic;

namespace ABCRetail.Cloud.Models.ViewModels
{
    public class AdminDashboardViewModel
    {
        public int CustomerCount { get; set; } = 0;
        public int ProductCount { get; set; } = 0;
        public List<Order> RecentOrders { get; set; } = new List<Order>();
        public double TotalRevenue { get; set; } = 0;
        public int PendingOrdersCount { get; set; } = 0;
        public int OutOfStockProductsCount { get; set; } = 0;
        public int LowStockProductsCount { get; set; } = 0;
    }
}