using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Areas;
using Game.Net;
using Game.UI.Editor;
using Unity.Collections;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// GET /map/checklist: the map editor's playability checklist as the Map Settings
    /// panel shows it (MapRequirementSystem plus MapPanelSystem.MapIsPlayable), with a
    /// reason for each failed item, and the save-time extras (description, preview,
    /// thumbnail) that are optional for publishing.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private BridgeResponse MapChecklist()
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (!editor)
            {
                return BridgeResponse.Error(409, "the map checklist exists only in the map editor");
            }
            MapRequirementSystem requirements = World.GetOrCreateSystemManaged<MapRequirementSystem>();
            MapPanelSystem panel = World.GetOrCreateSystemManaged<MapPanelSystem>();

            int startTiles = World.GetOrCreateSystemManaged<MapTileSystem>().GetStartTiles().Length;
            bool water = requirements.StartingAreaHasResource(MapFeature.SurfaceWater)
                || requirements.StartingAreaHasResource(MapFeature.GroundWater);
            int roadNodes = CountOutsideNodes<Road>();
            int trainNodes = CountOutsideNodes<TrainTrack>();
            bool? nameValid = LocalizationFieldValid(panel, "m_MapNameLocalization");
            bool? descriptionValid = LocalizationFieldValid(panel, "m_MapDescriptionLocalization");
            bool? hasPreview = PrivateFieldSet(panel, "m_Preview");
            bool? hasThumbnail = PrivateFieldSet(panel, "m_Thumbnail");

            var required = new List<object>
            {
                Item("Starting tiles", requirements.hasStartingArea,
                    $"{startTiles} starting tile(s)", "select starting tiles: Map Settings → Select starting tiles"),
                Item("Water in the starting area", water,
                    "surface water or groundwater in a starting tile",
                    "no surface water or groundwater in any starting tile: add water sources or groundwater there, or pick other tiles"),
                Item("Road outside connection", requirements.roadConnection,
                    $"{roadNodes} road node(s) on the map border",
                    "no road ends on the map border (x or z = ±7168); the game needs one to make the connection"),
                Item("Map name", nameValid,
                    "a name in the fallback language", "enter a map name: Map Settings → Map name"),
            };
            var optional = new List<object>
            {
                Item("Train outside connection", requirements.trainConnection, $"{trainNodes} train-track node(s) on the border", "no train track ends on the border"),
                Item("Air outside connection", requirements.airConnection, "an airplane outside connection", "no Airplane Outside Connection on the border"),
                Item("Electricity outside connection", requirements.electricityConnection, "a power line ends on the border", "no power line ends on the border"),
                Item("Oil", requirements.MapHasResource(MapFeature.Oil), "oil in a map tile", "no oil anywhere (resources import or brush)"),
                Item("Ore", requirements.MapHasResource(MapFeature.Ore), "ore in a map tile", "no ore anywhere"),
                Item("Forest", requirements.MapHasResource(MapFeature.Forest), "forest in a map tile", "no forest (trees) in any map tile"),
                Item("Fertile land", requirements.MapHasResource(MapFeature.FertileLand), "fertile land in a map tile", "no fertile land anywhere"),
            };
            var publishing = new List<object>
            {
                Item("Description", descriptionValid, "a description", "no description (optional): Map Settings → Map description"),
                Item("Preview image", hasPreview, "a preview image", "no preview image (optional): Map Settings → Preview"),
                Item("Thumbnail", hasThumbnail, "a thumbnail", "no thumbnail (optional): Map Settings → Thumbnail"),
            };

            bool playable;
            try
            {
                playable = panel.MapIsPlayable();
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"MapIsPlayable failed: {e.Message}");
                playable = requirements.hasStartingArea && water && requirements.roadConnection && nameValid == true;
            }

            return BridgeResponse.Json(new
            {
                playable,
                note = "playable = the editor's MapIsPlayable (all required items pass). Saving always works; sharing needs a playable map. " +
                       "Values refresh every UI frame; they reflect the map as it is now.",
                required,
                optional,
                publishing,
            });
        }

        private static object Item(string name, bool? pass, string passDetail, string failReason)
        {
            return new
            {
                item = name,
                pass,
                detail = pass == true ? passDetail : pass == false ? failReason : "could not be read",
            };
        }

        private int CountOutsideNodes<T>() where T : struct, IComponentData
        {
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<T>(), ComponentType.ReadOnly<Game.Net.OutsideConnection>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            }))
            {
                return query.CalculateEntityCount();
            }
        }

        /// <summary>LocalizationField.IsValid() on a private MapPanelSystem field (name, description).</summary>
        private static bool? LocalizationFieldValid(object panel, string fieldName)
        {
            try
            {
                FieldInfo field = panel.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                object value = field?.GetValue(panel);
                MethodInfo isValid = value?.GetType().GetMethod("IsValid", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                return isValid != null ? (bool)isValid.Invoke(value, null) : (bool?)null;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"checklist: {fieldName} unreadable: {e.Message}");
                return null;
            }
        }

        private static bool? PrivateFieldSet(object panel, string fieldName)
        {
            try
            {
                FieldInfo field = panel.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                return field == null ? (bool?)null : field.GetValue(panel) != null;
            }
            catch
            {
                return null;
            }
        }
    }
}
