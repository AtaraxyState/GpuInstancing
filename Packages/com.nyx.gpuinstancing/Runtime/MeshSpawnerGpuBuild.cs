using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Construction des instances sur GPU (Shaders/Resources/NyxGpuInstancing/InstanceBuild.compute) : placement procédural
    /// ou liste, filtrage par la zone, données d'instance, comptage par variante. Le CPU prépare quelques paramètres et ne lit
    /// que des compteurs (total, par variante, boîte des positions) pour dimensionner les buffers.
    /// </summary>
    public sealed partial class MeshSpawner
    {
        /// <summary>Variantes maximum d'un spawner (= NYX_MAX_VARIANTS, InstanceBuild.compute).</summary>
        public const int MaxVariants = 64;

        const string BuildShaderResource = "NyxGpuInstancing/InstanceBuild";
        const int PlaceThreadGroupSize = 256;                      // = NYX_PLACE_THREADS
        const int MaskWordsPerGroup = PlaceThreadGroupSize / 32;   // = NYX_MASK_WORDS
        const int StatsVariants = 8;                               // = NYX_STATS_VARIANTS : [0] gardés, [1..3] min, [4..6] max, [7] échelle
        const int StatsSize = StatsVariants + MaxVariants;
        static readonly int PlacementStride = Marshal.SizeOf<MeshPlacement>();

        static ComputeShader s_buildShader;
        static int s_kernelCountPlacements = -1;
        static int s_kernelScanGroups;
        static int s_kernelWriteInstances;
        static int s_kernelWritePlacements;
        static int s_kernelCountVariants;
        static readonly uint[] s_stats = new uint[StatsSize];
        static readonly uint[] s_statsReset = NewStatsReset();

        uint _validVariantsLow;           // variantes dessinables à la reconstruction (bits 0..31, 32..63)
        uint _validVariantsHigh;
        PlacementSetup _placementSetup;   // placement de la dernière reconstruction : refait sur GPU pour Placements

        /// <summary>
        /// Instances placées construites sur GPU (par défaut). false : les placements générés sur GPU sont lus par le CPU,
        /// le comportement écrit les instances en C# (CreateInstances), puis elles sont envoyées au GPU : bien plus lent, pour
        /// comparer ou déboguer. Un comportement qui surcharge CreateInstance ou CreateInstances passe toujours par le C#.
        /// </summary>
        public static bool CreateInstancesOnGpu { get; set; } = true;

        /// <summary>
        /// Lance 'kernel' avec un thread par instance (groupes de 64 threads, NYX_THREADS) : au-delà de 65 535 groupes, ils
        /// sont répartis en 2D et _NyxGroupsX permet au noyau de retrouver son index
        /// (index = (groupe.y × _NyxGroupsX + groupe.x) × 64 + thread). Pour les noyaux de construction des comportements.
        /// </summary>
        public static void DispatchInstances(ComputeShader shader, int kernel, int count)
        {
            if (count <= 0)
                return;
            GroupCounts((count + ThreadGroupSize - 1) / ThreadGroupSize, out int groupsX, out int groupsY);
            shader.SetInt(ShaderIds.GroupsX, groupsX);
            shader.Dispatch(kernel, groupsX, groupsY, 1);
        }

        static void GroupCounts(int groups, out int groupsX, out int groupsY)
        {
            groupsX = Mathf.Clamp(groups, 1, MaxGroupsPerDimension);
            groupsY = Mathf.Max(1, (groups + groupsX - 1) / groupsX);
        }

        static bool LoadBuildShader()
        {
            if (s_buildShader == null)
            {
                s_kernelCountPlacements = -1;
                s_buildShader = Resources.Load<ComputeShader>(BuildShaderResource);
                if (s_buildShader != null && s_buildShader.HasKernel("NyxCountPlacements") && s_buildShader.HasKernel("NyxScanGroups")
                    && s_buildShader.HasKernel("NyxWriteInstances") && s_buildShader.HasKernel("NyxWritePlacements")
                    && s_buildShader.HasKernel("NyxCountVariants"))
                {
                    s_kernelScanGroups = s_buildShader.FindKernel("NyxScanGroups");
                    s_kernelWriteInstances = s_buildShader.FindKernel("NyxWriteInstances");
                    s_kernelWritePlacements = s_buildShader.FindKernel("NyxWritePlacements");
                    s_kernelCountVariants = s_buildShader.FindKernel("NyxCountVariants");
                    s_kernelCountPlacements = s_buildShader.FindKernel("NyxCountPlacements");
                }
            }
            return s_kernelCountPlacements >= 0;
        }

        static uint[] NewStatsReset()
        {
            var reset = new uint[StatsSize];
            for (int i = 1; i <= 3; i++)
                reset[i] = uint.MaxValue;   // min x, y, z (bits ordonnés)
            return reset;
        }

        // ------------------------------------------------------------------------------------------
        // Reconstruction
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Instances du placement (zone, amas, liste), construites sur GPU directement dans le buffer d'instances. Si le
        /// comportement les écrit en C# : placements lus sur GPU, CreateInstances, envoi. Renvoie le nombre d'instances.
        /// </summary>
        int BuildPlacedInstances(MeshSpawnerBehavior behavior, NativeArray<int> perVariant)
        {
            _placementSetup = PreparePlacement();
            using PlacementPass pass = CountPlacements(_placementSetup, perVariant, out Bounds positions, out float maxScale);
            int count = pass.kept;
            EnsureInstanceBuffer(count);
            _buildBounds = new Bounds(transform.position, Vector3.one);
            if (count == 0)
                return 0;

            if (CreateInstancesOnGpu && !behavior.CreatesInstancesOnCpu)
            {
                s_buildShader.SetBuffer(s_kernelWriteInstances, BuildIds.Instances, _instanceBuffer);
                s_buildShader.Dispatch(s_kernelWriteInstances, pass.groupsX, pass.groupsY, 1);
                LastUploadMethod = UploadMethod.BuiltOnGpu;
            }
            else
            {
                _placements = ReadPlacements(pass, Allocator.Persistent);
                var instances = new NativeArray<GpuInstanceData>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                try
                {
                    behavior.CreateInstances(_placements, instances);
                    using (Markers.Upload.Auto())
                        UploadAll(instances);
                }
                finally
                {
                    instances.Dispose();
                }
                CountVariantsOnGpu(count, perVariant);   // le comportement a pu changer les variantes
            }

            float meshExtent = (MaxVariantRadius() + boundsPadding) * maxScale;
            _buildBounds = behavior.GetPlacementBounds(positions, meshExtent);
            if (clipToVolume)
            {
                Bounds volume = ZoneVolumeBounds();
                Bounds motion = behavior.GetPlacementBounds(positions, 0f);   // pivots seulement
                MotionExceedsVolumeHeight = motion.max.y > volume.max.y + ZoneHeightTolerance || motion.min.y < volume.min.y - ZoneHeightTolerance;
                volume.Expand(meshExtent * 2f);
                _zoneDrawBounds = volume;
            }
            return count;
        }

        /// <summary>Instances propres au comportement (UsesPlacement = false) : sur GPU si possible, sinon en C# puis envoyées.</summary>
        int BuildBehaviorInstances(MeshSpawnerBehavior behavior, NativeArray<int> perVariant)
        {
            int count = Mathf.Max(0, behavior.GetInstanceCount());
            EnsureInstanceBuffer(count);
            if (behavior.BuildInstancesOnGpu(_instanceBuffer, count, out Bounds bounds))
            {
                if (count > 0)
                    LastUploadMethod = UploadMethod.BuiltOnGpu;
            }
            else
            {
                var instances = new NativeArray<GpuInstanceData>(count, Allocator.TempJob);   // à zéro : le comportement peut ne pas tout écrire
                try
                {
                    bounds = behavior.BuildInstances(instances);
                    if (count > 0)
                    {
                        using (Markers.Upload.Auto())
                            UploadAll(instances);
                    }
                }
                finally
                {
                    instances.Dispose();
                }
            }
            _buildBounds = bounds;
            CountVariantsOnGpu(count, perVariant);
            return count;
        }

        void EnsureInstanceBuffer(int count)
        {
            int size = Mathf.Max(count, 1);
            if (_instanceBuffer != null && _instanceBuffer.IsValid() && _instanceBuffer.count == size)
                return;   // réutilisé : réécrit en entier, après les draws déjà soumis qui le lisent
            ReleaseInstanceBuffer();
            _instanceBuffer = new ComputeBuffer(size, GpuInstanceData.Stride, ComputeBufferType.Default);
        }

        /// <summary>Variantes dessinables et variante désactivée, pour les noyaux de construction.</summary>
        void SetVariantParameters(ComputeShader cs)
        {
            cs.SetInt(BuildIds.VariantCount, variants.Count);
            cs.SetInt(BuildIds.ValidVariantsLow, unchecked((int)_validVariantsLow));
            cs.SetInt(BuildIds.ValidVariantsHigh, unchecked((int)_validVariantsHigh));
            cs.SetInt(ShaderIds.DeadVariant, _deadVariant);
        }

        /// <summary>
        /// Instances écrites par un comportement (C# ou GPU) : nombre par variante, variantes invalides redirigées vers la
        /// variante désactivée (les index restent stables pour les mises à jour partielles). Lecture GPU de quelques entiers.
        /// </summary>
        void CountVariantsOnGpu(int count, NativeArray<int> perVariant)
        {
            for (int v = 0; v < perVariant.Length; v++)
                perVariant[v] = 0;
            if (count <= 0)
                return;
            ComputeShader cs = s_buildShader;
            var stats = new ComputeBuffer(StatsSize, sizeof(uint));
            try
            {
                stats.SetData(s_statsReset);
                SetVariantParameters(cs);
                cs.SetInt(BuildIds.Count, count);
                cs.SetBuffer(s_kernelCountVariants, BuildIds.Instances, _instanceBuffer);
                cs.SetBuffer(s_kernelCountVariants, BuildIds.Stats, stats);
                DispatchInstances(cs, s_kernelCountVariants, count);
                stats.GetData(s_stats);
            }
            finally
            {
                stats.Release();
            }
            for (int v = 0; v < perVariant.Length; v++)
                perVariant[v] = (int)s_stats[StatsVariants + v];
        }

        // ------------------------------------------------------------------------------------------
        // Placement sur GPU
        // ------------------------------------------------------------------------------------------

        /// <summary>Paramètres du placement, figés à la reconstruction : le GPU refait le même calcul pour Placements.</summary>
        sealed class PlacementSetup
        {
            public int candidates;
            public bool fromList;
            public List<MeshPlacement> list;   // fromList : liste du spawner (relue telle quelle)
            public int gridCount;
            public int cellsX = 1;
            public Vector4 grid;               // coin min (x, z), pas (x, z)
            public Vector4 shape;              // hauteur, rayon des amas, échelle min, échelle max
            public int seed;
            public int clumps = 1;
            public int randomVariants = 1;
            public int tested;                 // candidats [0, n) testés contre le polygone
            public bool clip;
            public Vector4[] anchors = Array.Empty<Vector4>();
            public Vector2[] polygon = Array.Empty<Vector2>();   // contour effectif, sommets monde (x, z)
            public Vector4 polygonBounds;
            public Vector4 height;             // bas, haut du volume (tolérance comprise)
            public Vector4 natural;            // bord naturel : amplitude, 1 / taille, lisière
            public Vector4 origin;             // centre (x, z), graine du bruit
        }

        /// <summary>Buffers temporaires d'un passage du placement (comptage puis écriture).</summary>
        sealed class PlacementPass : IDisposable
        {
            public ComputeBuffer list, anchors, mask, groups, stats, zone;
            public int groupCount, groupsX, groupsY, kept;

            public void Dispose()
            {
                list?.Release();
                anchors?.Release();
                mask?.Release();
                groups?.Release();
                stats?.Release();
                zone?.Release();
                list = anchors = mask = groups = stats = zone = null;
            }
        }

        /// <summary>
        /// Candidats : grille perturbée sur la boîte du polygone de la zone (1 par case, position aléatoire dans la case),
        /// puis amas autour des ancres ; ou liste sérialisée. Hauteur : face du haut. Lit les ancres (thread principal).
        /// </summary>
        PlacementSetup PreparePlacement()
        {
            var setup = new PlacementSetup();
            Vector3 top = ZoneCenter;
            NativeArray<float2> polygon = ZonePolygonWorld(Allocator.Temp, out float4 polygonBounds);
            setup.polygon = new Vector2[polygon.Length];
            for (int i = 0; i < polygon.Length; i++)
                setup.polygon[i] = polygon[i];
            // Bord naturel : le contour peut sortir du polygone de ± l'amplitude du bruit, la grille et la boîte aussi
            float margin = zoneEdgeNoise;
            bool natural = zoneEdgeNoise > 0f || zoneEdgeFeather > 0f;
            float4 gridBounds = polygonBounds + new float4(-margin, -margin, margin, margin);
            setup.polygonBounds = new Vector4(gridBounds.x, gridBounds.y, gridBounds.z, gridBounds.w);
            ZoneNaturalGpu(true, out setup.natural, out setup.origin);
            setup.height = new Vector4(top.y - zoneDepth - ZoneHeightTolerance, top.y + ZoneHeightTolerance, 0f, 0f);
            setup.clip = clipToVolume;
            setup.seed = randomSeed;
            setup.randomVariants = Mathf.Max(variants.Count, 1);
            setup.shape = new Vector4(top.y, clusterRadius, scaleRange.x, scaleRange.y);

            _anchorPositions.Clear();
            foreach (Transform anchor in clusterAnchors)
                _anchorPositions.Add(anchor != null ? anchor.position : Vector3.zero);

            if (source == PlacementSource.List)
            {
                setup.fromList = true;
                setup.list = placements;
                setup.candidates = placements.Count;
            }
            else
            {
                var size = new Vector2(gridBounds.z - gridBounds.x, gridBounds.w - gridBounds.y);
                int cellsX = 0, cellsZ = 0;
                float stepX = 0f, stepZ = 0f;
                if (densityPer100m2 > 0f && size.x > 0f && size.y > 0f)
                {
                    float cell = Mathf.Sqrt(100f / densityPer100m2);
                    cellsX = Mathf.Max(1, Mathf.RoundToInt(size.x / cell));
                    cellsZ = Mathf.Max(1, Mathf.RoundToInt(size.y / cell));
                    if ((long)cellsX * cellsZ > MaxGeneratedInstances)
                    {
                        Debug.LogWarning($"{name} : {(long)cellsX * cellsZ} cases de grille demandées sur la boîte de la zone, limité à {MaxGeneratedInstances}.", this);
                        float reduction = Mathf.Sqrt((float)MaxGeneratedInstances / ((long)cellsX * cellsZ));
                        cellsX = Mathf.Max(1, (int)(cellsX * reduction));
                        cellsZ = Mathf.Max(1, (int)(cellsZ * reduction));
                    }
                    stepX = size.x / cellsX;
                    stepZ = size.y / cellsZ;
                }

                var anchors = new List<Vector4>(clusterAnchors.Count);
                foreach (Transform anchor in clusterAnchors)
                {
                    if (anchor != null)
                        anchors.Add(anchor.position);
                }
                setup.anchors = anchors.ToArray();
                setup.gridCount = cellsX * cellsZ;
                setup.cellsX = Mathf.Max(cellsX, 1);
                setup.grid = new Vector4(gridBounds.x, gridBounds.y, stepX, stepZ);
                setup.clumps = Mathf.Max(clumpsPerCluster, 1);
                setup.candidates = setup.gridCount + anchors.Count * Mathf.Max(clumpsPerCluster, 0);
                // Rectangle aligné sur les axes : la grille est dedans par construction. Ne pas la tester évite de perdre
                // les points arrondis pile sur le bord (loin de l'origine) : même placement qu'avec l'ancienne zone.
                setup.tested = !natural && IsAxisAlignedRectangle(polygon, polygonBounds) ? 0 : setup.gridCount;
            }
            polygon.Dispose();
            return setup;
        }

        /// <summary>
        /// Comptage sur GPU (NyxCountPlacements + NyxScanGroups) puis lecture des compteurs (bloquante, quelques entiers) :
        /// gardés, par variante (si perVariant est créé), boîte des positions, échelle max. Le passage renvoyé garde ses
        /// buffers pour l'écriture (NyxWriteInstances ou NyxWritePlacements) : à libérer (using).
        /// </summary>
        PlacementPass CountPlacements(PlacementSetup setup, NativeArray<int> perVariant, out Bounds positions, out float maxScale)
        {
            ComputeShader cs = s_buildShader;
            var pass = new PlacementPass();
            try
            {
                int count = setup.candidates;
                pass.groupCount = (count + PlaceThreadGroupSize - 1) / PlaceThreadGroupSize;
                GroupCounts(pass.groupCount, out pass.groupsX, out pass.groupsY);
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

                cs.SetInt(BuildIds.PlaceCount, count);
                cs.SetInt(BuildIds.PlaceGroupCount, pass.groupCount);
                cs.SetInt(BuildIds.PlaceFromList, setup.fromList ? 1 : 0);
                cs.SetInt(BuildIds.PlaceGridCount, setup.gridCount);
                cs.SetInt(BuildIds.PlaceCellsX, setup.cellsX);
                cs.SetVector(BuildIds.PlaceGrid, setup.grid);
                cs.SetVector(BuildIds.PlaceShape, setup.shape);
                cs.SetInt(BuildIds.PlaceSeed, setup.seed);
                cs.SetInt(BuildIds.PlaceClumps, setup.clumps);
                cs.SetInt(BuildIds.PlaceRandomVariants, setup.randomVariants);
                cs.SetInt(BuildIds.PlaceTested, setup.tested);
                cs.SetInt(BuildIds.PlaceClip, setup.clip ? 1 : 0);
                cs.SetInt(ShaderIds.ZonePointCount, setup.polygon.Length);
                pass.zone = new ComputeBuffer(Mathf.Max(setup.polygon.Length, 1), 2 * sizeof(float));
                pass.zone.SetData(setup.polygon);
                cs.SetBuffer(s_kernelCountPlacements, ShaderIds.ZoneVertices, pass.zone);
                cs.SetVector(ShaderIds.ZoneBounds, setup.polygonBounds);
                cs.SetVector(ShaderIds.ZoneHeight, setup.height);
                cs.SetVector(ShaderIds.ZoneNatural, setup.natural);
                cs.SetVector(ShaderIds.ZoneOrigin, setup.origin);
                cs.SetInt(ShaderIds.GroupsX, pass.groupsX);
                foreach (int kernel in new[] { s_kernelCountPlacements, s_kernelWriteInstances, s_kernelWritePlacements })
                {
                    cs.SetBuffer(kernel, BuildIds.PlaceList, pass.list);
                    cs.SetBuffer(kernel, BuildIds.PlaceAnchors, pass.anchors);
                    cs.SetBuffer(kernel, BuildIds.PlaceMask, pass.mask);
                    cs.SetBuffer(kernel, BuildIds.PlaceGroups, pass.groups);
                }
                cs.SetBuffer(s_kernelCountPlacements, BuildIds.Stats, pass.stats);
                cs.SetBuffer(s_kernelScanGroups, BuildIds.PlaceGroups, pass.groups);

                if (pass.groupCount > 0)
                {
                    cs.Dispatch(s_kernelCountPlacements, pass.groupsX, pass.groupsY, 1);
                    cs.Dispatch(s_kernelScanGroups, 1, 1, 1);
                }
                pass.stats.GetData(s_stats);
                pass.kept = (int)s_stats[0];

                positions = new Bounds(transform.position, Vector3.zero);
                maxScale = 0f;
                if (pass.kept > 0)
                {
                    var min = new Vector3(Ordered(s_stats[1]), Ordered(s_stats[2]), Ordered(s_stats[3]));
                    var max = new Vector3(Ordered(s_stats[4]), Ordered(s_stats[5]), Ordered(s_stats[6]));
                    positions.SetMinMax(min, max);
                    maxScale = BitConverter.Int32BitsToSingle(unchecked((int)s_stats[7]));
                }
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

        /// <summary>Placements gardés, dans l'ordre (NyxWritePlacements), lus par le CPU (lecture bloquante, 28 o chacun).</summary>
        NativeArray<MeshPlacement> ReadPlacements(PlacementPass pass, Allocator allocator)
        {
            var output = new NativeArray<MeshPlacement>(pass.kept, allocator, NativeArrayOptions.UninitializedMemory);
            if (pass.kept == 0)
                return output;
            var buffer = new ComputeBuffer(pass.kept, PlacementStride);
            try
            {
                s_buildShader.SetBuffer(s_kernelWritePlacements, BuildIds.Placements, buffer);
                s_buildShader.Dispatch(s_kernelWritePlacements, pass.groupsX, pass.groupsY, 1);
                ReadBack(buffer, output);
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

        NativeArray<MeshPlacement> ReadPlacements(PlacementSetup setup, Allocator allocator)
        {
            using PlacementPass pass = CountPlacements(setup, default, out _, out _);
            return ReadPlacements(pass, allocator);
        }

        /// <summary>Placements de la dernière reconstruction, refaits sur GPU et lus à la première demande (puis gardés).</summary>
        NativeArray<MeshPlacement> EnsurePlacements()
        {
            if (!_placements.IsCreated && _placementSetup != null && SystemInfo.supportsComputeShaders && LoadBuildShader())
                _placements = ReadPlacements(_placementSetup, Allocator.Persistent);
            return _placements;
        }

        /// <summary>Lecture GPU bloquante dans une NativeArray (sans passer par le tas managé si la plateforme le permet).</summary>
        static void ReadBack<T>(ComputeBuffer buffer, NativeArray<T> output) where T : struct
        {
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref output, buffer);
                request.WaitForCompletion();
                if (!request.hasError)
                    return;
            }
            var managed = new T[output.Length];
            buffer.GetData(managed);
            output.CopyFrom(managed);
        }

        /// <summary>Flottant à partir de ses bits ordonnés (NyxOrderedBits, InstanceBuild.compute).</summary>
        static float Ordered(uint bits) =>
            BitConverter.Int32BitsToSingle(unchecked((int)((bits & 0x80000000u) != 0 ? bits & 0x7FFFFFFFu : ~bits)));

        static class BuildIds
        {
            public static readonly int VariantCount = Shader.PropertyToID("_NyxVariantCount");
            public static readonly int ValidVariantsLow = Shader.PropertyToID("_NyxValidVariantsLow");
            public static readonly int ValidVariantsHigh = Shader.PropertyToID("_NyxValidVariantsHigh");
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
            public static readonly int Count = Shader.PropertyToID("_NyxBuildCount");
        }
    }
}
