using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Placement sur GPU d'un <see cref="PlacementSetup"/> (InstancePlacement.compute) : comptage des candidats gardés par la
    /// zone, puis écriture des instances, ou des placements, à leur rang. Entre les deux, le CPU ne lit que des compteurs
    /// (total, par variante, boîte des positions, échelle max), pour dimensionner le buffer d'instances.
    /// Mémoire pendant un passage : masque des candidats (1 bit chacun) et, pour une liste, ses placements (28 o chacun).
    /// </summary>
    internal static class GpuPlacement
    {
        public const string ShaderResource = "NyxGpuInstancing/InstancePlacement";
        public const int ThreadGroupSize = 256;                    // = NYX_PLACE_THREADS
        const int MaskWordsPerGroup = ThreadGroupSize / 32;        // = NYX_MASK_WORDS
        const int StatsVariants = 8;                               // = NYX_STATS_VARIANTS : [0] gardés, [1..3] min, [4..6] max, [7] échelle
        const int StatsSize = StatsVariants + MeshSpawner.MaxVariants;
        static readonly int PlacementStride = Marshal.SizeOf<MeshPlacement>();

        static ComputeShader s_shader;
        static int s_kernelCount = -1;
        static int s_kernelScan;
        static int s_kernelWriteInstances;
        static int s_kernelWritePlacements;
        static readonly uint[] s_stats = new uint[StatsSize];
        static readonly uint[] s_statsReset = NewStatsReset();

        /// <summary>Buffers d'un passage (comptage, puis écriture) et ses compteurs. À libérer (using) : zone comprise.</summary>
        public sealed class Pass : IDisposable
        {
            internal ComputeBuffer list, anchors, mask, groups, stats;
            internal ZoneGpu zone;
            internal int groupCount, groupsX, groupsY;

            /// <summary>Candidats gardés.</summary>
            public int Kept { get; internal set; }

            /// <summary>Boîte des positions gardées (vide si aucune).</summary>
            public Bounds Positions { get; internal set; }

            /// <summary>Plus grande échelle gardée (0 si aucune).</summary>
            public float MaxScale { get; internal set; }

            public void Dispose()
            {
                list?.Release();
                anchors?.Release();
                mask?.Release();
                groups?.Release();
                stats?.Release();
                list = anchors = mask = groups = stats = null;
                zone?.Release();
            }
        }

        /// <summary>Compute shader et noyaux (une fois pour tous les spawners). false s'il est introuvable.</summary>
        public static bool LoadShader()
        {
            if (s_shader == null)
            {
                s_kernelCount = -1;
                s_shader = Resources.Load<ComputeShader>(ShaderResource);
                if (s_shader != null && s_shader.HasKernel("NyxCountPlacements") && s_shader.HasKernel("NyxScanGroups")
                    && s_shader.HasKernel("NyxWriteInstances") && s_shader.HasKernel("NyxWritePlacements"))
                {
                    s_kernelScan = s_shader.FindKernel("NyxScanGroups");
                    s_kernelWriteInstances = s_shader.FindKernel("NyxWriteInstances");
                    s_kernelWritePlacements = s_shader.FindKernel("NyxWritePlacements");
                    s_kernelCount = s_shader.FindKernel("NyxCountPlacements");
                }
            }
            return s_kernelCount >= 0;
        }

        /// <summary>
        /// Comptage sur GPU (NyxCountPlacements + NyxScanGroups) puis lecture des compteurs (bloquante, quelques entiers) :
        /// gardés, par variante (si perVariant est créé), boîte des positions, échelle max. Le passage garde ses buffers pour
        /// l'écriture (WriteInstances ou ReadPlacements) : à libérer (using).
        /// </summary>
        public static Pass Count(PlacementSetup setup, NativeArray<int> perVariant)
        {
            ComputeShader cs = s_shader;
            var pass = new Pass { zone = setup.zone };
            try
            {
                int count = setup.candidates;
                pass.groupCount = (count + ThreadGroupSize - 1) / ThreadGroupSize;
                SpawnerGpu.GroupCounts(pass.groupCount, out pass.groupsX, out pass.groupsY);
                bool fromList = setup.fromList && count > 0;
                pass.list = new ComputeBuffer(fromList ? count : 1, PlacementStride);
                if (fromList)
                    pass.list.SetData(setup.list);
                pass.anchors = new ComputeBuffer(Mathf.Max(setup.anchors.Length, 1), 4 * sizeof(float));
                if (setup.anchors.Length > 0)
                    pass.anchors.SetData(setup.anchors);
                pass.mask = new ComputeBuffer(Mathf.Max(pass.groupCount * MaskWordsPerGroup, 1), sizeof(uint));
                pass.groups = new ComputeBuffer(Mathf.Max(pass.groupCount, 1), sizeof(uint));
                pass.stats = new ComputeBuffer(StatsSize, sizeof(uint));
                pass.stats.SetData(s_statsReset);

                cs.SetInt(Ids.PlaceCount, count);
                cs.SetInt(Ids.PlaceGroupCount, pass.groupCount);
                cs.SetInt(Ids.PlaceFromList, setup.fromList ? 1 : 0);
                cs.SetInt(Ids.PlaceGridCount, setup.gridCount);
                cs.SetInt(Ids.PlaceCellsX, setup.cellsX);
                cs.SetVector(Ids.PlaceGrid, setup.grid);
                cs.SetVector(Ids.PlaceShape, setup.shape);
                cs.SetInt(Ids.PlaceSeed, setup.seed);
                cs.SetInt(Ids.PlaceClumps, setup.clumps);
                cs.SetInt(Ids.PlaceRandomVariants, setup.randomVariants);
                cs.SetInt(Ids.PlaceTested, setup.tested);
                cs.SetInt(Ids.PlaceClip, setup.clip ? 1 : 0);
                setup.zone.Bind(cs, s_kernelCount);
                cs.SetInt(ShaderIds.GroupsX, pass.groupsX);
                foreach (int kernel in new[] { s_kernelCount, s_kernelWriteInstances, s_kernelWritePlacements })
                {
                    cs.SetBuffer(kernel, Ids.PlaceList, pass.list);
                    cs.SetBuffer(kernel, Ids.PlaceAnchors, pass.anchors);
                    cs.SetBuffer(kernel, Ids.PlaceMask, pass.mask);
                    cs.SetBuffer(kernel, Ids.PlaceGroups, pass.groups);
                }
                cs.SetBuffer(s_kernelCount, Ids.Stats, pass.stats);
                cs.SetBuffer(s_kernelScan, Ids.PlaceGroups, pass.groups);

                if (pass.groupCount > 0)
                {
                    cs.Dispatch(s_kernelCount, pass.groupsX, pass.groupsY, 1);
                    cs.Dispatch(s_kernelScan, 1, 1, 1);
                }
                pass.stats.GetData(s_stats);
                pass.Kept = (int)s_stats[0];

                var positions = new Bounds();
                if (pass.Kept > 0)
                {
                    var min = new Vector3(Ordered(s_stats[1]), Ordered(s_stats[2]), Ordered(s_stats[3]));
                    var max = new Vector3(Ordered(s_stats[4]), Ordered(s_stats[5]), Ordered(s_stats[6]));
                    positions.SetMinMax(min, max);
                    pass.MaxScale = BitConverter.Int32BitsToSingle(unchecked((int)s_stats[7]));
                }
                pass.Positions = positions;
                if (perVariant.IsCreated)
                {
                    for (int v = 0; v < perVariant.Length; v++)
                        perVariant[v] = (int)s_stats[StatsVariants + v];
                }
                return pass;
            }
            catch
            {
                pass.Dispose();
                throw;
            }
        }

        /// <summary>Instances par défaut (lacet + échelle + position, graine) des candidats gardés, à leur rang, dans 'instances'.</summary>
        public static void WriteInstances(Pass pass, ComputeBuffer instances, VariantPlan variants)
        {
            variants.Apply(s_shader);
            s_shader.SetBuffer(s_kernelWriteInstances, Ids.Instances, instances);
            s_shader.Dispatch(s_kernelWriteInstances, pass.groupsX, pass.groupsY, 1);
        }

        /// <summary>Placements gardés, dans l'ordre (NyxWritePlacements), lus par le CPU (lecture bloquante, 28 o chacun).</summary>
        public static NativeArray<MeshPlacement> ReadPlacements(Pass pass, Allocator allocator)
        {
            var output = new NativeArray<MeshPlacement>(pass.Kept, allocator, NativeArrayOptions.UninitializedMemory);
            if (pass.Kept == 0)
                return output;
            var buffer = new ComputeBuffer(pass.Kept, PlacementStride);
            try
            {
                s_shader.SetBuffer(s_kernelWritePlacements, Ids.Placements, buffer);
                s_shader.Dispatch(s_kernelWritePlacements, pass.groupsX, pass.groupsY, 1);
                SpawnerGpu.ReadBack(buffer, output);
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                buffer.Release();
            }
            return output;
        }

        /// <summary>Placements de 'setup', refaits sur GPU et lus par le CPU (lecture bloquante, 28 o chacun).</summary>
        public static NativeArray<MeshPlacement> ReadPlacements(PlacementSetup setup, Allocator allocator)
        {
            using Pass pass = Count(setup, default);
            return ReadPlacements(pass, allocator);
        }

        static uint[] NewStatsReset()
        {
            var reset = new uint[StatsSize];
            for (int i = 1; i <= 3; i++)
                reset[i] = uint.MaxValue;   // min x, y, z (bits ordonnés)
            return reset;
        }

        /// <summary>Flottant à partir de ses bits ordonnés (NyxOrderedBits, InstancePlacement.compute).</summary>
        static float Ordered(uint bits) =>
            BitConverter.Int32BitsToSingle(unchecked((int)((bits & 0x80000000u) != 0 ? bits & 0x7FFFFFFFu : ~bits)));

        static class Ids
        {
            public static readonly int PlaceCount = Shader.PropertyToID("_NyxPlaceCount");
            public static readonly int PlaceGroupCount = Shader.PropertyToID("_NyxPlaceGroupCount");
            public static readonly int PlaceFromList = Shader.PropertyToID("_NyxPlaceFromList");
            public static readonly int PlaceGridCount = Shader.PropertyToID("_NyxPlaceGridCount");
            public static readonly int PlaceCellsX = Shader.PropertyToID("_NyxPlaceCellsX");
            public static readonly int PlaceGrid = Shader.PropertyToID("_NyxPlaceGrid");
            public static readonly int PlaceShape = Shader.PropertyToID("_NyxPlaceShape");
            public static readonly int PlaceSeed = Shader.PropertyToID("_NyxPlaceSeed");
            public static readonly int PlaceClumps = Shader.PropertyToID("_NyxPlaceClumps");
            public static readonly int PlaceRandomVariants = Shader.PropertyToID("_NyxPlaceRandomVariants");
            public static readonly int PlaceTested = Shader.PropertyToID("_NyxPlaceTested");
            public static readonly int PlaceClip = Shader.PropertyToID("_NyxPlaceClip");
            public static readonly int PlaceAnchors = Shader.PropertyToID("_NyxPlaceAnchors");
            public static readonly int PlaceList = Shader.PropertyToID("_NyxPlaceList");
            public static readonly int PlaceMask = Shader.PropertyToID("_NyxPlaceMask");
            public static readonly int PlaceGroups = Shader.PropertyToID("_NyxPlaceGroups");
            public static readonly int Stats = Shader.PropertyToID("_NyxBuildStats");
            public static readonly int Instances = Shader.PropertyToID("_NyxBuildInstances");
            public static readonly int Placements = Shader.PropertyToID("_NyxBuildPlacements");
        }
    }
}
