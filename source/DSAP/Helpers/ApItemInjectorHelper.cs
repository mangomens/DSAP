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

        // Aligned equip IDs for weapon/protector shop stubs.
        // DSR groups weapon IDs by floor(id/100)*100 and protector IDs by floor(id/1000)*1000
        // for icon/model lookup, so stubs must use IDs that are multiples of 100/1000.
        internal static Dictionary<long, int> WeaponAlignedEquipIds = new();
        internal static Dictionary<long, int> ProtectorAlignedEquipIds = new();
        private const int WEAPON_ALIGNED_BASE = 9_100_000;    // just above vanilla max ~9,021,000
        private const int PROTECTOR_ALIGNED_BASE = 2_700_000; // just above vanilla max ~2,643,000
        private const int WEAPON_ALIGNED_STRIDE = 100;
        private const int PROTECTOR_ALIGNED_STRIDE = 1000;
        internal static async Task AddAPItems(Dictionary<long, ScoutedItemInfo> scoutedLocationInfo)
        {
            // Add all locations to pool, to "stub" out in-game items.

            // Build set of shop location IDs so we can partition stubs for shop-specific param tables
            var shopLocIds = new HashSet<long>(
                LocationHelper.GetShopRows()
                              .Where(x => x.IsEnabled)
                              .Select(x => (long)x.Id));

            List<KeyValuePair<long, ScoutedItemInfo>> addedEntries = scoutedLocationInfo.ToList();
            //addedEntries.Sort((a, b) => a.Key.CompareTo(b.Key));

            var added_names = addedEntries.Select(x => new KeyValuePair<long, string>(x.Key, $"{x.Value.ItemDisplayName}\0")).ToList();
            var added_captions = addedEntries.Select(x => new KeyValuePair<long, string>(x.Key, BuildItemCaption(x))).ToList();

            var added_emk_names = MiscHelper.GetDsrEventItems().Select(x => new KeyValuePair<long, string>(x.Id, $"{x.Name}\0"));
            var added_emk_captions = MiscHelper.GetDsrEventItems().Select(x => new KeyValuePair<long, string>(x.Id, BuildDsrEventItemCaption()));

            added_names.AddRange(added_emk_names);
            added_captions.AddRange(added_emk_captions);

            added_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            added_captions.Sort((a, b) => a.Key.CompareTo(b.Key));

            var watch = System.Diagnostics.Stopwatch.StartNew();

            // separate entries:
            var groundGoodsEntries = new Dictionary<long, Tuple<ScoutedItemInfo, string, string>>();
            var apShopGoodsEntries = new Dictionary<long, Tuple<ScoutedItemInfo, string, string>>();
            var ourShopGoodsEntries = new Dictionary<long, Tuple<ScoutedItemInfo, string, string>>();
            var ourShopSpellsEntries = new Dictionary<long, Tuple<ScoutedItemInfo, string, string>>();
            var ourShopWeaponsEntries = new Dictionary<long, Tuple<ScoutedItemInfo, string, string>>();
            var ourShopProtectorsEntries = new Dictionary<long, Tuple<ScoutedItemInfo, string, string>>();
            var ourShopAccessoriesEntries = new Dictionary<long, Tuple<ScoutedItemInfo, string, string>>();
            int alignedWeaponIdShove = 0;
            int alignedProtectorIdShove = 0;

            WeaponAlignedEquipIds.Clear();
            ProtectorAlignedEquipIds.Clear();

            for (int i = 0; i < added_names.Count; i++)
            {
                (long locId, string nameValue) = added_names[i];
                string captionValue = added_captions[i].Value;
                scoutedLocationInfo.TryGetValue(locId, out var scoutedInfo);
                if (scoutedInfo != null && scoutedInfo.Player.Slot != App.Client.CurrentSession.ConnectionInfo.Slot)
                    nameValue = $"{scoutedInfo.Player}'s {nameValue.TrimEnd('\0')}\0";

                Tuple<ScoutedItemInfo, string, string> itemData = new Tuple<ScoutedItemInfo, string, string> (scoutedInfo, nameValue, captionValue);

                if (!shopLocIds.Contains(locId))
                    groundGoodsEntries.Add(locId, itemData);
                else if (scoutedInfo.Player.Slot != App.Client.CurrentSession.ConnectionInfo.Slot)
                    apShopGoodsEntries.Add(locId, itemData);
                else
                {
                    if (App.AllItemsByApId.TryGetValue((int)scoutedInfo.ItemId, out var dsrItem))
                    {
                        switch (dsrItem.Category)
                        {
                            case Enums.DSItemCategory.AnyWeapon:
                                ourShopWeaponsEntries.Add(locId, itemData);
                                WeaponAlignedEquipIds[locId] = WEAPON_ALIGNED_BASE + alignedWeaponIdShove * WEAPON_ALIGNED_STRIDE;
                                alignedWeaponIdShove++;
                                break;
                            case Enums.DSItemCategory.Armor:
                                ourShopProtectorsEntries.Add(locId, itemData);
                                ProtectorAlignedEquipIds[locId] = PROTECTOR_ALIGNED_BASE + alignedProtectorIdShove * PROTECTOR_ALIGNED_STRIDE;
                                alignedProtectorIdShove++;
                                break;
                            case Enums.DSItemCategory.Rings:
                                ourShopAccessoriesEntries.Add(locId, itemData);
                                break;
                            case Enums.DSItemCategory.Consumables:
                                if (dsrItem.SpellCategory == Enums.SpellCategory.None)
                                    ourShopGoodsEntries.Add(locId, itemData);
                                else
                                    ourShopSpellsEntries.Add(locId, itemData);
                                break;
                            default:
                                apShopGoodsEntries.Add(locId, itemData);
                                break;
                        }
                    }
                    else
                    {
                        apShopGoodsEntries.Add(locId, itemData);
                    }
                }
            }

            bool reloadRequired = ParamHelper.ReadFromBytes(out ParamStruct<EquipParamGoods> paramStruct,
                                                     EquipParamGoods.spOffset,
                                                     (ps) => ps.ParamEntries.Last().id >= 11109961);

            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of EquipParamGoods");
            }
            else
            {
                bool do_ground_goods_replacements = upgradeGroundGoods(groundGoodsEntries, paramStruct);// upgradeGroundGoods
                getGroundGoodsText(groundGoodsEntries, out var groundGoods_names, out var groundGoods_captions); // getGroundGoodsText
                
                bool do_ap_shop_goods_replacements = upgradeAPShopGoods(apShopGoodsEntries, paramStruct);// upgradeAPShopGoods
                getAPShopGoodsText(apShopGoodsEntries, out var apShopGoods_names, out var apShopGoods_captions);// getAPShopGoodsText
                
                bool do_our_shop_goods_replacements = upgradeOurShopGoods(ourShopGoodsEntries, paramStruct);// upgradeOurShopGoods
                getOurShopGoodsText(ourShopGoodsEntries, out var ourShopGoods_names, out var ourShopGoods_captions);// getOurShopGoodsText
               
                bool do_our_shop_spells_replacements = upgradeOurShopSpells(ourShopSpellsEntries, paramStruct); // upgradeOurShopSpells
                getOurShopSpellText(ourShopSpellsEntries, out var ourShopSpells_names, out var ourShopSpells_captions);// getOurShopSpellText

                ParamHelper.WriteFromParamSt(paramStruct, EquipParamGoods.spOffset);

                List<KeyValuePair<long, string>> goods_names = groundGoods_names.Concat(apShopGoods_names).Concat(ourShopGoods_names).ToList();
                List<KeyValuePair<long, string>> goods_captions = groundGoods_captions.Concat(apShopGoods_captions).Concat(ourShopGoods_captions).ToList();

                if (do_ground_goods_replacements || do_ap_shop_goods_replacements || do_our_shop_goods_replacements)
                {
                    AddMsgs(MsgManStruct.OFFSET_ITEM_NAMES,         goods_names,    "Item Names");
                    AddMsgs(MsgManStruct.OFFSET_ITEM_CAPTIONS,      goods_captions, "Item Captions");
                    AddMsgs(MsgManStruct.OFFSET_ITEM_DESCRIPTIONS,  goods_captions, "Item Descriptions");
                }

                if (do_our_shop_spells_replacements)
                {
                    AddMsgs(MsgManStruct.OFFSET_SPELL_NAMES,        ourShopSpells_names,    "Spell Names");
                    AddMsgs(MsgManStruct.OFFSET_SPELL_DESCRIPTIONS, ourShopSpells_captions, "Spell Descriptions");
                }
            }

            bool do_our_shop_weapons_replacements = upgradeOurShopWeapons(ourShopWeaponsEntries, WeaponAlignedEquipIds);// upgradeOurShopWeapons
            getOurShopWeaponText(ourShopWeaponsEntries, out var ourShopWeapons_names, out var ourShopWeapons_captions, out var ourShopWeapons_descriptions);// getOurShopWeaponText

            bool do_our_shop_protectors_replacements = upgradeOurShopProtectors(ourShopProtectorsEntries, ProtectorAlignedEquipIds);// upgradeOurShopProtectors
            getOurShopProtectorText(ourShopProtectorsEntries, out var ourShopProtectors_names, out var ourShopProtectors_captions, out var ourShopProtectors_descriptions);// getOurShopProtectorText

            bool do_our_shop_accessories_replacements = upgradeOurShopAccessories(ourShopAccessoriesEntries);// upgradeOurShopAccessories
            getOurShopAccessoryText(ourShopAccessoriesEntries, out var ourShopAccessories_names, out var ourShopAccessories_captions, out var ourShopAccessories_descriptions);// getOurShopAccessoryText

            if (do_our_shop_weapons_replacements)
            {
                AddMsgs(MsgManStruct.OFFSET_WEAPON_NAMES,        ourShopWeapons_names,        "Weapon Names");
                AddMsgs(MsgManStruct.OFFSET_WEAPON_CAPTIONS,     ourShopWeapons_captions,     "Weapon Captions");
                AddMsgs(MsgManStruct.OFFSET_WEAPON_DESCRIPTIONS, ourShopWeapons_descriptions, "Weapon Descriptions");
            }
            if (do_our_shop_protectors_replacements)
            {
                AddMsgs(MsgManStruct.OFFSET_ARMOR_NAMES,        ourShopProtectors_names,        "Armor Names");
                AddMsgs(MsgManStruct.OFFSET_ARMOR_CAPTIONS,     ourShopProtectors_captions,     "Armor Captions");
                AddMsgs(MsgManStruct.OFFSET_ARMOR_DESCRIPTIONS, ourShopProtectors_descriptions, "Armor Descriptions");
            }
            if (do_our_shop_accessories_replacements)
            {
                AddMsgs(MsgManStruct.OFFSET_RING_NAMES,        ourShopAccessories_names,        "Ring Names");
                AddMsgs(MsgManStruct.OFFSET_RING_CAPTIONS,     ourShopAccessories_captions,     "Ring Captions");
                AddMsgs(MsgManStruct.OFFSET_RING_DESCRIPTIONS, ourShopAccessories_descriptions, "Ring Descriptions");
            }
                
            Log.Logger.Information($"Added param stubs: {added_names.Count} goods, {ourShopWeaponsEntries.Count} weapons, {ourShopProtectorsEntries.Count} protectors, {ourShopAccessoriesEntries.Count} accessories");

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
        private static void getAPShopGoodsText(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> apShopGoodsEntries,out List<KeyValuePair<long, string>> apShopGoods_names, out List<KeyValuePair<long, string>> apShopGoods_captions)
        {
            apShopGoods_names = new List<KeyValuePair<long, string>>();
            apShopGoods_captions = new List<KeyValuePair<long, string>>();

            foreach (var apShopGoodsEntry in apShopGoodsEntries)
            {
                apShopGoods_names.Add(new KeyValuePair<long, string>(apShopGoodsEntry.Key, apShopGoodsEntry.Value.Item2));
                apShopGoods_captions.Add(new KeyValuePair<long, string>(apShopGoodsEntry.Key, apShopGoodsEntry.Value.Item3));
            }

            apShopGoods_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            apShopGoods_captions.Sort((a, b) => a.Key.CompareTo(b.Key));
        }

        private static void getGroundGoodsText(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> groundGoodsEntries, out List<KeyValuePair<long, string>> groundGoods_names, out List<KeyValuePair<long, string>> groundGoods_captions)
        {
            groundGoods_names = new List<KeyValuePair<long, string>>();
            groundGoods_captions = new List<KeyValuePair<long, string>>();

            foreach (var groundGoodsEntry in groundGoodsEntries)
            {
                groundGoods_names.Add(new KeyValuePair<long, string>(groundGoodsEntry.Key, groundGoodsEntry.Value.Item2));
                groundGoods_captions.Add(new KeyValuePair<long, string>(groundGoodsEntry.Key, groundGoodsEntry.Value.Item3));
            }

            groundGoods_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            groundGoods_captions.Sort((a, b) => a.Key.CompareTo(b.Key));
        }

        private static void getOurShopGoodsText(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopGoodsEntries,out List<KeyValuePair<long, string>> ourShopGoods_names, out List<KeyValuePair<long, string>> ourShopGoods_captions)
        {
            ulong msgManPtrGoods = Memory.ReadULong(0x141c7e3e8);
            ulong goodsCaptionFmgStart = Memory.ReadULong(msgManPtrGoods + (ulong)MsgManStruct.OFFSET_ITEM_CAPTIONS);

            ourShopGoods_names = new List<KeyValuePair<long, string>>();
            ourShopGoods_captions = new List<KeyValuePair<long, string>>();

            foreach (var ourShopGoodsEntry in ourShopGoodsEntries)
            {
                // For normal goods: replace caption with vanilla goods caption if available
                string caption = ourShopGoodsEntry.Value.Item3;
                if (App.AllItemsByApId.TryGetValue((int)ourShopGoodsEntry.Value.Item1.ItemId, out var goodsItem))
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

                ourShopGoods_names.Add(new KeyValuePair<long, string>(ourShopGoodsEntry.Key, ourShopGoodsEntry.Value.Item2));
                ourShopGoods_captions.Add(new KeyValuePair<long, string>(ourShopGoodsEntry.Key, caption));
            }
            
            ourShopGoods_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopGoods_captions.Sort((a, b) => a.Key.CompareTo(b.Key));
        }

        private static void getOurShopSpellText(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopSpellsEntries,out List<KeyValuePair<long, string>> ourShopSpells_names, out List<KeyValuePair<long, string>> ourShopSpells_captions)
        {
            ourShopSpells_names = new List<KeyValuePair<long, string>>();
            ourShopSpells_captions = new List<KeyValuePair<long, string>>();

            ulong msgManPtrGoods = Memory.ReadULong(0x141c7e3e8);
            ulong spellDescFmgStart    = Memory.ReadULong(msgManPtrGoods + (ulong)MsgManStruct.OFFSET_SPELL_DESCRIPTIONS);

            ParamHelper.ReadFromBytes(out ParamStruct<EquipParamGoods> goodsParamForMagic,
                EquipParamGoods.spOffset, (ps) => false);
            var vanillaMagicIdMap = new Dictionary<uint, int>();
            foreach (var ve in goodsParamForMagic.ParamEntries)
                vanillaMagicIdMap[ve.id] = BitConverter.ToInt32(goodsParamForMagic.ParamBytes, (int)ve.paramOffset + 0x28);

            foreach (var ourShopSpellEntry in ourShopSpellsEntries)
            {
                // For spells: look up vanilla description and resolve magicId for FMG keying.
                // DSR keys spell FMG lookups by the magicId (MagicParam row), not the goods ID.
                string caption = ourShopSpellEntry.Value.Item3;
                long spellFmgKey = ourShopSpellEntry.Key; // fallback to AP loc ID
                if (App.AllItemsByApId.TryGetValue((int)ourShopSpellEntry.Value.Item1.ItemId, out var spellItem))
                {
                    // Resolve magicId from vanilla goods param
                    if (vanillaMagicIdMap.TryGetValue((uint)spellItem.Id, out int magicId) && magicId > 0)
                        spellFmgKey = magicId;

                    ulong descLoc = FindMsg(spellDescFmgStart, (uint)spellFmgKey);
                    if (descLoc != 0)
                    {
                        byte[] strBytes = Memory.ReadByteArray(descLoc, 1024);
                        string vanillaStr = Encoding.Unicode.GetString(strBytes).Split('\0')[0];
                        if (!string.IsNullOrWhiteSpace(vanillaStr))
                            caption = vanillaStr + "\0";
                    }
                }
            
                ourShopSpells_names.Add(new KeyValuePair<long, string>(spellFmgKey, ourShopSpellEntry.Value.Item2));
                ourShopSpells_captions.Add(new KeyValuePair<long, string>(spellFmgKey, caption));
            }

            ourShopSpells_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopSpells_captions.Sort((a, b) => a.Key.CompareTo(b.Key));
        }

        private static void getOurShopWeaponText(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopWeaponsEntries,
        out List<KeyValuePair<long, string>> ourShopWeapons_names, out List<KeyValuePair<long, string>> ourShopWeapons_captions, out List<KeyValuePair<long, string>> ourShopWeapons_descriptions)
        {
            ourShopWeapons_names = new List<KeyValuePair<long, string>>();
            ourShopWeapons_captions = new List<KeyValuePair<long, string>>();
            ourShopWeapons_descriptions = new List<KeyValuePair<long, string>>();

            if (ourShopWeaponsEntries.Count > 0)
            {
                // Build weapon captions and descriptions: use vanilla text for known DSR weapons, AP caption otherwise.
                ulong msgManPtrWeapon = Memory.ReadULong(0x141c7e3e8);
                ulong weaponCaptionFmgStart = Memory.ReadULong(msgManPtrWeapon + (ulong)MsgManStruct.OFFSET_WEAPON_CAPTIONS);
                ulong weaponDescFmgStart    = Memory.ReadULong(msgManPtrWeapon + (ulong)MsgManStruct.OFFSET_WEAPON_DESCRIPTIONS);
                var weaponCaptionsWithVanilla = new List<KeyValuePair<long, string>>();
                var weaponShopDescriptions    = new List<KeyValuePair<long, string>>();
                foreach (var ourShopWeaponsEntry in ourShopWeaponsEntries)
                {
                    string caption = ourShopWeaponsEntry.Value.Item3;
                    string desc    = ourShopWeaponsEntry.Value.Item3;
                    if (App.AllItemsByApId.TryGetValue((int)ourShopWeaponsEntry.Value.Item1.ItemId, out var weaponItem))
                    {
                        var realEquipId = weaponItem.Id;
                        ulong captionLoc = FindMsg(weaponCaptionFmgStart, (uint)realEquipId);
                        if (captionLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(captionLoc, 512);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes).Split('\0')[0];
                            if (!string.IsNullOrWhiteSpace(vanillaStr))
                                caption = vanillaStr + "\0";
                        }
                        ulong descLoc = FindMsg(weaponDescFmgStart, (uint)realEquipId);
                        if (descLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(descLoc, 1024);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes).Split('\0')[0];
                            if (!string.IsNullOrWhiteSpace(vanillaStr))
                                desc = vanillaStr + "\0";
                        }
                    }
                    long alignedKey = WeaponAlignedEquipIds.TryGetValue(ourShopWeaponsEntry.Key, out int wAid) ? wAid : ourShopWeaponsEntry.Key;
                    ourShopWeapons_names.Add(new KeyValuePair<long, string>(WeaponAlignedEquipIds.TryGetValue(ourShopWeaponsEntry.Key, out int wn) ? wn : ourShopWeaponsEntry.Key, ourShopWeaponsEntry.Value.Item2));
                    ourShopWeapons_captions.Add(new KeyValuePair<long, string>(alignedKey, caption));
                    ourShopWeapons_descriptions.Add(new KeyValuePair<long, string>(alignedKey, desc));
                }
            }

            ourShopWeapons_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopWeapons_captions.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopWeapons_descriptions.Sort((a, b) => a.Key.CompareTo(b.Key));
        }

        private static void getOurShopProtectorText(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopProtectorsEntries,
        out List<KeyValuePair<long, string>> ourShopProtectors_names, out List<KeyValuePair<long, string>> ourShopProtectors_captions, out List<KeyValuePair<long, string>> ourShopProtectors_descriptions)
        {
            ourShopProtectors_names = new List<KeyValuePair<long, string>>();
            ourShopProtectors_captions = new List<KeyValuePair<long, string>>();
            ourShopProtectors_descriptions = new List<KeyValuePair<long, string>>();

            if (ourShopProtectorsEntries.Count > 0)
            {
                // Build protector captions and descriptions: use vanilla text for known DSR armor, AP caption otherwise.
                ulong msgManPtrArmor = Memory.ReadULong(0x141c7e3e8);
                ulong armorCaptionFmgStart = Memory.ReadULong(msgManPtrArmor + (ulong)MsgManStruct.OFFSET_ARMOR_CAPTIONS);
                ulong armorDescFmgStart    = Memory.ReadULong(msgManPtrArmor + (ulong)MsgManStruct.OFFSET_ARMOR_DESCRIPTIONS);
                var armorCaptionsWithVanilla = new List<KeyValuePair<long, string>>();
                var armorShopDescriptions    = new List<KeyValuePair<long, string>>();
                foreach (var ourShopProtectorsEntry in ourShopProtectorsEntries)
                {
                    string caption = ourShopProtectorsEntry.Value.Item3;
                    string desc    = ourShopProtectorsEntry.Value.Item3;
                    if (App.AllItemsByApId.TryGetValue((int)ourShopProtectorsEntry.Value.Item1.ItemId, out var protectorItem))
                    {
                        var realEquipId = protectorItem.Id;
                        ulong captionLoc = FindMsg(armorCaptionFmgStart, (uint)realEquipId);
                        if (captionLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(captionLoc, 512);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes).Split('\0')[0];
                            if (!string.IsNullOrWhiteSpace(vanillaStr))
                                caption = vanillaStr + "\0";
                        }
                        ulong descLoc = FindMsg(armorDescFmgStart, (uint)realEquipId);
                        if (descLoc != 0)
                        {
                            byte[] strBytes = Memory.ReadByteArray(descLoc, 1024);
                            string vanillaStr = Encoding.Unicode.GetString(strBytes).Split('\0')[0];
                            if (!string.IsNullOrWhiteSpace(vanillaStr))
                                desc = vanillaStr + "\0";
                        }
                    }
                    long alignedKey = ProtectorAlignedEquipIds.TryGetValue(ourShopProtectorsEntry.Key, out int pAid) ? pAid : ourShopProtectorsEntry.Key;
                    ourShopProtectors_names.Add(new KeyValuePair<long, string>(ProtectorAlignedEquipIds.TryGetValue(ourShopProtectorsEntry.Key, out int pn) ? pn : ourShopProtectorsEntry.Key, ourShopProtectorsEntry.Value.Item2));
                    ourShopProtectors_captions.Add(new KeyValuePair<long, string>(alignedKey, caption));
                    ourShopProtectors_descriptions.Add(new KeyValuePair<long, string>(alignedKey, desc));
                }           
            }

            ourShopProtectors_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopProtectors_captions.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopProtectors_descriptions.Sort((a, b) => a.Key.CompareTo(b.Key));    
        }

        private static void getOurShopAccessoryText(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopAccessoriesEntries,
        out List<KeyValuePair<long, string>> ourShopAccessories_names, out List<KeyValuePair<long, string>> ourShopAccessories_captions, out List<KeyValuePair<long, string>> ourShopAccessories_descriptions)
        {
            ourShopAccessories_names = new List<KeyValuePair<long, string>>();
            ourShopAccessories_captions = new List<KeyValuePair<long, string>>();
            ourShopAccessories_descriptions = new List<KeyValuePair<long, string>>();

            if (ourShopAccessoriesEntries.Count > 0)
            {
                // Build ring captions and descriptions: use vanilla text for known DSR rings, AP caption otherwise.
                // The shop info panel shows CAPTIONS (short one-liner). DESCRIPTIONS is shown in the inventory screen.
                ulong msgManPtr = Memory.ReadULong(0x141c7e3e8);
                ulong ringCaptionFmgStart = Memory.ReadULong(msgManPtr + (ulong)MsgManStruct.OFFSET_RING_CAPTIONS);
                ulong ringDescFmgStart    = Memory.ReadULong(msgManPtr + (ulong)MsgManStruct.OFFSET_RING_DESCRIPTIONS);
                var accessoryCaptionsWithVanilla = new List<KeyValuePair<long, string>>();
                var accessoryShopDescriptions    = new List<KeyValuePair<long, string>>();
                foreach (var ourShopAccessoriesEntry in ourShopAccessoriesEntries)
                {
                    string caption = ourShopAccessoriesEntry.Value.Item3;
                    string desc    = ourShopAccessoriesEntry.Value.Item3;
                    if (App.AllItemsByApId.TryGetValue((int)ourShopAccessoriesEntry.Value.Item1.ItemId, out var accessoryItem))
                    {
                        var realEquipId = accessoryItem.Id;
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
                    ourShopAccessories_names.Add(new KeyValuePair<long, string>(ourShopAccessoriesEntry.Key, ourShopAccessoriesEntry.Value.Item2));
                    ourShopAccessories_captions.Add(new KeyValuePair<long, string>(ourShopAccessoriesEntry.Key, caption));
                    ourShopAccessories_descriptions.Add(new KeyValuePair<long, string>(ourShopAccessoriesEntry.Key, desc));
                }
            }

            ourShopAccessories_names.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopAccessories_captions.Sort((a, b) => a.Key.CompareTo(b.Key));
            ourShopAccessories_descriptions.Sort((a, b) => a.Key.CompareTo(b.Key));
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


        private static bool upgradeOurShopWeapons(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopWeaponsEntries, Dictionary<long, int> alignedEquipIds)
        {
            bool reloadRequired = ParamHelper.ReadFromBytes(out ParamStruct<EquipParamWeapon> paramStruct,
                                                     EquipParamWeapon.spOffset,
                                                     (ps) => ps.ParamEntries.Last().id >= WEAPON_ALIGNED_BASE);
            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of EquipParamWeapon (shop stubs)");
                return false;
            }

            // Copy a real weapon row as template (first entry)
            byte[] templateBytes = new byte[EquipParamWeapon.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, templateBytes, 0, templateBytes.Length);

            // Default fallback icon: Dagger (1) at +0xBA (confirmed via Paramdex XML)
            byte[] defaultIconBytes = BitConverter.GetBytes((short)1);
            templateBytes[0xBA] = defaultIconBytes[0];
            templateBytes[0xBB] = defaultIconBytes[1];

            foreach (var ourShopWeaponEntry in ourShopWeaponsEntries)
            {
                byte[] parambytes;

                // Copy the FULL real weapon's param row so DSR has correct equipModelId,
                // weaponCategory, equipModelCategory, iconId, and all other rendering fields.
                if (App.AllItemsByApId.TryGetValue((int)ourShopWeaponEntry.Value.Item1.ItemId, out var dsrItem))
                {
                    var realEquipId = dsrItem.Id;
                    var realEntry = paramStruct.ParamEntries.FirstOrDefault(e => e.id == (uint)realEquipId);
                    if (realEntry.id == (uint)realEquipId)
                    {
                        parambytes = new byte[EquipParamWeapon.Size];
                        Array.Copy(paramStruct.ParamBytes, (int)realEntry.paramOffset, parambytes, 0, (int)EquipParamWeapon.Size);
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

                byte[] stringbytes = Encoding.ASCII.GetBytes($"{ourShopWeaponEntry.Value}\0");
                uint paramRowId = alignedEquipIds.TryGetValue(ourShopWeaponEntry.Key, out int wAid) ? (uint)wAid : (uint)ourShopWeaponEntry.Key;
                paramStruct.AddParam(paramRowId, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {ourShopWeaponsEntries.Count} weapon shop stubs to EquipParamWeapon");
            
            ParamHelper.WriteFromParamSt(paramStruct, EquipParamWeapon.spOffset);

            return true;
        }

        private static bool upgradeOurShopProtectors(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopProtectorsEntries, Dictionary<long, int> alignedEquipIds)
        {
            bool reloadRequired = ParamHelper.ReadFromBytes(out ParamStruct<EquipParamProtector> paramStruct,
                                                     EquipParamProtector.spOffset,
                                                     (ps) => ps.ParamEntries.Last().id >= PROTECTOR_ALIGNED_BASE);
            if (!reloadRequired)
            {
                Log.Logger.Debug("Skipping reload of EquipParamProtector (shop stubs)");
                return false;
            }

            // Copy a real protector row as template (first entry)
            byte[] templateBytes = new byte[EquipParamProtector.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, templateBytes, 0, templateBytes.Length);

            // Default fallback icon: Leather Armor (1052) at +0xA2/+0xA4 (confirmed via Paramdex XML)
            byte[] defaultIconBytes = BitConverter.GetBytes((short)1052);
            templateBytes[0xA2] = defaultIconBytes[0];
            templateBytes[0xA3] = defaultIconBytes[1];
            templateBytes[0xA4] = defaultIconBytes[0];
            templateBytes[0xA5] = defaultIconBytes[1];

            foreach (var ourShopProtectorEntry in ourShopProtectorsEntries)
            {
                byte[] parambytes;

                // Copy the FULL real armor's param row so DSR has correct equipModelId,
                // protectorCategory, iconId, and all other rendering fields.
                if (App.AllItemsByApId.TryGetValue((int)ourShopProtectorEntry.Value.Item1.ItemId, out var dsrItem))
                {
                    var realEquipId = dsrItem.Id;
                    var realEntry = paramStruct.ParamEntries.FirstOrDefault(e => e.id == (uint)realEquipId);
                    if (realEntry.id == (uint)realEquipId)
                    {
                        parambytes = new byte[EquipParamProtector.Size];
                        Array.Copy(paramStruct.ParamBytes, (int)realEntry.paramOffset, parambytes, 0, (int)EquipParamProtector.Size);
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
                
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{ourShopProtectorEntry.Value.Item2}\0");
                uint paramRowId = alignedEquipIds.TryGetValue(ourShopProtectorEntry.Key, out int pAid) ? (uint)pAid : (uint)ourShopProtectorEntry.Key;
                paramStruct.AddParam(paramRowId, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {ourShopProtectorsEntries.Count} protector shop stubs to EquipParamProtector");

            ParamHelper.WriteFromParamSt(paramStruct, EquipParamProtector.spOffset);
            
            return true;
        }

        private static bool upgradeOurShopAccessories(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopAccessoriesEntries)
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

            foreach (var ourShopAccessoriesEntry in ourShopAccessoriesEntries)
            {
                byte[] parambytes = (byte[])templateBytes.Clone();

                // Copy the FULL real ring's param row for consistent rendering fields.
                if (App.AllItemsByApId.TryGetValue((int)ourShopAccessoriesEntry.Value.Item1.ItemId, out var dsrItem))
                {
                    var realEquipId = dsrItem.Id;
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

                byte[] stringbytes = Encoding.ASCII.GetBytes($"{ourShopAccessoriesEntry.Value.Item2}\0");
                paramStruct.AddParam((uint)ourShopAccessoriesEntry.Key, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {ourShopAccessoriesEntries.Count} accessory shop stubs to EquipParamAccessory");

            ParamHelper.WriteFromParamSt(paramStruct, EquipParamAccessory.spOffset);
            
            return true;
        }

        private static bool upgradeAPShopGoods(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> apShopGoodsEntries, ParamStruct<EquipParamGoods> paramStruct)
        {
            // if we are here, we are updating the params.
            ushort new_entries = (ushort)apShopGoodsEntries.Count();


            uint goods_param_size = 0x5c;

            // Get first entry's Param (e.g. White Sign Soapstone), use it as basis for new params.
            byte[] parambytes = new byte[EquipParamGoods.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, parambytes, 0, parambytes.Length);

            parambytes[0x36] = 99; // max num
            parambytes[0x3a] = 1; // goods type = key
            parambytes[0x3b] = 0; // ref category = like key
            parambytes[0x3e] = 0; // use animation = 0
                                  // Is Only One?
                                  // Is Deposit?

            // For each new item, "Add Item" to ParamSt
            for (uint i = 0; i < new_entries; i++)
            {                
                var entry = apShopGoodsEntries.ToArray()[i];
                uint newid = (uint)entry.Key;
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value.Item2}\0");

                // set sort bytes in param based on id - not sure if this is grabbing top or bottom 2 bytes!! But filling all 4 put the items at the top instead.
                byte[] idbytes = BitConverter.GetBytes(newid);
                parambytes[0x1c] = idbytes[0]; // sort byte 0
                parambytes[0x1d] = idbytes[1]; // sort byte 1
                //parambytes[0x1e] = idbytes[2];
                //parambytes[0x1f] = idbytes[3];
                byte[] iconbytes = BitConverter.GetBytes((short)2042);
                parambytes[0x2c] = iconbytes[0]; // icon byte 0
                parambytes[0x2d] = iconbytes[1]; // icon byte 1
                parambytes[0x45] |= (byte)(0x30); // turn on isDrop and isDeposit bits
                // This will add the item to the array, and append its string to the NewString buffer

                paramStruct.AddParam(newid, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {new_entries} items to EquipParamGoods from {apShopGoodsEntries.First().Key} to {apShopGoodsEntries.Last().Key}");

            paramStruct.ParamEntries = paramStruct.ParamEntries.OrderBy(pe => pe.id).ToList();

            return true;
        }


        private static bool upgradeGroundGoods(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> addedGroundEntries, ParamStruct<EquipParamGoods> paramStruct)
        {
            // if we are here, we are updating the params.
            ushort new_entries = (ushort)addedGroundEntries.Count();

            uint goods_param_size = 0x5c;

            // Get first entry's Param (e.g. White Sign Soapstone), use it as basis for new params.
            byte[] parambytes = new byte[EquipParamGoods.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, parambytes, 0, parambytes.Length);

            parambytes[0x36] = 99; // max num
            parambytes[0x3a] = 1; // goods type = key
            parambytes[0x3b] = 0; // ref category = like key
            parambytes[0x3e] = 0; // use animation = 0
                                  // Is Only One?
                                  // Is Deposit?

            // For each new item, "Add Item" to ParamSt
            for (uint i = 0; i < new_entries; i++)
            {
                var entry = addedGroundEntries.ToArray()[i];
                uint newid = (uint)entry.Key;
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value.Item2}\0");
                // set sort bytes in param based on id - not sure if this is grabbing top or bottom 2 bytes!! But filling all 4 put the items at the top instead.
                byte[] idbytes = BitConverter.GetBytes(newid);
                parambytes[0x1c] = idbytes[0]; // sort byte 0
                parambytes[0x1d] = idbytes[1]; // sort byte 1
                //parambytes[0x1e] = idbytes[2];
                //parambytes[0x1f] = idbytes[3];
                byte[] iconbytes = BitConverter.GetBytes((short)2042);
                parambytes[0x2c] = iconbytes[0]; // icon byte 0
                parambytes[0x2d] = iconbytes[1]; // icon byte 1
                parambytes[0x45] |= (byte)(0x30); // turn on isDrop and isDeposit bits
                // This will add the item to the array, and append its string to the NewString buffer
                paramStruct.AddParam(newid, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {new_entries} items to EquipParamGoods from {addedGroundEntries.First().Key} to {addedGroundEntries.Last().Key}");

            paramStruct.ParamEntries = paramStruct.ParamEntries.OrderBy(pe => pe.id).ToList();

            return true;
        }

        private static bool upgradeOurShopGoods(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopGoodsEntries, ParamStruct<EquipParamGoods> paramStruct)
        {
            // if we are here, we are updating the params.
            ushort new_entries = (ushort)ourShopGoodsEntries.Count();

            uint goods_param_size = 0x5c;

            // Get first entry's Param (e.g. White Sign Soapstone), use it as basis for new params.
            byte[] parambytes = new byte[EquipParamGoods.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, parambytes, 0, parambytes.Length);

            parambytes[0x36] = 99; // max num
            parambytes[0x3b] = 0; // ref category
            parambytes[0x3e] = 0; // use animation = 0
                                  // Is Only One?
                                  // Is Deposit?

            var vanillaGoodsType = new Dictionary<uint, byte>();
            var vanillaIcon = new Dictionary<uint, short>();

            foreach (var ve in paramStruct.ParamEntries)
            {
                if (ve.id > 10000)
                    continue;
                vanillaGoodsType[ve.id] = paramStruct.ParamBytes[(int)ve.paramOffset + 0x3A];
                vanillaIcon[ve.id] = BitConverter.ToInt16(paramStruct.ParamBytes, (int)ve.paramOffset + 0x2C);
            }

            // For each new item, "Add Item" to ParamSt
            for (uint i = 0; i < new_entries; i++)
            {
                var entry = ourShopGoodsEntries.ToArray()[i];
                uint newid = (uint)entry.Key;
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value.Item2}\0");
                // set sort bytes in param based on id - not sure if this is grabbing top or bottom 2 bytes!! But filling all 4 put the items at the top instead.
                byte[] idbytes = BitConverter.GetBytes(newid);
                parambytes[0x1c] = idbytes[0]; // sort byte 0
                parambytes[0x1d] = idbytes[1]; // sort byte 1
                //parambytes[0x1e] = idbytes[2];
                //parambytes[0x1f] = idbytes[3];
                parambytes[0x45] |= (byte)(0x30); // turn on isDrop and isDeposit bits

                byte goodsType = 1;
                short icon = 2042; // Prism Stone fallback
                if (ourShopGoodsEntries.TryGetValue((long)newid, out var scoutedData) &&
                    App.AllItemsByApId.TryGetValue((int)scoutedData.Item1.ItemId, out var dsrItem))
                {
                    if (vanillaGoodsType.TryGetValue((uint)dsrItem.Id, out byte vanillaType))
                        goodsType = vanillaType;
                    if (vanillaIcon.TryGetValue((uint)dsrItem.Id, out short vanillaIconVal))
                        icon = vanillaIconVal;
                }

                parambytes[0x3a] = goodsType;
                byte[] iconbytes = BitConverter.GetBytes(icon);
                parambytes[0x2c] = iconbytes[0];
                parambytes[0x2d] = iconbytes[1];

                // This will add the item to the array, and append its string to the NewString buffer
                paramStruct.AddParam(newid, parambytes, stringbytes);
            }

            Log.Logger.Information($"Added {new_entries} items to EquipParamGoods from {ourShopGoodsEntries.First().Key} to {ourShopGoodsEntries.Last().Key}");

            paramStruct.ParamEntries = paramStruct.ParamEntries.OrderBy(pe => pe.id).ToList();

            return true;
        }

         private static bool upgradeOurShopSpells(Dictionary<long, Tuple<ScoutedItemInfo, string, string>> ourShopSpellsEntries, ParamStruct<EquipParamGoods> paramStruct)
        {
            // if we are here, we are updating the params.
            ushort new_entries = (ushort)ourShopSpellsEntries.Count();

            uint goods_param_size = 0x5c;

            // Get first entry's Param (e.g. White Sign Soapstone), use it as basis for new params.
            byte[] parambytes = new byte[EquipParamGoods.Size];
            Array.Copy(paramStruct.ParamBytes, paramStruct.ParamEntries[0].paramOffset, parambytes, 0, parambytes.Length);

            parambytes[0x36] = 99; // max num
            parambytes[0x3b] = 0; // ref category
            parambytes[0x3e] = 0; // use animation = 0
                                  // Is Only One?
                                  // Is Deposit?

            var vanillaGoodsType = new Dictionary<uint, byte>();
            var vanillaIcon = new Dictionary<uint, short>();
            var vanillaMagicId = new Dictionary<uint, int>();
            foreach (var ve in paramStruct.ParamEntries)
            {
                if (ve.id > 10000)
                    continue;
                vanillaGoodsType[ve.id] = paramStruct.ParamBytes[(int)ve.paramOffset + 0x3A];
                vanillaIcon[ve.id] = BitConverter.ToInt16(paramStruct.ParamBytes, (int)ve.paramOffset + 0x2C);
                vanillaMagicId[ve.id] = BitConverter.ToInt32(paramStruct.ParamBytes, (int)ve.paramOffset + 0x28);
            }

            // For each new item, "Add Item" to ParamSt
            for (uint i = 0; i < new_entries; i++)
            {
                var entry = ourShopSpellsEntries.ToArray()[i];
                uint newid = (uint)entry.Key;
                byte[] stringbytes = Encoding.ASCII.GetBytes($"{entry.Value.Item2}\0");
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
                if (ourShopSpellsEntries.TryGetValue((long)newid, out var scoutedData) &&
                    App.AllItemsByApId.TryGetValue((int)scoutedData.Item1.ItemId, out var dsrItem))
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

            Log.Logger.Information($"Added {new_entries} items to EquipParamGoods from {ourShopSpellsEntries.First().Key} to {ourShopSpellsEntries.Last().Key}");

            paramStruct.ParamEntries = paramStruct.ParamEntries.OrderBy(pe => pe.id).ToList();

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
            {
                // If an entry with this ID already exists (e.g. vanilla spell FMG), update it
                // in place so the override takes effect. Otherwise add a new entry.
                if (msgManStruct.MsgEntries.Any(e => e.id == (uint)input.Key))
                    msgManStruct.UpdateMsg((uint)input.Key, input.Value);
                else
                    msgManStruct.AddMsg((uint)input.Key, input.Value);
            }


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
