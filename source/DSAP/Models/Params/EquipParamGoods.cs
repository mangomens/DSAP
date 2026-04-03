namespace DSAP.Models
{
    internal class EquipParamGoods : IParam
    {
        public static uint Size { get; set; } = 0x4c; // may be technically incorrect (full row is 0x5c) but left as-is since existing code works
        public static int spOffset = 0xf0;
    }
}
