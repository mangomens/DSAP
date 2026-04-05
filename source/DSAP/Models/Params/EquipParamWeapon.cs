namespace DSAP.Models
{
    internal class EquipParamWeapon : IParam
    {
        public static uint Size { get; set; } = 0x110; // true DSR inter-entry spacing (272 bytes)
        public static int spOffset = 0x18;
    }
}
