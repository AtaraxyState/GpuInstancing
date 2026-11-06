using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Buffer d'instances d'un spawner et sa construction : par défaut sur GPU (<see cref="GpuPlacement"/> écrit les instances
    /// placées directement dans le buffer) ; sinon le comportement écrit ses instances en C# (placements lus sur GPU), puis
    /// elles sont envoyées, et <see cref="GpuVariantCounter"/> les compte par variante.
    ///
    /// Mémoire : le buffer d'instances (64 o chacune), gardé d'une reconstruction à l'autre s'il a la bonne taille. Côté CPU,
    /// les placements (28 o chacun, mémoire native) ne sont lus qu'à la demande (Placements, figer le placement) ou pour un
    /// comportement qui écrit ses instances en C# ; ces instances : N × 64 o natifs le temps de l'envoi.
    /// </summary>
    internal sealed class InstanceBuilder
    {
        static readonly int PlacementStride = Marshal.SizeOf<MeshPlacement>();

        ComputeBuffer _instances;
        NativeArray<MeshPlacement> _placements;
        PlacementSetup _placementSetup;   // placement de la dernière construction : refait sur GPU pour Placements
        PlacementList _placementList;

        /// <summary>Compute shaders de construction (placement, comptage par variante).</summary>
        public static string ShaderResources => $"{GpuPlacement.ShaderResource}, {GpuVariantCounter.ShaderResource}";

        /// <summary>Buffer d'instances (null avant la première construction).</summary>
        public ComputeBuffer Instances => _instances;

        /// <summary>Origine des instances de la dernière construction.</summary>
        public MeshSpawner.UploadMethod LastUploadMethod { get; private set; }

        public long GpuBytes => SpawnerGpu.Size(_instances);

        /// <summary>Placements gardés en mémoire native (octets) : seulement s'ils ont été lus.</summary>
        public long CpuMemoryBytes => _placements.IsCreated ? (long)_placements.Length * PlacementStride : 0L;

        /// <summary>Vue en lecture seule des placements de la dernière construction, lus sur GPU à la première demande.</summary>
        public IReadOnlyList<MeshPlacement> Placements => _placementList ??= new PlacementList(this);

        /// <summary>Compute shaders de construction (une fois pour tous les spawners). false si l'un est introuvable.</summary>
        public static bool LoadShaders() => GpuPlacement.LoadShader() && GpuVariantCounter.LoadShader();

        /// <summary>Début d'une reconstruction : les placements lus et le placement mémorisé sont oubliés.</summary>
        public void BeginRebuild()
        {
            DisposePlacements();
            _placementSetup = null;
            LastUploadMethod = MeshSpawner.UploadMethod.None;
        }

        /// <summary>
        /// Instances du placement : construites sur GPU, ou écrites en C# si le comportement le demande (placements lus sur
        /// GPU, CreateInstances, envoi). Renvoie le nombre d'instances ; 'positions' : boîte des pivots, 'maxScale' : plus
        /// grande échelle (si au moins une instance).
        /// </summary>
        public int BuildPlaced(MeshSpawnerBehavior behavior, PlacementSetup setup, VariantPlan variants, NativeArray<int> perVariant,
                               out Bounds positions, out float maxScale)
        {
            _placementSetup = setup;
            using GpuPlacement.Pass pass = GpuPlacement.Count(setup, perVariant);
            positions = pass.Positions;
            maxScale = pass.MaxScale;
            int count = pass.Kept;
            EnsureInstanceBuffer(count);
            if (count == 0)
                return 0;

            if (MeshSpawner.CreateInstancesOnGpu && !behavior.CreatesInstancesOnCpu)
            {
                GpuPlacement.WriteInstances(pass, _instances, variants);
                LastUploadMethod = MeshSpawner.UploadMethod.BuiltOnGpu;
                return count;
            }

            _placements = GpuPlacement.ReadPlacements(pass, Allocator.Persistent);
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
            GpuVariantCounter.Count(_instances, count, variants, perVariant);   // le comportement a pu changer les variantes
            return count;
        }

        /// <summary>Instances propres au comportement (UsesPlacement = false) : sur GPU si possible, sinon en C# puis envoyées.</summary>
        public int BuildFromBehavior(MeshSpawnerBehavior behavior, VariantPlan variants, NativeArray<int> perVariant, out Bounds bounds)
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
            GpuVariantCounter.Count(_instances, count, variants, perVariant);
            return count;
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

        /// <summary>Placements de la dernière construction, refaits sur GPU et lus à la première demande (puis gardés).</summary>
        NativeArray<MeshPlacement> EnsurePlacements()
        {
            if (!_placements.IsCreated && _placementSetup != null && SystemInfo.supportsComputeShaders && GpuPlacement.LoadShader())
                _placements = GpuPlacement.ReadPlacements(_placementSetup, Allocator.Persistent);
            return _placements;
        }

        void DisposePlacements()
        {
            if (_placements.IsCreated)
                _placements.Dispose();
            _placements = default;
        }

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
    }
}
