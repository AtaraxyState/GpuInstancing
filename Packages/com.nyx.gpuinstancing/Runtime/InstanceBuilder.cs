using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Construit le buffer d'instances d'un spawner, sur GPU (Shaders/Resources/NyxGpuInstancing/InstanceBuild.compute) :
    /// placement procédural ou liste, filtrage par la zone, données d'instance, comptage par variante. Le CPU prépare quelques
    /// paramètres et ne lit que des compteurs (total, par variante, boîte des positions) pour dimensionner les buffers.
    /// Un comportement qui écrit ses instances en C# reçoit les placements lus sur GPU ; ses instances sont envoyées ensuite.
    ///
    /// Mémoire : le buffer d'instances (64 o chacune), gardé d'une reconstruction à l'autre s'il a la bonne taille. Côté CPU,
    /// les placements (28 o chacun, mémoire native) ne sont lus qu'à la demande (Placements, figer le placement) ou pour un
    /// comportement qui écrit ses instances en C#. Pendant la construction seulement : masque des candidats (1 bit chacun) et,
    /// pour une liste, ses placements (28 o chacun) ; instances écrites en C# : N × 64 o natifs le temps de l'envoi.
    /// </summary>
    internal sealed class InstanceBuilder
    {
        public const string ShaderResource = "NyxGpuInstancing/InstanceBuild";
        const int PlaceThreadGroupSize = 256;                      // = NYX_PLACE_THREADS
        const int MaskWordsPerGroup = PlaceThreadGroupSize / 32;   // = NYX_MASK_WORDS
        const int StatsVariants = 8;                               // = NYX_STATS_VARIANTS : [0] gardés, [1..3] min, [4..6] max, [7] échelle
        const int StatsSize = StatsVariants + MeshSpawner.MaxVariants;
        static readonly int PlacementStride = Marshal.SizeOf<MeshPlacement>();

        static ComputeShader s_shader;
        static int s_kernelCountPlacements = -1;
        static int s_kernelScanGroups;
        static int s_kernelWriteInstances;
        static int s_kernelWritePlacements;
        static int s_kernelCountVariants;
        static readonly uint[] s_stats = new uint[StatsSize];
        static readonly uint[] s_statsReset = NewStatsReset();

        ComputeBuffer _instances;
        NativeArray<MeshPlacement> _placements;
        PlacementSetup _placementSetup;   // placement de la dernière construction : refait sur GPU pour Placements
        PlacementList _placementList;
        int _variantCount;
        uint _validVariantsLow;           // variantes dessinables (bits 0..31, 32..63)
        uint _validVariantsHigh;

        /// <summary>Buffer d'instances (null avant la première construction).</summary>
        public ComputeBuffer Instances => _instances;

        /// <summary>Origine des instances de la dernière construction.</summary>
        public MeshSpawner.UploadMethod LastUploadMethod { get; private set; }

        public long GpuBytes => SpawnerGpu.Size(_instances);

        /// <summary>Placements gardés en mémoire native (octets) : seulement s'ils ont été lus.</summary>
        public long CpuMemoryBytes => _placements.IsCreated ? (long)_placements.Length * PlacementStride : 0L;

        /// <summary>Vue en lecture seule des placements de la dernière construction, lus sur GPU à la première demande.</summary>
        public IReadOnlyList<MeshPlacement> Placements => _placementList ??= new PlacementList(this);

        /// <summary>Compute shader de construction et ses noyaux (une fois pour tous les spawners). false s'il est introuvable.</summary>
        public static bool LoadShader()
        {
            if (s_shader == null)
            {
                s_kernelCountPlacements = -1;
                s_shader = Resources.Load<ComputeShader>(ShaderResource);
                if (s_shader != null && s_shader.HasKernel("NyxCountPlacements") && s_shader.HasKernel("NyxScanGroups")
                    && s_shader.HasKernel("NyxWriteInstances") && s_shader.HasKernel("NyxWritePlacements")
                    && s_shader.HasKernel("NyxCountVariants"))
                {
                    s_kernelScanGroups = s_shader.FindKernel("NyxScanGroups");
                    s_kernelWriteInstances = s_shader.FindKernel("NyxWriteInstances");
                    s_kernelWritePlacements = s_shader.FindKernel("NyxWritePlacements");
                    s_kernelCountVariants = s_shader.FindKernel("NyxCountVariants");
                    s_kernelCountPlacements = s_shader.FindKernel("NyxCountPlacements");
                }
            }
            return s_kernelCountPlacements >= 0;
        }

        /// <summary>Début d'une reconstruction : les placements lus et le placement mémorisé sont oubliés.</summary>
        public void BeginRebuild()
        {
            DisposePlacements();
            _placementSetup = null;
            LastUploadMethod = MeshSpawner.UploadMethod.None;
        }

        /// <summary>
        /// Variantes de la construction : nombre et variantes dessinables (bits). Une instance d'une variante non dessinable
        /// pointe vers la variante désactivée (= variantCount) : les index restent stables.
        /// </summary>
        public void SetVariants(int variantCount, uint validLow, uint validHigh)
        {
            _variantCount = variantCount;
            _validVariantsLow = validLow;
            _validVariantsHigh = validHigh;
            ApplyVariantParameters();
        }

        /// <summary>
        /// Instances du placement, construites sur GPU directement dans le buffer d'instances. Si le comportement les écrit en
        /// C# : placements lus sur GPU, CreateInstances, envoi. Renvoie le nombre d'instances ; 'positions' : boîte des
        /// pivots, 'maxScale' : plus grande échelle (si au moins une instance).
        /// </summary>
        public int BuildPlaced(MeshSpawnerBehavior behavior, PlacementSetup setup, NativeArray<int> perVariant, out Bounds positions, out float maxScale)
        {
            _placementSetup = setup;
            using PlacementPass pass = CountPlacements(setup, perVariant, out positions, out maxScale);
            int count = pass.kept;
            EnsureInstanceBuffer(count);
            if (count == 0)
                return 0;

            if (MeshSpawner.CreateInstancesOnGpu && !behavior.CreatesInstancesOnCpu)
            {
                s_shader.SetBuffer(s_kernelWriteInstances, BuildIds.Instances, _instances);
                s_shader.Dispatch(s_kernelWriteInstances, pass.groupsX, pass.groupsY, 1);
                LastUploadMethod = MeshSpawner.UploadMethod.BuiltOnGpu;
            }
            else
            {
                _placements = ReadPlacements(pass, Allocator.Persistent);
                var instances = new NativeArray<GpuInstanceData>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                try
                {
                    behavior.CreateInstances(_placements, instances);
                    using (SpawnerMarkers.Upload.Auto())
                        Upload(instances);
                }
                finally
                {
                    instances.Dispose();
                }
                CountVariants(count, perVariant);   // le comportement a pu changer les variantes
            }
            return count;
        }

        /// <summary>Instances propres au comportement (UsesPlacement = false) : sur GPU si possible, sinon en C# puis envoyées.</summary>
        public int BuildFromBehavior(MeshSpawnerBehavior behavior, NativeArray<int> perVariant, out Bounds bounds)
        {
            int count = Mathf.Max(0, behavior.GetInstanceCount());
            EnsureInstanceBuffer(count);
            if (behavior.BuildInstancesOnGpu(_instances, count, out bounds))
            {
                if (count > 0)
                    LastUploadMethod = MeshSpawner.UploadMethod.BuiltOnGpu;
            }
            else
            {
                var instances = new NativeArray<GpuInstanceData>(count, Allocator.TempJob);   // à zéro : le comportement peut ne pas tout écrire
                try
                {
                    bounds = behavior.BuildInstances(instances);
                    if (count > 0)
                    {
                        using (SpawnerMarkers.Upload.Auto())
                            Upload(instances);
                    }
                }
                finally
                {
                    instances.Dispose();
                }
            }
            CountVariants(count, perVariant);
            return count;
        }

        /// <summary>Placements de 'setup', refaits sur GPU et lus par le CPU (lecture bloquante, 28 o chacun).</summary>
        public static NativeArray<MeshPlacement> ReadPlacements(PlacementSetup setup, Allocator allocator)
        {
            using PlacementPass pass = CountPlacements(setup, default, out _, out _);
            return ReadPlacements(pass, allocator);
        }

        /// <summary>Buffer d'instances et placements lus (spawner désactivé, ou sans comportement).</summary>
        public void Release()
        {
            _instances?.Release();
            _instances = null;
            DisposePlacements();
        }

        void EnsureInstanceBuffer(int count)
        {
            int size = Mathf.Max(count, 1);
            if (_instances != null && _instances.IsValid() && _instances.count == size)
                return;   // réutilisé : réécrit en entier, après les draws déjà soumis qui le lisent
            _instances?.Release();
            _instances = new ComputeBuffer(size, GpuInstanceData.Stride, ComputeBufferType.Default);
        }

        /// <summary>
        /// Envoie les instances écrites en C#. ComputeBuffer.SetData : copie par le pilote, puis transfert DMA. Mesuré plus
        /// rapide, jusqu'à la fin du GPU, qu'une écriture directe dans un buffer SubUpdates copié par un compute shader (lecture
        /// lente de la mémoire visible du CPU par le GPU).
        /// </summary>
        void Upload(NativeArray<GpuInstanceData> instances)
        {
            _instances.SetData(instances);
            LastUploadMethod = MeshSpawner.UploadMethod.SetData;
        }

        void ApplyVariantParameters()
        {
            ComputeShader cs = s_shader;
            cs.SetInt(BuildIds.VariantCount, _variantCount);
            cs.SetInt(BuildIds.ValidVariantsLow, unchecked((int)_validVariantsLow));
            cs.SetInt(BuildIds.ValidVariantsHigh, unchecked((int)_validVariantsHigh));
            cs.SetInt(ShaderIds.DeadVariant, _variantCount);
        }

        /// <summary>
        /// Instances écrites par un comportement (C# ou GPU) : nombre par variante, variantes invalides redirigées vers la
        /// variante désactivée (les index restent stables pour les mises à jour partielles). Lecture GPU de quelques entiers.
        /// </summary>
        void CountVariants(int count, NativeArray<int> perVariant)
        {
            for (int v = 0; v < perVariant.Length; v++)
                perVariant[v] = 0;
            if (count <= 0)
                return;
            ComputeShader cs = s_shader;
            var stats = new ComputeBuffer(StatsSize, sizeof(uint));
            try
            {
                stats.SetData(s_statsReset);
                ApplyVariantParameters();
                cs.SetInt(BuildIds.Count, count);
                cs.SetBuffer(s_kernelCountVariants, BuildIds.Instances, _instances);
                cs.SetBuffer(s_kernelCountVariants, BuildIds.Stats, stats);
                SpawnerGpu.DispatchInstances(cs, s_kernelCountVariants, count);
                stats.GetData(s_stats);
            }
            finally
            {
                stats.Release();
            }
            for (int v = 0; v < perVariant.Length; v++)
                perVariant[v] = (int)s_stats[StatsVariants + v];
        }

        /// <summary>Placements de la dernière construction, refaits sur GPU et lus à la première demande (puis gardés).</summary>
        NativeArray<MeshPlacement> EnsurePlacements()
        {
            if (!_placements.IsCreated && _placementSetup != null && SystemInfo.supportsComputeShaders && LoadShader())
                _placements = ReadPlacements(_placementSetup, Allocator.Persistent);
            return _placements;
        }

        void DisposePlacements()
        {
            if (_placements.IsCreated)
                _placements.Dispose();
            _placements = default;
        }

        static uint[] NewStatsReset()
        {
            var reset = new uint[StatsSize];
            for (int i = 1; i <= 3; i++)
                reset[i] = uint.MaxValue;   // min x, y, z (bits ordonnés)
            return reset;
        }

        // ------------------------------------------------------------------------------------------
        // Placement sur GPU
        // ------------------------------------------------------------------------------------------

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
        /// Comptage sur GPU (NyxCountPlacements + NyxScanGroups) puis lecture des compteurs (bloquante, quelques entiers) :
        /// gardés, par variante (si perVariant est créé), boîte des positions, échelle max. Le passage renvoyé garde ses
        /// buffers pour l'écriture (NyxWriteInstances ou NyxWritePlacements) : à libérer (using).
        /// </summary>
        static PlacementPass CountPlacements(PlacementSetup setup, NativeArray<int> perVariant, out Bounds positions, out float maxScale)
        {
            ComputeShader cs = s_shader;
            var pass = new PlacementPass();
            try
            {
                int count = setup.candidates;
                pass.groupCount = (count + PlaceThreadGroupSize - 1) / PlaceThreadGroupSize;
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

                positions = new Bounds();   // vide si rien n'est gardé
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
        static NativeArray<MeshPlacement> ReadPlacements(PlacementPass pass, Allocator allocator)
        {
            var output = new NativeArray<MeshPlacement>(pass.kept, allocator, NativeArrayOptions.UninitializedMemory);
            if (pass.kept == 0)
                return output;
            var buffer = new ComputeBuffer(pass.kept, PlacementStride);
            try
            {
                s_shader.SetBuffer(s_kernelWritePlacements, BuildIds.Placements, buffer);
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

        /// <summary>Flottant à partir de ses bits ordonnés (NyxOrderedBits, InstanceBuild.compute).</summary>
        static float Ordered(uint bits) =>
            BitConverter.Int32BitsToSingle(unchecked((int)((bits & 0x80000000u) != 0 ? bits & 0x7FFFFFFFu : ~bits)));

        /// <summary>Vue en lecture seule des placements (lus sur GPU à la première demande, mémoire native), sans copie.</summary>
        sealed class PlacementList : IReadOnlyList<MeshPlacement>
        {
            readonly InstanceBuilder _owner;

            public PlacementList(InstanceBuilder owner) => _owner = owner;

            public int Count
            {
                get
                {
                    NativeArray<MeshPlacement> placements = _owner.EnsurePlacements();
                    return placements.IsCreated ? placements.Length : 0;
                }
            }

            public MeshPlacement this[int index] => _owner.EnsurePlacements()[index];

            public IEnumerator<MeshPlacement> GetEnumerator()
            {
                NativeArray<MeshPlacement> placements = _owner.EnsurePlacements();
                int count = placements.IsCreated ? placements.Length : 0;
                for (int i = 0; i < count; i++)
                    yield return _owner._placements[i];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

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
