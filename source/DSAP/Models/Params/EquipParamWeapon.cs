namespace DSAP.Models
{
    internal class EquipParamWeapon : IParam
    {
        public static uint Size { get; set; } = 0x10b; // may be technically incorrect (full row is 0x110) but left as-is since existing code works
        public static int spOffset = 0x18;
    }
}
