# DSAP Shop Research: Raw Data Reference

This file stores large chunks of reference data (external sources, param layouts, ID tables, protocol specs) for future prompting without needing to re-fetch or re-scan.

---

## A. DSR ShopLineupParam Layout (from soulsmodding.com)

### Fields

| Field | Type | Offset | Description | References |
|-------|------|--------|-------------|------------|
| equipId | s32 | 0x0 | The item ID that is sold | EquipParamWeapon, EquipParamProtector, EquipParamAccessory, EquipParamGoods |
| value | s32 | 0x4 | The cost of item sold. Overrides sellValue in item's row. | |
| mtrlId | s32 | 0x8 | Material cost for purchasing/equipping a spell | EquipMtrlSetParam |
| eventFlag | s32 | 0xc | 8-bit event value storing sold item quantity. For non-infinite items. | Event Flag ID |
| qwcId | s32 | 0x10 | Demon's Souls leftover. Probably doesn't work. | QwcChange |
| sellQuantity | s16 | 0x14 | Max items sold. -1 = infinite. Requires eventFlag for quantity tracking. | |
| shopType | u8 | 0x16 | Shop row type (enum SHOP_LINEUP_SHOPTYPE) | |
| equipType | u8 | 0x17 | Equipment type of sold item (enum SHOP_LINEUP_EQUIPTYPE) | |
| pad_0 | dummy8 | 0x18 | Padding | |

**Total row size: 0x20 (32 bytes)**

### SHOP_LINEUP_SHOPTYPE Enum

| Value | Meaning |
|-------|---------|
| 0 | Shop menu |
| 1 | Enhancement menu |
| 2 | Magic menu |
| 3 | Miracle menu |
| 4 | Information menu |
| 5 | SAN Value menu |

### SHOP_LINEUP_EQUIPTYPE Enum

| Value | Meaning |
|-------|---------|
| 0 | Weapon |
| 1 | Armor |
| 2 | Accessory (Ring) |
| 3 | Good (Consumable/Key/Material) |
| 4 | Attunement (Spell) |

---

## All Merchants Reference

Complete list of all Dark Souls Remastered merchants (20 total) with onboarding status and location/row notes.

| Merchant | Rows | Region | Status | Notes |
|----------|------|--------|--------|-------|
| Crestfallen Merchant | ? | Sen's Fortress - After Second Fog | not-started | |
| Domhnall of Zena | 1550-1584 | Depths | not-started | |
| Andre | 1400-1420 | Undead Parish | not-started | |
| Oswald of Carim | 4400-4408 | Undead Parish - Bell Gargoyles | not-started | |
| Patches the Hyena | 1600-1617 | TBD (Missable) | not-started | |
| Shiva of the East | 1700-1709 | TBD (Missable) | not-started | |
| Snuggly | ? | TBD (Missable) | not-started | |
| Undead Merchant (Male) | 11110900+ | Undead Asylum | not-started | |
| Undead Merchant (Female) | 1200-1220 | Undead Parish | not-started | |
| Elizabeth | ? | TBD (DLC) | not-started | |
| Hawkeye Gough | ? | TBD (DLC) | not-started | |
| Marvelous Chester | ? | TBD (DLC) | not-started | |
| Big Hat Logan | 2100-2102 | TBD (Missable) | not-started | |
| Griggs of Vinheim | 2000-2021 | Lower Undead Burg - After Residence Key | not-started | |
| Laurentius of the Great Swamp | 3000-3004 | Depths | not-started | |
| Petrus of Thorolund | 4000-4006 | Firelink Shrine | not-started | |
| Quelana of Izalith | 3400-3407 | TBD (Missable) | not-started | pyromancy |
| Reah of Thorolund | 4200-4209 | TBD (Missable) | not-started | Thorolund miracles |
| Dusk of Oolacile | 2200-2205 | Darkroot Basin | not-started | oolacile sorcery |
| Ingward | 2400-2401 | Upper New Londo Ruins - After Fog | not-started | |

---

## B. Vanilla Shop ID Ranges (from soulsmodding.com)

Merchants use `OpenRegularShop(rangemin, rangemax)` in talkESD scripts.
Attunement menu uses `OpenMagicEquip(rangemin, rangemax)` in bonfire talkESD scripts.

| Merchant | ID Range | Notes |
|----------|----------|-------|
| Undead Merchant (Male) | 1100-1199 | |
| Undead Merchant (Female) | 1200-1299 | |
| Crestfallen Merchant | 5300-5599 | |
| Andre of Astora | 1400-1499 | |
| Domhnall of Zena¹ | 1500-1569 | Boss armor sold qty flags set to 0 via EMEVD |
| Domhnall of Zena² | 1500-1599 | (DLC present?) |
| Trusty Patches | 1600-1699 | |
| Shiva of the East | 1700-1799 | |
| Griggs of Vinheim¹ | 2000-2019 | |
| Griggs of Vinheim² | 2000-2099 | After Logan leaves Firelink |
| Rickert of Vinheim | 2100-2199 | |
| Dusk of Oolacile | 2200-2299 | |
| Ingward | 2400-2499 | |
| Laurentius | 3000-3099 | |
| Eingyi¹ | 3200-3209 | Servant List only |
| Eingyi² | 3200-3299 | Parasitic Egg Head required |
| Quelana of Izalith | 3400-3499 | |
| Petrus of Thorolund | 4000-4099 | |
| Rhea of Thorolund | 4200-4299 | |
| Oswald of Carim | 4400-4499 | |
| Logan¹ | 5000-5019 | While in Firelink Shrine |
| Logan² | 5000-5099 | While in Duke's Archives |
| Darkstalker Kaathe | 6100-6199 | |
| Giant Blacksmith | 6200-6299 | |
| Vamos | 6300-6399 | |
| Marvellous Chester | 6400-6499 | |
| Elizabeth | 6500-6599 | |
| Hawkeye Gough | 6600-6699 | |
| ATTUNEMENT | 10000-10099 | Bonfire attunement menu |

---

## C. SoloParamMan Offsets (from DSROffsets.cs / Nordgaren DSR-Gadget)

Location: `SoloParamMan + offset` → dereference chain to get actual param data.

| Param | Offset | Notes |
|-------|--------|-------|
| EquipParamWeapon | 0x18 | |
| EquipParamProtector | 0x60 | |
| EquipParamAccessory | 0xA8 | |
| EquipParamGoods | 0xF0 | |
| ReinforceParamWeapon | 0x138 | |
| ReinforceParamProtector | 0x180 | |
| NpcParam | 0x1C8 | |
| AtkParam | 0x210 | AtkParamNpc?? |
| AtkParamPC | 0x258 | |
| NpcThinkParam | 0x2A0 | |
| ObjectParam | 0x2E8 | |
| BulletParam | 0x330 | |
| BehaviourParam | 0x378 | |
| BehaviourParam2 | 0x3C0 | One is BehaviourParam, other is BehaviourParam_Pc |
| MagicParam | 0x408 | |
| SpEffectParam | 0x450 | |
| SpEffectVfxParam | 0x498 | |
| TalkParam | 0x4E0 | |
| MenuParamColorTable | 0x528 | |
| ItemLotParam | 0x570 | |
| MoveParam | 0x5B8 | Contains ptr to ParamResCap for ReinforceParamWeapon at 0x10 |
| CharacterInitParam | 0x600 | |
| EquipMtrlSetParam | 0x648 | |
| FaceParam | 0x690 | |
| RagdollParam | 0x6D8 | |
| **ShopLineupParam** | **0x720** | Contains ptr to ParamResCap for LightBank at 0x10 |
| QwcChangeParam | 0x768 | |
| QwcJudgeParam | 0x7B0 | Contains ptr to ParamResCap for DofBank at 0x10 |
| GameAreaParam | 0x7F8 | Contains ptr to ParamResCap for TalkParam at 0x10 |
| SkeletonParam | 0x840 | |
| CalcCorrectGraph | 0x888 | |
| LockCamParam | 0x8D0 | Contains ptr to ParamResCap for BulletParam at 0x10 |
| ObjActParam | 0x918 | |
| HitMtrlParam | 0x960 | |
| KnockBackParam | 0x9A8 | |
| LevelSyncParam | 0x9F0 | Contains ptr to ParamResCap for QwcChangeParam at 0x10 |
| CoolTimeParam | 0xA38 | |
| WhiteCoolTimeParam | 0xA80 | Contains ptr to ParamResCap for LevelSyncParam at 0x10 |

### Param Read Dereference Chain
```
SoloParam Root (AoB scan)
  → +offset → ResCapLoc pointer
  → Deref → BufferSize, BufferLoc
  → Deref → Raw param bytes
  → Parse: Prologue(0x10) + Header(0x30) + Entries(12×N) + Param data + Strings
```

ParamHelper constants: `ParamOffset1 = 0x38`, `ParamOffset2 = 0x10`

---

## D. DSR Memory MsgMan Offsets

Root pointer: `0x141c7e3e8` (hardcoded)

| Table | Offset from root |
|-------|-----------------|
| Item Names | +0x380 |
| Item Captions | +0x378 |
| Item Descriptions | +0x328 |
| System Text | +0x3e0 |

---

## E. Archipelago Network Protocol — Key Packet Specifications

### Connection Handshake
1. Client opens WebSocket → Server sends `RoomInfo`
2. Client may send `GetDataPackage` → Server sends `DataPackage`
3. Client sends `Connect` → Server sends `Connected` or `ConnectionRefused`
4. Server may send `ReceivedItems` (queued items)
5. Server sends `PrintJSON` (join notification)

### Key Packets

**LocationScouts** (Client → Server):
- `locations: list[int]` — location IDs to scout
- `create_as_hint: int` — 0=none, 1=announce, 2=announce once (new only)
- Response: `LocationInfo` with `locations: list[NetworkItem]`

**LocationChecks** (Client → Server):
- `locations: list[int]` — completed location IDs (duplicates OK)

**ReceivedItems** (Server → Client):
- `index: int` — next empty slot; 0 = full resync
- `items: list[NetworkItem]` — items received

**NetworkItem**:
- `item: int` — item ID (game-specific, offset by base_id)
- `location: int` — location ID where item was found
- `player: int` — player slot of the world containing the item
- `flags: int` — 0=none, 0b001=advancement, 0b010=useful, 0b100=trap

**items_handling flags in Connect**:
- `0b000` = no items
- `0b001` = items from other worlds
- `0b010` = items from own world (requires 0b001)
- `0b100` = starting inventory (requires 0b001)

### DataStorage
- `Get` → `Retrieved` (key-value read)
- `Set` → `SetReply` (key-value write with atomic operations)
- `SetNotify` → subscribes to `SetReply` for specific keys
- Special read keys: `_read_hints_{team}_{slot}`, `_read_slot_data_{slot}`

---

## F. Archipelago.MultiClient.Net — Key API Surface for DSAP

### Session Creation & Login
```csharp
var session = ArchipelagoSessionFactory.CreateSession("host", 38281);
LoginResult result = session.TryConnectAndLogin(
    "Dark Souls Remastered", playerName, ItemsHandlingFlags.AllItems,
    version: new Version(0, 6, 0), tags: new[] { "AP" },
    requestSlotData: true);
// LoginSuccessful has .SlotData (Dict<string,object>)
```

### Receiving Items
```csharp
session.Items.ItemReceived += (ReceivedItemsHelper helper) => {
    var item = helper.DequeueItem();
    // item.ItemId, item.LocationId, item.Player, item.Flags
    // item.ItemDisplayName, item.LocationName (lazy-resolved)
};
```

### Sending Location Checks
```csharp
session.Locations.CompleteLocationChecks(locationId1, locationId2);
// or async:
await session.Locations.CompleteLocationChecksAsync(locationId1);
```

### Scouting
```csharp
Dictionary<long, ScoutedItemInfo> info = 
    await session.Locations.ScoutLocationsAsync(
        HintCreationPolicy.None, locationIds);
// ScoutedItemInfo: .Player (receiver), .ItemDisplayName, .Flags, .ItemGame
```

### DataStorage
```csharp
// Write:
session.DataStorage[Scope.Slot, "key"] = value;
session.DataStorage[Scope.Slot, "counter"] += 5;
// Read (sync, blocks 2s):
int val = session.DataStorage[Scope.Slot, "key"];
// Read (async):
int val = await session.DataStorage[Scope.Slot, "key"].GetAsync<int>();
// Subscribe:
session.DataStorage[Scope.Slot, "key"].OnValueChanged += (old, new_, args) => { };
```

### DeathLink
```csharp
var dl = session.CreateDeathLinkService();
dl.EnableDeathLink(); dl.DisableDeathLink();
dl.OnDeathLinkReceived += (DeathLink d) => { /* d.Source, d.Cause */ };
dl.SendDeathLink(new DeathLink("Player", "cause"));
```

---

## G. DS1 Dumped Params Spreadsheet

Source: https://docs.google.com/spreadsheets/d/1KukblWL61We64-gNIyaAShga9h8RTXYmyFs98eQhY4E

(Google Sheets — contains full parameter dumps for Dark Souls 1 including all param tables. Access externally for column-level data.)

---

## H. DSAP Item ID Mapping Reference

**base_id = 11110000** (all AP IDs = base_id + dsr_code)

| Range (dsr_code) | Category | Examples |
|-------------------|----------|----------|
| 1000-1101 | Events | Progression milestones |
| 1200-1216 | Fog Wall Keys | Area fog wall unlock items |
| 1230-1251 | Boss Fog Wall Keys | Boss fog wall unlock items |
| 1300-1320 | Bonfire Warp Unlocks | Per-bonfire warp items |
| 2000-2084 | Consumables (single) | Eye of Death, Firebomb, etc. |
| 2100-2116 | Consumables (multi-qty) | "Firebomb x6", "Humanity x3" |
| 3000-3034 | Key Items | Keys, Lord Souls, Lordvessel, Smithboxes |
| 4000-4040 | Rings | All rings |
| 5000-5009 | Embers | Blacksmithing embers |
| 5010-5102 | Upgrade Materials | Titanite, etc. |
| 6000-6071 | Spells | Sorceries, Pyromancies, Miracles |
| 7000-7240 | Armor | All armor pieces |
| 8000-8152 | Weapons | Melee, Ranged, Spell Tools |
| 8200-8206 | Ammunition | Arrows/Bolts (bulk) |
| 8300-8303 | Pre-infused Weapons | Lightning Spear, etc. |
| 9000-9043 | Shields | All shields |
| 9900-9902 | Filler/Nothing | Pool padding |
| 10000 | Lag Trap | Special trap item |

---

## I. DSR Internal Item Category Bitmasks (C# DSItemCategory)

These are the bitmasks DSR uses internally to distinguish item types in memory:

| Category | Bitmask | Items |
|----------|---------|-------|
| Weapons/Shields/SpellTools/Ranged | 0x00000000 | Physical equipment |
| Armor | 0x10000000 | All armor pieces |
| Rings | 0x20000000 | All rings |
| Consumables/Keys/Spells/Upgrades | 0x40000000 | Most inventory items |
| DsrEvent (custom AP) | 0x11111111 | Fog wall unlock events |
| BonfireWarp (custom AP) | 0x11111112 | Bonfire warp unlocks |
| Trap (custom AP) | 0x33333333 | Trap items |

---

## J. Current DSAP Shop Item Locations (all 7)

| AP ID | Location Name | Default Item | Region |
|-------|--------------|--------------|--------|
| 11110042 | UB: Residence Key | Residence Key | Upper Undead Burg |
| 11110823 | UB: Bottomless Box | Bottomless Box | Upper Undead Burg |
| 11110824 | UB: Repairbox | Repairbox | Upper Undead Burg |
| 11110825 | UP: Andre - Bottomless Box | Bottomless Box | Undead Parish |
| 11110826 | UP: Andre - Weapon Smithbox | Weapon Smithbox | Undead Parish |
| 11110827 | UP: Andre - Armor Smithbox | Armor Smithbox | Undead Parish |
| 11110822 | UP: Andre - Crest of Artorias | Crest of Artorias | Undead Parish |

**Note**: SHOP_ITEM category (9) is currently NOT in `location_skip_categories` but IS excluded from `enabled_location_categories` by default, meaning these locations are not currently active in randomization.

---

## K. Key External Reference URLs

- DS1 Dumped Params: https://docs.google.com/spreadsheets/d/1KukblWL61We64-gNIyaAShga9h8RTXYmyFs98eQhY4E
- Vanilla Shops: https://www.soulsmodding.com/doku.php?id=ds1-refmat:vanilla-shops
- ShopLineupParam layout: https://www.soulsmodding.com/doku.php?id=ds1-refmat:param:shoplineupparam
- DSROffsets.cs (Nordgaren): https://github.com/Nordgaren/DSR-Gadget-Local-Loader/blob/077102955ae43b86c99d49189d8c9f1179b0b2f0/DSR-Gadget/Util/DSROffsets.cs
- AP MultiClient.Net packets: https://archipelagomw.github.io/Archipelago.MultiClient.Net/docs/packets.html
- AP Network Protocol: https://github.com/ArchipelagoMW/Archipelago/blob/main/docs/network%20protocol.md
- AP LocationScouts: https://archipelagomw.github.io/Archipelago.MultiClient.Net/docs/packets.html#locationscoutspacket
