namespace DSAP.Models
{
    internal class EquipParamProtector : IParam
    {
        public static uint Size { get; set; } = 0xe8;
        public static int spOffset = 0x60;
    }
}
