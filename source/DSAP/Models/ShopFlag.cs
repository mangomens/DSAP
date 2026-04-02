namespace DSAP.Models
{
    public class ShopFlag
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Row { get; set; }
        public int PurchaseFlag { get; set; }
        public bool IsEnabled { get; set; }
        public int VanillaEquipType { get; set; } = 3;
    }
}
