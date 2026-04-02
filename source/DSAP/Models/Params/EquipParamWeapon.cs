namespace DSAP.Models
{
    internal class EquipParamWeapon : IParam
    {
        public static uint Size { get; set; } = 0x10b; // 0x5 bytes short of full DSR row size (0x110)
        public static int spOffset = 0x18;
    }
}
