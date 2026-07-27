namespace ClusterSoakTest.Support
{
    public class LoadOrderDocument
    {
        public string Id { get; set; } = string.Empty;
        public string OrderId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public string StoreId { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string ItemSku { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public DateTime WrittenUtc { get; set; }
        public string Notes { get; set; } = string.Empty;
    }
}
