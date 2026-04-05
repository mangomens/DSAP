using Archipelago.MultiClient.Net.Models;
using DSAP.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static DSAP.Enums;

namespace DSAP.Helpers
{
    internal class ShopHelper
    {
        /// <summary>
        /// Build a mapping of ShopLineupParam row IDs to the AP location IDs
        /// that should replace them. All items become AP stubs; equipType is
        /// derived from the scouted item's category so the stub lands in the
        /// correct shop tab and FMG table.
        /// </summary>
        public static void BuildShopReplacementMap(
            out Dictionary<int, ShopReplacement> resultMap,
            Dictionary<long, ScoutedItemInfo> scoutedLocationInfo,
            Dictionary<int, DarkSoulsItem> allItemsByApId)
        {
            var result = new Dictionary<int, ShopReplacement>();
            var shopFlags = LocationHelper.GetShopFlags()
                                         .Where(x => x.IsEnabled).ToList();

            foreach (var (locId, scoutedInfo) in scoutedLocationInfo)
            {
                var matchingShopFlags = shopFlags.Where(x => x.Id == (int)locId);
                foreach (var shop in matchingShopFlags)
                {
                    int equipType = 3; // default to Goods for non-DSR items
                    if (allItemsByApId.TryGetValue((int)scoutedInfo.ItemId, out var dsrItem))
                        equipType = GetEquipType(dsrItem.Category);

                    // Use aligned equip IDs for weapons/protectors so DSR's icon grouping works
                    int equipId = (int)locId;
                    if (equipType == 0 && ApItemInjectorHelper.WeaponAlignedEquipIds.TryGetValue(locId, out int wId))
                        equipId = wId;
                    else if (equipType == 1 && ApItemInjectorHelper.ProtectorAlignedEquipIds.TryGetValue(locId, out int pId))
                        equipId = pId;

                    result[shop.Row] = new ShopReplacement
                    {
                        EquipId = equipId,
                        EquipType = equipType,
                        Value = 0,
                        SellQuantity = 1,
                        EventFlag = shop.PurchaseFlag,
                        ShopType = 0
                    };
                    Log.Logger.Verbose($"Shop replacement: row {shop.Row} -> AP loc {locId} equipId={equipId} ({shop.Name}), equipType={equipType}");
                }
            }

            Log.Logger.Information($"Built shop replacement map with {result.Count} entries");
            resultMap = result;
        }

        internal static int GetEquipType(DSItemCategory category)
        {
            return (int)category switch
            {
                0x00000000 => 0,  // weapons (melee, arrows, shields, staves, catalysts)
                0x10000000 => 1,  // armor
                0x20000000 => 2,  // rings
                _ => 3            // else goods and fallback
            };
        }

        /// <summary>
        /// Overwrite ShopLineupParam rows in DSR memory with AP replacements.
        /// </summary>
        public static void OverwriteShopParams(ParamStruct<ShopLineupParam> paramStruct, Dictionary<int, ShopReplacement> replacementMap)
        {
            int overwritten = 0;

            foreach (var entry in paramStruct.ParamEntries)
            {
                if (replacementMap.TryGetValue((int)entry.id, out var replacement))
                {
                    int offset = (int)entry.paramOffset;

                    // equipId (s32 at +0x0)
                    Array.Copy(BitConverter.GetBytes(replacement.EquipId), 0,
                        paramStruct.ParamBytes, offset + 0x0, 4);

                    // value / price (s32 at +0x4)
                    Array.Copy(BitConverter.GetBytes(replacement.Value), 0,
                        paramStruct.ParamBytes, offset + 0x4, 4);

                    // mtrlId (s32 at +0x8) — set to -1 (no material cost)
                    Array.Copy(BitConverter.GetBytes(-1), 0,
                        paramStruct.ParamBytes, offset + 0x8, 4);

                    // eventFlag (s32 at +0xC)
                    Array.Copy(BitConverter.GetBytes(replacement.EventFlag), 0,
                        paramStruct.ParamBytes, offset + 0xC, 4);

                    // sellQuantity (s16 at +0x14)
                    Array.Copy(BitConverter.GetBytes((short)replacement.SellQuantity), 0,
                        paramStruct.ParamBytes, offset + 0x14, 2);

                    // shopType (u8 at +0x16) — keep as 0 (regular shop)
                    paramStruct.ParamBytes[offset + 0x16] = (byte)replacement.ShopType;

                    // equipType (u8 at +0x17) — 3 = Goods
                    paramStruct.ParamBytes[offset + 0x17] = (byte)replacement.EquipType;

                    overwritten++;
                }
            }

            Log.Logger.Information($"{overwritten} shop items overwritten");
        }

        /// <summary>
        /// Full shop update pipeline: read → modify → write.
        /// </summary>
        public static bool UpdateShopLineupParams(
            Dictionary<int, ShopReplacement> replacementMap)
        {
            bool reloadRequired = ParamHelper.ReadFromBytes(
                out ParamStruct<ShopLineupParam> paramStruct,
                ShopLineupParam.spOffset,
                (ps) => ps.ParamEntries.Last().id >= 99999990);

            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of ShopLineupParam");
                return false;
            }

            OverwriteShopParams(paramStruct, replacementMap);

            // Add sentinel entry for reload detection
            byte[] dummyBytes = new byte[ShopLineupParam.Size];
            paramStruct.AddParam(99999998, dummyBytes, Encoding.ASCII.GetBytes(""));
            paramStruct.ParamEntries.Sort((x, y) => x.id.CompareTo(y.id));

            ParamHelper.WriteFromParamSt(paramStruct, ShopLineupParam.spOffset);
            return true;
        }
    }

    internal class ShopReplacement
    {
        public int EquipId { get; set; }
        public int EquipType { get; set; }
        public int Value { get; set; }
        public short SellQuantity { get; set; }
        public int EventFlag { get; set; }
        public int ShopType { get; set; }
    }
}
