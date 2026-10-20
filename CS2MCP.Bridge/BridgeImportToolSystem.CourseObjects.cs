using System;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// Course objects (0.9.23), ported from NetToolSystem.CreateDefinitionsJob:
    /// GetOwnerDefinition / CreateCourseObject / CreateSubNets / CalculatedInverseWeight /
    /// InvertCourse. A net prefab whose NetSubObjects use NetObjectPlacement.CourseStart or
    /// CourseEnd gets SubObjectFlags.CoursePlacement (the first one also MakeOwner, and the
    /// net GeometryFlags.SubOwner) in NetInitializeSystem. When the tool draws such a net,
    /// the MakeOwner object (the hydro plant for "Hydroelectric_Power_Plant_01 Dam") is
    /// created at the course start/end and becomes the owner of the course's edges, which
    /// are then its sub-nets (WaterPowered sums them); other CoursePlacement objects are
    /// created owned by it. Water-flow nets are drawn so that the flow crosses them from
    /// left to right (FlowRight) or right to left (FlowLeft); the tool inverts the course
    /// otherwise.
    /// </summary>
    public sealed partial class BridgeImportToolSystem
    {
        /// <summary>Flips the course when the water flows against the prefab's FlowLeft/FlowRight; returns the flow weight.</summary>
        private float OrientForFlow(ImportEdge edge, ref NetCourse course)
        {
            if (!EntityManager.HasComponent<PlaceableNetData>(edge.Prefab))
            {
                return 0f;
            }
            Game.Net.PlacementFlags flags = EntityManager.GetComponentData<PlaceableNetData>(edge.Prefab).m_PlacementFlags;
            if ((flags & (Game.Net.PlacementFlags.FlowLeft | Game.Net.PlacementFlags.FlowRight)) == 0)
            {
                return 0f;
            }
            float weight = FlowWeight(course.m_Curve, flags);
            edge.FlowWeight = weight;
            string mode = (edge.Flow ?? "auto").ToLowerInvariant();
            if (mode == "invert" || (mode == "auto" && weight < 0f))
            {
                InvertCourse(ref course);
                edge.Inverted = true;
            }
            return weight;
        }

        /// <summary>NetToolSystem.CalculatedInverseWeight: water velocity against the course's right-hand normal.</summary>
        internal float FlowWeight(Bezier4x3 curve, Game.Net.PlacementFlags flags)
        {
            WaterSurfaceData<SurfaceWater> water = m_WaterSystem.GetVelocitiesSurfaceData(out JobHandle deps);
            deps.Complete();
            float length = MathUtils.Length(curve);
            int samples = math.max(1, Mathf.RoundToInt(length * water.scale.x));
            float sum = 0f;
            for (int i = 0; i < samples; i++)
            {
                float t = (i + 0.5f) / samples;
                float3 position = MathUtils.Position(curve, t);
                float3 tangent = MathUtils.Tangent(curve, t);
                float2 velocity = WaterUtils.SampleVelocity(ref water, position);
                float dot = math.dot(velocity, math.normalizesafe(MathUtils.Right(tangent.xz)));
                sum += (flags & Game.Net.PlacementFlags.FlowLeft) != 0 ? -dot : dot;
            }
            return sum;
        }

        private static void InvertCourse(ref NetCourse course)
        {
            course.m_Curve = MathUtils.Invert(course.m_Curve);
            CommonUtils.Swap(ref course.m_StartPosition.m_Position, ref course.m_EndPosition.m_Position);
            CommonUtils.Swap(ref course.m_StartPosition.m_Rotation, ref course.m_EndPosition.m_Rotation);
            CommonUtils.Swap(ref course.m_StartPosition.m_Elevation, ref course.m_EndPosition.m_Elevation);
            CommonUtils.Swap(ref course.m_StartPosition.m_Flags, ref course.m_EndPosition.m_Flags);
            CommonUtils.Swap(ref course.m_StartPosition.m_ParentMesh, ref course.m_EndPosition.m_ParentMesh);
            CommonUtils.Swap(ref course.m_StartPosition.m_Entity, ref course.m_EndPosition.m_Entity);
            CommonUtils.Swap(ref course.m_StartPosition.m_SplitPosition, ref course.m_EndPosition.m_SplitPosition);
            quaternion turn = quaternion.RotateY(math.PI);
            course.m_StartPosition.m_Rotation = math.mul(turn, course.m_StartPosition.m_Rotation);
            course.m_EndPosition.m_Rotation = math.mul(turn, course.m_EndPosition.m_Rotation);
            // First/Last belong to the positions, not the ends that moved.
            CoursePosFlags firstLast = CoursePosFlags.IsFirst | CoursePosFlags.IsLast;
            CoursePosFlags a = course.m_StartPosition.m_Flags;
            CoursePosFlags b = course.m_EndPosition.m_Flags;
            course.m_StartPosition.m_Flags = (a & ~firstLast) | (b & firstLast);
            course.m_EndPosition.m_Flags = (b & ~firstLast) | (a & firstLast);
        }

        /// <summary>
        /// Creates the course objects of the edge's prefab and returns the owner definition
        /// for the course when one of them is MakeOwner.
        /// </summary>
        private bool DefineCourseObjects(ImportEdge edge, NetCourse course, EntityCommandBuffer commandBuffer, out OwnerDefinition owner)
        {
            owner = default;
            if (!EntityManager.HasBuffer<Game.Prefabs.SubObject>(edge.Prefab))
            {
                return false;
            }
            DynamicBuffer<Game.Prefabs.SubObject> subObjects = EntityManager.GetBuffer<Game.Prefabs.SubObject>(edge.Prefab, true);
            for (int i = 0; i < subObjects.Length; i++)
            {
                Game.Prefabs.SubObject subObject = subObjects[i];
                if ((subObject.m_Flags & SubObjectFlags.MakeOwner) != 0)
                {
                    Game.Objects.Transform transform = CourseObjectTransform(subObject, course.m_StartPosition, course.m_EndPosition);
                    CreateCourseObject(subObject.m_Prefab, transform, default, commandBuffer);
                    owner.m_Prefab = subObject.m_Prefab;
                    owner.m_Position = transform.m_Position;
                    owner.m_Rotation = transform.m_Rotation;
                    edge.CourseObjects.Add(CourseObjectName(subObject.m_Prefab) + " (owner)");
                    break;
                }
            }
            for (int i = 0; i < subObjects.Length; i++)
            {
                Game.Prefabs.SubObject subObject = subObjects[i];
                if ((subObject.m_Flags & (SubObjectFlags.CoursePlacement | SubObjectFlags.MakeOwner)) == SubObjectFlags.CoursePlacement)
                {
                    Game.Objects.Transform transform = CourseObjectTransform(subObject, course.m_StartPosition, course.m_EndPosition);
                    CreateCourseObject(subObject.m_Prefab, transform, owner, commandBuffer);
                    edge.CourseObjects.Add(CourseObjectName(subObject.m_Prefab));
                }
            }
            return owner.m_Prefab != Entity.Null;
        }

        private string CourseObjectName(Entity prefab)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            return prefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(prefab), out PrefabBase p) ? p.name : $"{prefab.Index}:{prefab.Version}";
        }

        private static Game.Objects.Transform CourseObjectTransform(Game.Prefabs.SubObject subObject, CoursePos start, CoursePos end)
        {
            CoursePos pos = (subObject.m_Flags & SubObjectFlags.StartPlacement) != 0 ? start : end;
            return new Game.Objects.Transform
            {
                m_Position = ObjectUtils.LocalToWorld(pos.m_Position, pos.m_Rotation, subObject.m_Position),
                m_Rotation = math.mul(pos.m_Rotation, subObject.m_Rotation),
            };
        }

        private void CreateCourseObject(Entity prefab, Game.Objects.Transform transform, OwnerDefinition owner, EntityCommandBuffer commandBuffer)
        {
            Entity e = commandBuffer.CreateEntity();
            var definition = new ObjectDefinition
            {
                m_ParentMesh = -1,
                m_Position = transform.m_Position,
                m_Rotation = transform.m_Rotation,
            };
            if (owner.m_Prefab != Entity.Null)
            {
                Game.Objects.Transform local = ObjectUtils.WorldToLocal(ObjectUtils.InverseTransform(new Game.Objects.Transform(owner.m_Position, owner.m_Rotation)), transform);
                definition.m_LocalPosition = local.m_Position;
                definition.m_LocalRotation = local.m_Rotation;
                commandBuffer.AddComponent(e, owner);
            }
            else
            {
                definition.m_LocalPosition = transform.m_Position;
                definition.m_LocalRotation = transform.m_Rotation;
            }
            commandBuffer.AddComponent(e, new CreationDefinition { m_Prefab = prefab });
            commandBuffer.AddComponent(e, definition);
            commandBuffer.AddComponent(e, default(Updated));
            CreateObjectSubNets(transform, prefab, commandBuffer);
        }

        /// <summary>NetToolSystem.CreateSubNets: the prefab sub-nets of a course object, owned by it.</summary>
        private void CreateObjectSubNets(Game.Objects.Transform transform, Entity prefab, EntityCommandBuffer commandBuffer)
        {
            if (!EntityManager.HasBuffer<Game.Prefabs.SubNet>(prefab))
            {
                return;
            }
            DynamicBuffer<Game.Prefabs.SubNet> subNets = EntityManager.GetBuffer<Game.Prefabs.SubNet>(prefab, true);
            var nodes = new NativeList<float4>(subNets.Length * 2, Allocator.Temp);
            for (int i = 0; i < subNets.Length; i++)
            {
                Game.Prefabs.SubNet subNet = subNets[i];
                if (subNet.m_NodeIndex.x >= 0)
                {
                    while (nodes.Length <= subNet.m_NodeIndex.x)
                    {
                        nodes.Add(default(float4));
                    }
                    nodes[subNet.m_NodeIndex.x] += new float4(subNet.m_Curve.a, 1f);
                }
                if (subNet.m_NodeIndex.y >= 0)
                {
                    while (nodes.Length <= subNet.m_NodeIndex.y)
                    {
                        nodes.Add(default(float4));
                    }
                    nodes[subNet.m_NodeIndex.y] += new float4(subNet.m_Curve.d, 1f);
                }
            }
            for (int j = 0; j < nodes.Length; j++)
            {
                nodes[j] /= math.max(1f, nodes[j].w);
            }
            ComponentLookup<NetGeometryData> geometry = GetComponentLookup<NetGeometryData>(true);
            bool leftHand = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>().leftHandTraffic;
            for (int k = 0; k < subNets.Length; k++)
            {
                Game.Prefabs.SubNet subNet = NetUtils.GetSubNet(subNets, k, leftHand, ref geometry);
                Entity e = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(e, new CreationDefinition { m_Prefab = subNet.m_Prefab });
                commandBuffer.AddComponent(e, default(Updated));
                commandBuffer.AddComponent(e, new OwnerDefinition
                {
                    m_Prefab = prefab,
                    m_Position = transform.m_Position,
                    m_Rotation = transform.m_Rotation,
                });
                NetCourse course = default;
                course.m_Curve = new Bezier4x3(
                    ObjectUtils.LocalToWorld(transform.m_Position, transform.m_Rotation, subNet.m_Curve.a),
                    ObjectUtils.LocalToWorld(transform.m_Position, transform.m_Rotation, subNet.m_Curve.b),
                    ObjectUtils.LocalToWorld(transform.m_Position, transform.m_Rotation, subNet.m_Curve.c),
                    ObjectUtils.LocalToWorld(transform.m_Position, transform.m_Rotation, subNet.m_Curve.d));
                course.m_StartPosition.m_Position = course.m_Curve.a;
                course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve), transform.m_Rotation);
                course.m_StartPosition.m_CourseDelta = 0f;
                course.m_StartPosition.m_Elevation = subNet.m_Curve.a.y;
                course.m_StartPosition.m_ParentMesh = subNet.m_ParentMesh.x;
                if (subNet.m_NodeIndex.x >= 0)
                {
                    course.m_StartPosition.m_Position = ObjectUtils.LocalToWorld(transform, nodes[subNet.m_NodeIndex.x].xyz);
                }
                course.m_EndPosition.m_Position = course.m_Curve.d;
                course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve), transform.m_Rotation);
                course.m_EndPosition.m_CourseDelta = 1f;
                course.m_EndPosition.m_Elevation = subNet.m_Curve.d.y;
                course.m_EndPosition.m_ParentMesh = subNet.m_ParentMesh.y;
                if (subNet.m_NodeIndex.y >= 0)
                {
                    course.m_EndPosition.m_Position = ObjectUtils.LocalToWorld(transform, nodes[subNet.m_NodeIndex.y].xyz);
                }
                course.m_Length = MathUtils.Length(course.m_Curve);
                course.m_FixedIndex = -1;
                course.m_StartPosition.m_Flags |= CoursePosFlags.IsFirst;
                course.m_EndPosition.m_Flags |= CoursePosFlags.IsLast;
                if (course.m_StartPosition.m_Position.Equals(course.m_EndPosition.m_Position))
                {
                    course.m_StartPosition.m_Flags |= CoursePosFlags.IsLast;
                    course.m_EndPosition.m_Flags |= CoursePosFlags.IsFirst;
                }
                commandBuffer.AddComponent(e, course);
                if (subNet.m_Upgrades != default(CompositionFlags))
                {
                    commandBuffer.AddComponent(e, new Upgraded { m_Flags = subNet.m_Upgrades });
                }
                if (m_ToolSystem.actionMode.IsEditor())
                {
                    commandBuffer.AddComponent(e, new LocalCurveCache { m_Curve = subNet.m_Curve });
                }
            }
            nodes.Dispose();
        }
    }
}
