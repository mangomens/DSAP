namespace DSAP.Models
{
    internal class EquipParamAccessory : IParam
    {
        public static uint Size { get; set; } = 0x40; // full DSR row size (may need verification)
        public static int spOffset = 0xa8;
    }
}
