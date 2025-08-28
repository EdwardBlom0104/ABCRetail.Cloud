using ABCRetail.Cloud.Models;

namespace ABCRetail.Cloud.Models.ViewModels
{
    public class CustomerDashboardViewModel
    {
        public required Customer Customer { get; set; }
        public List<Order> RecentOrders { get; set; } = new List<Order>();
        public int OrderCount { get; set; }
        public double TotalSpent { get; set; }
        public int PendingOrdersCount { get; set; }
        public int WishlistItemsCount { get; set; }
    }
}