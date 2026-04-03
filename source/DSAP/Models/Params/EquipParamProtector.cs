namespace DSAP.Models
{
    internal class EquipParamProtector : IParam
    {
        public static uint Size { get; set; } = 0xe8; // full DSR row size (may need verification)
        public static int spOffset = 0x60;
    }
}
