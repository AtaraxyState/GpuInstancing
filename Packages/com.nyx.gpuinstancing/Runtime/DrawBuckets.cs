using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Les draws d'un spawner, refaits à chaque reconstruction. Un « bucket » = un draw = (variante, LOD) : mesh, sous-mesh,
    /// matériau, zone d'index dans le buffer des visibles et arguments indirects. Chaque zone d'index (4 octets par instance)
    /// est dimensionnée pour le pire cas (toutes les instances de la variante dans ce LOD) ; les données de rendu (64 octets)
    /// ne sont écrites qu'une fois par instance visible, toutes variantes confondues. Plus la table des variantes lue par le
    /// culling GPU (bounds, distances de LOD, premier draw).
    /// </summary>
    internal sealed class DrawBuckets
    {
        /// <summary>Arguments d'un draw indirect : index par instance, instances, premier index, sommet de base, première instance.</summary>
        public const int ArgsPerDraw = 5;

        readonly DepthlessMaterials _depthless = new DepthlessMaterials();
        ComputeBuffer _variantBuffer;
        ComputeBuffer _offsetBuffer;

        /// <summary>Nombre de draws (0 tant que rien n'est construit).</summary>
        public int Count { get; private set; }
        public Mesh[] Meshes { get; private set; } = Array.Empty<Mesh>();
        public Material[] Materials { get; private set; } = Array.Empty<Material>();
        public int[] SubMeshes { get; private set; } = Array.Empty<int>();

        /// <summary>Libellé de chaque draw (« v0/LOD1 »...).</summary>
        public string[] Labels { get; private set; } = Array.Empty<string>();

        /// <summary>Début de la zone d'index de chaque draw.</summary>
        public uint[] Offsets { get; private set; } = Array.Empty<uint>();

        /// <summary>Arguments indirects initiaux d'une caméra : bloc caméra puis bloc ombre (ArgsPerDraw par draw).</summary>
        public uint[] ArgsTemplate { get; private set; } = Array.Empty<uint>();

        /// <summary>Instances qui peuvent être visibles (variantes dessinables).</summary>
        public int VisibleCapacity { get; private set; }

        /// <summary>Total des zones d'index.</summary>
        public int IndexCapacity { get; private set; }

        /// <summary>Index de la variante désactivée (= nombre de variantes à la construction).</summary>
        public int DeadVariant { get; private set; }

        public ComputeBuffer VariantBuffer => _variantBuffer;
        public ComputeBuffer OffsetBuffer => _offsetBuffer;
        public long GpuBytes => SpawnerGpu.Size(_variantBuffer) + SpawnerGpu.Size(_offsetBuffer);

        /// <summary>
        /// Draws des variantes (lodCounts : LODs valides de chacune, 0 = variante désactivée), d'après le nombre d'instances
        /// de chaque variante. Avec excludeFromDepthTexture, les draws utilisent des copies des matériaux hors profondeur.
        /// </summary>
        public void Build(IReadOnlyList<InstanceVariant> variants, int[] lodCounts, NativeArray<int> perVariant, MeshSpawnerBehavior behavior,
                          float boundsPadding, bool excludeFromDepthTexture)
        {
            int variantCount = lodCounts.Length;
            int bucketCount = 0;
            for (int v = 0; v < variantCount; v++)
                bucketCount += lodCounts[v];   // <= MeshSpawner.MaxBuckets (vérifié par le spawner)

            Meshes = new Mesh[bucketCount];
            Materials = new Material[bucketCount];
            SubMeshes = new int[bucketCount];
            Labels = new string[bucketCount];
            Offsets = new uint[Mathf.Max(bucketCount, 1)];
            ArgsTemplate = new uint[Mathf.Max(bucketCount, 1) * ArgsPerDraw * 2];   // bloc caméra + bloc ombre
            var variantInfos = new GpuVariantInfo[variantCount + 1];
            int bucket = 0;
            uint indexSlots = 0;
            int validInstances = 0;

            for (int v = 0; v < variantCount; v++)
            {
                if (lodCounts[v] == 0)
                {
                    variantInfos[v] = GpuVariantInfo.Disabled;
                    continue;
                }

                InstanceVariant variant = variants[v];
                int lods = lodCounts[v];
                validInstances += perVariant[v];
                Bounds local = LocalBounds(variant, lods);
                float padding = behavior.GetBoundsPadding(variant, local, boundsPadding);
                float cull = variant.cullDistance > 0f ? variant.cullDistance * variant.cullDistance : float.MaxValue;
                variantInfos[v] = new GpuVariantInfo
                {
                    boundsCenter = local.center,
                    boundsExtents = local.extents + Vector3.one * padding,
                    lodDistancesSq = new Vector4(LodStartSq(variant, 1, lods), LodStartSq(variant, 2, lods), LodStartSq(variant, 3, lods), cull),
                    firstBucket = (uint)bucket,
                    lodCount = (uint)lods
                };

                for (int lod = 0; lod < lods; lod++, bucket++)
                {
                    Mesh mesh = variant.lodMeshes[lod];
                    int subMesh = Mathf.Clamp(variant.subMeshIndex, 0, mesh.subMeshCount - 1);
                    Meshes[bucket] = mesh;
                    Materials[bucket] = _depthless.For(variant.material, excludeFromDepthTexture);
                    SubMeshes[bucket] = subMesh;
                    Labels[bucket] = $"v{v}/LOD{lod}";
                    Offsets[bucket] = indexSlots;
                    indexSlots += (uint)perVariant[v];

                    for (int block = 0; block < 2; block++)
                    {
                        int a = (block * bucketCount + bucket) * ArgsPerDraw;
                        ArgsTemplate[a + 0] = mesh.GetIndexCount(subMesh);
                        ArgsTemplate[a + 1] = 0;
                        ArgsTemplate[a + 2] = mesh.GetIndexStart(subMesh);
                        ArgsTemplate[a + 3] = mesh.GetBaseVertex(subMesh);
                        ArgsTemplate[a + 4] = 0;
                    }
                }
            }
            variantInfos[variantCount] = GpuVariantInfo.Disabled;

            _variantBuffer = new ComputeBuffer(variantInfos.Length, GpuVariantInfo.Stride, ComputeBufferType.Default);
            _offsetBuffer = new ComputeBuffer(Offsets.Length, sizeof(uint), ComputeBufferType.Default);
            _variantBuffer.SetData(variantInfos);
            _offsetBuffer.SetData(Offsets);
            VisibleCapacity = validInstances;
            IndexCapacity = (int)indexSlots;
            DeadVariant = variantCount;
            Count = bucketCount;
        }

        /// <summary>Les copies hors profondeur suivent leur matériau d'origine (à chaque rendu).</summary>
        public void SyncMaterials() => _depthless.Sync();

        /// <summary>Buffers et copies de matériaux. Les tableaux restent lisibles (libellés du dernier build).</summary>
        public void Release()
        {
            _variantBuffer?.Release();
            _offsetBuffer?.Release();
            _variantBuffer = _offsetBuffer = null;
            Count = 0;
            _depthless.Release();
        }

        /// <summary>LODs utilisables d'une variante (meshes à la suite, matériau présent), 0 = variante désactivée.</summary>
        public static int LodCount(InstanceVariant variant)
        {
            if (variant == null || variant.material == null || variant.lodMeshes == null)
                return 0;
            int count = 0;
            while (count < variant.lodMeshes.Length && count < MeshSpawner.MaxLods && variant.lodMeshes[count] != null)
                count++;
            return count;
        }

        static Bounds LocalBounds(InstanceVariant variant, int lods)
        {
            Bounds bounds = variant.lodMeshes[0].bounds;
            for (int i = 1; i < lods; i++)
                bounds.Encapsulate(variant.lodMeshes[i].bounds);
            return bounds;
        }

        /// <summary>Distance² de début du LOD 'lod' (seuils forcés croissants), +infini si ce LOD n'existe pas.</summary>
        static float LodStartSq(InstanceVariant variant, int lod, int lodCount)
        {
            if (lod >= lodCount || variant.lodDistances == null || variant.lodDistances.Length < lod)
                return float.MaxValue;
            float distance = 0f;
            for (int i = 0; i < lod; i++)
                distance = Mathf.Max(distance, variant.lodDistances[i]);
            return distance * distance;
        }
    }
}
