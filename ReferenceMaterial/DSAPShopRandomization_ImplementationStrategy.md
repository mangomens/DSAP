# DSAP Shop Randomization: Implementation Strategy

> **Historical document (2026-03-30).** This was the pre-implementation design plan. Shop Sanity is now fully implemented and working. Key differences from this plan:
> - **Native item passthrough** replaces the pure-wrapper approach for items destined for the local player. Real DSR `equipId`/`equipType` values are used instead of always forcing `equipType=3` (Goods).
> - **Purchase flag convention changed**: flags use spaced formula `71810000 + (i/32)*1000 + (i%32)*32` (32 tails apart) instead of dense `71810000 + rowId`. Dense packing caused flag corruption due to DSR's byte-level overwrite behavior (see SOT §6.3).
> - **Double-grant prevention** was added: `NativeShopLocationIds` HashSet + skip logic in `Client_ItemReceived`.
> - **Andre entry count** is 21, not 22.
> - See `ShopRandomization_ImplementationStatus.md` for current status.

This document describes a complete implementation strategy for adding shop item randomization to DSAP. It is based on direct analysis of the DSAP codebase, the Archipelago framework, the prior shop development attempt (visible in `alldumps_shops_only.txt`), and the existing ItemLot randomization system as a proven model.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [System Architecture Overview](#2-system-architecture-overview)
3. [Prior Work Analysis](#3-prior-work-analysis)
4. [The Wrapper Approach — Detailed Design](#4-the-wrapper-approach--detailed-design)
5. [Phase 1: Core Shop Randomization](#5-phase-1-core-shop-randomization)
6. [Phase 2: Visual Enhancement (Future)](#6-phase-2-visual-enhancement-future)
7. [Python Apworld Changes](#7-python-apworld-changes)
8. [C# Client Changes](#8-c-client-changes)
9. [Data Files — New JSON Resources](#9-data-files--new-json-resources)
10. [Purchase Detection & Event Flag Strategy](#10-purchase-detection--event-flag-strategy)
11. [Integration with Existing Systems](#11-integration-with-existing-systems)
12. [Edge Cases & Risk Mitigation](#12-edge-cases--risk-mitigation)
13. [Implementation Order & Dependencies](#13-implementation-order--dependencies)
14. [Testing Strategy](#14-testing-strategy)
15. [Appendix: Complete Shop Location Table](#15-appendix-complete-shop-location-table)

---

## 1. Executive Summary

### Goal
Make every (or most) merchant shop item in DSR an Archipelago location. When a player "buys" a shop item, it triggers an AP check instead of giving the vanilla item. The actual item placed at that location is determined by the multiworld randomizer and delivered through the normal AP item-receive pipeline.

### Core Insight
DSAP already creates EquipParamGoods "stub" entries for every scouted AP location (via `ApItemInjectorHelper.AddAPItems()`), complete with names, icons, and hooks that block inventory addition. Shop randomization can **reuse this entire infrastructure** by simply rewriting ShopLineupParam rows to point at these existing stub entries.

### Key Principle
The "wrapper" is not a new system — it's the **existing AP item stub system applied to shops**. Each shop slot gets its `equipId` overwritten to an AP location ID that already has a corresponding EquipParamGoods entry, MsgMan strings, and item-add hook coverage.

---

## 2. System Architecture Overview

```
┌─ GENERATION (Python apworld) ─────────────────────────────────┐
│                                                                 │
│  Options.py: shop_sanity toggle                                │
│  Locations.py: ~87+ SHOP_ITEM locations (all merchants)        │
│  __init__.py: enable SHOP_ITEM category when option is on      │
│  fill_slot_data(): include shop_sanity flag for client          │
│                                                                 │
└─────────────────── slot_data + scouted items ──────────────────┘
                              │
                              ▼
┌─ CLIENT (C# Desktop) ─────────────────────────────────────────┐
│                                                                 │
│  OnConnectedAsync():                                           │
│    1. ScoutLocations (already includes shops if enabled)        │
│    2. AddAPItems (already creates EquipParamGoods for all)      │
│    3. BuildLotParamIdToLotMap (existing ItemLot system)         │
│    4. NEW: BuildShopReplacementMap (shop → AP item mapping)     │
│    5. NEW: OverwriteShopLineupParams (rewrite shop rows)       │
│    6. UpdateItemLots (existing)                                 │
│    7. NEW: Monitor shop purchase flags                          │
│                                                                 │
│  ShopLineupParam rows (in DSR memory):                         │
│    Before: equipId=2002, equipType=3, eventFlag=11017140       │
│    After:  equipId=11110822, equipType=3, eventFlag=71811401   │
│                                                                 │
│  Purchase flow:                                                 │
│    Player buys → DSR sets eventFlag → client detects →          │
│    CompleteLocationChecks → AP routes item to recipient         │
│                                                                 │
│  Item hooks (existing):                                        │
│    0x1407479E0 blocks inventory add for AP item IDs             │
│    0x140728c90 blocks popup for AP item IDs                     │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. Prior Work Analysis

The `alldumps_shops_only.txt` reveals a prior shop randomization attempt. This is valuable evidence:

### What the prior attempt did
1. **Overwrote ShopLineupParam rows** — rows 1100-1134 (Undead Merchant Male) and 1200-1220 (Female Merchant) had their `equipId` changed to AP location IDs (11110xxx, 11112xxx)
2. **Assigned custom event flags** — used the `71811xxx` range for purchase tracking (avoiding collision with vanilla flags)
3. **Defined ~87 AP shop locations** covering: Undead Merchant Male (33), Female Merchant (21), Ingward (2), Andre (21), Oswald (8), plus the original 7 key-item locations
4. **Built a diagnostic system** (ShopFlagDiag) that validates flag→address resolution

### What the prior attempt proves
- **ShopLineupParam modification works** — DSR didn't crash with modified shop rows
- **Custom event flags (71811xxx) work** — they resolve to valid bit addresses via `GetEventFlagOffset()`
- **equipType=3 (Goods) works for AP items** — the overwritten rows all used equipType=3, pointing at EquipParamGoods entries
- **sellQty=1 with custom eventFlag works** — DSR tracks purchases using the custom flag

### What the prior attempt's row assignment scheme was
| Merchant | Shop Row Range | AP Location Range | Notes |
|----------|---------------|-------------------|-------|
| Undead Merchant (Male) | 1100-1134 | 11110900-11110934 | 33 items |
| Female Merchant | 1200-1220 | 11112900-11112920 | 21 items |
| Ingward | 2400-2401 | 11113000-11113001 | 2 items |
| Andre of Astora | 1400-1420 | 11113900-11113920 | 21 items |
| Oswald of Carim | 4400-4408 | 11114900-11114908 | 8 items |
| André key items | 1401-1404 | 11110822-11110827 | Existing 7 |
| UM key items | 1105, 1133, 1106 | 11110042, 823, 824 | Existing 7 |

### Purchase flag convention

**Original plan (SUPERSEDED):** `purchaseFlag = 71810000 + shopRow` — dense packing. This caused flag corruption because DSR overwrites entire bytes rather than individual bits. Two flags sharing the same byte corrupt each other.

**Actual implementation:** `purchaseFlag = 71810000 + (i/32)*1000 + (i%32)*32` — each flag is spaced 32 tails apart, ensuring every flag occupies its own 4-byte word. The full set of 86 flags is pre-computed in `ShopFlags.json`.

The 7181xxxx prefix is collision-free with vanilla flags (which use prefixes like 1101xxxx, 1130xxxx, etc.).

---

## 4. The Wrapper Approach — Detailed Design

### 4.1 What is a "Wrapper"?

A wrapper is an AP item stub that DSR's shop UI renders as if it were a normal purchasable item. It has:
- An `equipId` pointing to a valid EquipParamGoods entry (the AP stub)
- `equipType = 3` (Goods), so DSR looks it up in EquipParamGoods
- A name/description from MsgMan showing what AP item is there
- A price (configurable: 0 for free, vanilla price, or custom)
- `sellQuantity = 1` (one-time purchase = one AP check)
- A custom `eventFlag` for purchase detection

### 4.2 Why Goods-type wrappers work for everything

The existing `ApItemInjectorHelper.AddAPItems()` already creates EquipParamGoods entries for **every scouted location**. These entries have:
- `goodsType = 1` (key item) — appears in the correct inventory section
- `maxNum = 99` — won't fail stack checks
- Icon = 2042 (generic key item icon)
- Full MsgMan entries: name = "Player's ItemName", caption/description with progression/useful/trap/filler classification

Since all AP locations already have EquipParamGoods stubs, we simply point shop rows at them. The shop UI will:
1. Read `equipType=3` → look up EquipParamGoods with ID = `equipId`  
2. Find the AP stub entry → display its name ("Player1's Claymore") and icon
3. Player purchases → DSR's normal purchase flow fires
4. Item-add hook (0x1407479E0) detects AP-range ID → blocks inventory addition
5. DSR sets the eventFlag → client detects → AP check fires

### 4.3 What the player sees

In the shop menu, instead of "Longsword — 1000 souls", they see:
```
Player1's Claymore — 0 souls     (if free pricing)
Player1's Claymore — 1000 souls  (if vanilla pricing)
```

With classification info in the description:
```
"A Progression Archipelago item for Player1's Dark Souls Remastered."
```

After purchasing, the item disappears from the shop (sellQty=1 exhausted).

### 4.4 Handling the existing 7 SHOP_ITEM locations

The current 7 locations (Residence Key, Bottomless Box ×2, Repairbox, Weapon Smithbox, Armor Smithbox, Crest of Artorias) have **different AP IDs** than what the prior attempt used for the same merchant rows:

| Current AP ID | Current Name | Prior Attempt Row | Prior Attempt AP ID |
|--------------|-------------|-------------------|-------------------|
| 11110042 | UB: Residence Key | 1105 | 11110042 (same) |
| 11110823 | UB: Bottomless Box | 1133 | 11110823 (same) |
| 11110824 | UB: Repairbox | 1106 | 11110824 (same) |
| 11110825 | UP: Andre - Bottomless Box | 1501 | (different ID scheme) |
| 11110826 | UP: Andre - Weapon Smithbox | 1402 | 11113902 |
| 11110827 | UP: Andre - Armor Smithbox | 1403 | 11113903 |
| 11110822 | UP: Andre - Crest of Artorias | 1401 | 11113901 |

**Decision needed**: Keep the existing 7 AP IDs (preserving backward compatibility) or adopt the prior attempt's unified scheme. 

**Recommendation**: Keep existing 7 AP IDs for backward compatibility with existing seeds/saves. Add new shop locations using the prior attempt's convention for the remaining ~80 items. The JSON mapping file handles the translation from AP ID → ShopLineupParam row + event flag.

---

## 5. Phase 1: Core Shop Randomization

This phase delivers a working shop randomization system using Goods-type wrappers for all shop items.

### 5.1 Scope

| Component | Change |
|-----------|--------|
| Python: Options.py | Add `shop_sanity` toggle |
| Python: Locations.py | Add ~80 new SHOP_ITEM locations |
| Python: __init__.py | Enable SHOP_ITEM when option is on |
| C#: New ShopLineupParam.cs | IParam definition (Size=0x20, spOffset=0x720) |
| C#: New ShopHelper.cs | Read/overwrite shop params, build replacement map |
| C#: New ShopFlags.json | Resource mapping AP location → shop row + flag |
| C#: App.axaml.cs | Call ShopHelper in OnConnectedAsync, register shop location monitors |
| C#: LocationHelper.cs | Add GetShopFlagLocations() |
| C#: DarkSoulsOptions.cs | Parse shop_sanity from slot_data |

### 5.2 Player Experience

1. Player enables `shop_sanity` in their YAML options
2. During generation, shop items enter the multiworld pool
3. After connecting, the C# client overwrites shop param rows in memory
4. Player talks to a merchant → sees AP item names/descriptions in shop
5. Player purchases an item → AP check fires → item routed to recipient
6. Player receives items through normal AP flow (item-receive handler)

### 5.3 Pricing Strategy

Options for shop item pricing (configurable via option or hardcoded):

| Strategy | Description | Recommendation |
|----------|-------------|----------------|
| Free (value=0) | All AP checks are free | Simple, friendly |
| Vanilla price | Keep original item's price | Thematic but some items are very expensive |
| Flat rate | All AP checks cost the same (e.g., 1000 souls) | Fair, consistent |
| Classification-based | Progressive=expensive, filler=cheap | Interesting but reveals item importance |

**Recommendation for Phase 1**: Use value=0 (free). This avoids progression issues where a player can't afford a check. A pricing option can be added later.

---

## 6. Phase 2: Visual Enhancement (Future)

Phase 2 would add type-specific visual wrappers so shop items show appropriate icons:

| AP Item Type | Wrapper Strategy | Icon Source |
|-------------|-----------------|------------|
| DSR Weapon (own player) | Copy icon from EquipParamWeapon row | Real weapon icon |
| DSR Armor (own player) | Copy icon from EquipParamProtector row | Real armor icon |
| DSR Ring (own player) | Copy icon from EquipParamAccessory row | Real ring icon |
| DSR Goods (own player) | Copy icon from EquipParamGoods row | Real goods icon |
| Other player's item | Use generic AP icon (2042) | Key item icon |
| Trap | Use trap-specific icon | TBD |
| Progression | Use progression icon marker | TBD |

This requires:
- Reading multiple param tables to get icon IDs
- Setting per-item icons in the EquipParamGoods stubs
- Potentially different `equipType` values per shop row (complex)

**Not recommended for Phase 1** because:
- The simpler Goods-wrapper approach already delivers full functionality
- Type-specific wrappers require resolving cross-param-table icon compatibility
- The existing AP item stubs (all EquipParamGoods) are already proven to work in shops

---

## 7. Python Apworld Changes

### 7.1 Options.py — Add shop_sanity option

```python
class ShopSanity(Toggle):
    """
    Randomizes items sold by merchants. Each shop item becomes an AP location.
    """
    display_name = "Shop Sanity"
    default = False
```

Add to `DSROption` dataclass:
```python
shop_sanity: ShopSanity
```

### 7.2 Locations.py — Add new SHOP_ITEM locations

Add ~80 new `DSRLocationData` entries across appropriate regions. Use the naming convention from the prior attempt's ShopFlagDiag:

```python
# Undead Merchant (Male) — Upper Undead Burg
DSRLocationData(11110900, f"UB: Orange Guidance Soapstone", f"Orange Guidance Soapstone", DSRLocationCategory.SHOP_ITEM),
DSRLocationData(11110901, f"UB: Repair Powder", f"Repair Powder", DSRLocationCategory.SHOP_ITEM),
DSRLocationData(11110902, f"UB: Throwing Knife", f"Throwing Knife", DSRLocationCategory.SHOP_ITEM),
# ... ~30 more entries for Undead Merchant

# Female Undead Merchant — Lower Burg
DSRLocationData(11112900, f"LB: Female Merchant - Bloodred Moss Clump", f"Bloodred Moss Clump", DSRLocationCategory.SHOP_ITEM),
# ... ~20 more entries

# Ingward — New Londo Ruins
DSRLocationData(11113000, f"NL: Ingward - Resist Curse", f"Resist Curse", DSRLocationCategory.SHOP_ITEM),
DSRLocationData(11113001, f"NL: Ingward - Transient Curse", f"Transient Curse", DSRLocationCategory.SHOP_ITEM),

# Andre of Astora — Undead Parish
DSRLocationData(11113900, f"UP: Andre - Titanite Shard", f"Titanite Shard", DSRLocationCategory.SHOP_ITEM),
# ... ~20 more entries

# Oswald of Carim — Undead Parish (Bell Gargoyles)
DSRLocationData(11114901, f"UP: Oswald - Purging Stone", f"Purging Stone", DSRLocationCategory.SHOP_ITEM),
# ... ~8 more entries
```

**Important decisions per merchant**:
- **Infinite-stock items** (sellQty=-1 in vanilla, like arrows, basic weapons): Should these become AP locations? 
  - **Recommendation**: Yes — each becomes a one-time-purchase AP check. After purchase, the item disappears (sellQty=1). This is fine because the actual gameplay item is delivered via AP, not the shop.
- **Missable merchants** (Patches, Shiva, etc.): Include but mark in logic that they require specific conditions.
- **DLC merchants** (Elizabeth, Chester, Gough): Include if DLC support is in scope.

### 7.3 __init__.py — Enable SHOP_ITEM category

In `generate_early()`:
```python
if self.options.shop_sanity.value == True:
    self.enabled_location_categories.add(DSRLocationCategory.SHOP_ITEM)
```

In `fill_slot_data()`, add to options dict:
```python
"shop_sanity": self.options.shop_sanity.value,
```

### 7.4 Items.py — Add default items for new locations

Ensure all vanilla shop items exist in the item pool. Most already do (consumables, weapons, armor, spells are all in `item_dictionary`). Any missing items (e.g., Repair Powder, Throwing Knife, basic arrows) need entries:

```python
# If not already present:
("Repair Powder", XXXX, DSRItemCategory.CONSUMABLE),
("Throwing Knife", XXXX, DSRItemCategory.CONSUMABLE),
# etc.
```

For items that exist but aren't in the randomization pool (vendor-only consumables), they become `default_item` for the location but may be classified as FILLER during pool construction.

### 7.5 Region/Logic Considerations

Shop locations need proper region placement for logic:
- **Always-available merchants**: Undead Merchant (Male) in Upper Undead Burg, Andre in Undead Parish
- **Story-gated merchants**: Ingward (New Londo), Oswald (after Gargoyles), Griggs (after Residence Key)
- **Missable merchants**: Patches (can be killed/angered), Shiva (Forest Hunter covenant), Big Hat Logan (rescue chain)
- **DLC merchants**: Elizabeth, Chester, Gough (DLC access required)

Each merchant's shop locations should be in the region where that merchant is accessible, with appropriate entrance rules.

---

## 8. C# Client Changes

### 8.1 New Model: ShopLineupParam.cs

```csharp
// Models/Params/ShopLineupParam.cs
namespace DSAP.Models.Params
{
    internal class ShopLineupParam : IParam
    {
        public static int Size => 0x20; // 32 bytes per row
        public static int spOffset => 0x720; // SoloParamMan offset
    }
}
```

### 8.2 New Model: ShopFlag.cs

```csharp
// Models/ShopFlag.cs
namespace DSAP.Models
{
    public class ShopFlag
    {
        public int Id { get; set; }          // AP Location ID (e.g., 11110900)
        public string Name { get; set; }     // "UB: Orange Guidance Soapstone"
        public int Row { get; set; }         // ShopLineupParam row ID (e.g., 1100)
        public int PurchaseFlag { get; set; } // Custom event flag (e.g., 71811100)
        public bool IsEnabled { get; set; }  // Whether this location is active
    }
}
```

### 8.3 New Helper: ShopHelper.cs

This is the core new file, modeled directly after `ItemLotHelper.cs`:

```csharp
// Helpers/ShopHelper.cs
namespace DSAP.Helpers
{
    internal class ShopHelper
    {
        /// <summary>
        /// Build a mapping of ShopLineupParam row IDs to the AP location IDs 
        /// that should replace them.
        /// </summary>
        public static void BuildShopReplacementMap(
            out Dictionary<int, ShopReplacement> resultMap,
            Dictionary<long, ScoutedItemInfo> scoutedLocationInfo)
        {
            var result = new Dictionary<int, ShopReplacement>();
            var shopFlags = LocationHelper.GetShopFlags()
                                         .Where(x => x.IsEnabled).ToList();
            
            foreach (var (locId, scoutedInfo) in scoutedLocationInfo)
            {
                var matchingShopFlags = shopFlags.Where(x => x.Id == (int)locId);
                foreach (var shop in matchingShopFlags)
                {
                    result[shop.Row] = new ShopReplacement
                    {
                        EquipId = (int)locId,      // Point at AP EquipParamGoods stub
                        EquipType = 3,              // Goods (all AP stubs are Goods)
                        Value = 0,                  // Free (Phase 1)
                        SellQuantity = 1,           // One-time purchase
                        EventFlag = shop.PurchaseFlag,
                        ShopType = 0                // Regular shop menu
                    };
                }
            }
            
            resultMap = result;
        }
        
        /// <summary>
        /// Overwrite ShopLineupParam rows in DSR memory with AP replacements.
        /// Modeled after ItemLotHelper.OverwriteItemLots().
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
                    
                    // value (s32 at +0x4)
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
                    
                    // equipType (u8 at +0x17)
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
            paramStruct.AddParam(99999998, dummyBytes, 
                Encoding.ASCII.GetBytes(""));
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
```

### 8.4 LocationHelper.cs — Add shop flag loading + location building

```csharp
// In LocationHelper.cs:

private static List<ShopFlag> CachedShopFlags = null;
public static List<ShopFlag> GetShopFlags()
{
    if (CachedShopFlags != null) return CachedShopFlags;
    string json = MiscHelper.OpenEmbeddedResource("DSAP.Resources.ShopFlags.json");
    CachedShopFlags = JsonSerializer.Deserialize<List<ShopFlag>>(json);
    return CachedShopFlags;
}

private static List<ILocation> CachedShopLocations = null;
public static List<ILocation> GetShopFlagLocations()
{
    if (CachedShopLocations != null) return CachedShopLocations;
    
    List<ILocation> locations = new List<ILocation>();
    var shopFlags = GetShopFlags().Where(x => x.IsEnabled);
    var baseAddress = AddressHelper.GetEventFlagsOffset();
    
    foreach (var shop in shopFlags)
    {
        var (offset, bit) = AddressHelper.GetEventFlagOffset(shop.PurchaseFlag);
        locations.Add(new Location
        {
            Name = shop.Name,
            Address = baseAddress + offset,
            AddressBit = bit,
            Id = shop.Id
        });
    }
    
    CachedShopLocations = locations;
    return locations;
}
```

### 8.5 App.axaml.cs — Integration into connection flow

In `OnConnectedAsync()`, after the existing `await ApItemInjectorHelper.AddAPItems(scoutedLocationInfo)`:

```csharp
// After AddAPItems and BuildLotParamIdToLotMap:

if (DSOptions.ShopSanity)
{
    ShopHelper.BuildShopReplacementMap(
        out ShopReplacementMap, scoutedLocationInfo);
    ShopHelper.UpdateShopLineupParams(ShopReplacementMap);
    Log.Logger.Information("Shop randomization applied");
    Client.AddOverlayMessage("Shop randomization applied");
}
```

In the location monitoring setup (where all location lists are combined):

```csharp
// Add shop locations to the monitoring list:
if (DSOptions.ShopSanity)
{
    var shopLocations = LocationHelper.GetShopFlagLocations();
    fullLocationsList.AddRange(shopLocations);
}
```

### 8.6 DarkSoulsOptions.cs — Parse shop_sanity

Add field:
```csharp
public bool ShopSanity { get; set; }
```

In constructor from slot_data:
```csharp
ShopSanity = options.GetValueOrDefault("shop_sanity", 0) == 1;
```

---

## 9. Data Files — New JSON Resources

### 9.1 ShopFlags.json

Create `DSAP/source/DSAP/Resources/ShopFlags.json` as an embedded resource:

```json
[
  {
    "Id": 11110042,
    "Name": "UB: Residence Key",
    "Row": 1105,
    "PurchaseFlag": 71811105,
    "IsEnabled": true
  },
  {
    "Id": 11110823,
    "Name": "UB: Bottomless Box",
    "Row": 1133,
    "PurchaseFlag": 71811133,
    "IsEnabled": true
  },
  {
    "Id": 11110900,
    "Name": "UB: Orange Guidance Soapstone",
    "Row": 1100,
    "PurchaseFlag": 71811100,
    "IsEnabled": true
  },
  ...
]
```

The full file will have one entry per shop check (see Appendix §15 for the complete table derived from `alldumps_shops_only.txt` ShopFlagDiag).

### 9.2 JSON convention

- `Id`: AP Location ID — matches `DSRLocationData.id` in Locations.py
- `Name`: Human-readable name — matches `DSRLocationData.name`
- `Row`: ShopLineupParam row ID — used to find and overwrite the correct param entry
- `PurchaseFlag`: Custom event flag — used for purchase detection polling
- `IsEnabled`: Always true for Phase 1; could support per-merchant toggles later

---

## 10. Purchase Detection & Event Flag Strategy

### 10.1 How purchase detection works

1. Each ShopLineupParam row has an `eventFlag` field (s32 at offset 0xC)
2. When DSR's shop code processes a purchase, it writes to the bit address encoded by that event flag
3. The client's polling loop (`MonitorLocationsAsync`) reads the same bit address
4. When the bit transitions from 0→1, `LocationCompleted` fires → `CompleteLocationChecks` → AP server

### 10.2 Event flag assignment

> **SUPERSEDED**: The dense convention `71810000 + shopRowId` below was the original plan. It was changed to a spaced formula `71810000 + (i/32)*1000 + (i%32)*32` because DSR overwrites entire bytes rather than individual bits, causing flag corruption when multiple flags share a byte. See §3 Purchase flag convention note and SOT §6.3 for details. The actual flags are pre-computed in `ShopFlags.json`.

**Original plan** (for reference — these flag values are NOT used):

| Row Range | Merchant | Example Flag |
|-----------|----------|-------------|
| 1100-1134 | Undead Merchant (Male) | 71811100-71811134 |
| 1200-1220 | Female Merchant | 71811200-71811220 |
| 1400-1420 | Andre of Astora | 71811400-71811420 |
| 1500-1584 | Domhnall of Zena | 71811500-71811584 |
| 1600-1617 | Trusty Patches | 71811600-71811617 |
| 1700-1709 | Shiva of the East | 71811700-71811709 |
| 2000-2021 | Griggs of Vinheim | 71812000-71812021 |
| 2100-2102 | Rickert / Big Hat Logan | 71812100-71812102 |
| 2200-2205 | Dusk of Oolacile | 71812200-71812205 |
| 2400-2401 | Ingward | 71812400-71812401 |
| 3000-3004 | Laurentius | 71813000-71813004 |
| 3200-3212 | Eingyi | 71813200-71813212 |
| 3400-3407 | Quelana of Izalith | 71813400-71813407 |
| 4000-4006 | Petrus of Thorolund | 71814000-71814006 |
| 4200-4209 | Rhea of Thorolund | 71814200-71814209 |
| 4400-4408 | Oswald of Carim | 71814400-71814408 |
| 5000-5099 | Big Hat Logan | 71815000-71815099 |
| 5300-5599 | Crestfallen Merchant | 71815300-71815599 |
| 6100-6199 | Darkstalker Kaathe | 71816100-71816199 |
| 6200-6299 | Giant Blacksmith | 71816200-71816299 |
| 6300-6399 | Vamos | 71816300-71816399 |
| 6400-6499 | Marvellous Chester | 71816400-71816499 |
| 6500-6599 | Elizabeth | 71816500-71816599 |
| 6600-6699 | Hawkeye Gough | 71816600-71816699 |

### 10.3 Verifying flag addresses don't collide

`GetEventFlagOffset(71811100)` parses as `ABCCCDDD` = `7-181-1100`:
- Primary offset from digit 7 → a fixed offset (large, upper region)
- Secondary offset from 181 → 181 × 16 = 2896
- Tertiary from 1100 → byte=(1100/32)×4 = 136, remainder=1100%32 → bit in [0,31]

Vanilla flags use first digits 1, 5 (e.g., `11017140`, `50000000`). The 7xxxxxxx range is reserved/unused in vanilla DSR, making 7181xxxx safe.

### 10.4 Already-purchased detection

On reconnect, the client must check which shop flags are already set (just as it does for item lot flags). The existing `MonitorLocationsAsync` handles this — it checks each location's current flag state and reports any that are already set as `LocationCompleted`.

---

## 11. Integration with Existing Systems

### 11.1 AP Item Injection (ApItemInjectorHelper)

**No changes needed.** `AddAPItems()` already creates EquipParamGoods stubs for ALL scouted locations: item lots, shops, boss drops, etc. When shop_sanity adds new scouted locations, they automatically get stubs.

The item-add hooks (min/max range check on item ID) will automatically cover shop AP IDs since shop location IDs (11110xxx-11115xxx) fall within the overall scouted location range.

### 11.2 ItemLot System (ItemLotHelper)

**No changes needed.** The ItemLot replacement system is independent. Shop items don't overlap with ItemLot param entries (they're in different param tables).

### 11.3 Item Receive (Client_ItemReceived)

**No changes needed.** When a player purchases a shop item and the AP check fires, the item-receive handler on the receiving end works exactly as it does for ItemLot checks. Items are identified by AP ID and delivered via `AddAbstractItem()`.

### 11.4 Save Validation

**No changes needed.** The seed hash and slot validation system is independent of which locations are enabled.

### 11.5 Location Completion Handler (Client_LocationCompleted)

**No changes needed.** The handler calls `CompleteLocationChecks` for any completed location, regardless of whether it came from an item lot, boss, fog wall, or shop purchase.

---

## 12. Edge Cases & Risk Mitigation

### 12.1 What if a player can't afford a shop check?

**Phase 1 mitigation**: Set value=0 (free). All AP checks are free to purchase.

**Future option**: Add a pricing option. If pricing is non-zero, ensure progression items are NEVER placed at expensive shop locations (logic constraint in the apworld).

### 12.2 What if a merchant is dead/unavailable?

**Problem**: Some merchants can be killed (Undead Merchant, Patches) or are conditional (Shiva requires Forest Hunter covenant).

**Mitigation**: Place death-sensitive merchant locations in appropriate regions with logic rules. If a merchant is optional/missable, their items might be excluded from the required progression pool.

**Recommendation**: Start Phase 1 with only "safe" merchants (Andre, Undead Merchant, Female Merchant, Ingward, Oswald) who are extremely difficult or impossible to lose. Add missable merchants in a later phase.

### 12.3 What if DSR's shop code doesn't set our custom event flags?

**Risk**: Low — the prior attempt's dump data shows event flags at resolved addresses with `flag=unset`, indicating the flag resolution system works. DSR's shop code simply writes to the address computed from the eventFlag field.

**Mitigation**: The very first thing to test is a single shop row with a custom event flag. Purchase it and verify the flag is set.

### 12.4 What about shop items that unlock conditionally?

Some vanilla shop items only appear after certain events (e.g., boss soul → boss armor at Domhnall). The ShopLineupParam `eventFlag` field is repurposed for AP tracking, which might conflict with the vanilla conditional-unlock behavior.

**Analysis**: The vanilla `eventFlag` field tracks *sold quantity*, not appearance condition. Conditional appearance is controlled by the talkESD scripts (the `OpenRegularShop(min, max)` call range), not by event flags in the param. So repurposing `eventFlag` for AP tracking is safe.

### 12.5 What about the sellQty → eventFlag relationship?

When `sellQuantity > 0`, DSR uses `eventFlag` to count how many have been sold. For `sellQuantity = 1`, the flag gets set once (1 byte value). For `sellQuantity = -1, eventFlag = -1`, nothing is tracked.

Our AP items use `sellQuantity = 1, eventFlag = custom`, which means:
- DSR writes to the custom flag address when the item is sold
- The value stored is the count (1)
- Our flag polling detects the byte is non-zero → location complete

This matches the vanilla finite-item behavior exactly.

### 12.6 Save file persistence of event flags

DSR event flags are saved in the character save file. This means:
- If a player buys a shop AP item, the flag persists across sessions
- On reconnect, the client will detect the flag as already set
- `MonitorLocationsAsync` will fire `LocationCompleted` for it
- The AP server sees `LocationChecks` with that ID (idempotent)

This is the **correct** behavior — same as how item lot pickups persist.

### 12.7 Hook range coverage

The item-add hook uses `CMP r9d, min` / `CMP r9d, max` where min/max span all scouted location IDs. Since shop locations are scouted alongside all other locations, the min/max will automatically include them. No hook changes needed.

### 12.8 Multiple shop rows for the same vanilla item

Some vanilla items appear at multiple merchants (e.g., Titanite Shard at Andre AND Crestfallen Merchant). Each row is a separate AP location with its own AP ID and purchase flag. This is fine — they're distinct checks.

### 12.9 Merchants with expanding inventories

Some merchants change their inventory mid-game (Griggs after Logan leaves, Logan moving from Firelink to Duke's Archives). The `OpenRegularShop(min, max)` range changes in their talkESD scripts. Our ShopLineupParam modifications don't affect talkESD — the NPC still calls the same range. The rows within that range just have different content now.

If a merchant has TWO ranges (e.g., Griggs 2000-2019 initially, 2000-2099 after Logan leaves), the additional rows (2020-2099) would need to be populated correctly. Rows that exist but are outside the initial range won't be shown until the talkESD expands the range. This is a detail to handle per-merchant in the JSON data.

---

## 13. Implementation Order & Dependencies

### Phase 1 Implementation Steps (in dependency order)

```
Step 1: Python — Options.py
  └── Add shop_sanity toggle
  └── No dependencies

Step 2: Python — Items.py  
  └── Ensure all shop-sold items have item_dictionary entries
  └── May need to add missing consumables/materials

Step 3: Python — Locations.py
  └── Add new SHOP_ITEM location entries for all target merchants
  └── Depends on: step 2 (default_item names must exist)

Step 4: Python — __init__.py
  └── Enable SHOP_ITEM in generate_early()
  └── Add shop_sanity to fill_slot_data()
  └── Depends on: steps 1, 3

Step 5: C# — ShopLineupParam.cs (new model)
  └── IParam definition (Size=0x20, spOffset=0x720)
  └── No dependencies

Step 6: C# — ShopFlag.cs (new model)
  └── Data model for JSON resource
  └── No dependencies

Step 7: C# — ShopFlags.json (new resource)
  └── Create JSON with all shop location→row→flag mappings
  └── Depends on: step 3 (AP IDs must match)

Step 8: C# — ShopHelper.cs (new helper)
  └── BuildShopReplacementMap + OverwriteShopParams + UpdateShopLineupParams
  └── Depends on: steps 5, 6, 7

Step 9: C# — LocationHelper.cs
  └── Add GetShopFlags() + GetShopFlagLocations()
  └── Depends on: steps 6, 7

Step 10: C# — DarkSoulsOptions.cs
  └── Parse shop_sanity from slot_data
  └── No code dependencies (but needs step 4 to test)

Step 11: C# — App.axaml.cs
  └── Call ShopHelper in OnConnectedAsync
  └── Add shop locations to monitoring list
  └── Depends on: steps 8, 9, 10

Step 12: Integration testing
  └── Depends on: all previous steps
```

### Suggested development order (minimize risk):

1. **Start with the C# side** (steps 5-6) — define models
2. **Create ShopFlags.json** (step 7) — start with just 5 entries from Andre (safe merchant)
3. **Build ShopHelper.cs** (step 8) — implement and test with those 5 entries
4. **Test in isolation** — verify shop param overwrite works, purchase flags work
5. **Expand JSON** — add remaining merchants one at a time
6. **Python side** (steps 1-4) — once C# is proven, add the world generation support
7. **Full integration test** (step 12)

---

## 14. Testing Strategy

### 14.1 Unit-level tests

| Test | What to verify |
|------|---------------|
| ShopLineupParam read | Can read the full ShopLineupParam table from DSR memory |
| Row count matches expected | Number of param entries matches vanilla expectation |
| Custom flag resolution | `GetEventFlagOffset(71811100)` returns valid (offset, bit) |
| Flag address doesn't collide | Computed addresses for all 7181xxxx flags don't overlap with vanilla flags |
| Param overwrite | After calling `WriteFromParamSt`, reading back shows correct values |

### 14.2 Integration tests

| Test | What to verify |
|------|---------------|
| Shop displays AP items | Talk to merchant → see AP item names in shop |
| Purchase triggers flag | Buy AP item → custom event flag is set |
| Client detects purchase | Flag set → `LocationCompleted` fires |
| AP check completes | `CompleteLocationChecks` sends to server |
| Item disappears after purchase | Buy AP item → it's gone from shop |
| Reconnect persistence | Disconnect → reconnect → previously purchased items still gone |
| Item receive works | Other player buys shop check → item is received correctly |
| Hook blocks inventory | AP item doesn't actually appear in player's inventory |

### 14.3 Minimal test scenario

1. Enable shop_sanity in YAML
2. Generate multiworld with 2 players
3. Connect both clients
4. Player 1 talks to Andre → sees AP items
5. Player 1 buys "Player2's Estus Flask" from Andre
6. Verify: Player 2 receives Estus Flask
7. Verify: Shop item disappears from Andre's inventory
8. Verify: Player 1 did NOT get Estus Flask in their inventory

---

## 15. Appendix: Complete Shop Location Table

Derived from `alldumps_shops_only.txt` ShopFlagDiag section. These 87 locations represent the full set from the prior attempt's mapping. Phase 1 can implement a subset (safe merchants first).

### Undead Merchant (Male) — Upper Undead Burg (33 locations)

| AP ID | Name | Row | PurchaseFlag |
|-------|------|-----|-------------|
| 11110042 | UB: Residence Key | 1105 | 71811105 |
| 11110823 | UB: Bottomless Box | 1133 | 71811133 |
| 11110824 | UB: Repairbox | 1106 | 71811106 |
| 11110900 | UB: Orange Guidance Soapstone | 1100 | 71811100 |
| 11110901 | UB: Repair Powder | 1101 | 71811101 |
| 11110902 | UB: Throwing Knife | 1102 | 71811102 |
| 11110903 | UB: Firebomb | 1103 | 71811103 |
| 11110904 | UB: Lloyd's Talisman | 1104 | 71811104 |
| 11110907 | UB: Dagger | 1107 | 71811107 |
| 11110908 | UB: Shortsword | 1108 | 71811108 |
| 11110910 | UB: Scimitar | 1110 | 71811110 |
| 11110911 | UB: Rapier | 1111 | 71811111 |
| 11110912 | UB: Hand Axe | 1112 | 71811112 |
| 11110913 | UB: Club | 1113 | 71811113 |
| 11110914 | UB: Reinforced Club | 1114 | 71811114 |
| 11110915 | UB: Spear | 1115 | 71811115 |
| 11110916 | UB: Short Bow | 1116 | 71811116 |
| 11110917 | UB: East-West Shield | 1117 | 71811117 |
| 11110918 | UB: Small Leather Shield | 1118 | 71811118 |
| 11110919 | UB: Buckler | 1119 | 71811119 |
| 11110920 | UB: Leather Shield | 1120 | 71811120 |
| 11110921 | UB: Heater Shield | 1121 | 71811121 |
| 11110922 | UB: Warrior's Round Shield | 1122 | 71811122 |
| 11110923 | UB: Standard Arrow | 1123 | 71811123 |
| 11110924 | UB: Large Arrow | 1124 | 71811124 |
| 11110925 | UB: Wooden Arrow | 1125 | 71811125 |
| 11110927 | UB: Heavy Bolt | 1127 | 71811127 |
| 11110928 | UB: Wood Bolt | 1128 | 71811128 |
| 11110929 | UB: Chain Helm | 1129 | 71811129 |
| 11110930 | UB: Chain Armor | 1130 | 71811130 |
| 11110931 | UB: Leather Gauntlets | 1131 | 71811131 |
| 11110932 | UB: Chain Leggings | 1132 | 71811132 |
| 11110934 | UB: Dried Finger | 1134 | 71811134 |

### Female Undead Merchant — Lower Burg (21 locations)

| AP ID | Name | Row | PurchaseFlag |
|-------|------|-----|-------------|
| 11112900 | LB: Female Merchant - Bloodred Moss Clump | 1200 | 71811200 |
| 11112901 | LB: Female Merchant - Purple Moss Clump | 1201 | 71811201 |
| 11112902 | LB: Female Merchant - Blooming Purple Moss Clump | 1202 | 71811202 |
| 11112903 | LB: Female Merchant - Poison Throwing Knife | 1203 | 71811203 |
| 11112904 | LB: Female Merchant - Dung Pie | 1204 | 71811204 |
| 11112905 | LB: Female Merchant - Alluring Skull | 1205 | 71811205 |
| 11112906 | LB: Female Merchant - Charcoal Pine Resin | 1206 | 71811206 |
| 11112907 | LB: Female Merchant - Transient Curse | 1207 | 71811207 |
| 11112908 | LB: Female Merchant - Rotten Pine Resin | 1208 | 71811208 |
| 11112909 | LB: Female Merchant - Homeward Bone | 1209 | 71811209 |
| 11112910 | LB: Female Merchant - Prism Stone | 1210 | 71811210 |
| 11112911 | LB: Female Merchant - Humanity | 1211 | 71811211 |
| 11112912 | LB: Female Merchant - Fire Arrow | 1212 | 71811212 |
| 11112913 | LB: Female Merchant - Poison Arrow | 1213 | 71811213 |
| 11112914 | LB: Female Merchant - Purging Stone | 1214 | 71811214 |
| 11112915 | LB: Female Merchant - Standard Arrow | 1215 | 71811215 |
| 11112917 | LB: Female Merchant - Wooden Arrow | 1217 | 71811217 |
| 11112918 | LB: Female Merchant - Standard Bolt | 1218 | 71811218 |
| 11112919 | LB: Female Merchant - Heavy Bolt | 1219 | 71811219 |
| 11112920 | LB: Female Merchant - Wood Bolt | 1220 | 71811220 |

*(Note: AP ID 11112916 / Row 1216 is missing from the prior data — likely "Large Arrow" - verify)*

### Ingward — New Londo Ruins (2 locations)

| AP ID | Name | Row | PurchaseFlag |
|-------|------|-----|-------------|
| 11113000 | NL: Ingward - Resist Curse | 2400 | 71812400 (uses 11607000 in vanilla) |
| 11113001 | NL: Ingward - Transient Curse | 2401 | 71812401 |

### Andre of Astora — Undead Parish (21 locations)

| AP ID | Name | Row | PurchaseFlag |
|-------|------|-----|-------------|
| 11110822 | UP: Andre - Crest of Artorias | 1401 | 71811401 |
| 11110825 | UP: Andre - Bottomless Box* | 1501* | TBD |
| 11110826 | UP: Andre - Weapon Smithbox | 1402 | 71811402 |
| 11110827 | UP: Andre - Armor Smithbox | 1403 | 71811403 |
| 11113900 | UP: Andre - Titanite Shard | 1400 | 71811400 |
| 11113904 | UP: Andre - Repairbox | 1404 | 71811404 |
| 11113905 | UP: Andre - Longsword | 1405 | 71811405 |
| 11113906 | UP: Andre - Broadsword | 1406 | 71811406 |
| 11113907 | UP: Andre - Bastard Sword | 1407 | 71811407 |
| 11113908 | UP: Andre - Battle Axe | 1408 | 71811408 |
| 11113909 | UP: Andre - Warpick | 1409 | 71811409 |
| 11113910 | UP: Andre - Caestus | 1410 | 71811410 |
| 11113911 | UP: Andre - Pike | 1411 | 71811411 |
| 11113912 | UP: Andre - Large Leather Shield | 1412 | 71811412 |
| 11113913 | UP: Andre - Tower Kite Shield | 1413 | 71811413 |
| 11113914 | UP: Andre - Caduceus Kite Shield | 1414 | 71811414 |
| 11113915 | UP: Andre - Standard Arrow | 1415 | 71811415 |
| 11113916 | UP: Andre - Large Arrow | 1416 | 71811416 |
| 11113917 | UP: Andre - Wooden Arrow | 1417 | 71811417 |
| 11113918 | UP: Andre - Standard Bolt | 1418 | 71811418 |
| 11113919 | UP: Andre - Heavy Bolt | 1419 | 71811419 |
| 11113920 | UP: Andre - Wood Bolt | 1420 | 71811420 |

*(Note: Andre's Bottomless Box — the prior data assigned 11110825 → row 1501 which is actually in Domhnall's range. Existing Locations.py has this as AP ID 11110825 with name "UP: Andre - Bottomless Box". This needs reconciliation — the vanilla Bottomless Box from the Undead Merchant uses row 1501/equipId=2608/eventFlag=11007010 which is ID'd to Domhnall's range. Need to verify the correct row.)*

### Oswald of Carim — Undead Parish Bell Tower (8 locations)

| AP ID | Name | Row | PurchaseFlag |
|-------|------|-----|-------------|
| 11114901 | UP: Oswald - Purging Stone | 4401 | 71814401 |
| 11114902 | UP: Oswald - Indictment | 4402 | 71814402 |
| 11114903 | UP: Oswald - Karmic Justice | 4403 | 71814403 |
| 11114904 | UP: Oswald - Velka's Talisman | 4404 | 71814404 |
| 11114905 | UP: Oswald - Bloodbite Ring | 4405 | 71814405 |
| 11114906 | UP: Oswald - Poisonbite Ring | 4406 | 71814406 |
| 11114907 | UP: Oswald - Ring of Sacrifice | 4407 | 71814407 |
| 11114908 | UP: Oswald - Homeward Bone | 4408 | 71814408 |

### Remaining Merchants (future expansion)

These merchants were listed in the All Merchants Reference but NOT included in the prior attempt's ShopFlagDiag. They can be added incrementally:

| Merchant | Rows | Region | Complexity |
|----------|------|--------|-----------|
| Griggs of Vinheim | 2000-2021 | Lower Undead Burg | Medium (needs Residence Key) |
| Laurentius | 3000-3004 | Depths | Medium |
| Petrus of Thorolund | 4000-4006 | Firelink Shrine | Low (always available) |
| Domhnall of Zena | 1550-1584 | Depths/Firelink | High (moves, boss armor flags) |
| Patches the Hyena | 1600-1617 | Catacombs/Firelink | High (missable) |
| Shiva of the East | 1700-1709 | Darkroot Garden | High (covenant gate) |
| Dusk of Oolacile | 2200-2205 | Darkroot Basin | Medium (rescue required) |
| Eingyi | 3200-3212 | Quelaag's Domain | High (covenant + parasite egg) |
| Quelana of Izalith | 3400-3407 | Blighttown | Medium (pyro flame +10 trigger) |
| Rhea of Thorolund | 4200-4209 | Undead Parish/Duke's | High (quest chain, missable) |
| Big Hat Logan | 5000-5099 | Multiple | High (quest chain, missable) |
| Crestfallen Merchant | 5300-5599 | Sen's Fortress | Medium |
| Giant Blacksmith | 6200-6299 | Anor Londo | Medium |
| Vamos | 6300-6399 | Catacombs | Medium |
| Elizabeth | 6500-6599 | Royal Wood (DLC) | Medium (DLC) |
| Chester | 6400-6499 | Royal Wood (DLC) | Medium (DLC) |
| Gough | 6600-6699 | Royal Wood (DLC) | Medium (DLC) |
| Darkstalker Kaathe | 6100-6199 | The Abyss | High |

---

## Summary

The implementation leverages DSAP's existing infrastructure to minimize new code:

| What | Reused From | New Code |
|------|------------|----------|
| AP item stubs (EquipParamGoods) | ApItemInjectorHelper | None |
| Item names/descriptions (MsgMan) | ApItemInjectorHelper | None |
| Item-add/popup hooks | ApItemInjectorHelper | None |
| Param read/write | ParamHelper | ShopLineupParam model only |
| Event flag polling | LocationHelper/MonitorLocations | GetShopFlagLocations() |
| Location completion | Client_LocationCompleted | None |
| Item receiving | Client_ItemReceived | None |
| Replacement map pattern | ItemLotHelper.BuildLotParamIdToLotMap | ShopHelper.BuildShopReplacementMap |
| Param overwrite pattern | ItemLotHelper.OverwriteItemLots | ShopHelper.OverwriteShopParams |
| JSON resource pattern | ItemLots.json | ShopFlags.json |
| Option pattern | fogwall_sanity | shop_sanity |

The total new code is approximately:
- ~50 lines Python (option + generate_early + fill_slot_data)
- ~80+ lines Python (new location entries in Locations.py)
- ~10 lines C# (ShopLineupParam model)
- ~15 lines C# (ShopFlag model)
- ~150 lines C# (ShopHelper)
- ~30 lines C# (LocationHelper additions)
- ~10 lines C# (App.axaml.cs integration)
- ~10 lines C# (DarkSoulsOptions)
- 1 JSON resource file (~87 entries)
