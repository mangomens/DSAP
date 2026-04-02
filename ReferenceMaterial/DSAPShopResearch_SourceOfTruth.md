# DSAP Shop Research: Objective Source of Truth

This document summarizes objective, verifiable information about the DSAP system and its integration with Archipelago, based on code, logs, and data files. It is the primary reference for future feature work. Companion file: `DSAPShopResearch_RawData.md` (large data dumps, param layouts, external URLs).

---

## 1. Detailed Description of AP Client-Server Integration

### 1.1 Architecture Overview

DSAP has two independent components connected through an AP server:

- **Python apworld** (`DSAP/apworld/dsr/`): Runs server-side during multiworld generation. Defines regions, items, locations, options, and logic rules. Produces slot_data consumed by the client.
- **C# desktop client** (`DSAP/source/DSAP/`): An Avalonia UI app that hooks DSR's live process via memory read/write. Communicates with the AP server via `Archipelago.MultiClient.Net`.

### 1.2 Connection Flow (21 Steps)

1. User selects a DSR process ID and clicks Connect
2. `DarkSoulsClient` attaches to DSR via `ProcessName = "DarkSoulsRemastered"`
3. `ArchipelagoClient` opens WebSocket to AP server → receives `RoomInfo`
4. Client sends `Connect` with `game="Dark Souls Remastered"`, `ItemsHandlingFlags.AllItems` (0b111)
5. Server responds with `Connected` packet containing `slot_data`, `missing_locations`, `checked_locations`
6. If DeathLink option enabled, `session.CreateDeathLinkService().EnableDeathLink()`
7. Overlay initialized (if applicable)
8. `OnConnectedAsync` fires, beginning the initialization sequence:
   - Parse `DarkSoulsOptions` from `slot_data`
   - Build `EmkControllers` (fog wall lock list) from slot data
   - Build `SlotLocToItemUpgMap` (weapon upgrade mapping from `itemsAddress`/`itemsId`/`itemsUpgrades`)
   - **Scout ALL locations** → receives `Dictionary<long, ScoutedItemInfo>` via `ScoutLocationsAsync`
   - **AddAPItems** — inject AP item definitions into DSR memory (EquipParamGoods entries + MsgMan strings + function hooks)
   - **BuildLotParamIdToLotMap** — build ItemLot replacement map from scouted data
   - Randomize starting loadouts (if option enabled)
   - Remove weapon/spell requirements (if options enabled)
   - **UpdateItemLots** — overwrite ItemLotParam in DSR memory + force load screen
9. Post-connect: Detect already-received event keys, build location monitoring lists
10. `Client.MonitorLocationsAsync(fullLocationsList)` — begin polling event flags
11. Start EMK fog wall management loop (every 1 second)
12. Start InGame watcher

### 1.3 Key MultiClient.Net Usage

| Operation | API Call | Packet |
|-----------|---------|--------|
| Login | `session.TryConnectAndLogin(...)` | `Connect` → `Connected` |
| Scout locations | `session.Locations.ScoutLocationsAsync(HintCreationPolicy.None, ids)` | `LocationScouts` → `LocationInfo` |
| Report checks | `session.Locations.CompleteLocationChecks(ids)` | `LocationChecks` |
| Receive items | `session.Items.ItemReceived += handler` | `ReceivedItems` |
| DeathLink | `deathLinkService.SendDeathLink(...)` | `Bounce` (tag "DeathLink") |
| Goal complete | `session.SetGoalAchieved()` | `StatusUpdate(ClientGoal)` |
| Chat/commands | `session.Say(text)` | `Say` |

### 1.4 items_handling

DSAP uses `AllItems` (0b111) = receive items from other worlds + own world + starting inventory. This means every item placed in the DSR slot is received as a `ReceivedItems` packet.

---

## 2. Detailed Outline of Current AP Item Randomization Implementation

### 2.1 Generation Lifecycle (Python apworld)

| Step | Method | What Happens |
|------|--------|-------------|
| 1 | `generate_early()` | Determines enabled location categories based on options (fogwall_sanity, boss_fogwall_sanity). Builds `enabled_location_categories` set and excluded location list. |
| 2 | `create_regions()` | Builds ~110 named regions modeling DSR's world topology with uni/bidirectional connections. Creates Location objects in each region. Categories determine handling: enabled → real randomized location; locked → place vanilla item statically; disabled → create invisible event. |
| 3 | `create_items()` | Builds matched item pool. See §2.2. |
| 4 | `set_rules()` | (Implicit in `create_regions()` via `set_rule()` lambda conditions on connections) |
| 5 | `fill_slot_data()` | Serializes all options + weapon upgrade mapping + API version for client consumption. See §2.4. |

### 2.2 Item Pool Construction (`create_items()`)

1. For each location in the multiworld for this player:
   - If item category is SKIP, or location category is in `location_skip_categories` or `location_locked_categories` → place vanilla item locked
   - If location is excluded by user option → also lock vanilla
   - Otherwise → add to randomizable pool, increment `itempoolSize`
2. Build `RequiredItemPool` — progression/key items that must exist
3. Build `GuaranteedItemPool` — useful items that should appear if space permits
4. Identify `removable_items` (FILLER + Junk group items)
5. Swap removable items for required items, then guaranteed items
6. Replace remaining filler/junk with "Soul of a Proud Knight"
7. Add final pool to `multiworld.itempool`
8. Lock all skip/excluded items to their original locations

### 2.3 Location Categories and Skip Logic

| Category | Value | Behavior |
|----------|-------|----------|
| SKIP | 0 | Always skipped (never randomized) |
| EVENT | 1 | Skipped (in `location_skip_categories`) |
| BOSS | 2 | Skipped (in `location_skip_categories`) |
| BONFIRE | 3 | Skipped (in `location_skip_categories`) |
| DOOR | 4 | Enabled by default |
| ITEM_LOT | 5 | Enabled by default (largest category, ~600+ locations) |
| ENEMY_DROP | 6 | Enabled by default |
| FOG_WALL | 7 | Enabled only when `fogwall_sanity` is on |
| BOSS_FOG_WALL | 8 | Enabled only when `boss_fogwall_sanity` is on |
| **SHOP_ITEM** | **9** | **Currently NOT active in randomization — not in skip list but not enabled by default** |
| BONFIRE_WARP | 10 | In `location_locked_categories` — vanilla item placed locked |

### 2.4 slot_data Structure

```json
{
  "options": {
    "guaranteed_items": <int>,
    "fogwall_sanity": <0|1>,
    "boss_fogwall_sanity": <0|1>,
    "logic_to_access_catacombs": "<string key>",
    "randomize_starting_loadouts": <0|1>,
    "randomize_starting_gifts": <0|1>,
    "require_one_handed_starting_weapons": <0|1>,
    "extra_starting_weapon_for_melee_classes": <0|1>,
    "extra_starting_shield_for_all_classes": <0|1>,
    "starting_sorcery": <int>,
    "starting_miracle": <int>,
    "starting_pyromancy": <int>,
    "no_weapon_requirements": <0|1>,
    "no_spell_stat_requirements": <0|1>,
    "no_miracle_covenant_requirements": <0|1>,
    "upgraded_weapons_percentage": <0-100>,
    "upgraded_weapons_allowed_infusions": <list>,
    "upgraded_weapons_adjusted_levels": <0|1>,
    "upgraded_weapons_min_level": <0-15>,
    "upgraded_weapons_max_level": <0-15>,
    "enable_deathlink": <0|1>
  },
  "seed": "<seed_name>",
  "slot": "<player_name>",
  "base_id": 11110000,
  "itemsId": [<item_code>, ...],
  "itemsUpgrades": [<upgrade_tuple_or_null>, ...],
  "itemsAddress": ["<player>:<location_address>", ...],
  "apworld_api_version": "0.1.0.0"
}
```

The `itemsId`/`itemsUpgrades`/`itemsAddress` arrays are parallel — index i describes the item placed at a specific location. These are used by the C# client to build `SlotLocToItemUpgMap` for weapon upgrade application.

---

## 3. Detailed Outline of DSR Shop System

### 3.1 How DSR Shops Work (Vanilla)

DSR shops are defined by **ShopLineupParam** rows in the game's param tables. Each shop row is **32 bytes** and specifies:
- `equipId` (s32, offset 0x0): The item being sold (references EquipParamWeapon/Protector/Accessory/Goods)
- `value` (s32, offset 0x4): Override price (overrides the item's own sellValue)
- `mtrlId` (s32, offset 0x8): Material cost for purchasing
- `eventFlag` (s32, offset 0xC): Event flag tracking quantity sold (for finite items)
- `sellQuantity` (s16, offset 0x14): Max purchasable (-1 = infinite)
- `shopType` (u8, offset 0x16): 0=Shop, 1=Enhancement, 2=Magic, 3=Miracle, 4=Info, 5=SAN
- `equipType` (u8, offset 0x17): 0=Weapon, 1=Armor, 2=Accessory, 3=Good, 4=Attunement

### 3.2 How NPCs Open Shops

NPC talkESD scripts call `OpenRegularShop(rangemin, rangemax)` to display all ShopLineupParam rows whose IDs fall within [rangemin, rangemax]. Each merchant has designated ID ranges (see Raw Data §B for full table).

The attunement menu at bonfires uses `OpenMagicEquip(rangemin, rangemax)` in bonfire talkESD scripts (range: 10000-10099).

### 3.3 ShopLineupParam Memory Location

**SoloParamMan + 0x720** → follows the standard param dereference chain:
```
SoloParamMan + 0x720 → ResCapLoc pointer → deref → BufferSize/BufferLoc → deref → Raw param bytes
```
Standard offsets: `ParamOffset1 = 0x38`, `ParamOffset2 = 0x10` applied during traversal.

### 3.4 Current DSAP Shop Status

**Shop randomization is fully implemented and working** (as of 2026-04-02). The `shop_sanity` option enables 86 SHOP_ITEM locations across 5 merchants. When enabled, `ShopHelper.BuildShopReplacementMap()` builds AP item replacements and `UpdateShopLineupParams()` overwrites DSR's in-memory ShopLineupParam rows.

**Key design: Native item passthrough** — When a scouted shop item is a physical DSR item destined for the local player, the shop row uses the **real DSR equipId and equipType** (weapon/armor/ring/goods). This gives correct tab placement, icons, names, and descriptions without needing cross-category FMG injection. Non-physical items and items for other players use AP stub entries in EquipParamGoods (equipType=3).

**Double-grant prevention** — Native shop items are already obtained from the shop itself. The `Client_ItemReceived` handler skips granting items from the player's own slot when the location is in `NativeShopLocationIds`.

| Merchant | Region | Row Range | Location Count |
|----------|--------|-----------|---------------|
| Undead Merchant (Male) | Upper Undead Burg | 1100–1134 | 34 |
| Female Merchant | Lower Undead Burg | 1200–1220 | 21 |
| Andre of Astora | Undead Parish | 1400–1420 | 21 |
| Ingward | Upper New Londo Ruins | 2400–2401 | 2 |
| Oswald of Carim | Undead Parish | 4401–4408 | 8 |

---

## 4. Detailed Outline of DSR Items — Storage, Display, and Type Distinctions

### 4.1 Where Item Data Lives in Memory

**Game params** (SoloParamMan) — confirmed via `/paraminfo` diagnostic (2026-04-01):

| Param Table | SoloParam Offset | Entry Size | Entry Count | Notes |
|---|---|---|---|---|
| EquipParamWeapon | +0x18 | **0x110** (272 bytes) | 1245 | Weapons, shields, catalysts, bows |
| EquipParamProtector | +0x60 | **0xE8** (232 bytes) | 324 | Armor pieces |
| EquipParamAccessory | +0xA8 | **0x40** (64 bytes) | 41 | Rings |
| EquipParamGoods | +0xF0 | **0x5C** (92 bytes) | 827 | Consumables, keys, upgrade materials, spells |
| ShopLineupParam | +0x720 | **0x20** (32 bytes) | 392 | Shop inventory rows |
| MagicParam | +0x408 | 0x30 (48 bytes) | — | Spell definitions |
| MoveParam | +0x5B8 | 0x8D (141 bytes) | — | Movement parameters |
| ItemLotParam | +0x570 | 0x94 (148 bytes) | — | Item lots |
| CharaInitParam | +0x600 | 0xF0 (240 bytes) | — | Starting class loadouts |

Note: DSAP's existing `EquipParamGoods.cs` defines `Size = 0x4C` (76), while the measured inter-entry spacing is 0x5C (92). The difference is likely padding or trailing fields. The existing model works for injection because `ParamStruct.AddParam` only needs to write the first `Size` bytes.

Note: DSAP's existing `EquipParamWeapon.cs` defines `Size = 0x10B` (267), while the measured inter-entry spacing is 0x110 (272). Same padding situation.

**Icon field offsets** (within ParamBytes entry, from Paramdex XML — empirically confirmed by in-game testing 2026-04-01):
| Param Table | Field Name | Offset | Type | Known Values |
|---|---|---|---|---|
| EquipParamGoods | iconId | **+0x2C** | u16 | Prism Stone=2042, Throwing Knife=2021 |
| EquipParamWeapon | iconId | **+0xBA** | u16 | Dagger=1, Short Sword=6, Hand Axe=33 |
| EquipParamProtector | iconIdM | **+0xA2** | u16 | Leather Armor=1052, Chain Helm=1085 |
| EquipParamProtector | iconIdF | **+0xA4** | u16 | Leather Armor=1052, Chain Helm=1165 |
| EquipParamAccessory | iconId | **+0x22** | u16 | Ring of Sacrifice=4000, Ring of Fog=4009 |

**Note on prior +0x10 offset theory (DISPROVEN)**: A hex dump analysis incorrectly concluded offsets were +0x10 higher. In-game testing (2026-04-01) proved the original Paramdex offsets correct — writing icon 2042 to +0x2C shows Prism Stone; writing to +0x3C shows the template default (White Sign Soapstone). The hex dump base address likely included a 0x10-byte header that was already accounted for in ParamBytes indexing.

**FMG (FromSoft MeSsaGe) Tables — How DS1R Displays Item Text**

An FMG is a string lookup table mapping integer IDs to Unicode strings. The game stores separate FMG tables for each equip category: Goods Names, Weapon Names, Protector Names, Accessory Names, and corresponding Caption and Description tables. When the game needs to display an item's name in the shop UI, it looks up the item's param row ID in the FMG table that corresponds to its **equipType** — not a single shared table.

This is the root cause of our shop display problem: all AP item stubs are currently injected as EquipParamGoods entries with their text written only to the Goods FMG tables (+0x380 names, +0x378 captions, +0x328 descriptions). When `ShopLineupParam.equipType` is changed to put an item in the Weapons tab (equipType=0), the game looks up that item's ID in the *Weapon* Names FMG instead — and finds nothing, so it shows a blank or fallback string. To fix this, each AP stub must have its text written to the FMG tables matching its equipType.

All FMG tables hang off `MsgMan`, a singleton manager object. The root pointer is at a static address, and each FMG table is at a known offset from the root. The `MsgManHelper` / `AddMsgs()` infrastructure already handles reading, inserting entries, and rewriting FMG buffers — we just need the correct offset for each table.

**Complete MsgMan FMG Table Map** (root pointer at `0x141c7e3e8`) — via `/paraminfo` enhanced scan (2026-04-01):

Every non-NULL pointer in the 0x200-0x800 range has been scanned, FMG-validated, and identified by content sampling.

| Offset | Entries | Spans | Size | Identified Content | Sample Strings |
|--------|---------|-------|------|--------------------|----------------|
| +0x238 | 182 | 14 | 6,880 | Menu/UI Text | "Inventory", "Equipment" |
| +0x268 | 980 | 64 | 35,472 | System Dialog (copy 1) | "∞", "OK", "CANCEL" |
| +0x270 | 1,353 | 66 | 28,124 | System Text (copy 1) | IDs collide w/ weapon/protector → "DARK SOULS™: REMASTERED" |
| +0x278 | 508 | 82 | 19,136 | System Message Templates (copy 1) | "<?sysmsg@1?>", "<?sysmsg@2?>" |
| +0x280 | 286 | 35 | 27,236 | Controller Prompts (copy 1) | "<?selectLR?>:Select <?conclusion?>:Enter" |
| +0x288 | 272 | 26 | 8,636 | Menu Help Text (copy 1) | "Browse and use items", "Change equipment" |
| +0x290 | 124 | 10 | 3,284 | Character Creation (Japanese) | JPN descriptions of name/gender/class |
| +0x2D8 | 350 | 16 | 2,776 | Upgrade/UI Symbols | "③", "④", "①" |
| +0x2E0 | 35 | 2 | 384 | Debug/Input Text | "TEST_WIN32", "Movement" |
| +0x2E8 | 1,003 | 71 | 318,864 | Dialog Buttons (copy 1) | "OK", "CANCEL", "YES" |
| **+0x328** | **1,418** | **150** | **188,634** | **Goods Descriptions** | Prism Stone: "Warm pebble emitting a beautiful phasing..." |
| +0x330 | 1,236 | 61 | 37,844 | System Text (copy 2) | Protector ID→"Phantom Sign" (ID collision) |
| +0x338 | 508 | 82 | 19,136 | System Message Templates (copy 2) | "<?sysmsg@1?>", "<?sysmsg@2?>" |
| +0x340 | 1,003 | 71 | 318,864 | Dialog Buttons (copy 2) | "OK", "CANCEL", "YES" |
| +0x350 | 524 | 24 | 33,976 | **Spell/Magic Descriptions** | "Elementary sorcery. Fire soul arrow..." |
| +0x360 | 481 | 36 | 16,412 | Orange Soap Messages | "Try sliding down", "Weakness: Head" |
| **+0x370** | **53** | **2** | **18,932** | **Accessory Descriptions** | Ring of Sacrifice: "This ring was named after Havel the Roc..." |
| **+0x378** | **1,408** | **150** | **91,474** | **Goods Captions** | Prism Stone: "Path marker. Drop and listen to check he..." |
| **+0x380** | **1,468** | **150** | **52,100** | **Goods Names** | "Prism Stone", "Throwing Knife" |
| **+0x388** | **53** | **2** | **2,880** | **Accessory Captions** | Ring of Sacrifice: "Boosts maximum equipment load" |
| **+0x390** | **53** | **2** | **2,024** | **Accessory Names** | "Havel's Ring" |
| **+0x3B8** | **524** | **24** | **4,436** | **Spell/Magic Names** | "Soul Arrow" |
| +0x3C0 | 153 | 20 | 2,092 | Unknown (Havel ID→"") | Empty strings for accessory IDs |
| +0x3C8 | 222 | 20 | 3,008 | Unknown (Havel ID→"") | Empty strings for accessory IDs |
| +0x3D0 | 272 | 26 | 8,636 | Menu Help Text (copy 2) | "Browse and use items", "Change equipment" |
| +0x3D8 | 286 | 35 | 27,236 | Controller Prompts (copy 2) | "<?selectLR?>:Select <?conclusion?>:Enter" |
| **+0x3E0** | **1,354** | **67** | **30,042** | **System Text** | "DARK SOULS™: REMASTERED", "Multiplayer Victories" |
| +0x3E8 | 980 | 64 | 35,472 | System Dialog (copy 2) | "∞", "OK", "CANCEL" |

Non-FMG pointers (checked for indirect chains via Part B2 — none found):
+0x348, +0x358, +0x368, +0x398, +0x3A0, +0x3A8, +0x3B0

**Conclusion: Weapon and Protector name/caption/description FMGs are NOT accessible through MsgMan.** The entire 0x200-0x800 space has been exhaustively scanned and every FMG identified. None contain weapon names (e.g. "Dagger") or protector names (e.g. "Leather Armor"). Weapon/protector IDs that collide with system text entries (+0x270, +0x330, +0x3E0) return system strings, not item names.

**Implementation decision (SUPERSEDED)**: ~~Since weapon/protector FMGs cannot be found, AP stubs will be created in the correct param tables (for correct tab/icon) but ALL AP item text will be written to the Goods FMGs only.~~ **Actual implementation**: The native passthrough approach bypasses this problem entirely. When a shop location contains a real DSR item for the local player, the shop row uses the item's real `equipId` and `equipType` — DSR resolves the name, icon, and description from the item's own param table natively. AP stubs (for non-physical items or items for other players) still use EquipParamGoods with Goods FMG text, which works correctly since their equipType=3.

Each string table has: Header (0x1c bytes) → Span maps → String offset table → Unicode string data.

### 4.2 How DSAP Injects AP Items

When connecting, `ApItemInjectorHelper.AddAPItems()` performs:

1. **Create EquipParamGoods entries**: For each scouted location, a new param entry is added with:
   - ID = AP location ID (e.g., 11110042)
   - Category = Key Item
   - Icon = 2042 (generic icon)
   - Max stack = 99
   - Goods type = 1 (key)

2. **Add MsgMan strings**: For each AP item:
   - Name: `"{player}'s {item_display_name}"` (e.g., "Player1's Claymore")
   - Caption + Description: `"A {progression|useful|trap|normal} Archipelago item for {player}'s {game}."`

3. **Install function hooks** (x86-64 JMP-style detours):
   - `0x1407479E0` — Item-add hook: if item ID is in [min_ap_loc, max_ap_loc], `ret` (blocks DSR's normal item-add, preventing AP placeholder items from entering inventory)
   - `0x140728c90` — Popup hook: same range check, blocks the item-get popup for AP items

   `AP_ITEM_OFFSET = 10000` is used in range calculations.

### 4.3 DSR Internal Item Category Bitmask

DSR identifies item types via a bitmask prefix on the 32-bit item ID:
| Bitmask | Type |
|---------|------|
| 0x00000000 | Weapons, Shields, Spell Tools, Ranged |
| 0x10000000 | Armor |
| 0x20000000 | Rings |
| 0x40000000 | Consumables, Keys, Spells, Upgrade Materials |

DSAP adds custom categories:
| Bitmask | Type |
|---------|------|
| 0x11111111 | DsrEvent (fog wall unlocks) |
| 0x11111112 | BonfireWarp (bonfire warp unlocks) |
| 0x33333333 | Trap |

### 4.4 ShopLineupParam equipType Field

The `equipType` field in ShopLineupParam determines which param table the `equipId` references:
| equipType | Param Table | Meaning |
|-----------|-------------|---------|
| 0 | EquipParamWeapon | Weapon |
| 1 | EquipParamProtector | Armor |
| 2 | EquipParamAccessory | Accessory (Ring) |
| 3 | EquipParamGoods | Good (Consumable/Key/Material) |
| 4 | (Attunement) | Spell slot equip |

---

### 4.5 Vanilla Shop Row equipType Mapping (confirmed via `/paraminfo` 2026-04-01)

Complete equipType breakdown for all 87 merchant shop rows (shop_sanity OFF):

**Summary**: 47 Weapon (equipType=0), 4 Protector (equipType=1), 3 Accessory (equipType=2), 33 Goods (equipType=3)

**Protector rows (equipType=1)** — all at Undead Merchant Male:
| Row | equipId | Item | Value |
|---|---|---|---|
| 1129 | 170000 | Leather Armor set? | 500 |
| 1130 | 171000 | Leather Armor set? | 800 |
| 1131 | 172000 | Leather Armor set? | 500 |
| 1132 | 173000 | Leather Armor set? | 500 |

**Accessory rows (equipType=2)** — all at Oswald:
| Row | equipId | Item | Value |
|---|---|---|---|
| 4405 | 109 | Ring of Fog | 10000 |
| 4406 | 110 | Ring of Sacrifice? | 15000 |
| 4407 | 126 | Unknown ring | 5000 |

All remaining 80 rows are either Weapon (equipType=0, IDs 100000-2103000 range) or Goods (equipType=3, IDs 106-5700 range). Full data in `/paraminfo` dump files.

---

## 5. Detailed Outline of the AP Event System (DSR Actions → AP Server)

### 5.1 Location Monitoring Architecture

After connection, the client builds separate location lists per type:
- **Item Lots** (`GetItemLotLocations()`): EventFlagsBase + `GetEventFlagOffset(flag)` per ItemLots.json entry
- **Bosses** (`GetBossLocations()`): ProgressionFlagBase + Boss.Offset
- **Boss Flags** (`GetBossFlagLocations()`): EventFlagsBase + flag from BossFlags.json
- **Bonfires** (`GetBonfireFlagLocations()`): ProgressionFlagBase dereference chain + offset/bit from Bonfires.json
- **Doors** (`GetDoorFlagLocations()`): EventFlagsBase + flag from Doors.json
- **Fog Walls** (`GetFogWallFlagLocations()`): EventFlagsBase + flag from DsrEvents.json
- **Misc** (`GetMiscFlagLocations()`): EventFlagsBase + flag from MiscFlags.json

All lists combined → passed to `Client.MonitorLocationsAsync()`.

### 5.2 Polling Loop

`Archipelago.Core` monitors each Location object's `Address` + `AddressBit`. Each tick:
1. Read the byte at `Location.Address`
2. Check if bit `Location.AddressBit` is set
3. If newly set → fire `LocationCompleted` event

### 5.3 Location Completed Handler

When `Client_LocationCompleted(location)` fires:
1. If location name == "Lord of Cinder" → `session.SetGoalAchieved()`
2. If scouted info shows item belongs to another player → display popup message
3. AP server receives `LocationChecks` packet with the location ID
4. AP server routes the item to the receiving player's client

### 5.4 Item Receive Handler

When `Client_ItemReceived` fires:
1. **Native shop skip check**: If item is from the player's own slot AND the location is in `NativeShopLocationIds`, skip granting (the shop already gave the real item). Mark success.
2. Look up item in `AllItemsByApId` dictionary
3. Check `SlotLocToItemUpgMap` for weapon upgrade data → apply infusion+level
4. Route by item category:
   - **Trap** → `RunLagTrap()` (20-second effect)
   - **DsrEvent** → `ReceiveEventItem()` → find EmkController by ApId → unlock fog wall
   - **BonfireWarp** → `ReceiveBonfireWarpItem()` → write progression flag bit
   - **Normal item** → `AddItem()` or `AddItemWithMessage()` → execute shellcode in DSR process

### 5.5 Fog Wall (EMK) Management

Each fog wall is managed by an `EmkController`:
- `Eventid`/`Eventslot`: Identifies the fog wall event in DSR's linked list
- `MapId3`: First 3 digits of Eventid (determines which map)
- `HasKey`: Whether player has received this fog wall's AP item
- `Deactivated`: Whether node is currently removed from list
- `Saved_Ptr`: Pointer to removed linked list node

**Management loop (every 1 second)**:
- Traverse DSR's event linked list (head from AoB scan, nodes at +0x30=eventid, +0x34=slot, +0x68=next)
- If player does NOT have key AND event is in list AND player in correct map → unlink node (fog wall blocks)
- If player HAS key AND saved pointer exists → re-insert node at head (fog wall passable)

---

## 6. Detailed Explanation of ID/Number Systems

### 6.1 AP Location ID (aploc / `Id`)

The unique identifier for a randomizable location in the Archipelago system.

**Formula**: `base_id + dsr_code` where `base_id = 11110000`

Example: Location "UB: Residence Key" has `dsr_code` offset such that `Id = 11110042`.

This is what gets:
- Sent in `LocationChecks` packets to the AP server
- Used as keys in `ScoutLocationsAsync` results
- Stored in `DSRLocationData.id` in Locations.py
- Used as the `Id` field in `ItemLotFlag` / `BossFlag` / etc. JSON resources

### 6.2 ItemLotParamId (row)

The row ID in DSR's ItemLotParam table. This is a DSR-internal identifier with no AP significance.

**Defined in**: `ItemLots.json` as the `ItemLotParamId` field.

**Usage**: `ItemLotHelper.BuildLotParamIdToLotMap()` maps `ItemLotParamId → replacement ItemLot` so that when `UpdateItemLots()` rewrites the param table in memory, it can find and replace the correct row.

**Special values**: 
- `50000000` = White Sign Soapstone (SpecialItemLotIds)
- `50000100` = Key to the Seal (SpecialItemLotIds)

### 6.3 Event Flag (Flag / purchaseFlag)

An 8-digit integer that encodes a position in DSR's event flag bit array. Used for tracking whether an item lot has been picked up, a door opened, a boss killed, etc.

**Address resolution** (`AddressHelper.GetEventFlagOffset(flag)`):
```
Flag ID: ABCCCDDD (8 digits)
  A     → Primary offset (lookup table based on first digit)
  BCC   → Secondary offset (digits 1-3, ×16)  
  DDD   → Tertiary: byte_offset = ((DDD / 32) × 4) + primary + secondary
           bit_index = DDD % 32 → decomposed to byte and bit within that byte
Returns: (ulong byte_offset, int bit_index)
```

**Base address**: `EventFlagsBase` (resolved via AoB scan for event flags pattern)

**Usage in shops**: The ShopLineupParam `eventFlag` field uses the same system to track how many of a finite item have been sold.

**CRITICAL: DSR's byte-level overwrite behavior** — When DSR tracks a purchase via event flags, it **overwrites the entire byte** rather than using bitwise OR on individual bits. This means multiple flags sharing the same byte will corrupt each other's tracking state. Evidence: buying flag 71811220 (bit3) changed the shared byte from 0x10→0x08, clearing flag 71811219 (bit4). Purchase flags MUST be spaced so each occupies its own 4-byte word (32 tails apart minimum).

### 6.4 DSRLocationData `id` vs ItemLotFlag `Id`

These are the **same value** — the AP location ID. `DSRLocationData` in Locations.py defines it during world generation; `ItemLotFlag` in ItemLots.json defines it for corresponding C# client monitoring.

### 6.5 equipId

The DSR-internal item ID within its param table. For shop items, `ShopLineupParam.equipId` references an entry in the param table determined by `equipType`:
- equipType=0 → EquipParamWeapon row with ID = equipId
- equipType=1 → EquipParamProtector row with ID = equipId
- equipType=2 → EquipParamAccessory row with ID = equipId
- equipType=3 → EquipParamGoods row with ID = equipId

For DSAP's injected AP items, the `equipId` IS the AP location ID (e.g., 11110042) stored as a new EquipParamGoods entry.

### 6.6 Relationship Diagram

```
AP Location ID (e.g., 11110042)
  │
  ├── Used in: LocationChecks/ScoutLocations packets
  ├── Used in: DSRLocationData.id (Locations.py)
  ├── Used in: ItemLotFlag.Id (ItemLots.json)  
  ├── Used as: EquipParamGoods entry ID for injected AP items
  └── Used as: MsgMan entry ID for AP item names/captions/descriptions
  
ItemLotParamId (e.g., 1000)
  │
  └── Used in: ItemLotHelper replacement map (maps param row → new AP item lot)

Event Flag (e.g., 50000000)
  │
  ├── Used in: ItemLotFlag.Flag (which event bit tracks this pickup)
  ├── Used in: ShopLineupParam.eventFlag (tracks purchase count)
  └── Resolved to: (byte_offset, bit_index) relative to EventFlagsBase

equipId (e.g., 200000 for a specific weapon)
  │
  ├── Used in: ShopLineupParam.equipId (item sold in shop)
  ├── Used in: ItemLot.LotItemId (item dropped from lot)
  └── References: EquipParam{Weapon|Protector|Accessory|Goods} row
```

---

## 7. DSAP Repository Structure and Code Map

### 7.1 Python Apworld (`apworld/dsr/`)

| File | Purpose | Key Exports |
|------|---------|-------------|
| `__init__.py` | DSRWorld class — lifecycle methods, region graph, item pool, slot_data | `DSRWorld`, `base_id=11110000` |
| `Items.py` | Item definitions, upgrade system, pool builders | `DSRItemCategory`, `DSRItem`, `item_dictionary`, `BuildRequiredItemPool`, `UpgradeEquipment` |
| `Locations.py` | Location definitions (~850), category enum | `DSRLocationCategory`, `DSRLocationData`, `location_table`, `location_dictionary` |
| `Options.py` | All player-facing options | `DSROption` dataclass |
| `Groups.py` | Item/location name groups for YAML filtering | `item_name_groups`, `location_name_groups` |

### 7.2 C# Client (`source/DSAP/`)

| File | Purpose | Key Methods/Classes |
|------|---------|-------------------|
| `App.axaml.cs` | Main app (~1700 lines) — all connection, item, game logic | `OnConnectedAsync`, `Client_ItemReceived`, `Client_LocationCompleted`, `AddAbstractItem` |
| `DarkSoulsClient.cs` | IGameClient — DSR process attachment | `ProcessName="DarkSoulsRemastered"` |
| `Enums.cs` | Core enums | `DSItemCategory`, `DsrEventType`, `Bonfires`, `SpecialItemLotIds` |
| **Helpers/** | | |
| `AddressHelper.cs` | AoB patterns, event flag math, memory offsets | `GetEventFlagOffset()`, `GetEventFlagsOffset()`, `GetProgressionFlagOffset()` |
| `AoBHelper.cs` | Pattern scanner with caching | `Pointer()`, `Address()` |
| `ApItemInjectorHelper.cs` | Injects AP items into DSR memory | `AddAPItems()`, `AddAPItemHook()`, `AddAPItemPopupHook()` |
| `EmkHelper.cs` | Fog wall linked list manipulation | `PullEmkEvent()`, `PushEmkEvent()` |
| `ItemLotHelper.cs` | ItemLot replacement map, param overwrite | `BuildLotParamIdToLotMap()`, `UpdateItemLots()` |
| `LocationHelper.cs` | Maps game flags → AP Location objects | `GetItemLotLocations()`, `GetBossLocations()`, etc. |
| `MiscHelper.cs` | JSON resource loading, upgrades, position | `OpenEmbeddedResource()`, `UpgradeItem()` |
| `MsgManHelper.cs` | Message manager read/write | `ReadMsgManStruct()`, `WriteFromMsgManStruct()` |
| `ParamHelper.cs` | Generic param read/write | `ReadFromBytes()`, `WriteFromParamSt()` |
| **Models/** | Data models for all JSON resources and runtime state | `DarkSoulsItem`, `ItemLot`, `EmkController`, `ParamStruct<T>`, `MsgManStruct` |
| **Params/** | Row size/offset definitions per param type | `EquipParamGoods`, `EquipParamWeapon`, `MagicParam`, `ItemLotParam`, etc. |
| **Resources/** | Embedded JSON data files | `ItemLots.json`, `Bosses.json`, `Bonfires.json`, `DsrEvents.json`, etc. |

---

## 8. Key Constants Reference

| Constant | Value | Location | Purpose |
|----------|-------|----------|---------|
| base_id | 11110000 | `__init__.py`, `Enums.cs` | AP ID offset for all items and locations |
| AP_ITEM_OFFSET | 10000 | `ApItemInjectorHelper.cs` | Used in hook range calculation |
| Item-add hook addr | 0x1407479E0 | `ApItemInjectorHelper.cs` | DSR function detour |
| Popup hook addr | 0x140728c90 | `ApItemInjectorHelper.cs` | DSR popup detour |
| MsgMan root ptr | 0x141c7e3e8 | `MsgManHelper.cs` | Hardcoded pointer to MsgMan root |
| MsgMan Goods Names | +0x380 | Confirmed `/paraminfo` | FMG buffer for goods item names |
| MsgMan Goods Captions | +0x378 | Confirmed `/paraminfo` | FMG buffer for goods short descriptions |
| MsgMan Goods Descriptions | +0x328 | Confirmed `/paraminfo` | FMG buffer for goods long descriptions |
| MsgMan Ring Names | +0x390 | Confirmed `/paraminfo` | FMG buffer for accessory names |
| MsgMan Ring Captions | +0x388 | Confirmed `/paraminfo` | FMG buffer for accessory short descriptions |
| MsgMan Ring Descriptions | +0x370 | Confirmed `/paraminfo` | FMG buffer for accessory long descriptions |
| MsgMan System Text | +0x3E0 | Confirmed `/paraminfo` | FMG buffer for system messages |
| SoloParam → Weapon | +0x18 | `EquipParamWeapon.cs` | EquipParamWeapon entry size=0x110 |
| SoloParam → Protector | +0x60 | Confirmed `/paraminfo` | EquipParamProtector entry size=0xE8 |
| SoloParam → Accessory | +0xA8 | Confirmed `/paraminfo` | EquipParamAccessory entry size=0x40 |
| SoloParam → Goods | +0xF0 | `EquipParamGoods.cs` | EquipParamGoods entry size=0x5C |
| SoloParam → ShopLineup | +0x720 | `ShopLineupParam.cs` | ShopLineupParam entry size=0x20 |
| SoloParam → ItemLot | +0x570 | `AddressHelper.cs` | ItemLotParam entry size=0x94 |
| SoloParam → Magic | +0x408 | `MagicParam.cs` | MagicParam entry size=0x30 |
| SoloParam → CharaInit | +0x600 | `CharaInitParam.cs` | CharaInitParam entry size=0xF0 |
| SoloParam → Move | +0x5B8 | `MoveParam.cs` | MoveParam entry size=0x8D |
| Goods icon offset | +0x2C in entry | `ApItemInjectorHelper.cs` | short (s16), icon ID within EquipParamGoods |
| Purchase flag spacing | 32 tails apart in 7181xxxx | `ShopFlags.json` | Each flag gets own 4-byte word (see §6.3 byte-overwrite warning) |
| EMK node: eventid | +0x30 | `EmkHelper.cs` | Fog wall linked list |
| EMK node: eventslot | +0x34 | `EmkHelper.cs` | |
| EMK node: next_ptr | +0x68 | `EmkHelper.cs` | |
| ParamOffset1 | 0x38 | `ParamHelper.cs` | Param dereference chain |
| ParamOffset2 | 0x10 | `ParamHelper.cs` | |
| apworld_api_version | "0.1.0.0" | `__init__.py` / `DarkSoulsOptions.cs` | Client/apworld compat check |

---

## 9. Param Read/Write System

### 9.1 Reading a Param (Dereference Chain)

Confirmed via code (`ParamHelper.ReadFromBytes`) and `/paraminfo` diagnostics:

```
SoloParamAob.Address + spOffset (e.g., +0x720 for ShopLineupParam)
  → ReadULong → ResCapLoc pointer
  → ResCapLoc + 0x30 → ReadUInt → BufferSize
  → ResCapLoc + 0x38 → ReadULong → BufferLoc
```

**Buffer layout** starting at `BufferLoc - 0x10`:

```
Offset   Size    Field
------   ----    -----
0x00     0x10    Prologue (contains total allocation size at +0x00)
0x10     0x04    string_offset (int32, relative to 0x10)
0x14     0x02    paramsOffset (uint16, relative to 0x10)
0x16     0x04    (padding/flags)
0x1A     0x02    num_entries (uint16)
0x1C     0x14    (header continued, total header = 0x30 from offset 0x10)
0x40     N×0x0C  Entry table: per entry { uint32 id, uint32 paramOffset, int32 strOffset }
                 paramOffset is absolute from offset 0x10; subtract paramsOffset to get ParamBytes index
variable         Param data bytes (starting at offset 0x10 + paramsOffset)
variable         String table (starting at offset 0x10 + string_offset)
```

**Entry table starts at**: `BufferLoc + 0x30` (= `BufferLoc - 0x10 + 0x40` in AllBytes)

**Param data for entry i**: `BufferLoc - 0x10 + paramsOffset + (entry[i].paramOffset - paramsOffset)`

### 9.2 Writing a Param

1. Build complete byte array (header + entries + params + DescArea metadata)
2. Allocate new memory block in DSR process via `Memory.Allocate()`
3. Write byte array to new allocation
4. Switch the ResCapLoc pointer to the new memory address
5. Free old memory block

**DescArea** (28 bytes appended to every written param): stores `FullAllocLength`, `OldAddress`/`OldLength` (for cleanup), `SeedHash`, `Slot` (for session validation).

### 9.3 Current Param Modifications

| Param | Action | When |
|-------|--------|------|
| EquipParamGoods | Add new entries for AP items | On connect (ApItemInjectorHelper) |
| EquipParamWeapon | Zero str/dex/int/faith requirements | If no_weapon_requirements option |
| MagicParam | Zero stat requirements + covenant bits | If no_spell_stat_requirements option |
| ItemLotParam | Overwrite lot items with AP item IDs | On connect (ItemLotHelper) |
| CharaInitParam | Modify starting equipment slots | If randomize_starting_loadouts option |
| ShopLineupParam | Overwrite equipId/value/equipType/eventFlag per row | If shop_sanity option (ShopHelper) |

---

## 10. Save Validation System

On each connection, the client:
1. Reads seed hash + slot from event flag memory gaps (offsets 124-126 after event flag 960)
2. Compares against current AP session's seed hash and slot
3. If mismatch → warns player, prompts `/resetsave` or `/saveloaded`
4. If match → sets `SaveidSet = true`, enabling item receives and location checks

---

## 11. Weapon Upgrade System

- Weapons have a `DSRUpgradeType`: NotUpgradable, Unique, Armor, Infusable, InfusableRestricted, PyroFlame, PyroFlameAscended
- 10 infusion types: Normal (0-15), Crystal, Lightning, Raw, Magic, Enchanted, Divine, Occult, Fire, Chaos
- Each infusion has: (name, modifier, max_level, level_adjustment)
- `UpgradeEquipment()` (in `Items.py`) rolls RNG based on `upgraded_weapons_percentage` option
- Upgrade data passed to client via `fill_slot_data()` as `itemsAddress`/`itemsId`/`itemsUpgrades` arrays
- Client applies upgrades via `MiscHelper.UpgradeItem()` when receiving weapons

---

## 12. DeathLink Implementation

- **Send**: Monitors player HP address every tick via GameDataMan path (read-only). When HP drops to 0 → sends DeathLink bounce packet (25-second grace period to prevent loops)
- **Receive**: Writes 0 to the writable HP address via WorldChrManImp path, killing the player instantly
- **Two HP paths**: Read-only (GameDataMan chain) for monitoring, writable (WorldChrManImp chain) for killing

---

## 13. Client Commands

| Command | Action |
|---------|--------|
| `/connect` | Reconnect to AP server |
| `/help` | List commands |
| `/unstuck` | Teleport to last bonfire |
| `/warp` | Warp to specific bonfire |
| `/resetsave` | Clear save IDs for fresh session |
| `/saveloaded` | Confirm loaded save matches session |
| `/pid` | Show current DSR process ID |
| `/deathlink` | Toggle DeathLink |
| `/fog` / `/bossfog` | List fog wall states |
| `/lock` | List locked events |
| `/goalcheck` | Check goal completion status |
| `/diag` | Diagnostic information |
| `/shopdiag` | Dump shop param memory state to Documents/DSAP/ |
| `/paraminfo` | Dump param table metadata, MsgMan offsets, and raw param bytes to Documents/DSAP/ |
| `/lordvessel` | Check Lordvessel status |
| `/cef` / `/mef` | Check/modify event flags |

---

## 14. Archipelago World API Key Patterns (for Feature Development)

### 14.1 World Class Requirements

```python
class DSRWorld(World):
    game = "Dark Souls Remastered"
    base_id = 11110000
    options_dataclass = DSROption
    item_name_to_id = DSRItem.get_name_to_id()
    location_name_to_id = DSRLocation.get_name_to_id()
    item_name_groups = item_name_groups
    location_name_groups = location_name_groups
```

### 14.2 Item/Location ID Assignment

IDs must be unique per type per game. `code = base_id + dsr_code`. Events have `code = None` and are excluded from network data.

### 14.3 Fill Algorithm Understanding

AP's fill algorithm places items using `fill_restrictive()`:
- Items are placed round-robin across players
- Each placement must maintain logic solvability
- `progression` items are placed first, then `useful`, then `filler`
- `skip_balancing` flag exempts items from being moved to earlier spheres
- Item count must match location count (pad with filler)

### 14.4 Options → Client Transfer

Options are NOT automatically sent. Must be serialized in `fill_slot_data()`. Client receives as `slot_data` dict in `Connected` packet, accessed via `LoginSuccessful.SlotData`.

---

## 15. Multi-Param Stub Implementation (2026-04-01)

### 15.1 Problem

All AP shop items were forced into `EquipParamGoods` with `equipType=3`, causing them to appear in the wrong shop tab (e.g., weapons appearing in the Goods tab) with incorrect icons. The existing `upgradeGoods()` also wrote the icon to the wrong offset (`+0x2C` instead of `+0x3C`).

### 15.2 Approach

Create *additional* param stubs in the correct param table (Weapon/Protector/Accessory) for shop items that originally occupied those slots. All items retain their Goods stubs as a baseline for the pickup/popup hook systems. The `ShopLineupParam.equipType` is set to match the original slot so the game renders items in the correct tab with correct icons. Weapon/Protector FMGs are not accessible through MsgMan, so all text stays in Goods FMGs. Accessory (Ring) FMGs are accessible and receive text entries.

### 15.3 Files Modified (C# client-side only — no apworld changes)

1. **`Helpers/ApItemInjectorHelper.cs`** — Major refactor:
   - `AddAPItems()`: Builds `shopEquipTypes` lookup from ShopFlags, partitions entries by vanilla equip type, calls per-type upgrade methods, writes Ring FMG text for accessory items
   - `upgradeGoods()`: Fixed icon offset from `+0x2C` → `+0x3C` (was writing to wrong field)
   - Added `upgradeWeapons()`: Creates stubs in EquipParamWeapon (icon at `+0xCA` = Dagger icon 1)
   - Added `upgradeProtectors()`: Creates stubs in EquipParamProtector (iconM/F at `+0xB2`/`+0xB4` = Leather Armor icon 1052)
   - Added `upgradeAccessories()`: Creates stubs in EquipParamAccessory (icon at `+0x32` = Ring of Sacrifice icon 4000)

2. **`Helpers/ShopHelper.cs`** — `BuildShopReplacementMap()` uses `shop.VanillaEquipType` instead of hardcoded `3`

3. **`Resources/ShopFlags.json`** — Added `VanillaEquipType` field to all 87 entries (47 weapon=0, 4 protector=1, 3 accessory=2, 33 goods=3)

4. **`Models/ShopFlag.cs`** — Added `public int VanillaEquipType { get; set; } = 3;`

5. **`Models/MsgManStruct.cs`** — Added Ring FMG constants: `OFFSET_RING_NAMES=0x390`, `OFFSET_RING_CAPTIONS=0x388`, `OFFSET_RING_DESCRIPTIONS=0x370`

6. **`Models/Params/EquipParamProtector.cs`** — New file (Size=0xE8, spOffset=0x60)

7. **`Models/Params/EquipParamAccessory.cs`** — New file (Size=0x40, spOffset=0xA8)

8. **`Models/Params/EquipParamGoods.cs`** — Fixed Size from 0x4C to 0x5C

9. **`Models/Params/EquipParamWeapon.cs`** — Fixed Size from 0x10B to 0x110

### 15.4 Icon Offset Correction (REVERTED)

**Original +0x10 shift theory was WRONG.** In-game testing (2026-04-01) proved the Paramdex XML offsets were correct all along. Writing icon to +0x3C produced White Sign Soapstone (template default); writing to +0x2C produced Prism Stone (correct). All offsets reverted to original Paramdex values:

| Param Table | Correct Offset | Verification |
|---|---|---|
| EquipParamGoods | **+0x2C** | Writing 2042 here shows Prism Stone icon (confirmed in-game) |
| EquipParamWeapon | **+0xBA** | Paramdex XML (not yet tested in-game) |
| EquipParamProtector | **+0xA2/+0xA4** | Paramdex XML (not yet tested in-game) |
| EquipParamAccessory | **+0x22** | Paramdex XML (not yet tested in-game) |

### 15.5 Architecture

```
All AP Items → EquipParamGoods stubs (baseline for pickup/popup hooks)
                ↓ additionally, for shop items:
equipType=0 → EquipParamWeapon stub  → shop renders in Weapons tab
equipType=1 → EquipParamProtector stub → shop renders in Armor tab
equipType=2 → EquipParamAccessory stub → shop renders in Rings tab
equipType=3 → (Goods stub already exists) → shop renders in Goods tab

Text: All items → Goods FMGs (names/captions/descriptions)
      Accessory items → also Ring FMGs (names/captions/descriptions)
      Weapon/Protector items → NO dedicated FMGs available (not in MsgMan)
```

---

This file is intended to be updated only with information that is directly supported by logs, code, or other objective evidence. Subjective interpretations, assumptions, or speculative notes should be kept elsewhere.

**Companion file**: `DSAPShopResearch_RawData.md` — large data dumps, full param layouts, external reference URLs, complete ID tables.
