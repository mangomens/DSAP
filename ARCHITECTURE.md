# DSAP — Dark Souls Remastered Archipelago Client & World

## Complete Architecture Reference

---

## 1. Repository Structure

```
DSAP/
├── azure-pipelines.yml              # CI/CD pipeline
├── imgui.ini                        # Dear ImGui layout state
├── LICENSE / README.md
│
├── apworld/                         # Python — Archipelago World (server-side generation)
│   └── dsr/
│       ├── __init__.py              # DSRWorld class — region/item/connection logic
│       ├── Items.py                 # Item definitions, upgrade system, item pool building
│       ├── Locations.py             # Location definitions (~850 locations), categories
│       ├── Options.py               # All player-facing options (DSROption dataclass)
│       └── Groups.py               # Item/location name groups for YAML filtering
│
└── source/
    ├── DSAP/                        # C# Avalonia UI desktop client
    │   ├── App.axaml / App.axaml.cs # Main application (~1700 lines) — all connection, item, and game logic
    │   ├── DarkSoulsClient.cs       # IGameClient implementation — process attachment
    │   ├── Enums.cs                 # Core enumerations (categories, events, loadouts, bonfires)
    │   │
    │   ├── Helpers/
    │   │   ├── AddressHelper.cs     # Memory address resolution — AoB patterns, event flag math
    │   │   ├── AoBHelper.cs         # Array-of-Bytes pattern scanner with caching
    │   │   ├── ApItemInjectorHelper.cs  # Injects AP items into DSR's params + message tables + hooks
    │   │   ├── EmkHelper.cs         # Event (fog wall) linked-list manipulation
    │   │   ├── ItemLotHelper.cs     # ItemLot replacement map building and param overwriting
    │   │   ├── LocationHelper.cs    # Maps game flags → AP Location objects
    │   │   ├── MiscHelper.cs        # Item loading from JSON, upgrades, position, save IDs
    │   │   ├── MsgManHelper.cs      # Message manager read/write (item names, descriptions)
    │   │   └── ParamHelper.cs       # Generic param read/write, weapon/spell requirement removal
    │   │
    │   ├── Models/
    │   │   ├── BonfireFlag.cs       # {Name, Id, Flag}
    │   │   ├── BonfireWarp.cs       # EventFlag + Offset, AddressBit, Flag, ItemId, ItemName, DsrId
    │   │   ├── Boss.cs / BossFlag.cs
    │   │   ├── DarkSoulsItem.cs     # Item model — Name, Id, StackSize, UpgradeType, Category, ApId
    │   │   ├── DarkSoulsOptions.cs  # Options parsed from AP slot data
    │   │   ├── DescArea.cs          # Metadata block appended to params (seed, slot, reload info)
    │   │   ├── DoorFlag.cs / FogWallFlag.cs
    │   │   ├── DsrEvent.cs          # Fog wall event definition
    │   │   ├── EmkController.cs     # Runtime fog wall lock controller
    │   │   ├── EventFlag.cs         # Base {Name, Id, Flag}
    │   │   ├── Gift.cs              # Starting gift definition
    │   │   ├── IParam.cs            # Param interface
    │   │   ├── ItemLot.cs           # ItemLot param row (8 items per lot)
    │   │   ├── ItemLotFlag.cs       # EventFlag + IsEnabled, ItemLotParamId
    │   │   ├── ItemLotItem.cs       # Single item within an ItemLot row
    │   │   ├── LastBonfire.cs / Loadout.cs / ThiefItem.cs
    │   │   ├── MsgManStruct.cs      # Message manager binary structure (names, captions, descriptions)
    │   │   └── ParamStruct.cs       # Generic SoloParam binary structure (header, entries, strings)
    │   │
    │   ├── Params/                  # Param row size/offset definitions
    │   │   ├── CharaInitParam.cs    # Character initialization params
    │   │   ├── EquipParamGoods.cs   # Consumable/key item params
    │   │   ├── EquipParamWeapon.cs  # Weapon params (includes stat requirement offsets)
    │   │   ├── ItemLotParam.cs      # ItemLot param row layout
    │   │   ├── MagicParam.cs        # Spell params (stat/covenant requirements)
    │   │   └── MoveParam.cs         # Movement params
    │   │
    │   └── Resources/               # Embedded JSON data files
    │       ├── Bonfires.json        # Bonfire warp definitions
    │       ├── BonfireFlags.json    # Bonfire progression flags
    │       ├── BossFlags.json       # Boss kill flags
    │       ├── Bosses.json          # Boss definitions
    │       ├── Consumables.json, KeyItems.json, Rings.json, etc.  # Item catalogs
    │       ├── DsrEvents.json       # Fog wall event definitions
    │       ├── DoorFlags.json, FogWallFlags.json, MiscFlags.json
    │       ├── ItemLots.json        # ItemLot → event flag mappings
    │       ├── Gifts.json, ThiefItems.json
    │       └── Loadouts.json, LastBonfires.json
    │
    └── DSAP.Desktop/                # Platform host (Avalonia desktop entry point)
        └── DSAP.Desktop.csproj
```

---

## 2. High-Level Architecture

DSAP consists of two independent components that communicate via an **Archipelago server**:

```
┌─────────────────────────┐       ┌──────────────────┐       ┌─────────────────────────┐
│  Python Apworld (dsr/)  │       │   AP Server       │       │  C# Client (DSAP)       │
│  - World generation     │──────▶│   (multiworld)    │◀──────│  - DSR process hook      │
│  - Item/location defs   │       │                    │       │  - Memory read/write     │
│  - Region/logic graph   │       └──────────────────┘       │  - Param manipulation    │
│  - Slot data creation   │                                    │  - Fog wall management   │
└─────────────────────────┘                                    └─────────────────────────┘
```

**Base ID**: `11110000` — all AP item and location IDs are offset from this value.

---

## 3. Apworld (Python) — Server-Side Generation

### 3.1 DSRWorld (`__init__.py`)

The `DSRWorld` class extends `World` with:
- **`game`** = `"Dark Souls Remastered"`
- **`base_id`** = `11110000`
- **`options_dataclass`** = `DSROption`
- **`item_name_groups`** / **`location_name_groups`** imported from `Groups.py`

#### Lifecycle Methods

| Method | Purpose |
|--------|---------|
| `generate_early()` | Determines which location categories are enabled based on options (fogwall_sanity, boss_fogwall_sanity) |
| `create_regions()` | Builds ~110 named regions modeling DSR's world topology, with uni/bidirectional connections |
| `create_items()` | Builds matched item pool: required items → guaranteed items → fill remaining with filler/equipment/souls |
| `fill_slot_data()` | Returns dict with all options + weapon upgrade mapping (itemsAddress, itemsId, itemsUpgrades) + API version |

#### Region Graph

The world models DSR's interconnected areas as ~110 regions with connections. Key design patterns:

- **Sub-regions**: Areas are split by progression gates (e.g., "Sen's Fortress", "Sen's Fortress - After First Fog", "Sen's Fortress - Iron Golem")
- **Door regions**: Separate regions for doors/shortcuts (e.g., "Undead Burg Basement Door")
- **Boss regions**: Split into boss arena and post-boss areas
- **DLC**: Full Artorias of the Abyss content (Sanctuary Garden → Oolacile → Chasm of the Abyss)

Connections are created via `create_connection()` (one-way) and `create_connection_2way()`. Logic rules are set via `set_rule()` with lambda conditions checking for required items.

#### Location Handling in `create_region()`

For each location in a region:
1. **Enabled category** (EVENT, BOSS, ITEM_LOT, etc.) → Creates a real `DSRLocation` that participates in randomization
2. **Locked category** (BONFIRE_WARP when not randomized) → Places the default item statically (locked)
3. **Disabled category** → Creates an event location with a locked self-referencing item (invisible to player)

### 3.2 Items (`Items.py`)

**Categories** (`DSRItemCategory` enum):
| Value | Category | Description |
|-------|----------|-------------|
| 0 | SKIP | Not placed |
| 1 | EVENT | Progression events (e.g., "Dusk Rescued") |
| 2 | KEY_ITEM | Keys, quest items |
| 3 | BOSS_SOUL | Boss soul drops |
| 4-9 | WEAPON, SHIELD, ARMOR, RING, SPELL, CONSUMABLE | Equipment/consumables |
| 10-12 | EMBER, UPGRADE_MATERIAL, BOSS_SOUL | Smithing materials |
| 13-14 | FOGWALL, BOSSFOGWALL | Fog wall key items |
| 15-16 | FILLER, NOTHING | Pool padding |
| 17 | BONFIREWARP | Bonfire warp unlocks |

**Item ID Ranges** (DSR codes, add base_id for AP IDs):
| Range | Type |
|-------|------|
| 1000-1101 | Events |
| 1200-1216 | Fog Wall Keys |
| 1230-1251 | Boss Fog Wall Keys |
| 1300-1320 | Bonfire Warp Unlocks |
| 2000-2116 | Consumables |
| 3000-3034 | Key Items |
| 4000-4040 | Rings |
| 5000-5009 | Embers |
| 5010-5102 | Upgrade Materials |
| 6000-6071 | Spells (Sorceries, Pyromancies, Miracles) |
| 7000-7240 | Armor |
| 8000-8152 | Weapons (Melee, Ranged, Spell Tools) |
| 8200-8206 | Ammunition (bulk) |
| 8300-8303 | Pre-infused weapons (Lightning Spear, etc.) |
| 9000-9043 | Shields |
| 9900-9902 | Filler/Nothing |
| 10000 | Lag Trap |

**Weapon Upgrade System**:
- Weapons have a `DSRUpgradeType`: Infusable, InfusableRestricted, Unique, NotUpgradable, PyroFlame, PyroFlameAscended
- 10 infusion types: Normal (0-15), Crystal, Lightning, Raw, Magic, Enchanted, Divine, Occult, Fire, Chaos
- Each infusion has: (name, modifier, max_level, level_adjustment)
- `UpgradeEquipment()` rolls RNG based on `upgraded_weapons_percentage` option, picks random allowed infusion and level
- Upgrade data is passed to client via `fill_slot_data()` as `itemsAddress`/`itemsId`/`itemsUpgrades` arrays

### 3.3 Locations (`Locations.py`)

**Categories** (`DSRLocationCategory` enum):
| Value | Category | Description |
|-------|----------|-------------|
| 0 | SKIP | Never randomized |
| 1 | EVENT | Progression milestones |
| 2 | BOSS | Boss defeats |
| 3 | BONFIRE | Bonfire kindling |
| 4 | DOOR | Door/shortcut opening |
| 5 | ITEM_LOT | World item pickups (largest category) |
| 6 | ENEMY_DROP | Guaranteed enemy drops |
| 7 | FOG_WALL | Area fog wall passages (opt-in) |
| 8 | BOSS_FOG_WALL | Boss fog wall passages (opt-in) |
| 9 | SHOP_ITEM | Shop purchases |
| 10 | BONFIRE_WARP | Bonfire warp point unlocks |

**Location Tables**: Dict of `region_name → List[DSRLocationData]` covering all ~850 locations across ~110 regions. Each `DSRLocationData` contains:
- `id`: Absolute AP location ID (base_id + offset)
- `name`: Human-readable name (e.g., "AL: Lordvessel")
- `default_item`: Vanilla item at this location
- `category`: DSRLocationCategory

### 3.4 Options (`Options.py`)

The `DSROption` dataclass aggregates all options:

| Group | Option | Type | Default |
|-------|--------|------|---------|
| Sanity | `fogwall_sanity` | DefaultOnToggle | On |
| Sanity | `boss_fogwall_sanity` | Toggle | Off |
| Logic | `logic_to_access_catacombs` | Choice (5 options) | andre_or_undead_merchant |
| Equipment | `randomize_starting_loadouts` | DefaultOnToggle | On |
| Equipment | `randomize_starting_gifts` | DefaultOnToggle | On |
| Equipment | `require_one_handed_starting_weapons` | DefaultOnToggle | On |
| Equipment | `extra_starting_weapon_for_melee_classes` | Toggle | Off |
| Equipment | `extra_starting_shield_for_all_classes` | Toggle | Off |
| Equipment | `starting_sorcery` | Choice | Soul Arrow |
| Equipment | `starting_miracle` | Choice | Heal |
| Equipment | `starting_pyromancy` | Choice | Fireball |
| Equipment | `no_weapon_requirements` | Toggle | Off |
| Equipment | `no_spell_stat_requirements` | Toggle | Off |
| Equipment | `no_miracle_covenant_requirements` | DefaultOnToggle | On |
| Upgraded Weapons | `upgraded_weapons_percentage` | Range 0-100 | 0 |
| Upgraded Weapons | `upgraded_weapons_allowed_infusions` | OptionList | All 10 |
| Upgraded Weapons | `upgraded_weapons_adjusted_levels` | DefaultOnToggle | On |
| Upgraded Weapons | `upgraded_weapons_min_level` | Range 0-15 | 0 |
| Upgraded Weapons | `upgraded_weapons_max_level` | Range 0-15 | 15 |
| — | `enable_deathlink` | Toggle | Off |

### 3.5 Groups (`Groups.py`)

Defines `item_name_groups` (Key items, Fog Wall Keys, Weapons, Spells, Junk, etc.) and `location_name_groups` (per-region, All Doors, All Item Lots, All DLC regions, etc.) for YAML plando/exclusion filtering.

---

## 4. C# Client — Desktop Application

### 4.1 Process Attachment (`DarkSoulsClient.cs`)

Implements `IGameClient` interface from `Archipelago.Core`:
- **ProcessName**: `"DarkSoulsRemastered"`
- **IsConnected**: Whether DSR process handle is valid
- **ProcIds**: Enumerates all running DSR instances
- Supports multiple DSR instances running simultaneously

### 4.2 Core Enumerations (`Enums.cs`)

**`DSItemCategory`** (hex bitmask for DSR's internal item ID structure):
| Category | Bitmask | Items |
|----------|---------|-------|
| Weapons/Shields/SpellTools/Ranged | `0x00000000` | Physical equipment |
| Armor | `0x10000000` | All armor pieces |
| Rings | `0x20000000` | All rings |
| Consumables/Keys/Spells/Upgrades | `0x40000000` | Most inventory items |
| DsrEvent | `0x11111111` | Custom AP event items |
| BonfireWarp | `0x11111112` | Custom AP bonfire warp items |
| Trap | `0x33333333` | Trap items (Lag Trap) |

**`DsrEventType`**: FOGWALL=1, BOSSFOGWALL=2, EARLYFOGWALL=3

**`Bonfires`** enum: Maps bonfire names to their param IDs (e.g., `FirelinkShrine=1020980`).

### 4.3 Memory Architecture (`AddressHelper.cs`, `AoBHelper.cs`)

The client hooks DSR's process memory via `Archipelago.Core.Util.Memory`. Key memory structures are located via **Array-of-Bytes (AoB)** pattern scanning:

| AoB Pattern | Target | Purpose |
|-------------|--------|---------|
| `BaseB` | GameDataMan | Player stats, inventory, HP (read) |
| `BaseE` | WorldDataMan | Current map ID, world state |
| `BaseX` | WorldChrManImp | Player HP (writable, for deathlink) |
| `EmkHead` | Event list head | Fog wall linked list |
| `SoloParam` | Param table root | All game params (items, weapons, magic) |

#### AoB Scanner (`AoBHelper`)
- Scans for byte patterns in static memory regions (cached after first find)
- **`Pointer`**: AoB scan → extract relative offset → compute absolute address of global pointer
- **`Address`**: Dereferences the global pointer (NOT cached — value changes at runtime, e.g., after load screens)

#### Event Flag Address Resolution
DSR stores event flags as individual bits in a large byte array. The 8-digit flag ID encodes its location:
```
Flag ID: ABCCCDDD
  A     → Primary offset (lookup table, digit 0)
  BCC   → Secondary offset (digits 1-3, ×16)
  DDDD  → Tertiary calculation (digits 4-7):
           byte_offset = ((DDD / 32) × 4) + primary + secondary
           bit_index   = DDD % 32 → further decomposed to byte and bit
```

#### Save ID Storage
Uses gaps in the event flag memory (offsets 124-126 after event flag 960) to store:
- **Seed hash** — validates correct randomized save
- **Save ID** — tracks item receive progress
- **Slot** — AP player slot number

### 4.4 Connection & Initialization Flow (`App.axaml.cs`)

#### Full Connect Sequence

```
User clicks Connect
    │
    ├─ 1. Attach to DSR process via PID (DarkSoulsClient)
    ├─ 2. Create ArchipelagoClient, connect to AP server
    ├─ 3. Login to AP slot
    ├─ 4. Enable DeathLink if option is set
    ├─ 5. Initialize overlay (if running)
    │
    ▼ OnConnectedAsync fires:
    │
    ├─ 6. Fetch slot data from AP DataStorage
    ├─ 7. Parse DSOptions from slot data
    ├─ 8. Build EmkControllers (fog wall lock list) from slot data
    ├─ 9. Build SlotLocToItemUpgMap (weapon upgrade mapping)
    ├─10. Scout ALL locations → get scoutedLocationInfo
    ├─11. AddAPItems (inject AP item defs into DSR memory)
    │     ├─ Create EquipParamGoods entries for each AP item
    │     ├─ Add item names/captions/descriptions to MsgMan
    │     └─ Install function hooks (block normal item-add for AP items)
    ├─12. BuildLotParamIdToLotMap (build ItemLot replacement map)
    ├─13. RandomizeStartingLoadouts (if option enabled)
    ├─14. RemoveWeaponRequirements (if option enabled)
    ├─15. RemoveSpellRequirements (if option enabled)
    ├─16. UpdateItemLots → overwrite ItemLotParam in memory + force reload
    │
    ▼ Post-connect:
    │
    ├─17. DetectEventKeys → scan already-received items for fog wall unlocks
    ├─18. Build location lists for monitoring:
    │     (boss, itemlot, bonfire, door, fogwall, misc)
    ├─19. Client.MonitorLocationsAsync(fullLocationsList)
    ├─20. Start EMK watchers (fog wall management loop, every 1s)
    └─21. Start InGame watcher
```

### 4.5 Item Receive Flow (AP → DSR)

```
AP Server sends item to client
    │
    ▼ Client_ItemReceived fires
    │
    ├─ Look up item in AllItemsByApId dictionary
    ├─ Check SlotLocToItemUpgMap for weapon upgrades
    │   └─ If upgrade exists → apply infusion+level via MiscHelper.UpgradeItem()
    │
    ▼ AddAbstractItem() routes by category:
    │
    ├─ Trap → RunLagTrap() (20-second lag effect)
    ├─ DsrEvent → ReceiveEventItem()
    │   └─ Find EmkController by ApId → emk.Unlock()
    │   └─ On next EMK scan, fog wall re-added to linked list if player is in correct map
    ├─ BonfireWarp → ReceiveBonfireWarpItem()
    │   └─ Write progression flag bit at bonfire's offset
    ├─ Normal item → AddItem() or AddItemWithMessage()
    │   └─ Execute shellcode in DSR: writes item ID + category + quantity
    │   └─ With message variant: also triggers in-game pickup popup
    │
    └─ Mark success if player is still in-game
```

### 4.6 Location Check Flow (DSR → AP)

```
DSR player picks up item / defeats boss / passes fog wall
    │
    ▼ Location monitoring loop (Archipelago.Core)
    │
    ├─ For each monitored Location:
    │   └─ Read bit at Location.Address[Location.AddressBit]
    │   └─ If bit flipped to 1 → location is "completed"
    │
    ▼ Client_LocationCompleted fires
    │
    ├─ If location name == "Lord of Cinder" → send goal completion
    ├─ If scouted & item belongs to another player → show popup
    └─ AP server notified → sends item to recipient's client
```

#### How Locations Map to Memory

| Location Type | Address Resolution |
|---------------|-------------------|
| Item Lots | EventFlagsBase + `GetEventFlagOffset(flag)` → byte+bit from ItemLots.json |
| Bosses | ProgressionFlagBase + Boss.Offset |
| Boss Flags | ProgressionFlagBase + flag offset from BossFlags.json |
| Bonfires | ProgressionFlagBase + flag offset from BonfireFlags.json |
| Doors | EventFlagsBase + flag offset from DoorFlags.json |
| Fog Walls | EventFlagsBase + flag offset from FogWallFlags.json |
| Misc Flags | EventFlagsBase + flag offset from MiscFlags.json |

### 4.7 AP Item Injection System (`ApItemInjectorHelper.cs`)

When connecting, the client injects AP-specific item definitions into DSR's live memory:

#### Step 1: Create EquipParamGoods Entries
For each scouted location, creates a new EquipParamGoods entry:
- **ID**: location_id (the AP location ID, e.g., 11110042)
- **Category**: Key Item
- **Icon**: 2042 (generic AP icon)
- **Max stack**: 99

#### Step 2: Add Message Strings
For each AP item, adds entries to MsgMan (message manager):
- **Item Name**: e.g., "Claymore" (for own items) or "Andre - Crest of Artorias" (for others)
- **Item Caption**: "A {classification} Archipelago item for {player}'s {game}"
- **Item Description**: Same as caption

#### Step 3: Install Function Hooks
Two JMP-style hooks are installed in DSR's code:

| Hook Address | Purpose |
|-------------|---------|
| `0x1407479E0` | **Item-add hook**: If item ID is between min/max AP location IDs, blocks normal DSR item-add behavior (prevents adding AP placeholder items to inventory) |
| `0x140728c90` | **Popup hook**: Similarly blocks the item-get popup for AP item IDs |

**AP_ITEM_OFFSET** = `10000` — used when calculating the range of AP item IDs.

### 4.8 ItemLot Replacement System (`ItemLotHelper.cs`)

The core item randomization mechanism in DSR memory:

1. **`BuildLotParamIdToLotMap()`**:
   - For each scouted location: maps the original ItemLotParam ID → new ItemLot containing the AP item ID
   - Each replacement lot has a single `ItemLotItem` with `LotItemId = locationId`, `LotItemCategory = KeyItems`
   - Also fills Frampt's chest (param IDs 4000-4076) with Rubbish to prevent unrandomized items

2. **`UpdateItemLots()`** (called on connect):
   - Reads current ItemLotParam from memory
   - Adds initialization lots (White Sign Soapstone, Key to the Seal at special param IDs 50000000/50000100)
   - Overwrites matching param entries with replacement map
   - Writes modified param back to allocated memory
   - Forces a load screen via Homeward Bone to reload params

3. **Memory Layout**: Each ItemLot row is 148 bytes with 8 item slots per lot. The overwrite replaces the first slot with the AP item and zeroes out remaining slots.

### 4.9 Fog Wall / EMK System (`EmkHelper.cs`, `EmkController.cs`)

DSR manages active events (fog walls, triggers) via an in-memory **singly-linked list**. DSAP manipulates this list to lock/unlock fog walls:

#### Data Structure
```
EMK Node (in DSR memory):
  +0x30: event_id (int)
  +0x34: event_slot (int)
  +0x68: next_pointer (ulong) → next node or 0 (end of list)
```

#### EmkController
Each fog wall AP item creates an `EmkController`:
- **Eventid/Eventslot**: Identifies which fog wall event to manage  
- **MapId3**: First 3 digits of Eventid → determines which map this fog wall is in
- **HasKey**: Whether the player has received this fog wall's AP item
- **Deactivated**: Whether the fog wall is currently removed from the linked list
- **Saved_Ptr**: Stores the pointer to the removed node for re-insertion

#### Management Loop (every 1 second)
```
For each EmkController:
    Traverse DSR's event linked list
    │
    ├─ If player does NOT have key AND event found in list AND player in correct map:
    │   └─ "Pull" node from linked list (unlink it)
    │   └─ Save pointer in controller.Saved_Ptr
    │   └─ Fog wall becomes impassable
    │
    └─ If player HAS key AND Saved_Ptr exists AND player in correct map:
        └─ Re-insert saved node at list head
        └─ Fog wall becomes passable again
```

### 4.10 Param System (`ParamHelper.cs`, `ParamStruct.cs`)

DSR stores game data in "SoloParam" tables — binary structures in memory. DSAP reads, modifies, and writes these:

#### Reading Params
```
SoloParam Root (AoB scan)
  → +offset → ResCapLoc pointer
  → Deref → BufferSize, BufferLoc
  → Deref → Raw param bytes
  → Parse: Prologue(0x10) + Header(0x30) + Entries(12×N) + Param data + Strings
```

#### Writing Params
1. Allocate new memory block in DSR process
2. Build complete byte array (header + entries + params + DescArea metadata)
3. Switch the ResCapLoc pointer to new memory
4. Free old memory block

#### DescArea Metadata
Appended to every written param table:
- `FullAllocLength`: Size of allocation
- `OldAddress`/`OldLength`: Previous allocation (for cleanup on reload)
- `SeedHash`/`Slot`: Validates session continuity

#### Specific Param Modifications
| Param | Modification | Purpose |
|-------|-------------|---------|
| EquipParamGoods | Add new entries for AP items | AP item injection |
| EquipParamWeapon | Zero str/dex/int/faith bytes | Remove weapon requirements option |
| MagicParam | Zero int/faith + clear covenant bits | Remove spell requirements option |
| ItemLotParam | Overwrite lot items | Core item randomization |
| CharaInitParam | Modify starting equipment | Loadout randomization |

### 4.11 Message Manager System (`MsgManHelper.cs`, `MsgManStruct.cs`)

DSR's text strings (item names, descriptions, system messages) are stored in a MsgMan structure:

```
MsgMan Root: 0x141c7e3e8 (hardcoded pointer)
  +offsets to different string tables:
    +0x380: Item Names
    +0x378: Item Captions  
    +0x328: Item Descriptions
    +0x3e0: System Text
```

Each string table has:
- Header (0x1c bytes)
- Span maps (grouping entries)
- String offset table
- Unicode string data

DSAP adds new entries for AP items (names like "Claymore", captions like "A progression Archipelago item for Player1's Dark Souls Remastered").

### 4.12 DeathLink

- **Send**: Monitors player HP address every tick. When HP drops to 0 → sends DeathLink to AP server (with 25-second grace period to prevent loops)
- **Receive**: Writes 0 to the writable HP address, killing the player instantly
- **HP Addresses**: Two different pointer chains — one read-only (GameDataMan path) for monitoring, one writable (WorldChrManImp path) for killing

### 4.13 Save Validation

On each connection:
1. Read seed hash + slot from event flag memory gaps
2. Compare against current AP session's seed hash and slot
3. If mismatch → warn player, prompt `/resetsave` or `/saveloaded`
4. If match → set `SaveidSet = true`, enabling item receives and location checks

### 4.14 Client Commands

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
| `/fog` | List fog wall states |
| `/bossfog` | List boss fog wall states |
| `/lock` | List locked events |
| `/goalcheck` | Check goal completion status |
| `/diag` | Diagnostic information |
| `/lordvessel` | Check Lordvessel status |
| `/cef` / `/mef` | Check/modify event flags |

---

## 5. Data Flow Diagrams

### 5.1 Generation → Connection → Play

```
GENERATION (Python apworld):
  DSRWorld.create_regions()  → 110 regions with connections + rules
  DSRWorld.create_items()    → Item pool with keys, equipment, filler
  DSRWorld.fill_slot_data()  → Upgrade map, options, API version
                                    │
                                    ▼
                            AP Server stores slot data
                                    │
                                    ▼
CLIENT (C# DSAP):
  OnConnectedAsync()
    ├─ Fetch slot data
    ├─ Scout all locations → get placement info
    ├─ Inject AP items into DSR memory (params + messages + hooks)
    ├─ Build replacement map
    ├─ Overwrite ItemLotParam
    └─ Begin monitoring
```

### 5.2 Bidirectional Item Flow

```
OUTBOUND (player picks up item):
  DSR event flag bit flips → MonitorLocationsAsync detects → 
  LocationCompleted fires → AP server routes item to recipient

INBOUND (player receives item):
  AP server sends ItemReceived → Client_ItemReceived fires →
  Look up item → Route by category →
    Event:    Unlock EmkController (fog wall)
    Bonfire:  Write progression flag bit
    Trap:     Start LagTrap timer
    Normal:   Execute shellcode to add item to DSR inventory
```

---

## 6. Key Constants

| Constant | Value | Location |
|----------|-------|----------|
| Base ID | `11110000` | `__init__.py`, `Enums.cs` |
| AP_ITEM_OFFSET | `10000` | `ApItemInjectorHelper.cs` |
| Item-add hook addr | `0x1407479E0` | `ApItemInjectorHelper.cs` |
| Popup hook addr | `0x140728c90` | `ApItemInjectorHelper.cs` |
| MsgMan root | `0x141c7e3e8` | `MsgManHelper.cs` |
| ItemLot param offset | `+0x570` from SoloParam | `AddressHelper.cs` |
| DescArea size | `0x1c` (28 bytes) | `DescArea.cs` |
| ItemLot row size | 148 bytes (8 items) | `ItemLotHelper.cs` |
| EMK node offsets | `+0x30` eventid, `+0x34` slot, `+0x68` next | `EmkHelper.cs` |
| White Sign Soapstone | `50000000` | `Enums.cs` |
| Key to the Seal | `50000100` | `Enums.cs` |

---

## 7. Dependencies

### C# Client
- **Archipelago.Core** — Game client framework, memory access, location monitoring
- **Archipelago.MultiClient.Net** — AP protocol implementation
- **Avalonia** — Cross-platform UI framework
- **Serilog** — Structured logging
- **.NET 8.0** — Runtime target

### Python Apworld
- **BaseClasses** (Archipelago) — World, Region, Location, Item base classes
- **Options** (Archipelago) — Toggle, Choice, Range, etc.
- Standard Archipelago world API (no external dependencies)

---

## 8. Shop System

Shop items exist as `DSRLocationCategory.SHOP_ITEM` (value 9) in the location tables. Examples:
- "UB: Residence Key" (Undead Merchant)
- "UP: Andre - Crest of Artorias"
- Various merchant inventory items

These locations currently appear in the location tables but `SHOP_ITEM` is listed in `location_skip_categories`, meaning shop items are **not currently randomized** — they exist as data definitions for future implementation.

---

## 9. Version Compatibility

The apworld's `fill_slot_data()` includes an `api_version` string. The client's `DarkSoulsOptions` constructor compares this against its own expected version. If they mismatch, a warning is displayed: "Client or apworld out of date - instability and errors likely."
