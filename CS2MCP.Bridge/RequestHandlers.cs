using System;
using System.Collections.Generic;
using Game;
using Game.City;
using Game.SceneFlow;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// Executes bridge requests. Every method here runs on the simulation main
    /// thread (called from BridgeSystem.OnUpdate), so direct ECS access is safe.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private readonly BridgeSystem m_System;

        public RequestHandlers(BridgeSystem system)
        {
            m_System = system;
        }

        private World World => m_System.World;
        private EntityManager EntityManager => m_System.EntityManager;

        /// <summary>
        /// Non-null while the sim has not ticked since load: unlock replay may
        /// be pending and 'locked' flags / tax ranges read from ECS can be stale.
        /// </summary>
        private string LockStalenessWarning => m_System.SimulationHasTickedSinceLoad
            ? null
            : "simulation has not run since this save was loaded; 'locked' flags and ranges may be STALE " +
              "(unlock processing is pending). Unpause briefly via /sim/control for accurate values.";

        /// <summary>
        /// Locked is an IEnableableComponent: unlocking DISABLES it rather than
        /// removing it, so HasComponent alone would misreport every unlockable
        /// prefab as locked. Both checks are required.
        /// </summary>
        private bool IsLocked(Entity prefabEntity)
        {
            return EntityManager.HasComponent<Game.Prefabs.Locked>(prefabEntity)
                && EntityManager.IsComponentEnabled<Game.Prefabs.Locked>(prefabEntity);
        }

        /// <summary>
        /// 0.9.48: before bridge work that drives the game (imports, /map/load, /map/time with a
        /// settle) a foreign tool is replaced by the default tool, and the switch is logged. Move It
        /// was the active tool through A-v11, ate the owner's Load Map clicks and logged at the
        /// moment of crash #4. The bridge's own tools (CS2MCP.*) are never switched away: that
        /// would interrupt an import in progress.
        /// </summary>
        private void EnsureDefaultTool(BridgeRequest request)
        {
            bool drives = request.Method == "POST" && (request.Path.StartsWith("/import", StringComparison.Ordinal)
                || request.Path == "/map/load" || request.Path == "/build/demolish-area" || request.Path == "/debug/override-loop"
                || (request.Path == "/map/time" && (request.Body ?? "").IndexOf("settle", StringComparison.OrdinalIgnoreCase) >= 0));
            if (!drives)
            {
                return;
            }
            var tools = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            var defaultTool = World.GetOrCreateSystemManaged<Game.Tools.DefaultToolSystem>();
            Game.Tools.ToolBaseSystem active = tools.activeTool;
            if (active == null || active == defaultTool || (active.toolID ?? "").StartsWith("CS2MCP", StringComparison.Ordinal))
            {
                return;
            }
            Mod.Log.Info($"{request.Method} {request.Path}: active tool was '{active.toolID}' ({active.GetType().FullName}); switched to the default tool");
            tools.activeTool = defaultTool;
        }

        public BridgeResponse Handle(BridgeRequest request)
        {
            EnsureDefaultTool(request);
            if (request.Path.StartsWith("/build/", StringComparison.Ordinal)
                && (World.GetOrCreateSystemManaged<BridgeImportToolSystem>().IsBusy
                    || World.GetOrCreateSystemManaged<BridgeObjectToolSystem>().IsBusy))
            {
                return BridgeResponse.Error(409, "a network import is running; wait for it (GET /import/status) and retry");
            }
            switch (request.Path)
            {
                case "/state":
                    return GetState();
                case "/city/overview":
                    return GetCityOverview();
                case "/city/demand":
                    return GetDemand();
                case "/city/budget":
                    return GetBudget();
                case "/city/services":
                    return GetServices();
                case "/city/labor":
                    return GetLabor();
                case "/city/statistics":
                    return GetStatistics(request);
                case "/city/taxes":
                    return GetTaxes();
                case "/city/taxes/set":
                    return HandleSetTax(request);
                case "/city/policies":
                    return GetPolicies();
                case "/city/policies/set":
                    return HandleSetPolicy(request);
                case "/city/service-budgets":
                    return GetServiceBudgets();
                case "/city/service-budgets/set":
                    return HandleSetServiceBudget(request);
                case "/prefabs":
                    return GetPrefabs(request);
                case "/build/place":
                    return PlaceBuilding(request);
                case "/build/road":
                    return BuildRoad(request);
                case "/build/upgrade":
                    return HandleUpgradeRoad(request);
                case "/build/junction":
                    return SetJunctionControl(request);
                case "/zones":
                    return GetZoneTypes();
                case "/build/zone":
                    return ZoneArea(request);
                case "/build/demolish":
                    return Demolish(request);
                case "/city/buildings":
                    return ListBuildings(request);
                case "/city/roads":
                    return ListRoads(request);
                case "/city/loan":
                    return GetLoan();
                case "/city/loan/set":
                    return SetLoan(request);
                case "/city/fees":
                    return GetFees();
                case "/city/fees/set":
                    return SetFee(request);
                case "/city/objects":
                    return ListObjects(request);
                case "/camera":
                    return GetCamera();
                case "/camera/set":
                    return SetCamera(request);
                case "/city/terrain":
                    return GetTerrain(request);
                case "/city/gridmap":
                    return GetGridMap(request);
                case "/city/zoning":
                    return GetZoning(request);
                case "/city/notifications":
                    return GetNotifications(request);
                case "/entity/inspect":
                    return InspectEntity(request);
                case "/sim/control":
                    return SimControl(request);
                case "/sim/run":
                    return SimRun(request);
                case "/game/save":
                    return SaveGame(request);
                case "/city/tiles":
                    return GetTilesInfo();
                case "/districts":
                    return GetDistricts();
                case "/build/district":
                    return CreateDistrict(request);
                case "/district/policies":
                    return GetDistrictPolicies(request);
                case "/district/policies/set":
                    return SetDistrictPolicy(request);
                case "/screenshot":
                    return Screenshot(request);
                case "/transit/stops":
                    return ListTransitStops(request);
                case "/transit/stops/place":
                    return PlaceTransitStop(request);
                case "/transit/lines":
                    return ListTransitLines(request);
                case "/transit/lines/create":
                    return CreateTransitLine(request);
                case "/transit/lines/delete":
                    return DeleteTransitLine(request);
                case "/city/tiles/list":
                    return ListMapTiles(request);
                case "/city/tiles/buy":
                    return BuyMapTiles(request);
                case "/city/traffic":
                    return CityTraffic(request);
                case "/city/vehicles":
                    return VehicleCensus(request);
                case "/city/road-graph":
                    return RoadGraph(request);
                case "/build/road/replace":
                    return ReplaceRoad(request);
                case "/build/road/connect":
                    return ConnectRoad(request);
                case "/build/place/shoreline":
                    return PlaceShoreline(request);
                case "/build/place/roadside":
                    return PlaceRoadside(request);
                case "/city/buildings/near":
                    return BuildingsNear(request);
                case "/prefabs/info":
                    return PrefabInfo(request);
                case "/transit/lines/policies":
                    return ListLinePolicies(request);
                case "/transit/lines/policies/set":
                    return SetLinePolicy(request);
                case "/transit/lines/vehicles":
                    return ListLineVehicles(request);
                case "/transit/lines/vehicles/set":
                    return SetLineVehicles(request);
                case "/import/network":
                    return ImportNetwork(request);
                case "/import/status":
                    return ImportStatus();
                case "/import/report":
                    return ImportReport(request);
                case "/import/water":
                    return ImportWater(request);
                case "/import/water/undo":
                    return UndoWater(request);
                case "/import/objects":
                    return ImportObjects(request);
                case "/import/objects/undo":
                    return UndoObjects(request);
                case "/import/objects/status":
                    return ObjectsStatus();
                case "/import/surfaces":
                    return ImportSurfaces(request);
                case "/import/resources":
                    return ImportResources(request);
                case "/map/resources":
                    return ReadResources(request);
                case "/map/terrain-materials":
                    return TerrainMaterials();
                case "/import/terrain-paint":
                    return TerrainPaint(request);
                case "/debug/terrain-paint/probe":
                    return TerrainPaintProbe(request);
                case "/import/terrain-paint/undo":
                    return TerrainPaintUndo(request);
                case "/map/terrain-textures/status":
                    return TerrainTexturesStatus(request);
                case "/map/terrain-textures":
                    return TerrainTextures(request);
                case "/map/time":
                    return MapTime(request);
                case "/graph/diff":
                    return GraphDiff(request);
                case "/net/count":
                    return NetCount();
                case "/map/look/schema":
                    return LookSchema();
                case "/net/pillars/remove":
                    return RemovePillarsRequest(request);
                case "/debug/prefab-refs":
                    return PrefabRefsDebug(request);
                case "/diagnostics/hierarchy":
                    return HierarchyDiagnostics(request);
                case "/map/checklist":
                    return MapChecklist();
                case "/debug/override-loop":
                    return OverrideLoop(request);
                case "/objects/near":
                    return ObjectsNear(request);
                case "/build/demolish-area":
                    return DemolishArea(request);
                case "/map/load":
                case "/map/load/status":
                    return MapLoad(request);
                case "/debug/editor-ui":
                    return EditorUiDebug();
                case "/map/save":
                case "/map/save/status":
                    return MapSave(request);
                case "/map/settings":
                    return MapSettings(request);
                case "/entity/subnets":
                    return EntitySubNets(request);
                case "/graph/export":
                    return ExportGraph(request);
                case "/import/tmpe":
                    return ImportTmpe(request);
                case "/import/tmpe/undo":
                    return UndoTmpe(request);
                case "/import/tmpe/status":
                    return TmpeStatus();
                case "/import/undo":
                    return UndoImport(request);
                default:
                    return BridgeResponse.Error(404,
                        $"unknown endpoint: {request.Path}; available: /ping /state /city/overview /city/demand " +
                        "/city/budget /city/services /city/labor /city/statistics /city/taxes /city/taxes/set " +
                        "/city/policies /city/policies/set /city/service-budgets /city/service-budgets/set " +
                        "/prefabs /build/place /build/demolish /city/buildings /sim/control /screenshot");
            }
        }

        private BridgeResponse GetState()
        {
            GameManager manager = GameManager.instance;
            bool inGame = manager != null && manager.gameMode == GameMode.Game;

            object simulation = null;
            string cityName = null;
            if (inGame)
            {
                SimulationSystem sim = World.GetOrCreateSystemManaged<SimulationSystem>();
                TimeSystem time = World.GetOrCreateSystemManaged<TimeSystem>();
                cityName = World.GetOrCreateSystemManaged<CityConfigurationSystem>().cityName;
                simulation = new
                {
                    paused = sim.selectedSpeed <= 0f,
                    selectedSpeed = sim.selectedSpeed,
                    effectiveSpeed = sim.smoothSpeed,
                    frameIndex = sim.frameIndex,
                    gameDateTime = SafeGameDateTime(time),
                };
            }

            return BridgeResponse.Json(new
            {
                gameMode = manager != null ? manager.gameMode.ToString() : "Unknown",
                isLoading = manager != null && manager.isGameLoading,
                cityLoaded = inGame,
                cityName,
                simulation,
            });
        }

        private BridgeResponse GetCityOverview()
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }

            CitySystem citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            TimeSystem time = World.GetOrCreateSystemManaged<TimeSystem>();
            SimulationSystem sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            string cityName = World.GetOrCreateSystemManaged<CityConfigurationSystem>().cityName;

            Population population = EntityManager.HasComponent<Population>(city)
                ? EntityManager.GetComponentData<Population>(city)
                : default;

            return BridgeResponse.Json(new
            {
                cityName,
                population = population.m_Population,
                populationWithMoveIn = population.m_PopulationWithMoveIn,
                averageHappiness = population.m_AverageHappiness,
                averageHealth = population.m_AverageHealth,
                money = citySystem.moneyAmount,
                xp = citySystem.XP,
                gameYear = time.year,
                gameDateTime = SafeGameDateTime(time),
                simulationPaused = sim.selectedSpeed <= 0f,
                simulationSpeed = sim.selectedSpeed,
            });
        }

        private BridgeResponse GetDemand()
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            ResidentialDemandSystem residential = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            CommercialDemandSystem commercial = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            IndustrialDemandSystem industrial = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();

            JobHandle deps;
            Dictionary<string, int> lowFactors = ReadFactors(residential.GetLowDensityDemandFactors(out deps), deps);
            Dictionary<string, int> mediumFactors = ReadFactors(residential.GetMediumDensityDemandFactors(out deps), deps);
            Dictionary<string, int> highFactors = ReadFactors(residential.GetHighDensityDemandFactors(out deps), deps);
            Dictionary<string, int> commercialFactors = ReadFactors(commercial.GetDemandFactors(out deps), deps);
            Dictionary<string, int> industrialFactors = ReadFactors(industrial.GetIndustrialDemandFactors(out deps), deps);
            Dictionary<string, int> officeFactors = ReadFactors(industrial.GetOfficeDemandFactors(out deps), deps);

            return BridgeResponse.Json(new
            {
                note = "buildingDemand uses the game's internal 0-255 scale; companyDemand counters can exceed 255; " +
                       "factors are raw signed contributions (positive pushes demand up, negative down). " +
                       "Values only refresh while the simulation is running (unpause briefly for fresh numbers).",
                residential = new
                {
                    householdDemand = residential.householdDemand,
                    buildingDemand = new
                    {
                        lowDensity = residential.buildingDemand.x,
                        mediumDensity = residential.buildingDemand.y,
                        highDensity = residential.buildingDemand.z,
                    },
                    factors = new
                    {
                        lowDensity = lowFactors,
                        mediumDensity = mediumFactors,
                        highDensity = highFactors,
                    },
                },
                commercial = new
                {
                    companyDemand = commercial.companyDemand,
                    buildingDemand = commercial.buildingDemand,
                    factors = commercialFactors,
                },
                industrial = new
                {
                    companyDemand = industrial.industrialCompanyDemand,
                    buildingDemand = industrial.industrialBuildingDemand,
                    factors = industrialFactors,
                },
                office = new
                {
                    companyDemand = industrial.officeCompanyDemand,
                    buildingDemand = industrial.officeBuildingDemand,
                    factors = officeFactors,
                },
                storage = new
                {
                    companyDemand = industrial.storageCompanyDemand,
                    buildingDemand = industrial.storageBuildingDemand,
                },
            });
        }

        private BridgeResponse SimControl(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            SimulationSystem sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            bool changed = false;

            if (request.TryGetFloat("speed", out float speed))
            {
                sim.selectedSpeed = Mathf.Clamp(speed, 0f, 8f);
                changed = true;
            }
            if (request.TryGetBool("paused", out bool paused))
            {
                if (paused)
                {
                    sim.selectedSpeed = 0f;
                }
                else if (sim.selectedSpeed <= 0f)
                {
                    sim.selectedSpeed = 1f;
                }
                changed = true;
            }

            if (!changed)
            {
                return BridgeResponse.Error(400, "provide ?speed=0..8 and/or ?paused=true|false");
            }

            return BridgeResponse.Json(new
            {
                paused = sim.selectedSpeed <= 0f,
                selectedSpeed = sim.selectedSpeed,
            });
        }

        /// <summary>
        /// Returns null: the ScreenshotRunner coroutine completes the request
        /// itself at end-of-frame (the back buffer is unreadable mid-update).
        /// </summary>
        private BridgeResponse Screenshot(BridgeRequest request)
        {
            ScreenshotRunner.Ensure().Capture(request);
            return null;
        }

        private bool TryGetCity(out Entity city, out BridgeResponse error)
        {
            city = Entity.Null;
            error = null;

            GameManager manager = GameManager.instance;
            if (manager == null || manager.gameMode != GameMode.Game)
            {
                error = BridgeResponse.Error(409, manager != null && manager.gameMode == GameMode.Editor
                    ? "this endpoint needs a city and is not available in the editor"
                    : "no city loaded (in main menu or editor); load a save first");
                return false;
            }

            city = World.GetOrCreateSystemManaged<CitySystem>().City;
            if (city == Entity.Null)
            {
                error = BridgeResponse.Error(409, "city entity not initialized yet, try again shortly");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gate for endpoints that only need the world (terrain, networks,
        /// prefabs, tools), not a city: true in a loaded city (exactly as
        /// TryGetCity) and, as an explicit editor branch, in the map/asset
        /// editor, where no city entity exists. In the editor milestone locks
        /// do not apply, so callers skip their locked-prefab checks.
        /// </summary>
        private bool TryGetCityOrEditor(out bool editor, out BridgeResponse error)
        {
            editor = false;
            GameManager manager = GameManager.instance;
            if (manager != null && manager.gameMode == GameMode.Editor)
            {
                if (manager.isGameLoading)
                {
                    error = BridgeResponse.Error(409, "the editor is still loading, try again shortly");
                    return false;
                }
                editor = true;
                error = null;
                return true;
            }
            return TryGetCity(out _, out error);
        }

        private static string SafeGameDateTime(TimeSystem time)
        {
            try
            {
                return time.GetCurrentDateTime().ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, int> ReadFactors(NativeArray<int> factors, JobHandle deps)
        {
            deps.Complete();
            var result = new Dictionary<string, int>();
            if (!factors.IsCreated)
            {
                return result;
            }
            int count = Math.Min(factors.Length, (int)DemandFactor.Count);
            for (int i = 0; i < count; i++)
            {
                if (factors[i] != 0)
                {
                    result[((DemandFactor)i).ToString()] = factors[i];
                }
            }
            return result;
        }
    }
}
