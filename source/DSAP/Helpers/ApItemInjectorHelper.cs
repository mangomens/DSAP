using Archipelago.Core.Util;
using Archipelago.MultiClient.Net.Models;
using DSAP.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DSAP.Helpers
{
    public class ApItemInjectorHelper
    {
        public const int AP_ITEM_OFFSET = 10000; // add to loc id for ap items
        private static readonly object _memAllocLock = new object();
        internal static async Task AddAPItems(Dictionary<long, ScoutedItemInfo> scoutedLocationInfo)
        {
            // Build set of shop location IDs so we can partition stubs for shop-specific param tables
            var shopLocIds = new HashSet<long>(
                LocationHelper.GetShopFlags()
                              .Where(x => x.IsEnabled)
                              .Select(x => (long)x.Id));

            // All scouted entries — used for Goods stubs and Goods FMG (unchanged path)
            List<KeyValuePair<long, ScoutedItemInfo>> addedEntries = scoutedLocationInfo.ToList();

            var added_names = addedEntries.Select(x => new KeyValuePair<long, string>(x.Key, $"{x.Value.Player}'s {x.Value.ItemDisplayName}\0")).ToList();
            var added_captions = addedEntries.Select(x => new KeyValuePair<long, string>(x.Key, BuildItemCaption(x))).ToList();

            var added_emk_names = MiscHelper.GetDsrEventItems().Select(x => new KeyValuePair<long, string>(x.Id, $"{x.Name}\0"));
            var added_emk_captions = MiscHelper.GetDsrEventItems().Select(x => new KeyValuePair<long, string>(x.Id, BuildDsrEventItemCaption()));

            added_names.AddRange(added_emk_names);
            added_captions.AddRange(added_emk_captions);

            added_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            added_captions.Sort((a, b) => a.Key.CompareTo(b.Key));

            var watch = System.Diagnostics.Stopwatch.StartNew();

            // --- Goods stubs + Goods FMG (all items, unchanged) ---
            bool do_replacements = upgradeGoods(added_names, scoutedLocationInfo);

             // Partition FMG text lists: spells use a separate MsgMan FMG (+0x3B8 names, +0x350 descriptions).
            // DSR looks up spell-type goods in the spell FMG, not the goods FMG, so we must write to both.
            var spell_names    = new List<KeyValuePair<long, string>>();
            var spell_descs    = new List<KeyValuePair<long, string>>();
            var goods_names    = new List<KeyValuePair<long, string>>();
            var goods_captions = new List<KeyValuePair<long, string>>();

            ulong msgManPtrGoods = Memory.ReadULong(0x141c7e3e8);
            ulong goodsCaptionFmgStart = Memory.ReadULong(msgManPtrGoods + (ulong)MsgManStruct.OFFSET_ITEM_CAPTIONS);
            ulong spellDescFmgStart    = Memory.ReadULong(msgManPtrGoods + (ulong)MsgManStruct.OFFSET_SPELL_DESCRIPTIONS);

            for (int i = 0; i < added_names.Count; i++)
            {
                var nameEntry    = added_names[i];
                var captionEntry = added_captions[i];

                // Check if this is a spell item
                bool isSpell = scoutedLocationInfo.TryGetValue(nameEntry.Key, out var si) &&
                               App.AllItemsByApId.TryGetValue((int)si.ItemId, out var dsrItemCheck) &&
                               dsrItemCheck.SpellCategory != Enums.SpellCategory.None;

                if (isSpell)
                {
                    // For spells: look up vanilla description from the spell description FMG
                    string spellDesc = captionEntry.Value;
                    if (si != null && App.AllItemsByApId.TryGetValue((int)si.ItemId, out var spellItem))
                    {
                        ulong descLoc = FindMsg(spellDescFmgStart, (uint)spellItem.Id);
                        if (descLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(descLoc, 1024);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes).Split('\0')[0];
                            if (!string.IsNullOrWhiteSpace(vanillaStr))
                                spellDesc = vanillaStr + "\0";
                        }
                    }
                    spell_names.Add(nameEntry);
                    spell_descs.Add(new KeyValuePair<long, string>(captionEntry.Key, spellDesc));
                }
                else
                {
                    // For normal goods: replace caption with vanilla goods caption if available
                    string caption = captionEntry.Value;
                    if (si != null && App.AllItemsByApId.TryGetValue((int)si.ItemId, out var goodsItem))
                    {
                        ulong captionLoc = FindMsg(goodsCaptionFmgStart, (uint)goodsItem.Id);
                        if (captionLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(captionLoc, 512);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes).Split('\0')[0];
                            if (!string.IsNullOrWhiteSpace(vanillaStr))
                                caption = vanillaStr + "\0";
                        }
                    }
                    goods_names.Add(nameEntry);
                    goods_captions.Add(new KeyValuePair<long, string>(captionEntry.Key, caption));
                }
            }

            // Write text to Goods FMGs (non-spell items only)
            AddMsgs(MsgManStruct.OFFSET_ITEM_NAMES,         goods_names,    "Item Names");
            AddMsgs(MsgManStruct.OFFSET_ITEM_CAPTIONS,      goods_captions, "Item Captions");
            AddMsgs(MsgManStruct.OFFSET_ITEM_DESCRIPTIONS,  goods_captions, "Item Descriptions");

            // Write text to Spell FMGs (spell items only — DSR reads spell names/descriptions from here)
            if (spell_names.Count > 0)
            {
                AddMsgs(MsgManStruct.OFFSET_SPELL_NAMES,        spell_names, "Spell Names");
                AddMsgs(MsgManStruct.OFFSET_SPELL_DESCRIPTIONS, spell_descs, "Spell Descriptions");
            }

            // --- Per-type stubs for shop items only ---
            // Partition shop entries by the equipType of the scouted item so the stub lands in the
            // correct param table, which DSR uses to determine shop tab and icon.
            var weaponShopEntries    = new List<KeyValuePair<long, string>>();
            //var weaponShopCaptions  = new List<KeyValuePair<long, string>>(); // uncomment when weapon FMG offsets are known
            var protectorShopEntries = new List<KeyValuePair<long, string>>();
            //var protectorShopCaptions = new List<KeyValuePair<long, string>>(); // uncomment when protector FMG offsets are known
            var accessoryShopEntries = new List<KeyValuePair<long, string>>();
            var accessoryShopCaptions = new List<KeyValuePair<long, string>>();
            var accessoryRealEquipIds = new Dictionary<long, int>(); // locId → real DSR equip ID for icon + vanilla description lookup

            foreach (var (locId, scoutedInfo) in scoutedLocationInfo)
            {
                if (!shopLocIds.Contains(locId)) continue;

                int equipType = 3; // default goods
                if (App.AllItemsByApId.TryGetValue((int)scoutedInfo.ItemId, out var dsrItem))
                    equipType = ShopHelper.GetEquipType(dsrItem.Category);

                string name    = $"{scoutedInfo.Player}'s {scoutedInfo.ItemDisplayName}\0";
                string caption = BuildItemCaption(new KeyValuePair<long, ScoutedItemInfo>(locId, scoutedInfo));

                switch (equipType)
                {
                    case 0:
                        weaponShopEntries.Add(new KeyValuePair<long, string>(locId, name));
                        //weaponShopCaptions.Add(new KeyValuePair<long, string>(locId, caption)); // uncomment when weapon FMG offsets are known
                        break;
                    case 1:
                        protectorShopEntries.Add(new KeyValuePair<long, string>(locId, name));
                        //protectorShopCaptions.Add(new KeyValuePair<long, string>(locId, caption)); // uncomment when protector FMG offsets are known
                        break;
                    case 2:
                        accessoryShopEntries.Add(new KeyValuePair<long, string>(locId, name));
                        accessoryShopCaptions.Add(new KeyValuePair<long, string>(locId, caption));
                        if (dsrItem != null) accessoryRealEquipIds[locId] = dsrItem.Id;
                        break;
                    // case 3: goods — already handled above
                }
            }

            weaponShopEntries.Sort((a, b) => a.Key.CompareTo(b.Key));
            //weaponShopCaptions.Sort((a, b) => a.Key.CompareTo(b.Key)); // uncomment when weapon FMG offsets are known
            protectorShopEntries.Sort((a, b) => a.Key.CompareTo(b.Key));
            //protectorShopCaptions.Sort((a, b) => a.Key.CompareTo(b.Key)); // uncomment when protector FMG offsets are known
            accessoryShopEntries.Sort((a, b) => a.Key.CompareTo(b.Key));
            accessoryShopCaptions.Sort((a, b) => a.Key.CompareTo(b.Key));

            if (weaponShopEntries.Count > 0)
            {
                upgradeWeapons(weaponShopEntries);
                // TODO: add weapon FMG offset constants to MsgManStruct and uncomment:
                //AddMsgs(MsgManStruct.OFFSET_WEAPON_NAMES,        weaponShopEntries,    "Weapon Names");
                //AddMsgs(MsgManStruct.OFFSET_WEAPON_CAPTIONS,     weaponShopCaptions,   "Weapon Captions");
                //AddMsgs(MsgManStruct.OFFSET_WEAPON_DESCRIPTIONS, weaponShopCaptions,   "Weapon Descriptions");
            }
            if (protectorShopEntries.Count > 0)
            {
                upgradeProtectors(protectorShopEntries);
                // TODO: add protector FMG offset constants to MsgManStruct and uncomment:
                //AddMsgs(MsgManStruct.OFFSET_ARMOR_NAMES,        protectorShopEntries,   "Armor Names");
                //AddMsgs(MsgManStruct.OFFSET_ARMOR_CAPTIONS,     protectorShopCaptions,  "Armor Captions");
                //AddMsgs(MsgManStruct.OFFSET_ARMOR_DESCRIPTIONS, protectorShopCaptions,  "Armor Descriptions");
            }
            if (accessoryShopEntries.Count > 0)
            {
                upgradeAccessories(accessoryShopEntries, accessoryRealEquipIds);

                // Build ring captions and descriptions: use vanilla text for known DSR rings, AP caption otherwise.
                // The shop info panel shows CAPTIONS (short one-liner). DESCRIPTIONS is shown in the inventory screen.
                ulong msgManPtr = Memory.ReadULong(0x141c7e3e8);
                ulong ringCaptionFmgStart = Memory.ReadULong(msgManPtr + (ulong)MsgManStruct.OFFSET_RING_CAPTIONS);
                ulong ringDescFmgStart    = Memory.ReadULong(msgManPtr + (ulong)MsgManStruct.OFFSET_RING_DESCRIPTIONS);
                var accessoryCaptionsWithVanilla = new List<KeyValuePair<long, string>>();
                var accessoryShopDescriptions    = new List<KeyValuePair<long, string>>();
                foreach (var entry in accessoryShopCaptions)
                {
                    string caption = entry.Value; // AP text fallback
                    string desc    = entry.Value;
                    if (accessoryRealEquipIds.TryGetValue(entry.Key, out int realEquipId))
                    {
                        ulong captionLoc = FindMsg(ringCaptionFmgStart, (uint)realEquipId);
                        if (captionLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(captionLoc, 512);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes);
                            caption = vanillaStr.Split('\0')[0] + "\0";
                        }

                        ulong descLoc = FindMsg(ringDescFmgStart, (uint)realEquipId);
                        if (descLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(descLoc, 1024);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes);
                            desc = vanillaStr.Split('\0')[0] + "\0";
                        }
                    }
                    accessoryCaptionsWithVanilla.Add(new KeyValuePair<long, string>(entry.Key, caption));
                    accessoryShopDescriptions.Add(new KeyValuePair<long, string>(entry.Key, desc));
                }
                accessoryCaptionsWithVanilla.Sort((a, b) => a.Key.CompareTo(b.Key));
                accessoryShopDescriptions.Sort((a, b) => a.Key.CompareTo(b.Key));

                // Ring FMGs are accessible — write names, captions, and descriptions
                AddMsgs(MsgManStruct.OFFSET_RING_NAMES,        accessoryShopEntries,         "Ring Names");
                AddMsgs(MsgManStruct.OFFSET_RING_CAPTIONS,     accessoryCaptionsWithVanilla, "Ring Captions");
                AddMsgs(MsgManStruct.OFFSET_RING_DESCRIPTIONS, accessoryShopDescriptions,    "Ring Descriptions");
            }

            Log.Logger.Information($"Added param stubs: {added_names.Count} goods, {weaponShopEntries.Count} weapons, {protectorShopEntries.Count} protectors, {accessoryShopEntries.Count} accessories");

            watch.Stop();
            Log.Logger.Information($"Finished adding new items params + msg text, took {watch.ElapsedMilliseconds}ms");
            App.Client.AddOverlayMessage($"Finished adding new items params + msg text, took {watch.ElapsedMilliseconds}ms");

            var local_ap_keys = added_emk_names.ToList();
            local_ap_keys.Sort((a, b) => a.Key.CompareTo(b.Key));
            // add item removal hook for all "location" items AND all ap items
            AddAPItemHook(scoutedLocationInfo.Min(x => x.Key), scoutedLocationInfo.Max(x => x.Key));
            // add item popup removal hook for all "location" items
            AddAPItemPopupHook(scoutedLocationInfo.Min(x => x.Key), scoutedLocationInfo.Max(x => x.Key));
        }

        private static void AddAPItemHook(long min, long max)
        {
            ulong target_func_start = 0x1407479E0;
            byte[] replaced_instructions = Memory.ReadByteArray(target_func_start, 14);
            ulong replacement_func_start_addr = (ulong)Memory.Allocate(1000, Memory.PAGE_EXECUTE_READWRITE);

            var jmpstub = new byte[]
            {
                0xff, 0x25, 0x00, 0x00, 0x00, 0x00,       //jmp    QWORD PTR [rip+0x0]        # 6 <_main+0x6>
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // target address
                // then the address to jump to (8 bytes)
            };
            Array.Copy(BitConverter.GetBytes(replacement_func_start_addr), 0, jmpstub, 6, 8); // target address

            //CMP r9d,0x12345678
            //JL OVER
            //CMP r9d,0x12345678
            //JG OVER
            // RET and 5 nops (could be replaced with mov r9d,<value>)
            // OVER (label)
            // 14 nops (replaced with source 14 bytes overwritten by jmp instruction)
            //  jmp        qword[rip+0]
            // <return address>
            var new_instructions = new byte[]
            {
                0x41, 0x81, 0xf8, 0x78, 0x56, 0x34, 0x12,    // cmp r9d,0x12345678
                0x7c, 0x0f,                                  // jl     OVER
                0x41, 0x81, 0xf8, 0x78, 0x56, 0x34, 0x12,    // cmp    r9d,0x12345678
                0x7f, 0x06,                                  // jg     OVER
                0xc3, 0x90, 0x90, 0x90, 0x90, 0x90,          // ret and 5 nops
                //0x41, 0xb8, 0x72, 0x01, 0x00, 0x00,          // mov    r9d,0x172 (dec 370)
                // OVER (label)
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,    // 14 nops -> get replaced with source 14 bytes
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
                0xff, 0x25, 0x00, 0x00, 0x00, 0x00,          // jmp    QWORD PTR [rip+0x8]
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // jmp's target address
            };

            Array.Copy(BitConverter.GetBytes(min), 0, new_instructions, 3, 4); // min
            Array.Copy(BitConverter.GetBytes(max), 0, new_instructions, 12, 4); // max
            Array.Copy(replaced_instructions, 0, new_instructions, 24, 14); // replaced_instructions
            Array.Copy(BitConverter.GetBytes(target_func_start + 14), 0, new_instructions, 44, 8); // target address


            Memory.WriteByteArray(replacement_func_start_addr, new_instructions); // write new instructions into its hook area
            Memory.WriteByteArray(target_func_start, jmpstub); // write jmp stub (e.g. "create hook")
        }
        private static void AddAPItemPopupHook(long min, long max)
        {
            ulong target_func_start = 0x140728c90;
            byte[] replaced_instructions = Memory.ReadByteArray(target_func_start, 14);
            ulong replacement_func_start_addr = (ulong)Memory.Allocate(1000, Memory.PAGE_EXECUTE_READWRITE);

            var jmpstub = new byte[]
            {
                0xff, 0x25, 0x00, 0x00, 0x00, 0x00,       //jmp    QWORD PTR [rip+0x0]        # 6 <_main+0x6>
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // target address
                // then the address to jump to (8 bytes)
            };
            Array.Copy(BitConverter.GetBytes(replacement_func_start_addr), 0, jmpstub, 6, 8); // target address

            //CMP r9d,0x12345678
            //JL OVER
            //CMP r9d,0x12345678
            //JG OVER
            // RET and 5 nops (could be replaced with mov r9d,<value>)
            // OVER (label)
            // 14 nops (replaced with source 14 bytes overwritten by jmp instruction)
            //  jmp        qword[rip+0]
            // <return address>
            var new_instructions = new byte[]
            {
                0x41, 0x81, 0xf8, 0x78, 0x56, 0x34, 0x12,    // cmp r9d,0x12345678
                0x7c, 0x0f,                                  // jl     OVER
                0x41, 0x81, 0xf8, 0x78, 0x56, 0x34, 0x12,    // cmp    r9d,0x12345678
                0x7f, 0x06,                                  // jg     OVER
                0xc3, 0x90, 0x90, 0x90, 0x90, 0x90,          // ret and 5 nops
                //0x41, 0xb8, 0x72, 0x01, 0x00, 0x00,          // mov    r9d,0x172 (dec 370)
                // OVER (label)
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,    // 14 nops -> get replaced with source 14 bytes
                0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
                0xff, 0x25, 0x00, 0x00, 0x00, 0x00,          // jmp    QWORD PTR [rip+0x8]
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // jmp's target address
            };

            Array.Copy(BitConverter.GetBytes(min), 0, new_instructions, 3, 4); // min
            Array.Copy(BitConverter.GetBytes(max), 0, new_instructions, 12, 4); // max
            Array.Copy(replaced_instructions, 0, new_instructions, 24, 14); // replaced_instructions
            Array.Copy(BitConverter.GetBytes(target_func_start + 14), 0, new_instructions, 44, 8); // target address


            Memory.WriteByteArray(replacement_func_start_addr, new_instructions); // write new instructions into its hook area
            Memory.WriteByteArray(target_func_start, jmpstub); // write jmp stub (e.g. "create hook")
        }

        internal static string BuildItemCaption(KeyValuePair<long, ScoutedItemInfo> item)
        {
            const byte progression = 0b001;
            const byte useful = 0b010;
            const byte trap = 0b100;
            string item_type = "normal";
            if (((byte)item.Value.Flags) == 0b001) item_type = "Progression";
            if (((byte)item.Value.Flags) == 0b010) item_type = "Useful";
            if (((byte)item.Value.Flags) == 0b100) item_type = "Trap";
            return $"A {item_type} Archipelago item for {item.Value.Player}'s {item.Value.ItemGame}.\0";
        }
        internal static string BuildDsrEventItemCaption()
        {
            return "A boon from another world. Makes a fog wall passable.\0";
        }


        private static bool upgradeWeapons(List<KeyValuePair<long, string>> addedEntries)
        {
            bool reloadRequired = ParamHelper.ReadFromBytes(out ParamStruct<EquipParamWeapon> paramStruct,
                                                     EquipParamWeapon.spOffset,
                                                     (ps) => ps.ParamEntries.Last().id >= 11109961);
            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of EquipParamWeapon (shop stubs)");
                return false;
            }

            // Copy a real weapon row as template (first entry)
            byte[] parambytes = new byte[EquipParamWeapon.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, parambytes, 0, parambytes.Length);

            // Set icon to Dagger (1) at +0xBA (confirmed via Paramdex XML)
            byte[] weaponIconBytes = BitConverter.GetBytes((short)1);
            parambytes[0xBA] = weaponIconBytes[0];
            parambytes[0xBB] = weaponIconBytes[1];

            foreach (var entry in addedEntries)
            {
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value}\0");
                paramStruct.AddParam((uint)entry.Key, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {addedEntries.Count} weapon shop stubs to EquipParamWeapon");
            ParamHelper.WriteFromParamSt(paramStruct, EquipParamWeapon.spOffset);
            return true;
        }

        private static bool upgradeProtectors(List<KeyValuePair<long, string>> addedEntries)
        {
            bool reloadRequired = ParamHelper.ReadFromBytes(out ParamStruct<EquipParamProtector> paramStruct,
                                                     EquipParamProtector.spOffset,
                                                     (ps) => ps.ParamEntries.Last().id >= 11109961);
            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of EquipParamProtector (shop stubs)");
                return false;
            }

            // Copy a real protector row as template (first entry)
            byte[] parambytes = new byte[EquipParamProtector.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, parambytes, 0, parambytes.Length);

            // Set iconIdM and iconIdF to Leather Armor (1052) at +0xA2/+0xA4 (confirmed via Paramdex XML)
            byte[] protectorIconBytes = BitConverter.GetBytes((short)1052);
            parambytes[0xA2] = protectorIconBytes[0];
            parambytes[0xA3] = protectorIconBytes[1];
            parambytes[0xA4] = protectorIconBytes[0];
            parambytes[0xA5] = protectorIconBytes[1];

            foreach (var entry in addedEntries)
            {
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value}\0");
                paramStruct.AddParam((uint)entry.Key, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {addedEntries.Count} protector shop stubs to EquipParamProtector");
            ParamHelper.WriteFromParamSt(paramStruct, EquipParamProtector.spOffset);
            return true;
        }

        private static bool upgradeAccessories(List<KeyValuePair<long, string>> addedEntries, Dictionary<long, int> realEquipIds)
        {
            bool reloadRequired = ParamHelper.ReadFromBytes(out ParamStruct<EquipParamAccessory> paramStruct,
                                                     EquipParamAccessory.spOffset,
                                                     (ps) => ps.ParamEntries.Last().id >= 11109961);
            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of EquipParamAccessory (shop stubs)");
                return false;
            }

            // Copy a real accessory row as template (first entry)
            byte[] templateBytes = new byte[EquipParamAccessory.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, templateBytes, 0, templateBytes.Length);

            // Default fallback icon: Ring of Sacrifice (4000) at +0x22 (confirmed via Paramdex XML)
            byte[] defaultIconBytes = BitConverter.GetBytes((short)4000);
            templateBytes[0x22] = defaultIconBytes[0];
            templateBytes[0x23] = defaultIconBytes[1];

            foreach (var entry in addedEntries)
            {
                byte[] parambytes = (byte[])templateBytes.Clone();

                // Copy the FULL real ring's param row for consistent rendering fields.
                if (realEquipIds.TryGetValue(entry.Key, out int realEquipId))
                {
                    var realEntry = paramStruct.ParamEntries.FirstOrDefault(e => e.id == (uint)realEquipId);
                    if (realEntry.id == (uint)realEquipId)
                    {
                        parambytes = new byte[EquipParamAccessory.Size];
                        Array.Copy(paramStruct.ParamBytes, (int)realEntry.paramOffset, parambytes, 0, (int)EquipParamAccessory.Size);
                    }
                    else
                    {
                        parambytes = (byte[])templateBytes.Clone();
                    }
                }
                else
                {
                    parambytes = (byte[])templateBytes.Clone();
                }

                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value}\0");
                paramStruct.AddParam((uint)entry.Key, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {addedEntries.Count} accessory shop stubs to EquipParamAccessory");
            paramStruct.ParamEntries.Sort((x, y) => x.id.CompareTo(y.id));
            ParamHelper.WriteFromParamSt(paramStruct, EquipParamAccessory.spOffset);
            return true;
        }

        private static bool upgradeGoods(List<KeyValuePair<long, string>> addedEntries, Dictionary<long, ScoutedItemInfo> scoutedLocationInfo)
        {
            // Read in the Param Structure
            // Modify it,
            // Then save it back
            bool reloadRequired = ParamHelper.ReadFromBytes(out ParamStruct<EquipParamGoods> paramStruct,
                                                     EquipParamGoods.spOffset,
                                                     (ps) => ps.ParamEntries.Last().id >= 11109961);
            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of EquipParamGoods");
                return false;
            }
            // if we are here, we are updating the params.

            ushort new_entries = (ushort)addedEntries.Count();

            uint goods_param_size = 0x5c;

            // Get first entry's Param (e.g. White Sign Soapstone), use it as basis for new params.
            byte[] parambytes = new byte[EquipParamGoods.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, parambytes, 0, parambytes.Length);

            parambytes[0x36] = 99; // max num
            parambytes[0x3b] = 0; // ref category
            parambytes[0x3e] = 0; // use animation = 0
                                  // Is Only One?
                                  // Is Deposit?

            // Build vanilla goodsType, icon, and magicId lookups from original entries BEFORE the loop.
            // We must not search ParamEntries inside the loop because AddParam() grows that list,
            // and a stub entry's paramOffset points past ParamBytes — causing IndexOutOfRangeException.
            var vanillaGoodsType = new Dictionary<uint, byte>();
            var vanillaIcon = new Dictionary<uint, short>();
            var vanillaMagicId = new Dictionary<uint, int>();
            foreach (var ve in paramStruct.ParamEntries)
            {
                vanillaGoodsType[ve.id] = paramStruct.ParamBytes[(int)ve.paramOffset + 0x3A];
                vanillaIcon[ve.id] = BitConverter.ToInt16(paramStruct.ParamBytes, (int)ve.paramOffset + 0x2C);
                vanillaMagicId[ve.id] = BitConverter.ToInt32(paramStruct.ParamBytes, (int)ve.paramOffset + 0x28);
            }

            // For each new item, "Add Item" to ParamSt
            for (uint i = 0; i < new_entries; i++)
            {
                var entry = addedEntries.ToArray()[i];
                uint newid = (uint)entry.Key;
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value}\0");
                // set sort bytes in param based on id - not sure if this is grabbing top or bottom 2 bytes!! But filling all 4 put the items at the top instead.
                byte[] idbytes = BitConverter.GetBytes(newid);
                parambytes[0x1c] = idbytes[0]; // sort byte 0
                parambytes[0x1d] = idbytes[1]; // sort byte 1
                //parambytes[0x1e] = idbytes[2];
                //parambytes[0x1f] = idbytes[3];
                parambytes[0x45] |= (byte)(0x30); // turn on isDrop and isDeposit bits

                // Look up goodsType (+0x3A), icon (+0x2C), and magicId (+0x28) from the vanilla
                // EquipParamGoods row. Falls back to Prism Stone icon + key items tab for
                // foreign-world items or anything not in EquipParamGoods.
                byte goodsType = 1;
                short icon = 2042; // Prism Stone fallback
                int magicId = -1;  // default: no magic reference
                if (scoutedLocationInfo.TryGetValue((long)newid, out var scoutedInfo) &&
                    App.AllItemsByApId.TryGetValue((int)scoutedInfo.ItemId, out var dsrItem))
                {
                    if (vanillaGoodsType.TryGetValue((uint)dsrItem.Id, out byte vanillaType))
                        goodsType = vanillaType;
                    if (vanillaIcon.TryGetValue((uint)dsrItem.Id, out short vanillaIconVal))
                        icon = vanillaIconVal;
                    // For spell items, copy the vanilla magicId so DSR's magic panel can resolve
                    // the existing MagicParam entry (Uses, Slots, Type display correctly).
                    if (vanillaMagicId.TryGetValue((uint)dsrItem.Id, out int vanillaMagic) && vanillaMagic != -1)
                        magicId = vanillaMagic;
                }
                parambytes[0x3a] = goodsType;
                byte[] iconbytes = BitConverter.GetBytes(icon);
                parambytes[0x2c] = iconbytes[0];
                parambytes[0x2d] = iconbytes[1];
                byte[] magicIdBytes = BitConverter.GetBytes(magicId);
                parambytes[0x28] = magicIdBytes[0];
                parambytes[0x29] = magicIdBytes[1];
                parambytes[0x2a] = magicIdBytes[2];
                parambytes[0x2b] = magicIdBytes[3];

                // This will add the item to the array, and append its string to the NewString buffer
                paramStruct.AddParam(newid, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {new_entries} items to EquipParamGoods from {addedEntries.First().Key} to {addedEntries.Last().Key}");
            paramStruct.ParamEntries.Sort((x, y) => x.id.CompareTo(y.id));
            ParamHelper.WriteFromParamSt(paramStruct, EquipParamGoods.spOffset);

            return true;
        }

        internal static void AddMsgs(int msgManOffset, List<KeyValuePair<long, string>> instrings, string msgsName)
        {
            // Read in system text FMGs
            bool reloadRequired = MsgManHelper.ReadMsgManStruct(out MsgManStruct msgManStruct,
                                                     msgManOffset,
                                                     (ps) => ps.MsgEntries.Last().id >= 99999990);
            if (!reloadRequired)
            {
                Log.Logger.Warning($"Warning: Could not reload {msgsName} msgs.");
                return;
            }

            foreach (var input in instrings)
                msgManStruct.AddMsg((uint)input.Key, input.Value);


            msgManStruct.AddMsg(99999998, ""); // add dummy message to mark that we've been here
            msgManStruct.MsgEntries.Sort((x, y) => (x.id.CompareTo(y.id)));
            Log.Logger.Information($"Updated {msgsName} struct");

            MsgManHelper.WriteFromMsgManStruct(msgManStruct, msgManOffset); // write the msgs update
        }
        private static void UpdateItemText(ulong strloc, int len, string newstring)
        {
            if (strloc == 0)
            {
                Log.Logger.Information($"strloc = {strloc}"); return;
            }
            byte[] ba = Memory.ReadByteArray(strloc, len);
            string su16 = Encoding.Unicode.GetString(ba);
            string[] sub16 = su16.Split("\0");
            Log.Logger.Information($"Padding to {sub16[0].Length} bytes");
            int available_space = sub16[0].Length;
            string newptxt = newstring;
            if (newstring.Length > available_space)
                newptxt = newstring.Substring(0, sub16[0].Length);

            byte[] newba = Encoding.Unicode.GetBytes(newptxt);
            Memory.WriteByteArray(strloc, newba);
            Log.Logger.Information($"String found: {su16}, \n@{strloc:X}");
            Log.Logger.Information($"Wrote string {newptxt}");
        }

        internal static ulong FindMsg(ulong MsgsStart, uint id)
        {
            ulong GoodsMsgsStrTableOffset = Memory.ReadULong(MsgsStart + 0x14);
            ushort GoodsMsgsCompareEntries = Memory.ReadUShort(MsgsStart + 0xc);
            ulong GoodsMsgsCompareStart = MsgsStart + 0x1c;
            uint compareEntrySize = 0xc;
            for (uint curridx = 0; curridx < GoodsMsgsCompareEntries; curridx++)
            {
                ulong currentry = GoodsMsgsCompareStart + (compareEntrySize * curridx);
                uint low = Memory.ReadUInt(currentry + 0x4);
                uint high = Memory.ReadUInt(currentry + 0x8);
                if (low <= id && id <= high)
                {
                    uint baseoffset = Memory.ReadUInt(currentry + 0x0);
                    uint idoffset = id - low;
                    uint strEntryOffset = 4 * (idoffset + baseoffset);

                    ulong itemstroffset = Memory.ReadUInt(MsgsStart + GoodsMsgsStrTableOffset + strEntryOffset);
                    ulong itemstrloc = MsgsStart + itemstroffset;
                    return itemstrloc;
                }
            }
            return 0;
        }

        public static void ChangePrismStoneText()
        {
            var item = MiscHelper.GetAllItems().Find(x => x.Name.ToLower().Contains("prism stone"));
            uint itemid = (uint)item.Id;
            //uint itemid = 9014;

            ulong MsgMan = Memory.ReadULong(0x141c7e3e8);
            ulong GoodsMsgsStart = Memory.ReadULong(MsgMan + 0x380);
            ulong GoodsCaptionMsgsStart = Memory.ReadULong(MsgMan + 0x378);
            ulong GoodsInfoMsgStart = Memory.ReadULong(MsgMan + 0x328);
            ulong itemNameStrLoc = FindMsg(GoodsMsgsStart, itemid);
            ulong itemCaptionStrLoc = FindMsg(GoodsCaptionMsgsStart, itemid);
            ulong itemInfoStrLoc = FindMsg(GoodsInfoMsgStart, itemid);

            UpdateItemText(itemNameStrLoc, 100, "AP Item\0");
            UpdateItemText(itemCaptionStrLoc, 100, "This is an item that belongs to another world...\0");
            UpdateItemText(itemInfoStrLoc, 500, "*narrator voice* We're not sure how this got here. \nBest hold on to it. \n\nJust in case.\0");

            ulong equipGoodsParamResCap = Memory.ReadULong((ulong)(AddressHelper.SoloParamAob.Address + 0xF0));
            //upgradeGoods(equipGoodsParamResCap);
            //AddMsgs(9015, new List<string>() { "AP Item From Player 2's world" });
            return;
        }
    }
}
