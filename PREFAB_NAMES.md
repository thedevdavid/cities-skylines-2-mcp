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
