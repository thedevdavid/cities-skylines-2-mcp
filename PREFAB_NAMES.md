# CS2 vanilla prefab names for the converter

Source: the prefab names that key the game's localisation (`Assets.NAME[<prefab>]` in `Cities2_Data/Content/Game/Locale.cok`, CS2 1.6.2) and the decompiled `Game.dll`. These are the base-game names; content packs add more. **The running game is the authority:** `GET /prefabs?category=tree&limit=200` (trees, bushes), `category=building`, `category=net`, and `/import/objects` with `options.dryRun` checks every name in a file at once. Names are matched case-insensitively.

## Trees (placeable; the bridge sets the growth stage from `age`)

Each species also has stage variants (`…Adult01`, `…Child01`, `…Teen01`, `…Elderly01`, `…Dead01`, `…Stump01`). Those are mesh variants used by the tree's growth state, not separate prefabs to place: place the base name and pass `age`.

| Prefab | Notes |
|---|---|
| `AppleTree01` | |
| `BirchTree01` | |
| `OakTree01` | |
| `PineTree01` | |
| `SpruceTree01` | |
| `EuropeanSpruce01` | no growth-stage variants listed |
| `NA_HickoryTree01` | North American |
| `NA_LindenTree01` | North American |
| `NA_LondonPlaneTree01` | North American |
| `EU_AlderTree01` | European |
| `EU_ChestnutTree01` | European |
| `EU_PoplarTree01` | European |
| `CoconutPalmTree01` | palm |
| `FloridaPalmTree01` | palm |
| `RoyalPalmTree01` | palm |
| `SylvesterPalmTree01` | palm |
| `TreeWildRandom01` | placeholder: the game picks a wild species per object (good default for CS1 forests) |
| `TreeCityRandom01` | placeholder: urban species |

## Bushes and flowers

| Prefab | Notes |
|---|---|
| `GreenBushWild01`, `GreenBushWild02` | wild, with growth stages |
| `FlowerBushWild01`, `FlowerBushWild02` | wild, with growth stages |
| `GreenBushCity01`–`GreenBushCity03` | urban |
| `FlowerBushCity01`–`FlowerBushCity03` | urban |
| `BushWildRandom01` | placeholder, wild |
| `BushCityRandom01` | placeholder, urban |
| `HedgeHigh01`, `HedgeHigh02`, `HedgeLow01`, `HedgeLow02` | hedges |
| `HedgePieceHigh01`, `HedgePieceHigh02`, `HedgePieceLow01`, `HedgePieceLow02`, `HedgePieceHighRandom01`, `HedgePieceLowRandom01` | hedge pieces |
| `Flowerbed01`, `Flowerbed02` | decoration |

## Rocks and boulders (decoration)

| Prefab | Notes |
|---|---|
| `Rock01` … `Rock30` | 30 rocks |
| `RockRandom01` | placeholder, random rock |
| `Boulder01` … `Boulder08` | 8 boulders |
| `BoulderRandom01` | placeholder, random boulder |
| `StandingStone01` … `StandingStone08`, `StandingStoneRandom01` | standing stones |
| `RuinsStoneCairns01`–`03`, `RuinsStoneFormation01`–`02`, `RuinsStoneHeadFormation01` | ruins |
| `PileStone{Small,Medium,Large,Massive}{Low,Medium,High}01` | stone piles (e.g. `PileStoneLargeHigh01`) |

## Outside connections

| Prefab | How it is created |
|---|---|
| `Road Outside Connection - Oneway`, `Road Outside Connection - Twoway` | **Not placed.** The game adds the marker by itself to a road node on the terrain border (`GenerateNodesSystem` tags border nodes with `Game.Net.OutsideConnection`; the road prefab's sub-object with `RequireOutsideConnection` then spawns). Import the road so that it ends at x or z = ±7168. `/import/objects` with `kind: outsideConnection, net: road` verifies this and reports the node. |
| `Train Outside Connection - Oneway`, `Train Outside Connection - Twoway` | Same, with a train track ending at the border (`net: rail`). |
| `Powerline Outside Connection` | Same mechanism on a power line ending at the border (inferred from `ElectricityOutsideConnection`; not verified in-game). |
| `Water Outside Connection` | Water-pipe border connection, same mechanism (inferred; not verified). |
| `Airplane Outside Connection` | **Free object on the border.** Place with `/import/objects`, `kind: outsideConnection` (any `net`, e.g. `air`); the bridge snaps it onto the nearest terrain edge and faces it outward, as the editor's object tool does. |
| `Ship Outside Connection - Twoway` | Placed like the airplane one by the bridge (free border object). Whether the editor instead spawns it on a ship-lane border node could not be confirmed from code; check in-game. |

## Power lines (networks: import with `/import/network`)

| Prefab | Notes |
|---|---|
| `High-voltage Line` | overhead high-voltage line (pylons `PowerLinePylon47m01`/`52m01`/`57m01` are its sub-objects) |
| `Low-voltage Line` | the small overhead line (poles `PowerLinePole8m01`/`13m01`/`18m01`) |
| `High-voltage Ground Cable`, `Low-voltage Ground Cable` | underground variants |

The lane/marker names (`High-voltage Cable Lane`, `Low-voltage Marker`, …) are internal parts, not placeable.

## Ship paths (networks: import with `/import/network`)

`WaterwayPrefab` networks. There is no prefab with "Ship path", "Shipping" or "Sea lane" in its name; the names are:

| Prefab | Notes |
|---|---|
| `Narrow Seaway`, `Medium Seaway`, `Wide Seaway` | the placeable ship paths for cargo and passenger ships (lane `Ship Drive Lane 50`) |
| `Narrow Boatway`, `Medium Boatway`, `Wide Boatway` | paths for smaller boats (most likely the Bridges & Ports boat lines) |

`ShipwayBuoy01` is a prop. Whether a seaway node on the border spawns `Ship Outside Connection - Twoway` by itself, as roads do, is to be checked in-game (NOTES.md). `GenerateNodesSystem` tags border nodes of any net with `Game.Net.OutsideConnection`; the marker appears only if the seaway prefab lists it as a sub-object that requires an outside connection.

## Custom assets (PDX Mods packs) and decals

- **How custom assets are found:** packs loaded in the playset register with the same `PrefabSystem` as vanilla ones, so `/import/objects` resolves their names the same way. `GET /prefabs?category=object&query=…&limit=1000&offset=…` (MCP `cs2_find_prefabs`, category `object`) lists every object prefab: props, decals, rocks, bushes, markers, trees and buildings, custom ones included. Use it to check the converter's names against the live list. To be confirmed at game time (NOTES.md).
- **Decals:** CS2 has no separate decal prefab kind. A decal is an ordinary static object prefab whose mesh (`RenderPrefab`) carries `DecalProperties` and is projected onto the surface below. `/import/objects` places it through the same object definition path as any prop. Leave `y` empty (terrain surface) and use `rotation` for the yaw.
- **Surfaces:** list them with `GET /prefabs?category=surface`.

## Walls and fences

Checked against the localisation keys (names exist). The **dimensions are not in `Game.dll`**: they live in the compressed asset bundles, so they can only be read from the running game. From bridge 0.9.17 on, `GET /prefabs/info?name=<prefab>` also answers for object prefabs, with `geometry.size` {x, y, z} in metres; a wall's length is the longer of x and z. Measure all of them in one go once the game is up:

```bash
for p in EU_Wall01 EU_Wall01Long EU_Wall01Short EU_Wall01ExtraShort EU_Wall03 EU_Wall03Long EU_Wall03Short EU_Wall03ExtraShort EU_WallLow01 RuinsCastle01 OldCastle01; do curl -s "http://127.0.0.1:8642/prefabs/info?name=$p" | node -e 'let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{const j=JSON.parse(d);console.log(j.name||j.error,JSON.stringify(j.geometry&&j.geometry.size))})'; done
```

| Family | Variants (all exist) | Notes |
|---|---|---|
| `EU_Wall01` … `EU_Wall09` | base, `Long`, `Short`, `ExtraShort` (e.g. `EU_Wall03Short`) | European garden and boundary walls in 9 styles. The converter's `EU_Wall01`, `EU_Wall03`, `EU_Wall01Short`, `EU_Wall01ExtraShort` and `EU_Wall03Short` all exist. |
| `EU_WallLow01` … `EU_WallLow09` | base, `Long`, `Short`, `ExtraShort` | low versions |
| `EU_WallRandom01`, `EU_WallLongRandom01`, `EU_WallShortRandom01`, `EU_WallExtraShortRandom01`, and the `EU_WallLow…Random01` set | placeholders | random style per piece |
| `EU_FenceResidential{High,Low}01–06`, `…PieceHigh/Low…`, `…Short…` | | European residential fences |
| `NA_FenceResidential{High,Low}01–05` and pieces | | North American fences |
| `FenceIndustrial{High,Low}01–02` and pieces | | industrial fences |
| `PergolaModularWall01–07` (and `L`/`R`) | | pergola walls |

**Castle and stone-fort candidates:** CS2 has no vanilla stone fort-wall prop; there is no "stone wall", "rampart" or "battlement" piece.
- **Closest whole-asset options:** `RuinsCastle01` (castle ruin), `OldCastle01`, and the landmark building `ServiceBuildingMedievalCastle`. `MedievalCastleMasterSpots` and `MedievalCastleSpotShow01–03` are effect/marker objects, not walls.
- **Natural stone accents along a line:** `StandingStone01–08`, `RuinsStoneFormation01–02` and `RuinsStoneCairns01–03`.
- **Keeping a continuous wall:** use the stoniest `EU_Wall` style (pick visually in game; styles 01–09 differ in material), or a PDX Mods wall pack from the playset (list with `cs2_find_prefabs` category `object`, query `Wall`).

**Measured live (0.9.17, `/prefabs/info`):** the length runs along the prefab's local **z** axis, so a piece placed with yaw r runs along heading r (0 = +z). The origin is the piece centre.

| Prefab | Length (z) | Height (y) | Thickness (x) |
|---|---|---|---|
| `EU_Wall01`, `EU_Wall03` | 3.00 m | 0.90 m | 0.19 / 0.18 m |
| `EU_Wall01Long`, `EU_Wall03Long` | 5.00 m | 0.90 m | 0.19 / 0.18 m |
| `EU_Wall01Short`, `EU_Wall03Short` | 1.00 m | 0.90 m | 0.19 / 0.18 m |
| `EU_Wall01ExtraShort`, `EU_Wall03ExtraShort` | 0.50 m | 0.90 m | 0.19 / 0.18 m |

At 0.9 m high, these are garden walls, not fort walls.
