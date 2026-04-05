namespace DSAP.Models
{
    internal class EquipParamGoods : IParam
    {
        public static uint Size { get; set; } = 0x5c; // true DSR inter-entry spacing (92 bytes)
        public static int spOffset = 0xf0;
    }
}
