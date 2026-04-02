using Archipelago.Core.Util;
using Archipelago.MultiClient.Net.Models;
using DSAP.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Location = Archipelago.Core.Models.Location;
using static DSAP.Enums;

namespace DSAP.Helpers
{
    internal class ShopHelper
    {
        /// <summary>
        /// Build a mapping of ShopLineupParam row IDs to the AP location IDs
        /// that should replace them. Native DSR items for the local player
        /// use the real DSR equip ID and type; foreign items use AP stubs.
        /// </summary>
        public static void BuildShopReplacementMap(
            out Dictionary<int, ShopReplacement> resultMap,
            out HashSet<long> nativeShopLocIds,
            Dictionary<long, ScoutedItemInfo> scoutedLocationInfo,
            Dictionary<int, DarkSoulsItem> allItemsByApId,
            int mySlot)
        {
            var result = new Dictionary<int, ShopReplacement>();
            var nativeLocIds = new HashSet<long>();
            var shopFlags = LocationHelper.GetShopFlags()
                                         .Where(x => x.IsEnabled).ToList();
            int nativeCount = 0;

            foreach (var (locId, scoutedInfo) in scoutedLocationInfo)
            {
                var matchingShopFlags = shopFlags.Where(x => x.Id == (int)locId);
                foreach (var shop in matchingShopFlags)
                {
                    // Check if this is a native DSR item destined for us
                    if (scoutedInfo.Player.Slot == mySlot
                        && allItemsByApId.TryGetValue((int)scoutedInfo.ItemId, out var dsrItem)
                        && IsPhysicalItemCategory(dsrItem.Category))
                    {
                        result[shop.Row] = new ShopReplacement
                        {
                            EquipId = dsrItem.Id,
                            EquipType = GetEquipType(dsrItem.Category),
                            Value = 0,
                            SellQuantity = 1,
                            EventFlag = shop.PurchaseFlag,
                            ShopType = 0
                        };
                        nativeCount++;
                        nativeLocIds.Add(locId);
                        Log.Logger.Verbose($"Shop replacement (native): row {shop.Row} -> {dsrItem.Name} (id={dsrItem.Id}, type={GetEquipType(dsrItem.Category)})");
                    }
                    else
                    {
                        result[shop.Row] = new ShopReplacement
                        {
                            EquipId = (int)locId,
                            EquipType = 3,          // Goods — AP stub in EquipParamGoods
                            Value = 0,
                            SellQuantity = 1,
                            EventFlag = shop.PurchaseFlag,
                            ShopType = 0
                        };
                        Log.Logger.Verbose($"Shop replacement (stub): row {shop.Row} -> AP loc {locId} ({shop.Name})");
                    }
                }
            }

            Log.Logger.Information($"Built shop replacement map with {result.Count} entries ({nativeCount} native, {result.Count - nativeCount} stubs)");
            resultMap = result;
            nativeShopLocIds = nativeLocIds;
        }

        private static bool IsPhysicalItemCategory(DSItemCategory category)
        {
            int val = (int)category;
            return val == 0x00000000 || val == 0x10000000 ||
                   val == 0x20000000 || val == 0x40000000;
        }

        private static int GetEquipType(DSItemCategory category)
        {
            return (int)category switch
            {
                0x00000000 => 0,  // weapon (melee, ranged, shields, spell tools)
                0x10000000 => 1,  // protector (armor)
                0x20000000 => 2,  // accessory (rings)
                _ => 3            // goods (consumables, keys, spells, upgrade mats)
            };
        }

        /// <summary>
        /// Overwrite ShopLineupParam rows in DSR memory with AP replacements.
        /// </summary>
        public static void OverwriteShopParams(
            ParamStruct<ShopLineupParam> paramStruct,
            Dictionary<int, ShopReplacement> replacementMap)
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

        /// <summary>
        /// Read current ShopLineupParam from memory and dump all rows to a file
        /// alongside what we expected to write, plus purchase flag status.
        /// </summary>
        public static void DumpShopDiagnostics(Dictionary<int, ShopReplacement> replacementMap)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP Shop Diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"ReplacementMap entries: {replacementMap.Count}");
            sb.AppendLine();

            // Read current param state from memory
            ParamHelper.ReadFromBytes(
                out ParamStruct<ShopLineupParam> paramStruct,
                ShopLineupParam.spOffset,
                (ps) => false); // always read, never skip

            var shopFlags = LocationHelper.GetShopFlags();
            var shopFlagsByRow = shopFlags.ToDictionary(x => x.Row, x => x);

            var baseEventAddr = AddressHelper.GetEventFlagsOffset();

            sb.AppendLine($"Total param rows read: {paramStruct.ParamEntries.Count}");
            sb.AppendLine();

            // Dump ALL rows (filter to ranges we care about)
            sb.AppendLine("=== All ShopLineupParam Rows (merchant ranges) ===");
            sb.AppendLine("Row | equipId | value | mtrlId | eventFlag | pad | sellQty | shopType | equipType | EXPECTED equipId | MATCH | flagName | purchaseFlag | flagSet");
            sb.AppendLine("--- | ------- | ----- | ------ | --------- | --- | ------- | -------- | --------- | ---------------- | ----- | -------- | ------------ | -------");

            foreach (var entry in paramStruct.ParamEntries.OrderBy(e => e.id))
            {
                int rowId = (int)entry.id;
                int offset = (int)entry.paramOffset;

                // Read current values from param bytes
                int equipId = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x0);
                int value = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x4);
                int mtrlId = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x8);
                int eventFlag = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0xC);
                int pad = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x10);
                short sellQty = BitConverter.ToInt16(paramStruct.ParamBytes, offset + 0x14);
                byte shopType = paramStruct.ParamBytes[offset + 0x16];
                byte equipType = paramStruct.ParamBytes[offset + 0x17];

                // Check if we have an expected replacement
                string expectedEquipId = "";
                string match = "";
                string flagName = "";
                string purchaseFlagStr = "";
                string flagSetStr = "";

                if (replacementMap.TryGetValue(rowId, out var expected))
                {
                    expectedEquipId = expected.EquipId.ToString();
                    match = (equipId == expected.EquipId && value == expected.Value
                             && equipType == expected.EquipType && eventFlag == expected.EventFlag)
                        ? "YES" : "NO";
                }

                if (shopFlagsByRow.TryGetValue(rowId, out var flag))
                {
                    flagName = flag.Name;
                    purchaseFlagStr = flag.PurchaseFlag.ToString();
                    // Check if purchase flag is set in memory
                    var (flagOffset, flagBit) = AddressHelper.GetEventFlagOffset(flag.PurchaseFlag);
                    var loc = new Location
                    {
                        Address = baseEventAddr + flagOffset,
                        AddressBit = flagBit
                    };
                    flagSetStr = loc.Check() ? "SET" : "unset";
                }

                sb.AppendLine($"{rowId} | {equipId} | {value} | {mtrlId} | {eventFlag} | {pad} | {sellQty} | {shopType} | {equipType} | {expectedEquipId} | {match} | {flagName} | {purchaseFlagStr} | {flagSetStr}");
            }

            sb.AppendLine();
            sb.AppendLine("=== ShopFlags not found in param entries ===");
            var paramRowIds = paramStruct.ParamEntries.Select(e => (int)e.id).ToHashSet();
            foreach (var flag in shopFlags.Where(f => f.IsEnabled && !paramRowIds.Contains(f.Row)))
            {
                sb.AppendLine($"MISSING ROW: {flag.Row} ({flag.Name}, AP ID={flag.Id})");
            }

            sb.AppendLine();
            sb.AppendLine("=== Replacement map entries not matched to any param row ===");
            foreach (var (row, repl) in replacementMap)
            {
                if (!paramRowIds.Contains(row))
                    sb.AppendLine($"ORPHAN REPLACEMENT: row={row} equipId={repl.EquipId}");
            }

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_shop_diag_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());

            Log.Logger.Warning($"Shop diagnostics written to: {fullPath}");
            Log.Logger.Warning($"Replacement map: {replacementMap.Count} entries, Param rows: {paramStruct.ParamEntries.Count}");

            // Also log a quick summary to the client
            int matchCount = 0;
            int mismatchCount = 0;
            foreach (var entry in paramStruct.ParamEntries)
            {
                int rowId = (int)entry.id;
                if (replacementMap.TryGetValue(rowId, out var expected))
                {
                    int offset2 = (int)entry.paramOffset;
                    int curEquipId = BitConverter.ToInt32(paramStruct.ParamBytes, offset2 + 0x0);
                    if (curEquipId == expected.EquipId) matchCount++;
                    else mismatchCount++;
                }
            }
            Log.Logger.Warning($"Rows matching expected: {matchCount}, mismatched: {mismatchCount}");
        }

        /// <summary>
        /// Analyze goodsType byte values across all EquipParamGoods entries.
        /// Groups entries by goodsType value and shows curated items per category.
        /// </summary>
        public static void DumpGoodsTypeAnalysis()
        {
            ParamHelper.ReadFromBytes(out ParamStruct<EquipParamGoods> paramStruct,
                EquipParamGoods.spOffset, (ps) => false);

            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP Goods Type Analysis — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Total EquipParamGoods entries: {paramStruct.ParamEntries.Count}");
            sb.AppendLine($"Entry size: 0x{EquipParamGoods.Size:X} ({EquipParamGoods.Size})");
            sb.AppendLine();

            // --- Part 1: goodsType histogram across ALL entries ---
            sb.AppendLine("=== Part 1: goodsType (ParamBytes[0x3A]) histogram ===");
            var groups = new Dictionary<byte, List<uint>>();
            foreach (var entry in paramStruct.ParamEntries)
            {
                byte gt = paramStruct.ParamBytes[entry.paramOffset + 0x3A];
                if (!groups.ContainsKey(gt))
                    groups[gt] = new List<uint>();
                groups[gt].Add(entry.id);
            }
            foreach (var kvp in groups.OrderBy(x => x.Key))
            {
                var ids = kvp.Value;
                string sampleIds = string.Join(", ", ids.Take(15));
                if (ids.Count > 15) sampleIds += $" ... ({ids.Count} total)";
                sb.AppendLine($"  goodsType={kvp.Key}: count={ids.Count}, ids=[{sampleIds}]");
            }

            // --- Part 2: Neighboring byte histogram for context ---
            sb.AppendLine();
            sb.AppendLine("=== Part 2: Neighboring byte histograms (confirm field boundaries) ===");
            int[] neighborOffsets = { 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F };
            foreach (int off in neighborOffsets)
            {
                var hist = new Dictionary<byte, int>();
                foreach (var entry in paramStruct.ParamEntries)
                {
                    byte val = paramStruct.ParamBytes[entry.paramOffset + off];
                    if (!hist.ContainsKey(val)) hist[val] = 0;
                    hist[val]++;
                }
                sb.Append($"  [0x{off:X2}]: ");
                foreach (var kvp in hist.OrderBy(x => x.Key))
                    sb.Append($"{kvp.Key}×{kvp.Value} ");
                sb.AppendLine();
            }

            // --- Part 3: Curated items with known shop categories ---
            sb.AppendLine();
            sb.AppendLine("=== Part 3: Curated items — goodsType + nearby bytes ===");
            sb.AppendLine("Format: ID (name) [expected_category] => goodsType=X, bytes[0x34..0x40]=hex");
            var curated = new (uint id, string name, string cat)[] {
                // Consumables
                (100,  "White Sign Soapstone", "online/key?"),
                (210,  "Repair Powder",        "consumable"),
                (260,  "Green Blossom",         "consumable"),
                (290,  "Firebomb",              "consumable"),
                (300,  "Black Firebomb",        "consumable"),
                (330,  "Homeward Bone",         "consumable"),
                (340,  "Lloyd's Talisman",      "consumable"),
                (370,  "Prism Stone",           "consumable"),
                (380,  "Indictment",            "consumable"),
                // Upgrade Materials
                (1000, "Titanite Shard",        "upgrade"),
                (1010, "Large Titanite Shard",  "upgrade"),
                (1020, "Green Titanite Shard",  "upgrade"),
                (1030, "Titanite Chunk",        "upgrade"),
                (1040, "Twinkling Titanite",    "upgrade"),
                (1050, "Demon Titanite",        "upgrade"),
                (1060, "Titanite Slab",         "upgrade"),
                // Key Items
                (2001, "Master Key",            "key"),
                (2002, "Basement Key",          "key"),
                (2005, "Sewer Chamber Key",     "key"),
                (2007, "Crest of Artorias",     "key"),
                (2010, "Residence Key",         "key"),
                (2011, "Big Pilgrim's Key",     "key"),
                (2501, "Lordvessel",            "key"),
                // Souls
                (400,  "Soul of a Lost Undead", "soul"),
                (410,  "Large Soul Lost Undead","soul"),
                // Misc
                (384,  "Peculiar Doll",         "key?"),
                (500,  "Copper Coin",           "misc"),
                (510,  "Rubbish",               "misc"),
                // Embers (upgrade-like?)
                (800,  "Large Ember",           "ember"),
                (801,  "Very Large Ember",      "ember"),
                (810,  "Crystal Ember",         "ember"),
            };

            foreach (var (id, name, cat) in curated)
            {
                var entry = paramStruct.ParamEntries.Find(e => e.id == id);
                if (entry.id == 0 && id != 0)
                {
                    // Check if entry wasn't found (default tuple)
                    bool found = paramStruct.ParamEntries.Any(e => e.id == id);
                    if (!found) { sb.AppendLine($"  ID {id} ({name}) [{cat}]: NOT FOUND"); continue; }
                }
                byte goodsType = paramStruct.ParamBytes[entry.paramOffset + 0x3A];
                // Also grab bytes 0x34..0x43 for context
                string hexRange = "";
                for (int b = 0x34; b < Math.Min(0x44, (int)EquipParamGoods.Size); b++)
                    hexRange += $"{paramStruct.ParamBytes[entry.paramOffset + b]:X2} ";
                sb.AppendLine($"  ID {id,5} ({name,-25}) [{cat,-12}] => goodsType={goodsType}, bytes[0x34..0x43]={hexRange.TrimEnd()}");
            }

            // --- Part 4: All enum-like byte positions (2-10 distinct values) ---
            sb.AppendLine();
            sb.AppendLine("=== Part 4: All enum-like byte positions in EquipParamGoods ===");
            sb.AppendLine("Positions with 2-10 distinct values across all entries:");
            for (int pos = 0; pos < (int)EquipParamGoods.Size; pos++)
            {
                var vals = new Dictionary<byte, int>();
                foreach (var entry in paramStruct.ParamEntries)
                {
                    byte v = paramStruct.ParamBytes[entry.paramOffset + pos];
                    if (!vals.ContainsKey(v)) vals[v] = 0;
                    vals[v]++;
                }
                if (vals.Count >= 2 && vals.Count <= 10)
                {
                    sb.Append($"  [0x{pos:X2}]: {vals.Count} values — ");
                    foreach (var kvp in vals.OrderBy(x => x.Key))
                        sb.Append($"{kvp.Key}×{kvp.Value} ");
                    sb.AppendLine();
                }
            }

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_goodstype_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());
            Log.Logger.Warning($"Goods type analysis written to: {fullPath}");
        }

        /// <summary>
        /// Dump param table metadata and MsgMan offsets to help plan multi-equip-type shop stubs.
        /// Run with ShopSanity OFF so shop rows are vanilla.
        /// </summary>
        public static void DumpParamInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP Param Info Diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();

            // --- Part A: Param table entry sizes ---
            sb.AppendLine("=== Part A: Param Table Entry Sizes ===");
            int[] paramOffsets = { 0x18, 0x60, 0xA8, 0xF0, 0x720 };
            string[] paramNames = { "EquipParamWeapon", "EquipParamProtector", "EquipParamAccessory", "EquipParamGoods", "ShopLineupParam" };

            for (int p = 0; p < paramOffsets.Length; p++)
            {
                try
                {
                    ulong resCapLoc = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + paramOffsets[p]));
                    if (resCapLoc == 0) { sb.AppendLine($"{paramNames[p]} (+0x{paramOffsets[p]:X}): ResCapLoc is NULL"); continue; }

                    int bufferSize = (int)Memory.ReadUInt(resCapLoc + 0x30);
                    ulong bufferLoc = Memory.ReadULong(resCapLoc + 0x38);

                    // Read header from bufferLoc - 0x10 (need 0x80 to cover entry table)
                    byte[] header = Memory.ReadByteArray(bufferLoc - 0x10, 0x80);
                    int baseOffset = 0x10;
                    int stringOffset = BitConverter.ToInt32(header, baseOffset + 0x0);
                    ushort paramsOff = BitConverter.ToUInt16(header, baseOffset + 0x4);
                    ushort numEntries = BitConverter.ToUInt16(header, baseOffset + 0xA);

                    // Compute entry size from spacing between first two entries
                    int entrySize = 0;
                    if (numEntries >= 2)
                    {
                        int ent0Off = baseOffset + 0x30;
                        uint param0 = BitConverter.ToUInt32(header, ent0Off + 4) - paramsOff;
                        uint param1 = BitConverter.ToUInt32(header, ent0Off + 0xC + 4) - paramsOff;
                        entrySize = (int)(param1 - param0);
                    }

                    // Read first 5 entry IDs from entry table at bufferLoc + 0x30
                    byte[] entryTable = Memory.ReadByteArray(bufferLoc + 0x30, Math.Min((int)numEntries, 5) * 0xC);
                    var ids = new List<string>();
                    for (int i = 0; i < Math.Min((int)numEntries, 5); i++)
                    {
                        uint eid = BitConverter.ToUInt32(entryTable, i * 0xC);
                        ids.Add(eid.ToString());
                    }

                    sb.AppendLine($"{paramNames[p]} (+0x{paramOffsets[p]:X}): entries={numEntries}, entrySize=0x{entrySize:X} ({entrySize}), bufferSize={bufferSize}, firstIds=[{string.Join(", ", ids)}]");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{paramNames[p]} (+0x{paramOffsets[p]:X}): ERROR - {ex.Message}");
                }
            }

            // --- Part B: MsgMan table scan ---
            sb.AppendLine();
            sb.AppendLine("=== Part B: MsgMan Pointer Scan ===");
            sb.AppendLine("Scanning MsgMan root + offsets 0x200..0x800 for non-null pointers.");
            sb.AppendLine("Known: Goods Names=+0x380, Goods Captions=+0x378, Goods Descriptions=+0x328, System Text=+0x3E0");
            sb.AppendLine();

            ulong msgManRoot = Memory.ReadULong(0x141c7e3e8);
            sb.AppendLine($"MsgMan root: 0x{msgManRoot:X}");

            // Known test IDs per type — will try to look up in each FMG buffer
            var testLookups = new (string typeName, uint testId)[]
            {
                ("Weapon(Dagger)", 100000),
                ("Weapon(ShortSword)", 200000),
                ("Protector(ChainHelm)", 230000),
                ("Protector(LeatherArmor)", 140000),
                ("Accessory(RingSacrifice)", 100),
                ("Accessory(Havel)", 0),
                ("Goods(PrismStone)", 370),
                ("Goods(Firebomb)", 290)
            };

            for (int off = 0x200; off <= 0x800; off += 8)
            {
                try
                {
                    ulong ptr = Memory.ReadULong(msgManRoot + (ulong)off);
                    if (ptr == 0 || ptr < 0x10000) continue; // skip null/clearly-invalid

                    // Try to read FMG header
                    uint fmgSize = Memory.ReadUInt(ptr + 0x4);
                    ushort numSpanMaps = Memory.ReadUShort(ptr + 0xC);
                    ushort numStrEntries = Memory.ReadUShort(ptr + 0x10);

                    if (fmgSize == 0 || fmgSize > 0x1000000 || numSpanMaps > 200 || numStrEntries > 50000)
                        continue; // not a valid FMG

                    // Try to find each test ID
                    var found = new List<string>();
                    foreach (var (typeName, testId) in testLookups)
                    {
                        ulong strLoc = ApItemInjectorHelper.FindMsg(ptr, testId);
                        if (strLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(strLoc, 120);
                            string str = Encoding.Unicode.GetString(strBytes);
                            int nullIdx = str.IndexOf('\0');
                            if (nullIdx >= 0) str = str.Substring(0, nullIdx);
                            if (str.Length > 40) str = str.Substring(0, 40) + "...";
                            found.Add($"{typeName}=\"{str}\"");
                        }
                    }

                    string foundStr = found.Count > 0 ? string.Join(", ", found) : "(no test IDs found)";
                    sb.AppendLine($"  +0x{off:X3}: ptr=0x{ptr:X}, size={fmgSize}, spans={numSpanMaps}, entries={numStrEntries} => {foundStr}");

                    // For UNIDENTIFIED FMGs, dump span ranges + first few resolvable strings
                    if (found.Count == 0)
                    {
                        // Dump first 5 span entries (each 0xC bytes at ptr+0x1C)
                        int spansToDump = Math.Min((int)numSpanMaps, 5);
                        for (int si = 0; si < spansToDump; si++)
                        {
                            ulong spanAddr = ptr + 0x1C + (ulong)(si * 0xC);
                            uint baseIdx = Memory.ReadUInt(spanAddr + 0x0);
                            uint low = Memory.ReadUInt(spanAddr + 0x4);
                            uint high = Memory.ReadUInt(spanAddr + 0x8);
                            sb.AppendLine($"    span[{si}]: ids {low}..{high} (baseIdx={baseIdx})");
                        }
                        // Read a few actual strings from the first span
                        ulong strTableOff = Memory.ReadUInt(ptr + 0x14);
                        uint span0Base = Memory.ReadUInt(ptr + 0x1C + 0x0);
                        uint span0Low = Memory.ReadUInt(ptr + 0x1C + 0x4);
                        int samplesToRead = Math.Min(3, (int)numStrEntries);
                        for (int si = 0; si < samplesToRead; si++)
                        {
                            try
                            {
                                uint strEntryOff = 4 * (span0Base + (uint)si);
                                uint strOff = Memory.ReadUInt(ptr + strTableOff + strEntryOff);
                                if (strOff == 0 || strOff > fmgSize) continue;
                                byte[] sBytes = Memory.ReadByteArray(ptr + strOff, 80);
                                string s = Encoding.Unicode.GetString(sBytes);
                                int ni = s.IndexOf('\0');
                                if (ni >= 0) s = s.Substring(0, ni);
                                if (s.Length > 50) s = s.Substring(0, 50) + "...";
                                uint sampleId = span0Low + (uint)si;
                                sb.AppendLine($"    sample id={sampleId}: \"{s}\"");
                            }
                            catch { }
                        }
                    }
                }
                catch { /* skip invalid reads */ }
            }

            // --- Part B2: Check non-pointer offsets for indirect FMG arrays ---
            sb.AppendLine();
            sb.AppendLine("=== Part B2: MsgMan indirect pointer check ===");
            sb.AppendLine("Checking if any MsgMan offsets hold pointers to structures containing FMG pointers.");
            // Some FMGs may be behind a two-level pointer: MsgMan+off -> struct -> FMG
            // Check a few suspicious offsets
            for (int off = 0x200; off <= 0x800; off += 8)
            {
                try
                {
                    ulong ptr1 = Memory.ReadULong(msgManRoot + (ulong)off);
                    if (ptr1 == 0 || ptr1 < 0x10000) continue;

                    // Check if this is NOT a valid FMG (already handled above)
                    uint maybeFmgSize = Memory.ReadUInt(ptr1 + 0x4);
                    ushort maybeSpans = Memory.ReadUShort(ptr1 + 0xC);
                    if (maybeFmgSize > 0 && maybeFmgSize < 0x1000000 && maybeSpans <= 200)
                        continue; // already handled as direct FMG

                    // Not a valid FMG — try reading it as a pointer to a struct with FMG pointers
                    sb.AppendLine($"  +0x{off:X3}: ptr=0x{ptr1:X} (not FMG) — checking sub-pointers at +0x0..+0x40:");
                    for (int suboff = 0; suboff <= 0x40; suboff += 8)
                    {
                        try
                        {
                            ulong subptr = Memory.ReadULong(ptr1 + (ulong)suboff);
                            if (subptr == 0 || subptr < 0x10000) continue;
                            uint subFmgSize = Memory.ReadUInt(subptr + 0x4);
                            ushort subSpans = Memory.ReadUShort(subptr + 0xC);
                            ushort subEntries = Memory.ReadUShort(subptr + 0x10);
                            if (subFmgSize > 0 && subFmgSize < 0x1000000 && subSpans > 0 && subSpans <= 200 && subEntries > 0)
                            {
                                // Found a sub-FMG! Try weapon/protector IDs
                                var subFound = new List<string>();
                                foreach (var (typeName, testId) in testLookups)
                                {
                                    ulong strLoc = ApItemInjectorHelper.FindMsg(subptr, testId);
                                    if (strLoc != 0)
                                    {
                                        byte[] strBytes = Memory.ReadByteArray(strLoc, 120);
                                        string str = Encoding.Unicode.GetString(strBytes);
                                        int nullIdx = str.IndexOf('\0');
                                        if (nullIdx >= 0) str = str.Substring(0, nullIdx);
                                        if (str.Length > 40) str = str.Substring(0, 40) + "...";
                                        subFound.Add($"{typeName}=\"{str}\"");
                                    }
                                }
                                string subFoundStr = subFound.Count > 0 ? string.Join(", ", subFound) : "(no test IDs)";
                                sb.AppendLine($"    sub+0x{suboff:X2}: ptr=0x{subptr:X}, size={subFmgSize}, spans={subSpans}, entries={subEntries} => {subFoundStr}");
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // --- Part C: Vanilla Shop equipTypes ---
            sb.AppendLine();
            sb.AppendLine("=== Part C: Vanilla ShopLineupParam equipTypes (merchant rows only) ===");
            sb.AppendLine("Row | equipId | value | sellQty | shopType | equipType | eventFlag");
            sb.AppendLine("--- | ------- | ----- | ------- | -------- | --------- | ---------");

            try
            {
                ParamHelper.ReadFromBytes(
                    out ParamStruct<ShopLineupParam> shopParam,
                    ShopLineupParam.spOffset,
                    (ps) => false);

                // Our merchant row ranges
                var merchantRanges = new (int min, int max, string name)[]
                {
                    (1100, 1134, "Undead Merchant Male"),
                    (1200, 1220, "Female Merchant"),
                    (1400, 1420, "Andre"),
                    (2400, 2401, "Ingward"),
                    (4401, 4408, "Oswald")
                };

                foreach (var entry in shopParam.ParamEntries.OrderBy(e => e.id))
                {
                    int rowId = (int)entry.id;
                    bool inRange = merchantRanges.Any(r => rowId >= r.min && rowId <= r.max);
                    if (!inRange) continue;

                    int offset = (int)entry.paramOffset;
                    int equipId = BitConverter.ToInt32(shopParam.ParamBytes, offset + 0x0);
                    int value = BitConverter.ToInt32(shopParam.ParamBytes, offset + 0x4);
                    int eventFlag = BitConverter.ToInt32(shopParam.ParamBytes, offset + 0xC);
                    short sellQty = BitConverter.ToInt16(shopParam.ParamBytes, offset + 0x14);
                    byte shopType = shopParam.ParamBytes[offset + 0x16];
                    byte equipType = shopParam.ParamBytes[offset + 0x17];

                    string rangeName = merchantRanges.First(r => rowId >= r.min && rowId <= r.max).name;
                    sb.AppendLine($"{rowId} | {equipId} | {value} | {sellQty} | {shopType} | {equipType} | {eventFlag} | {rangeName}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ERROR reading ShopLineupParam: {ex.Message}");
            }

            // --- Part D: Raw param bytes for known vanilla items (icon field identification) ---
            sb.AppendLine();
            sb.AppendLine("=== Part D: Raw Param Bytes for Known Items ===");
            sb.AppendLine("Dumping first N bytes of known vanilla entries to identify icon field offsets.");
            sb.AppendLine("Goods icon is at +0x2C (short). Looking for icon fields in other types.");
            sb.AppendLine();

            // Items to dump: (paramName, spOffset, itemId, expectedIconHint)
            var itemsToDump = new (string name, int spOff, uint itemId, string hint)[] {
                // Weapons - Dagger icon=0 typically, Short Sword, etc
                ("Weapon: Dagger (100000)", 0x18, 100000, "iconId likely near start"),
                ("Weapon: Short Sword (200000)", 0x18, 200000, ""),
                ("Weapon: Hand Axe (400000)", 0x18, 400000, ""),
                // Protectors - Leather Armor set
                ("Protector: Leather Armor (140000)", 0x60, 140000, ""),
                ("Protector: Chain Helm (230000)", 0x60, 230000, ""),
                // Accessories
                ("Accessory: Havel Ring (0)", 0xA8, 0, ""),
                ("Accessory: Ring of Sacrifice (100)", 0xA8, 100, ""),
                ("Accessory: Ring of Fog (109)", 0xA8, 109, ""),
                // Goods (known: icon at +0x2C)
                ("Goods: Prism Stone (370)", 0xF0, 370, "icon@+0x2C"),
                ("Goods: Firebomb (290)", 0xF0, 290, "icon@+0x2C"),
            };

            foreach (var (name, spOff, itemId, hint) in itemsToDump)
            {
                try
                {
                    ulong resCapLoc = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + spOff));
                    if (resCapLoc == 0) { sb.AppendLine($"{name}: ResCapLoc NULL"); continue; }
                    ulong bufferLoc = Memory.ReadULong(resCapLoc + 0x38);

                    // Read full header to find entry count and entry table
                    byte[] hdr = Memory.ReadByteArray(bufferLoc - 0x10, 0x40);
                    ushort numEntries = BitConverter.ToUInt16(hdr, 0x10 + 0xA);
                    ushort paramsOff = BitConverter.ToUInt16(hdr, 0x10 + 0x4);

                    // Read entry table at bufferLoc + 0x30 (AllBytes offset 0x40)
                    byte[] entryTable = Memory.ReadByteArray(bufferLoc + 0x30, numEntries * 0xC);
                    int foundIdx = -1;
                    uint foundParamOff = 0;
                    for (int i = 0; i < numEntries; i++)
                    {
                        uint eid = BitConverter.ToUInt32(entryTable, i * 0xC);
                        if (eid == itemId)
                        {
                            foundIdx = i;
                            foundParamOff = BitConverter.ToUInt32(entryTable, i * 0xC + 4) - paramsOff;
                            break;
                        }
                    }

                    if (foundIdx < 0) { sb.AppendLine($"{name}: ID not found in param table"); continue; }

                    // Compute entry size from first two entries
                    uint e0Off = BitConverter.ToUInt32(entryTable, 4) - paramsOff;
                    uint e1Off = BitConverter.ToUInt32(entryTable, 0xC + 4) - paramsOff;
                    int entrySize = (int)(e1Off - e0Off);

                    // Data at: bufferLoc - 0x10 + paramsOff + foundParamOff
                    ulong dataAddr = bufferLoc - 0x10 + paramsOff + foundParamOff;
                    int readLen = Math.Min(entrySize, 280); // cap at 280 bytes
                    byte[] paramBytes = Memory.ReadByteArray(dataAddr, readLen);

                    sb.AppendLine($"{name} ({hint}): idx={foundIdx}, paramOff=0x{foundParamOff:X}, entrySize=0x{entrySize:X}");
                    // Hex dump in rows of 16
                    for (int row = 0; row < readLen; row += 16)
                    {
                        int len = Math.Min(16, readLen - row);
                        string hex = BitConverter.ToString(paramBytes, row, len).Replace("-", " ");
                        // Also show short values at each even offset
                        var shorts = new List<string>();
                        for (int s = row; s < row + len - 1; s += 2)
                        {
                            short val = BitConverter.ToInt16(paramBytes, s);
                            if (val != 0 && val != -1) shorts.Add($"+0x{s:X2}={val}");
                        }
                        string shortStr = shorts.Count > 0 ? "  " + string.Join(" ", shorts) : "";
                        sb.AppendLine($"  +0x{row:X3}: {hex}{shortStr}");
                    }
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{name}: ERROR - {ex.Message}");
                }
            }

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_paraminfo_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());

            Log.Logger.Warning($"Param info diagnostics written to: {fullPath}");
        }

        /// <summary>
        /// Dump event flag bit states for all shop purchase flags.
        /// Shows the raw bytes surrounding each flag so we can see if
        /// neighboring bits are getting set when only one should be.
        /// </summary>
        public static void DumpShopEventFlags()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP Shop Event Flags — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine("Shows event flag bit states for all shop purchase flags.");
            sb.AppendLine("If buying one item sets multiple bits, we have a collision bug.");
            sb.AppendLine();

            var shopFlags = LocationHelper.GetShopFlags().Where(x => x.IsEnabled).OrderBy(x => x.Row).ToList();
            var baseEventAddr = AddressHelper.GetEventFlagsOffset();

            // Group flags by their byte address to spot shared-byte patterns
            var byByte = new SortedDictionary<ulong, List<(ShopFlag flag, int bit)>>();

            sb.AppendLine($"{"Row",-6} {"PurchaseFlag",-12} {"AddrOff",-12} {"Bit",-4} {"SET?",-5} {"Name"}");
            sb.AppendLine(new string('-', 80));

            int setCount = 0;
            foreach (var shop in shopFlags)
            {
                var (flagOffset, flagBit) = AddressHelper.GetEventFlagOffset(shop.PurchaseFlag);
                ulong absAddr = baseEventAddr + flagOffset;

                // Read the actual bit
                byte rawByte = Memory.ReadByte(absAddr);
                bool isSet = (rawByte & (1 << flagBit)) != 0;
                if (isSet) setCount++;

                sb.AppendLine($"{shop.Row,-6} {shop.PurchaseFlag,-12} 0x{flagOffset:X6}     {flagBit,-4} {(isSet ? "SET" : "---"),-5} {shop.Name}");

                if (!byByte.ContainsKey(absAddr))
                    byByte[absAddr] = new List<(ShopFlag, int)>();
                byByte[absAddr].Add((shop, flagBit));
            }

            sb.AppendLine();
            sb.AppendLine($"Total: {shopFlags.Count} flags, {setCount} currently SET");

            // Part 2: Raw byte dump grouped by shared address
            sb.AppendLine();
            sb.AppendLine("=== Shared-byte analysis ===");
            sb.AppendLine("Flags sharing the same event flag byte address.");
            sb.AppendLine("If we buy one and the whole byte changes, we'll see it here.");
            sb.AppendLine();

            foreach (var (addr, flags) in byByte)
            {
                if (flags.Count < 2) continue; // only show bytes shared by multiple flags

                byte rawByte = Memory.ReadByte(addr);
                string bits = Convert.ToString(rawByte, 2).PadLeft(8, '0');

                sb.AppendLine($"  Addr 0x{addr:X}: byte=0x{rawByte:X2} bits={bits}");
                foreach (var (flag, bit) in flags.OrderByDescending(f => f.bit))
                {
                    bool isSet = (rawByte & (1 << bit)) != 0;
                    sb.AppendLine($"    bit {bit}: {(isSet ? "SET" : "---")} — Row {flag.Row} ({flag.Name})");
                }
                sb.AppendLine();
            }

            // Part 3: Dump 8-byte window around each unique byte address
            sb.AppendLine("=== Raw byte windows (8 bytes around each flag byte) ===");
            var uniqueAddrs = byByte.Keys.ToList();
            // Deduplicate to 4-byte aligned windows
            var windows = new SortedSet<ulong>();
            foreach (var addr in uniqueAddrs)
                windows.Add(addr & ~3UL); // align to 4-byte boundary

            foreach (var windowBase in windows)
            {
                byte[] rawBytes = Memory.ReadByteArray(windowBase, 8);
                string hex = BitConverter.ToString(rawBytes).Replace("-", " ");
                string bitsStr = string.Join(" ", rawBytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
                sb.AppendLine($"  0x{windowBase:X}: {hex}  |  {bitsStr}");
            }

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_shopflags_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());

            Log.Logger.Warning($"Shop event flags written to: {fullPath} ({setCount}/{shopFlags.Count} flags set)");
        }

        /// <summary>
        /// Dump ALL 0x20 raw bytes of every merchant ShopLineupParam row.
        /// Annotates known fields and highlights unknown bytes at +0x10..+0x13 and +0x18..+0x1F.
        /// </summary>
        public static void DumpShopRawBytes()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP ShopRaw Dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine("Full 0x20-byte hex dump of every merchant ShopLineupParam row.");
            sb.AppendLine();
            sb.AppendLine("Field layout (0x20 = 32 bytes per row):");
            sb.AppendLine("  +0x00..+0x03: equipId (s32)");
            sb.AppendLine("  +0x04..+0x07: value (s32)");
            sb.AppendLine("  +0x08..+0x0B: mtrlId (s32)");
            sb.AppendLine("  +0x0C..+0x0F: eventFlag (s32)");
            sb.AppendLine("  +0x10..+0x13: UNKNOWN_10 (s32) — suspected second event flag or availability condition");
            sb.AppendLine("  +0x14..+0x15: sellQuantity (s16)");
            sb.AppendLine("  +0x16:        shopType (u8)");
            sb.AppendLine("  +0x17:        equipType (u8)");
            sb.AppendLine("  +0x18..+0x1F: UNKNOWN_18 (8 bytes)");
            sb.AppendLine();

            ParamHelper.ReadFromBytes(
                out ParamStruct<ShopLineupParam> paramStruct,
                ShopLineupParam.spOffset,
                (ps) => false);

            var merchantRanges = new (int min, int max, string name)[]
            {
                (1100, 1134, "Undead Merchant Male"),
                (1200, 1220, "Female Merchant"),
                (1400, 1420, "Andre"),
                (2400, 2401, "Ingward"),
                (4401, 4408, "Oswald")
            };

            string currentMerchant = "";
            foreach (var entry in paramStruct.ParamEntries.OrderBy(e => e.id))
            {
                int rowId = (int)entry.id;
                var range = merchantRanges.FirstOrDefault(r => rowId >= r.min && rowId <= r.max);
                if (range.name == null) continue;

                if (range.name != currentMerchant)
                {
                    currentMerchant = range.name;
                    sb.AppendLine($"--- {currentMerchant} (rows {range.min}..{range.max}) ---");
                }

                int offset = (int)entry.paramOffset;
                int bytesToRead = Math.Min((int)ShopLineupParam.Size, paramStruct.ParamBytes.Length - offset);
                if (bytesToRead < (int)ShopLineupParam.Size) { sb.AppendLine($"  Row {rowId}: TRUNCATED (only {bytesToRead} bytes available)"); continue; }

                // Read known fields
                int equipId = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x0);
                int value = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x4);
                int mtrlId = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x8);
                int eventFlag = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0xC);
                int unknown10 = BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x10);
                short sellQty = BitConverter.ToInt16(paramStruct.ParamBytes, offset + 0x14);
                byte shopType = paramStruct.ParamBytes[offset + 0x16];
                byte equipType = paramStruct.ParamBytes[offset + 0x17];

                // Full hex dump
                string hex = BitConverter.ToString(paramStruct.ParamBytes, offset, (int)ShopLineupParam.Size).Replace("-", " ");

                sb.AppendLine($"  Row {rowId}: {hex}");
                sb.AppendLine($"    equipId={equipId} value={value} mtrlId={mtrlId} eventFlag={eventFlag}");
                sb.AppendLine($"    UNK_10={unknown10} (0x{unknown10:X8})  sellQty={sellQty} shopType={shopType} equipType={equipType}");

                // Dump +0x18..+0x1F individually
                var tail = new byte[8];
                Array.Copy(paramStruct.ParamBytes, offset + 0x18, tail, 0, 8);
                int tail_s32_0 = BitConverter.ToInt32(tail, 0);
                int tail_s32_1 = BitConverter.ToInt32(tail, 4);
                sb.AppendLine($"    UNK_18=[{BitConverter.ToString(tail).Replace("-", " ")}] as s32: {tail_s32_0}, {tail_s32_1}");
            }

            // Summary: aggregate unknown field values
            sb.AppendLine();
            sb.AppendLine("=== Summary: Unique values for unknown fields across merchant rows ===");
            var unk10Values = new SortedSet<int>();
            var unk18_0Values = new SortedSet<int>();
            var unk18_4Values = new SortedSet<int>();

            foreach (var entry in paramStruct.ParamEntries.OrderBy(e => e.id))
            {
                int rowId = (int)entry.id;
                if (!merchantRanges.Any(r => rowId >= r.min && rowId <= r.max)) continue;

                int offset = (int)entry.paramOffset;
                unk10Values.Add(BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x10));
                unk18_0Values.Add(BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x18));
                unk18_4Values.Add(BitConverter.ToInt32(paramStruct.ParamBytes, offset + 0x1C));
            }

            sb.AppendLine($"  UNK_10 distinct values: [{string.Join(", ", unk10Values.Select(v => $"{v} (0x{v:X8})"))}]");
            sb.AppendLine($"  UNK_18[0..3] distinct values: [{string.Join(", ", unk18_0Values.Select(v => $"{v} (0x{v:X8})"))}]");
            sb.AppendLine($"  UNK_18[4..7] distinct values: [{string.Join(", ", unk18_4Values.Select(v => $"{v} (0x{v:X8})"))}]");

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_shopraw_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());

            Log.Logger.Warning($"Shop raw dump written to: {fullPath}");
        }

        /// <summary>
        /// Deep structural verification of the ShopLineupParam buffer in game memory.
        /// Reads the raw buffer including endTable, verifies binary search table,
        /// and compares with a vanilla (unmodified) param's endTable format.
        /// </summary>
        public static void DumpShopStructure()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP Shop Structure Verification — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();

            // ---- Read ShopLineupParam buffer from game memory ----
            ulong resCapLoc = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + ShopLineupParam.spOffset));
            int bufferSize = (int)Memory.ReadUInt(resCapLoc + 0x30);
            ulong bufferLoc = Memory.ReadULong(resCapLoc + 0x38);

            sb.AppendLine($"resCapLoc      = 0x{resCapLoc:X}");
            sb.AppendLine($"bufferSize     = 0x{bufferSize:X} ({bufferSize})");
            sb.AppendLine($"bufferLoc      = 0x{bufferLoc:X}");
            sb.AppendLine();

            // Read prologue (16 bytes before bufferLoc) + buffer + endTable + padding
            ulong readStart = bufferLoc - 0x10;
            int endTableAlignedOff = (bufferSize + 0xF) & ~0xF;

            // Parse header first to get num_entries
            byte[] headerBytes = Memory.ReadByteArray(bufferLoc, 0x30);
            int hdrStringOff = BitConverter.ToInt32(headerBytes, 0x0);
            ushort hdrParamOff = BitConverter.ToUInt16(headerBytes, 0x4);
            ushort numEntries = BitConverter.ToUInt16(headerBytes, 0xA);

            int endTableSize = 8 * numEntries;
            int totalRead = 0x10 + endTableAlignedOff + endTableSize + 0x20; // extra safety margin
            byte[] raw = Memory.ReadByteArray(readStart, totalRead);

            sb.AppendLine("--- Prologue (16 bytes before bufferLoc) ---");
            ulong prologueVal = BitConverter.ToUInt64(raw, 0);
            sb.AppendLine($"  prologue[0..7] (ulong) = 0x{prologueVal:X} ({prologueVal})");
            sb.AppendLine($"  prologue[0..3] (uint)  = 0x{BitConverter.ToUInt32(raw, 0):X}");
            sb.AppendLine($"  expected (bufferSize)  = 0x{bufferSize:X}");
            sb.AppendLine($"  match? {(prologueVal == (ulong)bufferSize ? "YES" : "NO — MISMATCH!")}");
            sb.AppendLine($"  prologue hex: {BitConverter.ToString(raw, 0, 16).Replace("-", " ")}");
            sb.AppendLine();

            sb.AppendLine("--- Header (0x30 bytes) ---");
            sb.AppendLine($"  string_offset  = 0x{hdrStringOff:X} ({hdrStringOff})");
            sb.AppendLine($"  params_offset  = 0x{hdrParamOff:X} ({hdrParamOff})");
            sb.AppendLine($"  num_entries    = {numEntries}");
            sb.AppendLine($"  header hex[0..15]: {BitConverter.ToString(raw, 0x10, 16).Replace("-", " ")}");
            sb.AppendLine();

            // ---- Entry Table ----
            sb.AppendLine($"--- Entry Table ({numEntries} entries, 12 bytes each) ---");
            int entTableStart = 0x10 + 0x30; // in raw[]
            var entries = new List<(uint id, uint paramOff, uint strOff)>();
            bool entriesSorted = true;
            uint prevEntId = 0;

            for (int i = 0; i < numEntries; i++)
            {
                int off = entTableStart + (i * 12);
                uint eid = BitConverter.ToUInt32(raw, off);
                uint epOff = BitConverter.ToUInt32(raw, off + 4);
                uint esOff = BitConverter.ToUInt32(raw, off + 8);
                entries.Add((eid, epOff, esOff));

                if (i > 0 && eid < prevEntId) entriesSorted = false;
                prevEntId = eid;
            }

            sb.AppendLine($"  Sorted by ID? {(entriesSorted ? "YES" : "NO — UNSORTED!")}");
            sb.AppendLine($"  First 5 entries:");
            for (int i = 0; i < Math.Min(5, entries.Count); i++)
                sb.AppendLine($"    [{i}] id={entries[i].id} paramOff=0x{entries[i].paramOff:X} strOff=0x{entries[i].strOff:X}");
            sb.AppendLine($"  Last 5 entries:");
            for (int i = Math.Max(0, entries.Count - 5); i < entries.Count; i++)
                sb.AppendLine($"    [{i}] id={entries[i].id} paramOff=0x{entries[i].paramOff:X} strOff=0x{entries[i].strOff:X}");
            sb.AppendLine();

            // ---- EndTable (binary search table) ----
            int etStart = 0x10 + endTableAlignedOff; // in raw[]
            sb.AppendLine($"--- EndTable (binary search table) ---");
            sb.AppendLine($"  endTableAlignedOff = 0x{endTableAlignedOff:X} (from bufferLoc)");
            sb.AppendLine($"  bufferSize         = 0x{bufferSize:X}");
            sb.AppendLine($"  alignment gap      = {endTableAlignedOff - bufferSize} bytes");
            sb.AppendLine($"  raw offset in dump = 0x{etStart:X}");
            sb.AppendLine($"  endTable size      = {endTableSize} bytes ({numEntries} entries × 8)");
            sb.AppendLine();

            // Dump bytes between strings end and endTable (the alignment gap)
            int gapStart = 0x10 + bufferSize;
            int gapSize = endTableAlignedOff - bufferSize;
            if (gapSize > 0)
            {
                sb.AppendLine($"  Alignment gap bytes ({gapSize} bytes at raw offset 0x{gapStart:X}):");
                sb.AppendLine($"    {BitConverter.ToString(raw, gapStart, gapSize).Replace("-", " ")}");
                sb.AppendLine();
            }

            var endTableEntries = new List<(uint id, uint val)>();
            bool etSorted = true;
            uint prevEtId = 0;
            bool allSequentialIndices = true;

            for (int i = 0; i < numEntries; i++)
            {
                int off = etStart + (i * 8);
                uint etId = BitConverter.ToUInt32(raw, off);
                uint etVal = BitConverter.ToUInt32(raw, off + 4);
                endTableEntries.Add((etId, etVal));

                if (i > 0 && etId < prevEtId) etSorted = false;
                prevEtId = etId;

                if (etVal != (uint)i) allSequentialIndices = false;
            }

            sb.AppendLine($"  Sorted by ID? {(etSorted ? "YES" : "NO — UNSORTED!")}");
            sb.AppendLine($"  Second field is sequential index (0,1,2,...)? {(allSequentialIndices ? "YES" : "NO")}");
            sb.AppendLine();

            sb.AppendLine($"  First 10 endTable entries:");
            for (int i = 0; i < Math.Min(10, endTableEntries.Count); i++)
                sb.AppendLine($"    [{i}] id={endTableEntries[i].id,-12} val={endTableEntries[i].val,-8} (0x{endTableEntries[i].val:X})");
            sb.AppendLine($"  Last 10 endTable entries:");
            for (int i = Math.Max(0, endTableEntries.Count - 10); i < endTableEntries.Count; i++)
                sb.AppendLine($"    [{i}] id={endTableEntries[i].id,-12} val={endTableEntries[i].val,-8} (0x{endTableEntries[i].val:X})");
            sb.AppendLine();

            // ---- Cross-reference: endTable vs entry table ----
            sb.AppendLine("--- EndTable ↔ Entry Table Cross-Reference ---");
            int crossMismatches = 0;
            foreach (var (etId, etVal) in endTableEntries)
            {
                if (etVal >= (uint)entries.Count)
                {
                    sb.AppendLine($"  ERROR: endTable id={etId} → index={etVal} but only {entries.Count} entries!");
                    crossMismatches++;
                    continue;
                }
                var targetEntry = entries[(int)etVal];
                if (targetEntry.id != etId)
                {
                    sb.AppendLine($"  MISMATCH: endTable id={etId} → index={etVal} → entry id={targetEntry.id}");
                    crossMismatches++;
                }
            }
            sb.AppendLine($"  Cross-reference mismatches: {crossMismatches}");
            sb.AppendLine();

            // ---- Binary search simulation for merchant rows ----
            sb.AppendLine("--- Binary Search Simulation (merchant rows) ---");
            var merchantRows = new int[] { 1100, 1101, 1102, 1103, 1104, 1105, 1106, 1107, 1108, 1110,
                1111, 1112, 1113, 1114, 1115, 1116, 1117, 1118, 1119, 1120,
                1121, 1122, 1123, 1124, 1125, 1126, 1127, 1128, 1129, 1130,
                1131, 1132, 1133, 1134, 1200, 1201, 1202, 1203, 1204, 1205,
                1206, 1207, 1208, 1209, 1210, 1211, 1212, 1213, 1214, 1215,
                1216, 1217, 1218, 1219, 1220 };

            foreach (int searchId in merchantRows)
            {
                // Simulate binary search on endTable
                int lo = 0, hi = endTableEntries.Count - 1;
                int foundIdx = -1;
                int steps = 0;
                while (lo <= hi)
                {
                    steps++;
                    int mid = (lo + hi) / 2;
                    uint midId = endTableEntries[mid].id;
                    if (midId == (uint)searchId) { foundIdx = mid; break; }
                    else if (midId < (uint)searchId) lo = mid + 1;
                    else hi = mid - 1;
                }

                if (foundIdx < 0)
                {
                    sb.AppendLine($"  Row {searchId}: NOT FOUND in endTable!");
                    continue;
                }

                uint resolvedIndex = endTableEntries[foundIdx].val;
                if (resolvedIndex >= entries.Count)
                {
                    sb.AppendLine($"  Row {searchId}: endTable→index {resolvedIndex} OUT OF BOUNDS (max={entries.Count - 1})");
                    continue;
                }

                var entry = entries[(int)resolvedIndex];
                // Read param data at this entry's offset
                int paramDataOff = 0x10 + (int)entry.paramOff;
                int equipId = BitConverter.ToInt32(raw, paramDataOff + 0x0);
                int eventFlag = BitConverter.ToInt32(raw, paramDataOff + 0xC);
                short sellQty = BitConverter.ToInt16(raw, paramDataOff + 0x14);
                byte equipType = raw[paramDataOff + 0x17];

                // Check event flag state
                string flagState = "n/a";
                if (eventFlag > 0)
                {
                    var (flagOff, flagBit) = AddressHelper.GetEventFlagOffset(eventFlag);
                    ulong flagAddr = AddressHelper.GetEventFlagsOffset() + flagOff;
                    byte flagByte = Memory.ReadByte(flagAddr);
                    bool isSet = (flagByte & (1 << flagBit)) != 0;
                    flagState = $"byte=0x{flagByte:X2} bit{flagBit}={( isSet ? "SET" : "---")}";
                }

                sb.AppendLine($"  Row {searchId}: bsearch→et[{foundIdx}]→ent[{resolvedIndex}] (id={entry.id}) " +
                              $"equipId={equipId} eqType={equipType} flag={eventFlag} sellQty={sellQty} " +
                              $"flagState=[{flagState}] steps={steps}");
            }
            sb.AppendLine();

            // ---- Compare with vanilla param endTable format ----
            sb.AppendLine("--- Vanilla Param EndTable Comparison (MoveParam @ 0x5B8) ---");
            try
            {
                ulong vanResCapLoc = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + 0x5B8));
                int vanBufSize = (int)Memory.ReadUInt(vanResCapLoc + 0x30);
                ulong vanBufLoc = Memory.ReadULong(vanResCapLoc + 0x38);

                byte[] vanHeader = Memory.ReadByteArray(vanBufLoc, 0x30);
                ushort vanNumEntries = BitConverter.ToUInt16(vanHeader, 0xA);

                int vanEtAlignedOff = (vanBufSize + 0xF) & ~0xF;
                int vanEtSize = 8 * vanNumEntries;
                byte[] vanEtBytes = Memory.ReadByteArray(vanBufLoc + (ulong)vanEtAlignedOff, vanEtSize);

                sb.AppendLine($"  MoveParam: bufSize=0x{vanBufSize:X} bufLoc=0x{vanBufLoc:X} entries={vanNumEntries}");
                sb.AppendLine($"  endTable at bufLoc+0x{vanEtAlignedOff:X}");
                sb.AppendLine();

                bool vanAllSeqIdx = true;
                sb.AppendLine($"  First 10 endTable entries:");
                for (int i = 0; i < Math.Min(10, (int)vanNumEntries); i++)
                {
                    uint vid = BitConverter.ToUInt32(vanEtBytes, i * 8);
                    uint vval = BitConverter.ToUInt32(vanEtBytes, i * 8 + 4);
                    if (vval != (uint)i) vanAllSeqIdx = false;
                    sb.AppendLine($"    [{i}] id={vid,-12} val={vval,-8} (0x{vval:X})");
                }
                sb.AppendLine($"  Sequential indices? {(vanAllSeqIdx ? "YES (first 10)" : "NO")}");

                // Also check: is the second field maybe a data offset?
                // If so, it would be related to entry.paramOff
                byte[] vanEntBytes = Memory.ReadByteArray(vanBufLoc + 0x30, 12 * Math.Min(10, (int)vanNumEntries));
                sb.AppendLine();
                sb.AppendLine($"  Cross-reference (first 10): endTable.val vs entry.paramOff");
                for (int i = 0; i < Math.Min(10, (int)vanNumEntries); i++)
                {
                    uint vid = BitConverter.ToUInt32(vanEtBytes, i * 8);
                    uint vval = BitConverter.ToUInt32(vanEtBytes, i * 8 + 4);

                    uint eId = BitConverter.ToUInt32(vanEntBytes, i * 12);
                    uint eParamOff = BitConverter.ToUInt32(vanEntBytes, i * 12 + 4);

                    sb.AppendLine($"    [{i}] et(id={vid}, val={vval}) ↔ ent(id={eId}, paramOff=0x{eParamOff:X}) {(vid == eId ? "id✓" : "id✗")}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ERROR reading vanilla param: {ex.Message}");
            }
            sb.AppendLine();

            // ---- Event flag byte range full dump ----
            sb.AppendLine("--- Event Flag Byte Range (all shop flags) ---");
            sb.AppendLine("Full hex dump of all bytes in the event flag region used by shop flags.");
            sb.AppendLine("Run before AND after purchase, then diff to find unexpected bit changes.");
            sb.AppendLine();

            var shopFlags = LocationHelper.GetShopFlags().Where(x => x.IsEnabled).ToList();
            var baseEventAddr = AddressHelper.GetEventFlagsOffset();

            // Find min/max byte offsets
            ulong minOff = ulong.MaxValue, maxOff = 0;
            foreach (var sf in shopFlags)
            {
                var (off, _) = AddressHelper.GetEventFlagOffset(sf.PurchaseFlag);
                if (off < minOff) minOff = off;
                if (off > maxOff) maxOff = off;
            }

            // Read the full range + 4 bytes on each side
            ulong rangeStart = baseEventAddr + minOff - 4;
            int rangeLen = (int)(maxOff - minOff) + 8;
            byte[] flagBytes = Memory.ReadByteArray(rangeStart, rangeLen);

            sb.AppendLine($"  Event flags base: 0x{baseEventAddr:X}");
            sb.AppendLine($"  Range: offset 0x{minOff - 4:X} to 0x{maxOff + 4:X} ({rangeLen} bytes)");
            sb.AppendLine();

            // Hex dump in 16-byte rows
            for (int i = 0; i < rangeLen; i += 16)
            {
                int count = Math.Min(16, rangeLen - i);
                string addr = $"0x{(rangeStart - baseEventAddr + (ulong)i):X6}";
                string hex = BitConverter.ToString(flagBytes, i, count).Replace("-", " ");
                sb.AppendLine($"  {addr}: {hex}");
            }

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_shopstruct_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());

            Log.Logger.Warning($"Shop structure verification written to: {fullPath}");
        }

        /// <summary>
        /// Check whether each shop item's equipId actually resolves in the game's
        /// equip param tables. Also verifies that equip param pointers haven't been
        /// reverted by the game engine after a purchase.
        /// </summary>
        public static void DumpShopVisibility()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP Shop Visibility Check — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine("Checks whether each shop item's equipId can be resolved in the game's equip param tables.");
            sb.AppendLine();

            // ---- Check equip param pointer status ----
            sb.AppendLine("--- Equip Param Pointer Status ---");
            var paramChecks = new (string name, int spOffset, uint sentinelMinId)[]
            {
                ("EquipParamGoods",     0xF0,  11109961),
                ("EquipParamWeapon",    0x18,  11110000),
                ("EquipParamProtector", 0x60,  11110000),
                ("EquipParamAccessory", 0xA8,  11110000),
                ("ShopLineupParam",     0x720, 99999990),
            };

            var equipParamEntries = new Dictionary<string, HashSet<uint>>();

            foreach (var (name, spOff, sentinelMin) in paramChecks)
            {
                ulong resCapLoc = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + spOff));
                int bufSize = (int)Memory.ReadUInt(resCapLoc + 0x30);
                ulong bufLoc = Memory.ReadULong(resCapLoc + 0x38);

                // Read header to get entry count
                byte[] header = Memory.ReadByteArray(bufLoc, 0x30);
                ushort numEntries = BitConverter.ToUInt16(header, 0xA);
                ushort paramsOff = BitConverter.ToUInt16(header, 0x4);

                // Read all entry IDs
                byte[] entBytes = Memory.ReadByteArray(bufLoc + 0x30, 12 * numEntries);
                var ids = new HashSet<uint>();
                uint maxId = 0;
                for (int i = 0; i < numEntries; i++)
                {
                    uint eid = BitConverter.ToUInt32(entBytes, i * 12);
                    ids.Add(eid);
                    if (eid > maxId) maxId = eid;
                }

                bool hasOurEntries = maxId >= sentinelMin;
                sb.AppendLine($"  {name}: resCap=0x{resCapLoc:X} buf=0x{bufLoc:X} size=0x{bufSize:X} entries={numEntries} maxId={maxId} modified={hasOurEntries}");
                equipParamEntries[name] = ids;
            }
            sb.AppendLine();

            // ---- Build lookup: which equipIds exist in each param table ----
            var goodsIds = equipParamEntries.GetValueOrDefault("EquipParamGoods", new HashSet<uint>());
            var weaponIds = equipParamEntries.GetValueOrDefault("EquipParamWeapon", new HashSet<uint>());
            var protectorIds = equipParamEntries.GetValueOrDefault("EquipParamProtector", new HashSet<uint>());
            var accessoryIds = equipParamEntries.GetValueOrDefault("EquipParamAccessory", new HashSet<uint>());

            // ---- Read ShopLineupParam and check each merchant row ----
            ulong shopResCap = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + ShopLineupParam.spOffset));
            int shopBufSize = (int)Memory.ReadUInt(shopResCap + 0x30);
            ulong shopBufLoc = Memory.ReadULong(shopResCap + 0x38);

            byte[] shopHeader = Memory.ReadByteArray(shopBufLoc, 0x30);
            ushort shopNumEntries = BitConverter.ToUInt16(shopHeader, 0xA);
            ushort shopParamsOff = BitConverter.ToUInt16(shopHeader, 0x4);
            int shopStringOff = BitConverter.ToInt32(shopHeader, 0x0);

            // Read full entry table + param data
            int shopEntTableSize = 12 * shopNumEntries;
            byte[] shopEntBytes = Memory.ReadByteArray(shopBufLoc + 0x30, shopEntTableSize);
            byte[] shopParamBytes = Memory.ReadByteArray(shopBufLoc + (ulong)shopParamsOff,
                                                          shopStringOff - shopParamsOff);

            var baseEventAddr = AddressHelper.GetEventFlagsOffset();
            var shopFlags = LocationHelper.GetShopFlags().Where(x => x.IsEnabled).ToDictionary(x => x.Row, x => x);

            var merchantRanges = new (int min, int max, string name)[]
            {
                (1100, 1134, "Undead Merchant Male"),
                (1200, 1220, "Female Merchant"),
                (1400, 1420, "Andre"),
                (2400, 2401, "Ingward"),
                (4401, 4408, "Oswald"),
            };

            sb.AppendLine("--- Shop Item Resolution (per merchant) ---");
            sb.AppendLine("For each row: can the equipId be found in the corresponding equip param table?");
            sb.AppendLine("equipType: 0=Weapon, 1=Protector, 2=Accessory, 3=Goods");
            sb.AppendLine();

            foreach (var (rangeMin, rangeMax, merchantName) in merchantRanges)
            {
                sb.AppendLine($"=== {merchantName} (rows {rangeMin}-{rangeMax}) ===");
                int visibleCount = 0;
                int hiddenByFlag = 0;
                int unresolvable = 0;

                for (int i = 0; i < shopNumEntries; i++)
                {
                    uint rowId = BitConverter.ToUInt32(shopEntBytes, i * 12);
                    if (rowId < (uint)rangeMin || rowId > (uint)rangeMax) continue;

                    uint paramOff = BitConverter.ToUInt32(shopEntBytes, i * 12 + 4);
                    int localOff = (int)(paramOff - shopParamsOff);

                    int equipId = BitConverter.ToInt32(shopParamBytes, localOff + 0x0);
                    int value = BitConverter.ToInt32(shopParamBytes, localOff + 0x4);
                    int eventFlag = BitConverter.ToInt32(shopParamBytes, localOff + 0xC);
                    short sellQty = BitConverter.ToInt16(shopParamBytes, localOff + 0x14);
                    byte shopType = shopParamBytes[localOff + 0x16];
                    byte equipType = shopParamBytes[localOff + 0x17];

                    // Check event flag
                    bool flagSet = false;
                    if (eventFlag > 0)
                    {
                        var (flagOff, flagBit) = AddressHelper.GetEventFlagOffset(eventFlag);
                        byte flagByte = Memory.ReadByte(baseEventAddr + flagOff);
                        flagSet = (flagByte & (1 << flagBit)) != 0;
                    }

                    // Check if equipId resolves in the corresponding table
                    bool resolves = false;
                    string tableName = "";
                    switch (equipType)
                    {
                        case 0: resolves = weaponIds.Contains((uint)equipId); tableName = "Weapon"; break;
                        case 1: resolves = protectorIds.Contains((uint)equipId); tableName = "Protector"; break;
                        case 2: resolves = accessoryIds.Contains((uint)equipId); tableName = "Accessory"; break;
                        case 3: resolves = goodsIds.Contains((uint)equipId); tableName = "Goods"; break;
                        default: tableName = $"UNKNOWN({equipType})"; break;
                    }

                    string status;
                    if (flagSet) { status = "SOLD"; hiddenByFlag++; }
                    else if (!resolves) { status = "UNRESOLVABLE"; unresolvable++; }
                    else { status = "VISIBLE"; visibleCount++; }

                    string flagName = shopFlags.TryGetValue((int)rowId, out var sf) ? sf.Name : "?";

                    sb.AppendLine($"  Row {rowId}: [{status,-13}] equipId={equipId,-10} eqType={equipType}({tableName,-9}) " +
                                  $"flag={eventFlag} flagSet={flagSet} resolves={resolves} sellQty={sellQty} | {flagName}");
                }

                sb.AppendLine($"  SUMMARY: visible={visibleCount} sold={hiddenByFlag} unresolvable={unresolvable} total={visibleCount + hiddenByFlag + unresolvable}");
                sb.AppendLine();
            }

            // ---- Spot-check some AP stub IDs in EquipParamGoods ----
            sb.AppendLine("--- AP Stub Spot-Check (EquipParamGoods) ---");
            var spotCheckIds = new uint[] { 11110900, 11110901, 11110902, 11110903, 11110904,
                                            11110042, 11110823,
                                            11112900, 11112901, 11112902, 11112903, 11112904 };
            foreach (uint sid in spotCheckIds)
            {
                bool found = goodsIds.Contains(sid);
                sb.AppendLine($"  EquipParamGoods[{sid}]: {(found ? "FOUND" : "MISSING")}");
            }
            sb.AppendLine();

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_shopvis_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());

            Log.Logger.Warning($"Shop visibility check written to: {fullPath}");
        }

        /// <summary>
        /// Compare vanilla (original) ShopLineupParam rows with our modified ones.
        /// Reads the original buffer via DescArea.OldAddress and the current buffer
        /// via the active pointer, then dumps both side-by-side for merchant rows.
        /// </summary>
        public static void DumpVanillaComparison()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DSAP Vanilla vs Modified Shop Comparison — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();

            // Read current (modified) param
            ulong resCapLoc = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + ShopLineupParam.spOffset));
            int modBufSize = (int)Memory.ReadUInt(resCapLoc + 0x30);
            ulong modBufLoc = Memory.ReadULong(resCapLoc + 0x38);

            // Parse modified header
            byte[] modHeader = Memory.ReadByteArray(modBufLoc, 0x30);
            ushort modNumEntries = BitConverter.ToUInt16(modHeader, 0xA);
            ushort modParamsOff = BitConverter.ToUInt16(modHeader, 0x4);
            int modStringOff = BitConverter.ToInt32(modHeader, 0x0);

            // Read modified entries and params
            byte[] modEntBytes = Memory.ReadByteArray(modBufLoc + 0x30, 12 * modNumEntries);
            byte[] modParamBytes = Memory.ReadByteArray(modBufLoc + (ulong)modParamsOff, modStringOff - modParamsOff);

            // Build modified entry lookup
            var modEntries = new Dictionary<uint, (uint paramOff, int localOff)>();
            for (int i = 0; i < modNumEntries; i++)
            {
                uint eid = BitConverter.ToUInt32(modEntBytes, i * 12);
                uint epOff = BitConverter.ToUInt32(modEntBytes, i * 12 + 4);
                modEntries[eid] = (epOff, (int)(epOff - modParamsOff));
            }

            // Read vanilla param via DescArea
            // DescArea location: bufferLoc + bufferSize + 8*numEntries + 0xF
            int descOffset = modBufSize + 8 * modNumEntries + 0xF;
            ulong descLoc = modBufLoc + (ulong)descOffset;
            var descArea = Memory.ReadObject<DescArea>(descLoc);

            sb.AppendLine($"Modified: buf=0x{modBufLoc:X} size=0x{modBufSize:X} entries={modNumEntries}");
            sb.AppendLine($"DescArea: OldAddress=0x{descArea.OldAddress:X} OldLength=0x{descArea.OldLength:X}");
            sb.AppendLine();

            // Read vanilla header, entries, params
            byte[] vanHeader = Memory.ReadByteArray(descArea.OldAddress, 0x30);
            ushort vanNumEntries = BitConverter.ToUInt16(vanHeader, 0xA);
            ushort vanParamsOff = BitConverter.ToUInt16(vanHeader, 0x4);
            int vanStringOff = BitConverter.ToInt32(vanHeader, 0x0);

            sb.AppendLine($"Vanilla: entries={vanNumEntries} paramsOff=0x{vanParamsOff:X} stringOff=0x{vanStringOff:X}");
            sb.AppendLine($"Modified: entries={modNumEntries} paramsOff=0x{modParamsOff:X} stringOff=0x{modStringOff:X}");
            sb.AppendLine();

            byte[] vanEntBytes = Memory.ReadByteArray(descArea.OldAddress + 0x30, 12 * vanNumEntries);
            byte[] vanParamBytes = Memory.ReadByteArray(descArea.OldAddress + (ulong)vanParamsOff, vanStringOff - vanParamsOff);

            // Build vanilla entry lookup
            var vanEntries = new Dictionary<uint, (uint paramOff, int localOff)>();
            for (int i = 0; i < vanNumEntries; i++)
            {
                uint eid = BitConverter.ToUInt32(vanEntBytes, i * 12);
                uint epOff = BitConverter.ToUInt32(vanEntBytes, i * 12 + 4);
                vanEntries[eid] = (epOff, (int)(epOff - vanParamsOff));
            }

            sb.AppendLine($"Vanilla-only rows (not in modified): {vanEntries.Keys.Except(modEntries.Keys).Count()}");
            sb.AppendLine($"Modified-only rows (not in vanilla): {modEntries.Keys.Except(vanEntries.Keys).Count()}");
            sb.AppendLine();

            // For each merchant row, dump vanilla vs modified side-by-side
            var merchantRanges = new (int min, int max, string name)[]
            {
                (1100, 1134, "Undead Merchant Male"),
                (1200, 1220, "Female Merchant"),
                (1400, 1420, "Andre"),
            };

            foreach (var (rMin, rMax, mName) in merchantRanges)
            {
                sb.AppendLine($"=== {mName} (rows {rMin}-{rMax}) ===");
                sb.AppendLine($"{"Row",-6} | {"Field",-12} | {"Vanilla",-20} | {"Modified",-20} | {"Match"}");
                sb.AppendLine(new string('-', 90));

                for (uint row = (uint)rMin; row <= (uint)rMax; row++)
                {
                    if (!vanEntries.TryGetValue(row, out var vanE)) continue;

                    bool hasMod = modEntries.TryGetValue(row, out var modE);
                    if (!hasMod) { sb.AppendLine($"{row,-6} | NOT IN MODIFIED PARAM"); continue; }

                    // Read all 0x20 bytes from both
                    int vOff = vanE.localOff;
                    int mOff = modE.localOff;

                    int vEquipId = BitConverter.ToInt32(vanParamBytes, vOff + 0x0);
                    int mEquipId = BitConverter.ToInt32(modParamBytes, mOff + 0x0);
                    int vValue = BitConverter.ToInt32(vanParamBytes, vOff + 0x4);
                    int mValue = BitConverter.ToInt32(modParamBytes, mOff + 0x4);
                    int vMtrlId = BitConverter.ToInt32(vanParamBytes, vOff + 0x8);
                    int mMtrlId = BitConverter.ToInt32(modParamBytes, mOff + 0x8);
                    int vEventFlag = BitConverter.ToInt32(vanParamBytes, vOff + 0xC);
                    int mEventFlag = BitConverter.ToInt32(modParamBytes, mOff + 0xC);
                    int vQwcId = BitConverter.ToInt32(vanParamBytes, vOff + 0x10);
                    int mQwcId = BitConverter.ToInt32(modParamBytes, mOff + 0x10);
                    short vSellQty = BitConverter.ToInt16(vanParamBytes, vOff + 0x14);
                    short mSellQty = BitConverter.ToInt16(modParamBytes, mOff + 0x14);
                    byte vShopType = vanParamBytes[vOff + 0x16];
                    byte mShopType = modParamBytes[mOff + 0x16];
                    byte vEquipType = vanParamBytes[vOff + 0x17];
                    byte mEquipType = modParamBytes[mOff + 0x17];

                    // Dump +0x18..+0x1F
                    string vTail = BitConverter.ToString(vanParamBytes, vOff + 0x18, 8).Replace("-", " ");
                    string mTail = BitConverter.ToString(modParamBytes, mOff + 0x18, 8).Replace("-", " ");

                    string eq(object a, object b) => a.Equals(b) ? "=" : "DIFF";

                    sb.AppendLine($"{row,-6} | {"equipId",-12} | {vEquipId,-20} | {mEquipId,-20} | {eq(vEquipId, mEquipId)}");
                    sb.AppendLine($"{"",6} | {"value",-12} | {vValue,-20} | {mValue,-20} | {eq(vValue, mValue)}");
                    sb.AppendLine($"{"",6} | {"mtrlId",-12} | {vMtrlId,-20} | {mMtrlId,-20} | {eq(vMtrlId, mMtrlId)}");
                    sb.AppendLine($"{"",6} | {"eventFlag",-12} | {vEventFlag,-20} | {mEventFlag,-20} | {eq(vEventFlag, mEventFlag)}");
                    sb.AppendLine($"{"",6} | {"qwcId",-12} | {vQwcId,-20} | {mQwcId,-20} | {eq(vQwcId, mQwcId)}");
                    sb.AppendLine($"{"",6} | {"sellQty",-12} | {vSellQty,-20} | {mSellQty,-20} | {eq(vSellQty, mSellQty)}");
                    sb.AppendLine($"{"",6} | {"shopType",-12} | {vShopType,-20} | {mShopType,-20} | {eq(vShopType, mShopType)}");
                    sb.AppendLine($"{"",6} | {"equipType",-12} | {vEquipType,-20} | {mEquipType,-20} | {eq(vEquipType, mEquipType)}");
                    sb.AppendLine($"{"",6} | {"tail(+18)",-12} | {vTail,-20} | {mTail,-20} | {eq(vTail, mTail)}");

                    // Full hex dump
                    string vHex = BitConverter.ToString(vanParamBytes, vOff, 0x20).Replace("-", " ");
                    string mHex = BitConverter.ToString(modParamBytes, mOff, 0x20).Replace("-", " ");
                    sb.AppendLine($"{"",6} | VAN hex: {vHex}");
                    sb.AppendLine($"{"",6} | MOD hex: {mHex}");
                    sb.AppendLine();
                }
            }

            // Write to file
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSAP");
            Directory.CreateDirectory(dumpDir);
            string filename = $"dsap_vanilla_compare_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string fullPath = Path.Combine(dumpDir, filename);
            File.WriteAllText(fullPath, sb.ToString());

            Log.Logger.Warning($"Vanilla comparison written to: {fullPath}");
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
