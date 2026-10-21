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

## Rocks, cliffs and ground cover: geometry bounds (measured live)

Read with `GET /prefabs/info?name=<prefab>` (bridge 0.9.19, `geometry.boundsMin` / `boundsMax`, the prefab's `ObjectGeometryData.m_Bounds` in local metres around the pivot) on 2026-10-20, with the cs2-playset packs from `cs2-playset/ASSET_PACKS.md` loaded. `Size` is the game's `m_Size` field, which is not always max − min (the game sets y to max.y); use the bounds for placement.

- **min.y is negative for every prefab:** the pivot sits above the mesh bottom. An object placed with its pivot on the terrain (y empty) is already sunk by |min.y|.
- **For a pack rock or cliff,** the sinking is the author's intent: the mesh skirt goes under the ground, and `CliffFreeStanding`, `CliffSideTall` and `CliffSlim` reach 105–131 m down. `y = terrain − min.y` lifts the bottom of the mesh onto the terrain, so the whole skirt shows and the piece floats. Use it only where the CS1 prop sat with its base on the ground and the CS2 mesh has no skirt; the safe default is pivot on terrain (no y). For cliffs, lift by a fraction at most.
- **Vanilla `Rock01`–`Rock30`** have min.y = −max.y: the pivot is at the mesh centre, so half the rock is buried. Lift by |min.y| (0.22–0.40 m) if the full stone should show.
- **The trailing space in `pd Long Grass Prop ` is part of the prefab name.**

| Prefab | Pack | Size x × y × z (m) | min.y | max.y | x range | z range |
|---|---|---|---|---|---|---|
| `rzrock1` | RZ Rocks | 19.24 × 8.85 × 14.64 | -1.19 | 8.85 | -8.84..9.62 | -7.32..3.76 |
| `rzrock2` | RZ Rocks | 24.59 × 9.80 × 22.46 | -1.13 | 9.80 | -12.30..11.15 | -10.20..11.23 |
| `rzrock3` | RZ Rocks | 28.06 × 8.74 × 18.78 | -0.65 | 8.74 | -14.03..13.11 | -9.39..5.77 |
| `rzrock4` | RZ Rocks | 26.51 × 10.37 × 25.13 | -0.92 | 10.37 | -12.37..13.25 | -9.91..12.56 |
| `rzrock5` | RZ Rocks | 12.75 × 2.87 × 23.07 | -0.75 | 2.87 | -6.18..6.38 | -9.69..11.53 |
| `rzrock6` | RZ Rocks | 14.88 × 14.56 × 24.88 | -0.62 | 14.56 | -7.44..7.22 | -12.44..10.04 |
| `rzrock7` | RZ Rocks | 13.21 × 4.72 × 20.34 | -0.41 | 4.72 | -6.45..6.61 | -9.65..10.17 |
| `rzrock8` | RZ Rocks | 30.02 × 10.74 × 36.86 | -0.76 | 10.74 | -14.12..15.01 | -18.43..8.52 |
| `RZaridrock1` | Arid Rocks | 10.40 × 3.77 × 6.56 | -0.37 | 3.77 | -5.20..4.78 | -3.28..3.26 |
| `RZaridrock2` | Arid Rocks | 7.09 × 2.97 × 13.00 | -0.25 | 2.97 | -3.54..2.89 | -4.27..6.50 |
| `RZaridrock3` | Arid Rocks | 3.64 × 1.42 × 3.01 | -0.30 | 1.42 | -1.82..1.04 | -1.51..1.42 |
| `RZaridrock4` | Arid Rocks | 5.00 × 1.27 × 3.16 | -0.30 | 1.27 | -2.50..2.15 | -1.09..1.58 |
| `RZaridrock5` | Arid Rocks | 8.37 × 5.52 × 2.51 | -0.17 | 5.52 | -3.96..4.19 | -1.23..1.26 |
| `CliffFreeStanding` | Cliffs and Rocks Pack | 157.50 × 64.75 × 164.80 | -130.66 | 64.75 | -62.83..78.75 | -82.40..77.20 |
| `CliffRock3` | Cliffs and Rocks Pack | 77.50 × 17.30 × 98.97 | -13.71 | 17.30 | -38.07..38.75 | -42.09..49.48 |
| `CliffRock5` | Cliffs and Rocks Pack | 38.15 × 14.35 × 36.41 | -2.57 | 14.35 | -19.07..17.10 | -18.06..18.20 |
| `CliffRock6` | Cliffs and Rocks Pack | 60.71 × 8.50 × 55.32 | -1.10 | 8.50 | -23.15..30.36 | -25.99..27.66 |
| `CliffSide2` | Cliffs and Rocks Pack | 161.08 × 62.28 × 146.44 | -23.39 | 62.28 | -57.66..80.54 | -72.45..73.22 |
| `CliffSide2Left` | Cliffs and Rocks Pack | 161.08 × 68.36 × 156.74 | -29.68 | 68.36 | -57.66..80.54 | -74.21..78.37 |
| `CliffSide2Right` | Cliffs and Rocks Pack | 161.08 × 68.43 × 155.21 | -29.61 | 68.43 | -57.66..80.54 | -77.60..74.98 |
| `CliffSideTall` | Cliffs and Rocks Pack | 161.08 × 62.28 × 146.44 | -109.22 | 62.28 | -57.66..80.54 | -72.57..73.22 |
| `CliffSlim` | Cliffs and Rocks Pack | 161.08 × 62.28 × 85.10 | -104.88 | 62.28 | -57.66..80.54 | -42.10..42.55 |
| `CliffStack` | Cliffs and Rocks Pack | 60.97 × 48.59 × 67.90 | -21.62 | 48.59 | -27.55..30.49 | -33.95..31.34 |
| `pd Long Grass` | pd Long Grass | 3.36 × 1.76 × 3.36 | -0.79 | 1.76 | -1.52..1.52 | -1.68..1.68 |
| `pd Long Grass Prop ` | pd Long Grass | 6.72 × 2.52 × 6.72 | -0.73 | 2.52 | -3.03..2.92 | -3.36..2.98 |
| `Grass01 (Styx's)` | Styx's grass | 0.29 × 0.60 × 0.29 | -0.02 | 0.60 | -0.14..0.14 | -0.14..0.14 |
| `Grass02 (Styx's)` | Styx's grass | 0.48 × 0.99 × 0.48 | -0.05 | 0.99 | -0.24..0.24 | -0.24..0.24 |
| `Grass03 (Styx's)` | Styx's grass | 0.48 × 1.71 × 0.48 | -0.09 | 1.71 | -0.24..0.24 | -0.24..0.24 |
| `Grass04 (Styx's)` | Styx's grass | 0.29 × 0.54 × 0.29 | -0.08 | 0.54 | -0.14..0.14 | -0.14..0.14 |
| `Grass05 (Styx's)` | Styx's grass | 0.48 × 0.96 × 0.48 | -0.08 | 0.96 | -0.24..0.24 | -0.24..0.24 |
| `Grass06 (Styx's)` | Styx's grass | 0.48 × 1.71 × 0.48 | -0.09 | 1.71 | -0.24..0.24 | -0.24..0.24 |
| `gaga - Fern 1` | Ferns | 1.00 × 0.86 × 1.00 | -0.01 | 0.86 | -0.50..0.50 | -0.44..0.44 |
| `gaga - Fern 2` | Ferns | 1.03 × 0.86 × 1.03 | -0.01 | 0.86 | -0.45..0.45 | -0.51..0.51 |
| `ReikUndergrowth01` | Leafy Undergrowth | 2.54 × 2.06 × 2.54 | -0.96 | 2.06 | -1.27..1.27 | -1.09..1.09 |
| `ReikUndergrowth01Trimmed` | Leafy Undergrowth | 3.71 × 1.12 × 3.71 | -1.04 | 1.12 | -1.85..1.85 | -0.92..0.92 |
| `ReikUndergrowth01XL` | Leafy Undergrowth | 13.70 × 1.71 × 13.70 | -1.57 | 1.71 | -6.58..6.58 | -6.85..6.85 |
| `ReikUndergrowth02` | Leafy Undergrowth | 2.54 × 2.06 × 2.54 | -0.96 | 2.06 | -1.27..1.27 | -1.09..1.09 |
| `ReikUndergrowth02Trimmed` | Leafy Undergrowth | 3.71 × 1.12 × 3.71 | -1.04 | 1.12 | -1.85..1.85 | -0.92..0.92 |
| `ReikUndergrowth02XL` | Leafy Undergrowth | 13.70 × 1.71 × 13.70 | -1.57 | 1.71 | -6.58..6.58 | -6.85..6.85 |
| `PD-YellowFlower` | Flowers | 0.54 × 0.37 × 0.54 | -0.19 | 0.37 | -0.24..0.24 | -0.27..0.27 |
| `pd Bloom` | Flowers | 1.34 × 0.91 × 1.34 | -0.29 | 0.91 | -0.67..0.67 | -0.61..0.61 |
| `Rock01` | vanilla | 1.00 × 0.31 × 0.94 | -0.31 | 0.31 | -0.50..0.50 | -0.47..0.47 |
| `Rock02` | vanilla | 1.00 × 0.29 × 0.76 | -0.29 | 0.29 | -0.50..0.50 | -0.38..0.38 |
| `Rock03` | vanilla | 0.87 × 0.30 × 0.89 | -0.30 | 0.30 | -0.44..0.44 | -0.44..0.44 |
| `Rock04` | vanilla | 0.81 × 0.31 × 0.83 | -0.31 | 0.31 | -0.40..0.40 | -0.42..0.42 |
| `Rock05` | vanilla | 0.86 × 0.32 × 0.83 | -0.32 | 0.32 | -0.43..0.43 | -0.42..0.42 |
| `Rock06` | vanilla | 0.96 × 0.40 × 1.05 | -0.40 | 0.40 | -0.48..0.48 | -0.52..0.52 |
| `Rock07` | vanilla | 0.92 × 0.31 × 0.92 | -0.31 | 0.31 | -0.46..0.46 | -0.46..0.46 |
| `Rock08` | vanilla | 1.05 × 0.33 × 0.85 | -0.33 | 0.33 | -0.52..0.52 | -0.43..0.43 |
| `Rock09` | vanilla | 0.98 × 0.33 × 0.90 | -0.33 | 0.33 | -0.49..0.49 | -0.45..0.45 |
| `Rock10` | vanilla | 0.97 × 0.33 × 0.93 | -0.33 | 0.33 | -0.49..0.49 | -0.46..0.46 |
| `Rock11` | vanilla | 1.00 × 0.32 × 0.98 | -0.32 | 0.32 | -0.50..0.50 | -0.49..0.49 |
| `Rock12` | vanilla | 0.86 × 0.27 × 0.82 | -0.27 | 0.27 | -0.43..0.43 | -0.41..0.41 |
| `Rock13` | vanilla | 0.90 × 0.26 × 0.84 | -0.26 | 0.26 | -0.45..0.45 | -0.42..0.42 |
| `Rock14` | vanilla | 0.94 × 0.36 × 0.87 | -0.36 | 0.36 | -0.47..0.47 | -0.43..0.43 |
| `Rock15` | vanilla | 0.94 × 0.28 × 0.87 | -0.28 | 0.28 | -0.47..0.47 | -0.43..0.43 |
| `Rock16` | vanilla | 0.89 × 0.22 × 0.76 | -0.22 | 0.22 | -0.44..0.44 | -0.38..0.38 |
| `Rock17` | vanilla | 0.96 × 0.28 × 0.82 | -0.28 | 0.28 | -0.48..0.48 | -0.41..0.41 |
| `Rock18` | vanilla | 0.80 × 0.27 × 0.89 | -0.27 | 0.27 | -0.40..0.40 | -0.45..0.45 |
| `Rock19` | vanilla | 0.89 × 0.26 × 0.76 | -0.26 | 0.26 | -0.44..0.44 | -0.38..0.38 |
| `Rock20` | vanilla | 0.65 × 0.33 × 0.80 | -0.33 | 0.33 | -0.32..0.32 | -0.40..0.40 |
| `Rock21` | vanilla | 0.82 × 0.33 × 0.68 | -0.33 | 0.33 | -0.41..0.41 | -0.34..0.34 |
| `Rock22` | vanilla | 0.93 × 0.25 × 0.80 | -0.25 | 0.25 | -0.47..0.47 | -0.40..0.40 |
| `Rock23` | vanilla | 0.83 × 0.23 × 0.94 | -0.23 | 0.23 | -0.41..0.41 | -0.47..0.47 |
| `Rock24` | vanilla | 1.07 × 0.27 × 0.94 | -0.27 | 0.27 | -0.53..0.53 | -0.47..0.47 |
| `Rock25` | vanilla | 1.03 × 0.32 × 0.88 | -0.32 | 0.32 | -0.52..0.52 | -0.44..0.44 |
| `Rock26` | vanilla | 0.99 × 0.36 × 0.84 | -0.36 | 0.36 | -0.49..0.49 | -0.42..0.42 |
| `Rock27` | vanilla | 0.95 × 0.31 × 0.96 | -0.31 | 0.31 | -0.47..0.47 | -0.48..0.48 |
| `Rock28` | vanilla | 0.88 × 0.38 × 0.88 | -0.38 | 0.38 | -0.44..0.44 | -0.44..0.44 |
| `Rock29` | vanilla | 1.05 × 0.34 × 0.92 | -0.34 | 0.34 | -0.52..0.52 | -0.46..0.46 |
| `Rock30` | vanilla | 0.97 × 0.33 × 0.83 | -0.33 | 0.33 | -0.48..0.48 | -0.42..0.42 |
| `Boulder01` | vanilla | 5.57 × 2.43 × 4.37 | -1.35 | 2.43 | -2.50..2.78 | -2.18..2.19 |
| `Boulder02` | vanilla | 4.82 × 2.43 × 4.04 | -1.64 | 2.43 | -2.01..2.41 | -1.93..2.02 |
| `Boulder03` | vanilla | 4.80 × 1.84 × 5.44 | -1.24 | 1.84 | -2.36..2.40 | -2.26..2.72 |
| `Boulder04` | vanilla | 5.77 × 2.58 × 4.60 | -1.30 | 2.58 | -2.67..2.88 | -2.30..2.02 |
| `Boulder05` | vanilla | 4.71 × 2.09 × 5.01 | -1.33 | 2.09 | -2.35..2.29 | -2.50..2.47 |
| `Boulder06` | vanilla | 4.85 × 2.53 × 4.66 | -1.65 | 2.53 | -2.18..2.43 | -2.33..2.21 |
| `Boulder07` | vanilla | 5.32 × 1.82 × 4.27 | -1.36 | 1.82 | -2.49..2.66 | -2.14..2.06 |
| `Boulder08` | vanilla | 5.53 × 1.76 × 5.26 | -1.83 | 1.76 | -2.65..2.77 | -2.37..2.63 |

## Vanilla pillar objects (from Locale.cok, 0.9.42)

- **Placeholders:** the net deck spawns one of these, and the game picks a variant: `Pillar Placeholder` (roads), `Train Pillar Placeholder`, `Subway Pillar Placeholder`, `Pillar Pedestrian Placeholder`.
- **Variants:**
  - **Roads:** `Vertical Pillar`, `Horizontal Pillar 6/8/10/12/13/16/19/20/21/22/24/25/27/28/33/38/43` (the number is the deck width the crossbeam spans) and `Pillar Top NN`.
  - **Other nets:** the same sets with a `Train `, `Subway ` or `Pedestrian ` prefix, plus `Vertical Pillar Pedestrian`.
- **Bridge towers:**
  - suspension: `2LaneSuspensionBridgePillar`, `3LaneSuspensionBridgePillar`, `4LaneSuspensionBridgePillar`, `5LaneSuspensionBridgePillar`;
  - arch and covered: `2LaneTrussArchBridgePillar`, `4LaneTiedArchBridgePillar`, `2LaneWoodenCoveredBridgePillar`;
  - cable-stayed and extradosed: `6LaneExtradosedBridgePillar`, `8LaneCableStayedBridgePillar`, `PedestrianBridgeCableStayedPillar`;
  - named bridges: `GoldenGateBridgePillar01`, `GrandBridgePillar01`;
  - each tower also has a `… Placeholder`.
- **Other pillars:**
  - B&P net pieces: `ExtradosedBridge01NetPillar(Base/Top)`, `TrussArchBridge01NetPillar(Base)`;
  - B&P pier: `Pier01Pillar01/02`, `Pier01PillarBase01/02`, `Pier01PillarSupport01/02`, `Pier01PillarTop1/2`;
  - subway station: `SubwayStationElevated01Pillar`.
- **Sizes:** not readable offline. `GET /prefabs/info?name=…` gives size, bounds, and since 0.9.42 the pillar type, the stack data and the placeholder variant count.
- **Placing them with `/import/objects`:** it accepts any prefab with `ObjectData`, so pillars too.
  - A standalone pillar has no owner, so `AlignSystem` never stretches it to the ground.
  - A stack pillar (the vertical ones) gets its range in `GenerateObjectsSystem`. Direction `Up` reaches down by the object's elevation: give `y` = the top, and the bridge passes elevation = y − ground. Other directions keep a fixed default length.
  - A placeholder picks a random variant, not the best fit.
