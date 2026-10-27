using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Mathematics;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.44: raster terrain paint. Instead of one TerrainMaterialSystem.ApplyBrush per stamp
    /// (1.6 M stamps, 11 min on a large map), every group of polygons with the same material and strength
    /// is rasterised on the CPU into one coverage mask (inside 1, the feather as a linear ramp
    /// from the outline, softEdge as an outward ramp) and handed to the game's own private
    /// TerrainMaterialSystem.ApplyToSplatmap as the brush texture of a single square brush over
    /// the group's bounds, once per splatmap. The paint kernel, the channel and the strength
    /// handling are the game's, so the result is what one brush stamp with that falloff does.
    ///
    /// The kernel's mapping of brush-texture uv to world x/z (flips, transpose) and whether it
    /// clips the brush to its inscribed circle cannot be read from managed code; the first
    /// raster paint of a session calibrates them with a 16x16 test mask on the splatmaps,
    /// reads the result back and restores the splatmaps.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private sealed class RasterCalibration
        {
            public bool FlipU;
            public bool FlipV;
            public bool Transpose;
            public bool CircleClip;
            public string Detail;
        }

        private static RasterCalibration s_RasterCalibration;
        private static MethodInfo s_ApplyToSplatmap;

        private sealed class RasterGroup
        {
            public Entity Material;
            public float Strength;
            public List<(string id, float2[] ring, float feather, float soft)> Polygons = new List<(string, float2[], float, float)>();
            public List<(string id, float2 centre, float radius)> Strokes = new List<(string, float2, float)>();
        }

        /// <summary>CPU copy of a brush prefab's texture (the stamp footprint), read back once.</summary>
        private sealed class BrushFootprint
        {
            public int Width;
            public int Height;
            public float[] Values;

            public float Sample(float u, float v)
            {
                if (u < 0f || v < 0f || u > 1f || v > 1f)
                {
                    return 0f;
                }
                float x = u * Width - 0.5f, y = v * Height - 0.5f;
                int x0 = math.clamp((int)math.floor(x), 0, Width - 1), y0 = math.clamp((int)math.floor(y), 0, Height - 1);
                int x1 = math.min(x0 + 1, Width - 1), y1 = math.min(y0 + 1, Height - 1);
                float fx = math.saturate(x - x0), fy = math.saturate(y - y0);
                float a = math.lerp(Values[y0 * Width + x0], Values[y0 * Width + x1], fx);
                float b = math.lerp(Values[y1 * Width + x0], Values[y1 * Width + x1], fx);
                return math.lerp(a, b, fy);
            }
        }

        private static readonly Dictionary<Texture, BrushFootprint> s_BrushFootprints = new Dictionary<Texture, BrushFootprint>();

        /// <summary>
        /// Reads the brush texture through a 64x64 render texture. The value is the alpha channel
        /// for Alpha8 brushes, else the red channel.
        /// </summary>
        private static BrushFootprint Footprint(Texture texture)
        {
            if (texture == null)
            {
                return null;
            }
            if (s_BrushFootprints.TryGetValue(texture, out BrushFootprint cached))
            {
                return cached;
            }
            const int size = 64;
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var read = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            RenderTexture active = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                read.Apply();
                bool alpha = texture is Texture2D t2 && t2.format == TextureFormat.Alpha8;
                Color32[] pixels = read.GetPixels32();
                var footprint = new BrushFootprint { Width = size, Height = size, Values = new float[size * size] };
                for (int i = 0; i < pixels.Length; i++)
                {
                    footprint.Values[i] = (alpha ? pixels[i].a : pixels[i].r) / 255f;
                }
                s_BrushFootprints[texture] = footprint;
                return footprint;
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(read);
            }
        }

        private static bool ResolveRasterApi(out string error)
        {
            if (s_ApplyToSplatmap == null)
            {
                s_ApplyToSplatmap = typeof(TerrainMaterialSystem).GetMethod("ApplyToSplatmap", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            error = s_ApplyToSplatmap == null ? "TerrainMaterialSystem.ApplyToSplatmap not found (game update?); use options.method \"stamp\"" : null;
            return error == null;
        }

        private static RenderTexture SplatmapField(TerrainMaterialSystem system, string name)
        {
            var rt = typeof(TerrainMaterialSystem).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(system) as RenderTexture;
            if (rt != null && !rt.IsCreated())
            {
                rt.Create();
            }
            return rt;
        }

        /// <summary>One square brush of side <paramref name="side"/> centred at <paramref name="centre"/> on both splatmaps.</summary>
        private void DispatchMask(TerrainMaterialSystem system, Entity material, Texture mask, float2 centre, float side, float strength)
        {
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            var brush = new Brush
            {
                m_Tool = material,
                m_Position = new float3(centre.x, 0f, centre.y),
                m_Target = new float3(centre.x, 0f, centre.y),
                m_Start = new float3(centre.x, 0f, centre.y),
                m_Angle = 0f,
                m_Size = side,
                m_Strength = math.max(strength, 0.0001f),
                m_Opacity = 1f,
            };
            RenderTexture playable = SplatmapField(system, "m_UserSplatmap");
            RenderTexture world = SplatmapField(system, "m_UserSplatmapWorld");
            s_ApplyToSplatmap.Invoke(system, new object[] { playable, brush, mask, terrain.playableArea, terrain.playableOffset });
            s_ApplyToSplatmap.Invoke(system, new object[] { world, brush, mask, terrain.worldSize, terrain.worldOffset });
        }

        private static Texture2D MaskTexture(int n, Func<int, int, byte> value)
        {
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "CS2MCP paint mask",
            };
            NativeArray<byte> data = texture.GetRawTextureData<byte>();
            for (int py = 0; py < n; py++)
            {
                for (int px = 0; px < n; px++)
                {
                    byte v = value(px, py);
                    int i = (py * n + px) * 4;
                    data[i] = v;
                    data[i + 1] = v;
                    data[i + 2] = v;
                    data[i + 3] = v;
                }
            }
            // makeNoLongerReadable: the CPU copy (64 MB at 4096²) is freed at upload.
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>
        /// 0.9.45: the mask texture from the world grid in one tight loop (the per-pixel delegate and
        /// NativeArray indexer cost ~1.5 s per 4096² mask in testing).
        /// </summary>
        private static Texture2D MaskFromGrid(float[] grid, int n, float2 origin, float cell, int texN, float2 brushOrigin, float brushSide, RasterCalibration cal)
        {
            var pixels = new uint[texN * texN];
            // Grid column/row per texture column/row (each texture axis maps to one world axis).
            var mapU = new int[texN];
            var mapV = new int[texN];
            for (int i = 0; i < texN; i++)
            {
                float t = (i + 0.5f) / texN;
                float a = cal.FlipU ? 1f - t : t;
                float b = cal.FlipV ? 1f - t : t;
                // u -> x (or z when transposed), v -> z (or x)
                float worldU = (cal.Transpose ? brushOrigin.y : brushOrigin.x) + a * brushSide;
                float worldV = (cal.Transpose ? brushOrigin.x : brushOrigin.y) + b * brushSide;
                mapU[i] = (int)math.floor((worldU - (cal.Transpose ? origin.y : origin.x)) / cell);
                mapV[i] = (int)math.floor((worldV - (cal.Transpose ? origin.x : origin.y)) / cell);
            }
            for (int py = 0; py < texN; py++)
            {
                int gv = mapV[py];
                if (gv < 0 || gv >= n)
                {
                    continue;
                }
                int row = py * texN;
                for (int px = 0; px < texN; px++)
                {
                    int gu = mapU[px];
                    if (gu < 0 || gu >= n)
                    {
                        continue;
                    }
                    float value = cal.Transpose ? grid[gu * n + gv] : grid[gv * n + gu];
                    if (value <= 0f)
                    {
                        continue;
                    }
                    uint c = (uint)(value * 255f + 0.5f);
                    pixels[row + px] = c | (c << 8) | (c << 16) | (c << 24);
                }
            }
            var texture = new Texture2D(texN, texN, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "CS2MCP paint mask",
            };
            texture.SetPixelData(pixels, 0);
            // makeNoLongerReadable: the CPU copy (64 MB at 4096²) is freed at upload.
            texture.Apply(false, true);
            return texture;
        }

        private static Color32 ReadRaw(RenderTexture rt, TerrainSystem terrain, float2 world)
        {
            float2 uv = (world - terrain.playableOffset) / terrain.playableArea;
            int x = math.clamp((int)(uv.x * rt.width), 0, rt.width - 1);
            int y = math.clamp((int)(uv.y * rt.height), 0, rt.height - 1);
            RenderTexture active = RenderTexture.active;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
                tex.Apply();
                return tex.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = active;
                UnityEngine.Object.Destroy(tex);
            }
        }

        private static int Change(Color32 a, Color32 b)
        {
            return math.abs(a.r - b.r) + math.abs(a.g - b.g) + math.abs(a.b - b.b) + math.abs(a.a - b.a);
        }

        /// <summary>
        /// Paints a 16x16 mask (quadrant px&lt;8,py&lt;8 = 255, px&gt;=8,py&lt;8 = 128, rest 0) over a
        /// 64-texel square at the playable centre, reads where it landed, restores both splatmaps.
        /// </summary>
        private RasterCalibration Calibrate(TerrainMaterialSystem system, Entity material)
        {
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            RenderTexture playable = SplatmapField(system, "m_UserSplatmap");
            RenderTexture world = SplatmapField(system, "m_UserSplatmapWorld");
            RenderTexture backupPlayable = Snapshot(playable);
            RenderTexture backupWorld = Snapshot(world);
            float texel = terrain.playableArea.x / playable.width;
            float side = 64f * texel;
            float2 centre = terrain.playableOffset + terrain.playableArea * 0.5f;
            float q = side * 0.25f;
            float2[] probes =
            {
                centre + new float2(-q, -q), centre + new float2(q, -q), centre + new float2(-q, q), centre + new float2(q, q),
            };
            float c = side * 0.45f;
            float2[] corners =
            {
                centre + new float2(-c, -c), centre + new float2(c, -c), centre + new float2(-c, c), centre + new float2(c, c),
            };
            var before = new Color32[4];
            var beforeCorner = new Color32[4];
            for (int i = 0; i < 4; i++)
            {
                before[i] = ReadRaw(playable, terrain, probes[i]);
                beforeCorner[i] = ReadRaw(playable, terrain, corners[i]);
            }
            Texture2D mask = MaskTexture(16, (px, py) => py < 8 ? (px < 8 ? (byte)255 : (byte)128) : (byte)0);
            var result = new RasterCalibration();
            try
            {
                DispatchMask(system, material, mask, centre, side, 1f);
                var change = new int[4];
                var cornerChange = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    change[i] = Change(ReadRaw(playable, terrain, probes[i]), before[i]);
                    cornerChange[i] = Change(ReadRaw(playable, terrain, corners[i]), beforeCorner[i]);
                }
                // probes: 0 (-x,-z), 1 (+x,-z), 2 (-x,+z), 3 (+x,+z)
                int a = 0, b = -1;
                for (int i = 1; i < 4; i++)
                {
                    if (change[i] > change[a])
                    {
                        a = i;
                    }
                }
                for (int i = 0; i < 4; i++)
                {
                    if (i != a && change[i] > 0 && (b < 0 || change[i] > change[b]))
                    {
                        b = i;
                    }
                }
                result.Detail = $"changes at (-x-z, +x-z, -x+z, +x+z): {string.Join("/", change)}; corners {string.Join("/", cornerChange)}";
                if (change[a] == 0 || b < 0 || change[b] >= change[a])
                {
                    result = null;
                }
                else
                {
                    bool aPlusX = (a & 1) != 0, aPlusZ = (a & 2) != 0;
                    bool bPlusX = (b & 1) != 0, bPlusZ = (b & 2) != 0;
                    if (aPlusZ == bPlusZ && aPlusX != bPlusX)
                    {
                        // u runs along x, v along z.
                        result.Transpose = false;
                        result.FlipU = aPlusX;
                        result.FlipV = aPlusZ;
                    }
                    else if (aPlusX == bPlusX && aPlusZ != bPlusZ)
                    {
                        // u runs along z, v along x.
                        result.Transpose = true;
                        result.FlipU = aPlusZ;
                        result.FlipV = aPlusX;
                    }
                    else
                    {
                        result = null;
                    }
                    if (result != null)
                    {
                        result.CircleClip = cornerChange[a] == 0;
                        result.Detail = $"changes at (-x-z, +x-z, -x+z, +x+z): {string.Join("/", change)}; corners {string.Join("/", cornerChange)}";
                    }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(mask);
                Restore(backupPlayable, playable);
                Restore(backupWorld, world);
                ReleaseSnapshot(backupPlayable);
                ReleaseSnapshot(backupWorld);
                var whole = new Bounds2(terrain.worldOffset, terrain.worldOffset + terrain.worldSize);
                terrain.OnAreaChanged(in whole);
            }
            return result;
        }

        private static void ReleaseSnapshot(RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
        }

        /// <summary>
        /// Rasterises a group into a world-aligned coverage grid over the square
        /// [origin, origin + side]² with n x n cells (cell (0,0) at the min corner).
        /// </summary>
        private static float[] RasterGroupGrid(RasterGroup group, float2 origin, float side, int n, BrushFootprint brush)
        {
            // keep[] = product of (1 - v_i * s): what repeated stamps of strength s leave of the old value
            // if the kernel moves the texel toward the target by brush x strength each time.
            var keep = new float[n * n];
            for (int i = 0; i < keep.Length; i++)
            {
                keep[i] = 1f;
            }
            float s = math.max(group.Strength, 0.0001f);
            float cell = side / n;
            if (brush != null)
            {
                foreach ((string id, float2 centre, float radius) in group.Strokes)
                {
                    float r = math.max(radius, 1.5f * cell);
                    int sx0 = math.clamp((int)math.floor((centre.x - r - origin.x) / cell), 0, n - 1);
                    int sx1 = math.clamp((int)math.ceil((centre.x + r - origin.x) / cell), 0, n - 1);
                    int sz0 = math.clamp((int)math.floor((centre.y - r - origin.y) / cell), 0, n - 1);
                    int sz1 = math.clamp((int)math.ceil((centre.y + r - origin.y) / cell), 0, n - 1);
                    for (int iz = sz0; iz <= sz1; iz++)
                    {
                        for (int ix = sx0; ix <= sx1; ix++)
                        {
                            float2 p = origin + new float2((ix + 0.5f) * cell, (iz + 0.5f) * cell);
                            float2 uv = (p - centre) / (2f * r) + 0.5f;
                            float b = brush.Sample(uv.x, uv.y);
                            if (b > 0f)
                            {
                                keep[iz * n + ix] *= 1f - b * s;
                            }
                        }
                    }
                }
            }
            foreach ((string id, float2[] ring, float feather, float soft) in group.Polygons)
            {
                float band = math.max(feather, soft);
                float2 min = ring[0], max = ring[0];
                foreach (float2 p in ring)
                {
                    min = math.min(min, p);
                    max = math.max(max, p);
                }
                int x0 = math.clamp((int)math.floor((min.x - band - origin.x) / cell), 0, n - 1);
                int x1 = math.clamp((int)math.ceil((max.x + band - origin.x) / cell), 0, n - 1);
                int z0 = math.clamp((int)math.floor((min.y - band - origin.y) / cell), 0, n - 1);
                int z1 = math.clamp((int)math.ceil((max.y + band - origin.y) / cell), 0, n - 1);
                int w = x1 - x0 + 1, h = z1 - z0 + 1;
                // Distance to the outline, only within the band (edges stamped as capsules).
                float[] dist = null;
                if (band > 0f)
                {
                    dist = new float[w * h];
                    for (int i = 0; i < dist.Length; i++)
                    {
                        dist[i] = float.MaxValue;
                    }
                    for (int e = 0; e < ring.Length; e++)
                    {
                        float2 a = ring[e], b = ring[(e + 1) % ring.Length];
                        int ex0 = math.max(x0, (int)math.floor((math.min(a.x, b.x) - band - origin.x) / cell));
                        int ex1 = math.min(x1, (int)math.ceil((math.max(a.x, b.x) + band - origin.x) / cell));
                        int ez0 = math.max(z0, (int)math.floor((math.min(a.y, b.y) - band - origin.y) / cell));
                        int ez1 = math.min(z1, (int)math.ceil((math.max(a.y, b.y) + band - origin.y) / cell));
                        float2 ab = b - a;
                        float len2 = math.lengthsq(ab);
                        for (int iz = ez0; iz <= ez1; iz++)
                        {
                            for (int ix = ex0; ix <= ex1; ix++)
                            {
                                float2 p = origin + new float2((ix + 0.5f) * cell, (iz + 0.5f) * cell);
                                float t = len2 > 0f ? math.saturate(math.dot(p - a, ab) / len2) : 0f;
                                float d = math.distance(p, a + ab * t);
                                int k = (iz - z0) * w + (ix - x0);
                                if (d < dist[k])
                                {
                                    dist[k] = d;
                                }
                            }
                        }
                    }
                }
                var xs = new List<float>();
                for (int iz = z0; iz <= z1; iz++)
                {
                    float z = origin.y + (iz + 0.5f) * cell;
                    xs.Clear();
                    for (int e = 0; e < ring.Length; e++)
                    {
                        float2 a = ring[e], b = ring[(e + 1) % ring.Length];
                        if ((a.y > z) != (b.y > z))
                        {
                            xs.Add(a.x + (z - a.y) / (b.y - a.y) * (b.x - a.x));
                        }
                    }
                    xs.Sort();
                    int span = 0;
                    for (int ix = x0; ix <= x1; ix++)
                    {
                        float x = origin.x + (ix + 0.5f) * cell;
                        while (span < xs.Count && xs[span] <= x)
                        {
                            span++;
                        }
                        bool inside = (span & 1) == 1;
                        float d = dist != null ? dist[(iz - z0) * w + (ix - x0)] : float.MaxValue;
                        float v;
                        if (inside)
                        {
                            v = feather > 0f ? math.saturate(d / feather) : 1f;
                        }
                        else
                        {
                            v = feather <= 0f && soft > 0f ? math.saturate(1f - d / soft) : 0f;
                        }
                        if (v > 0f)
                        {
                            keep[iz * n + ix] *= 1f - v * s;
                        }
                    }
                }
            }
            // Mask for one dispatch of strength s: (1 - keep) / s.
            for (int i = 0; i < keep.Length; i++)
            {
                keep[i] = math.saturate((1f - keep[i]) / s);
            }
            return keep;
        }

        /// <summary>Paints the groups; returns per-group detail objects.</summary>
        private List<object> RasterPaint(TerrainMaterialSystem system, List<RasterGroup> groups, RasterCalibration cal, System.Diagnostics.Stopwatch clock, Texture brushTexture)
        {
            BrushFootprint footprint = Footprint(brushTexture);
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            RenderTexture playable = SplatmapField(system, "m_UserSplatmap");
            float texel = terrain.playableArea.x / playable.width;
            var details = new List<object>();
            foreach (RasterGroup group in groups)
            {
                double t0 = clock.Elapsed.TotalMilliseconds;
                float2 min = new float2(float.MaxValue), max = new float2(float.MinValue);
                foreach ((string id, float2[] ring, float feather, float soft) in group.Polygons)
                {
                    float band = math.max(feather, soft) + 2f * texel;
                    foreach (float2 p in ring)
                    {
                        min = math.min(min, p - band);
                        max = math.max(max, p + band);
                    }
                }
                foreach ((string id, float2 c, float radius) in group.Strokes)
                {
                    float r = math.max(radius, 1.5f * texel) + 2f * texel;
                    min = math.min(min, c - r);
                    max = math.max(max, c + r);
                }
                float2 centre = (min + max) * 0.5f;
                float extent = math.cmax(max - min);
                // The mask grid covers the bounds; the brush square may be larger (circle clipping).
                int n = math.clamp((int)math.ceil(extent / texel), 8, 4096);
                float cell = extent / n;
                float brushSide = cal.CircleClip ? extent * 1.4143f : extent;
                int texN = math.clamp((int)math.ceil(brushSide / cell), 8, 4096);
                float2 origin = centre - extent * 0.5f;
                float[] grid = RasterGroupGrid(group, origin, extent, n, footprint);
                double t1 = clock.Elapsed.TotalMilliseconds;
                float2 brushOrigin = centre - brushSide * 0.5f;
                float texCell = brushSide / texN;
                Texture2D mask = MaskFromGrid(grid, n, origin, cell, texN, brushOrigin, brushSide, cal);
                double t2 = clock.Elapsed.TotalMilliseconds;
                try
                {
                    DispatchMask(system, group.Material, mask, centre, brushSide, group.Strength);
                }
                finally
                {
                    UnityEngine.Object.Destroy(mask);
                }
                double t3 = clock.Elapsed.TotalMilliseconds;
                details.Add(new
                {
                    material = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>().GetPrefab<Game.Prefabs.PrefabBase>(group.Material).name,
                    strength = group.Strength,
                    polygons = group.Polygons.Count,
                    strokes = group.Strokes.Count,
                    bounds = new[] { Math.Round(min.x, 1), Math.Round(min.y, 1), Math.Round(max.x, 1), Math.Round(max.y, 1) },
                    maskSize = texN,
                    rasterMs = Math.Round(t1 - t0, 1),
                    uploadMs = Math.Round(t2 - t1, 1),
                    dispatchMs = Math.Round(t3 - t2, 1),
                });
            }
            return details;
        }
    }
}
