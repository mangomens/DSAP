namespace DSAP.Models
{
    internal class ShopLineupParam : IParam
    {
        public static uint Size { get; set; } = 0x20; // 32 bytes per row
        public static int spOffset = 0x720;
    }
}
