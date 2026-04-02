# Shop Randomization — Implementation Status

**Last Updated:** 2026-04-02  
**Status:** Complete and working — deployed, tested in-game, all 86 shop locations functional

---

## Overview

Shop Randomization ("Shop Sanity") adds merchant inventories as Archipelago locations. When enabled, each item a merchant sells becomes an AP check. The client rewrites DSR's in-memory ShopLineupParam rows to point at AP item stubs, and custom purchase flags track which checks the player has completed.

**Scope (Phase 1):** 5 safe merchants, 86 total shop locations, all items free (value=0), single-purchase (sellQuantity=1).

### Merchants Included

| Merchant | Region | Rows | Entry Count |
|---|---|---|---|
| Undead Merchant (Male) | Upper Undead Burg | 1100–1134 | 34 |
| Female Merchant | Lower Undead Burg | 1200–1220 | 21 |
| Andre the Blacksmith | Undead Parish | 1400–1420 | 21 |
| Ingward | Upper New Londo Ruins | 2400–2401 | 2 |
| Oswald of Carim | Undead Parish (Bell Gargoyles) | 4401–4408 | 8 |

### Key Technical Details

- **ShopLineupParam**: 32 bytes/row, SoloParamMan offset 0x720
- **Purchase flag convention**: Spaced formula `71810000 + (i/32)*1000 + (i%32)*32` — each flag 32 tails apart, ensuring every flag gets its own 4-byte word (dense `71810000 + rowId` caused flag corruption due to DSR's byte-level overwrite behavior)
- **Wrapper approach**: Overwrites ShopLineupParam rows to reference existing AP EquipParamGoods stubs for non-local or non-physical items. **Native passthrough**: When a shop item is a physical DSR item for the local player, uses the real `equipId` and `equipType` (weapon/armor/ring/goods) instead of a goods stub — gives correct tab placement, icons, names, and descriptions natively.
- **Double-grant prevention**: `NativeShopLocationIds` HashSet tracks which locations are native passthroughs. `Client_ItemReceived` skips granting items from the player's own slot for these locations (the shop already gave the real item).
- **Sentinel entry**: Row ID 99999998 prevents redundant reloads (same pattern used by ItemLotHelper)
- **Existing hooks**: The item-add hook at 0x1407479E0 and popup hook at 0x140728c90 already cover AP item IDs — shop purchases of AP items automatically route through these

### Known Issue (Resolved)

`sellQuantity=1` does NOT cause cursor/index corruption — confirmed in testing. Items disappear from the shop after purchase as expected.

### Excluded from Phase 1

- Andre's Bottomless Box (AP ID 11110825, row 1501) — row is in Domhnall's range, handled by existing system
- All other merchants (Domhnall, Patches, Giant Blacksmith, etc.) — deferred to later phases

---

## File Changes

### New Files (C#)

#### `source/DSAP/Models/Params/ShopLineupParam.cs`
IParam definition for the ShopLineupParam table. Defines `Size = 0x20` (32 bytes per row) and `spOffset = 0x720` (location in SoloParamMan). Used by `ShopHelper` to read/write shop data from DSR memory.

#### `source/DSAP/Models/ShopFlag.cs`
Data model for JSON deserialization of shop flag entries. Properties:
- `Id` (int) — AP location ID (e.g., 11110042)
- `Name` (string) — Display name (e.g., "UB: Residence Key")
- `Row` (int) — ShopLineupParam row number to overwrite
- `PurchaseFlag` (int) — Custom event flag set on purchase (71810000 + Row)
- `IsEnabled` (bool) — Whether this entry participates in randomization

#### `source/DSAP/Resources/ShopFlags.json`
Embedded JSON resource mapping all 87 shop locations to their param rows and purchase flags. Organized by merchant. Includes 5 pre-existing SHOP_ITEM locations (Residence Key, Bottomless Box, Repairbox, Crest of Artorias, Weapon/Armor Smithbox) alongside 82 new ones.

#### `source/DSAP/Helpers/ShopHelper.cs`
Core shop randomization logic. Three main methods:

- **`BuildShopReplacementMap(out map, out nativeShopLocIds, scoutedLocationInfo)`** — Iterates scouted AP locations, matches against enabled ShopFlags by ID, creates `ShopReplacement` entries. For items destined for the local player that are physical DSR items, uses native equipId/equipType. For all others, uses AP goods stub (equipType=3). Also outputs a `HashSet<long>` of native shop location IDs for double-grant prevention.
- **`OverwriteShopParams(paramBytes, rowCount, replacementMap)`** — Binary-level mutation of param bytes. For each row whose ID matches a replacement, writes: equipId, value (0), mtrlId (-1), eventFlag (purchase flag), sellQuantity (1), shopType, equipType (3 = Goods).
- **`UpdateShopLineupParams(replacementMap)`** — Full pipeline: reads ShopLineupParam via `ParamHelper.ReadFromBytes()` → calls `OverwriteShopParams` → adds sentinel row 99999998 → writes back via `ParamHelper.WriteFromParamSt()`.

`ShopReplacement` inner class holds: EquipId, EquipType (3), Value (0), SellQuantity (1), EventFlag, ShopType.

### Modified Files (C#)

#### `source/DSAP/Helpers/LocationHelper.cs`
Added `#region Shop Helpers` with two methods:
- **`GetShopFlags()`** — Loads and caches `ShopFlags.json` from embedded resources via `MiscHelper.OpenEmbeddedResource()`. Returns `List<ShopFlag>`.
- **`GetShopFlagLocations()`** — Converts enabled ShopFlags into `List<ILocation>` using `AddressHelper.GetEventFlagOffset(shop.PurchaseFlag)` for memory address resolution.

#### `source/DSAP/Models/DarkSoulsOptions.cs`
Added `public bool ShopSanity { get; set; }` property, parsed from slot_data via `ShopSanity = GetBool("shop_sanity")`.

#### `source/DSAP/App.axaml.cs`
Five integration points:
1. **Static field** (~line 53): `private static Dictionary<int, ShopReplacement> ShopReplacementMap`
2. **OnConnectedAsync — build map** (~line 1703): When `DSOptions.ShopSanity` is true, calls `ShopHelper.BuildShopReplacementMap()` after item lot map is built
3. **OnConnectedAsync — apply params** (~line 1716): Calls `ShopHelper.UpdateShopLineupParams()` after `UpdateItemLots()`, with logging and overlay message
4. **Location monitoring** (~line 859): Adds `LocationHelper.GetShopFlagLocations()` to `fullLocationsList` when ShopSanity is enabled
5. **OnDisconnected — cleanup** (~line 1740): Clears `ShopReplacementMap`

#### `source/DSAP/DSAP.csproj`
Added `ShopFlags.json` to `<None Remove>` and `<EmbeddedResource Include>` sections alongside existing resource files.

### Modified Files (Python apworld)

#### `apworld/dsr/Options.py`
Added `ShopSanity(Toggle)` class with display_name "Shop Sanity". Added to the "Sanity" `OptionGroup` and to the `DSROption` dataclass as `shop_sanity: ShopSanity`.

#### `apworld/dsr/Locations.py`
Added 82 new `DSRLocationData` entries with `DSRLocationCategory.SHOP_ITEM` across 5 regions:
- **Upper Undead Burg**: 31 new entries (IDs 11110900–11110934) — Undead Merchant Male
- **Undead Parish**: 18 new entries (IDs 11113900–11113920) — Andre
- **Undead Parish - Bell Gargoyles**: 8 entries (IDs 11114901–11114908) — Oswald
- **Lower Undead Burg**: 21 entries (IDs 11112900–11112920) — Female Merchant
- **Upper New Londo Ruins - After Fog**: 2 entries (IDs 11113000–11113001) — Ingward

`DSRLocationCategory.SHOP_ITEM = 9` already existed in the enum.

#### `apworld/dsr/__init__.py`
Two changes:
1. In `generate_early()`: When `self.options.shop_sanity.value == True`, adds `DSRLocationCategory.SHOP_ITEM` to `self.enabled_location_categories`
2. In `fill_slot_data()`: Passes `"shop_sanity": self.options.shop_sanity.value` in the options dict so the C# client knows to activate shop randomization

---

## Architecture Flow

```
[AP Server]                          [DSAP C# Client]
    |                                       |
    |  slot_data.shop_sanity = true         |
    |-------------------------------------->|  DarkSoulsOptions parses ShopSanity
    |                                       |
    |  Scout 87 SHOP_ITEM location IDs      |
    |<--------------------------------------|  
    |                                       |
    |  Return scouted item info             |
    |-------------------------------------->|  BuildShopReplacementMap()
    |                                       |     matches scoutedInfo → ShopFlags.json
    |                                       |     creates ShopReplacement per row
    |                                       |
    |                                       |  UpdateShopLineupParams()
    |                                       |     reads DSR memory (ShopLineupParam)
    |                                       |     overwrites rows with AP stubs
    |                                       |     writes back + sentinel
    |                                       |
    |                                       |  MonitorLocationsAsync()
    |                                       |     polls purchase flags (7181xxxx)
    |                                       |     fires LocationCompleted on 0→1
    |  Location check completed             |
    |<--------------------------------------|
    |                                       |
    |  Send item to recipient               |
    |-------------------------------------->|  (standard AP item delivery)
```

---

## Validation Results (2026-03-31)

- **C# build**: Succeeded with warnings only (all pre-existing, none from new code)
- **Python syntax**: All 3 modified files parse cleanly
- **Location IDs**: 853 total across all categories, 0 duplicates
- **Item names**: All 87 SHOP_ITEM default items verified to exist in Items.py
- **Deployed**: DSAP.Desktop.exe + dsr.apworld both deployed

---

## What Comes Next

- **Phase 2**: Add more merchants (Domhnall, Patches, Giant Blacksmith, etc.)
- **Phase 2+**: Variable pricing, multi-quantity items, shop-specific filler pools
