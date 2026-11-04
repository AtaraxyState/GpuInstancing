using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Jobs Burst de construction : rien dans le tas managé (NativeArray libérées après l'envoi au GPU), en parallèle.
    /// CompileSynchronously : dans l'éditeur, la première reconstruction attend la compilation Burst au lieu de tourner en C#.
    /// </summary>
    public static class MeshSpawnerJobs
    {
        public const int BatchSize = 4096;

        /// <summary>Matrice lacet (rotation Y, convention Unity) + échelle uniforme + translation, en 3 lignes.</summary>
        public static GpuInstanceData TrsInstance(float3 position, float yawDegrees, float scale, float seed, int variant)
        {
            math.sincos(math.radians(yawDegrees), out float s, out float c);
            return new GpuInstanceData
            {
                row0 = new Vector4(c * scale, 0f, s * scale, position.x),
                row1 = new Vector4(0f, scale, 0f, position.y),
                row2 = new Vector4(-s * scale, 0f, c * scale, position.z),
                custom = new Vector4(seed, 0f, 0f, variant)
            };
        }

        /// <summary>Instances par défaut : position, lacet et échelle du placement, graine en custom.x.</summary>
        [BurstCompile(CompileSynchronously = true)]
        public struct TrsInstancesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<MeshPlacement> placements;
            [WriteOnly] public NativeArray<GpuInstanceData> output;

            public void Execute(int index)
            {
                MeshPlacement p = placements[index];
                output[index] = TrsInstance(p.position, p.yaw, p.scale, p.seed, p.variant);
            }
        }

        /// <summary>
        /// Placement procédural : grille perturbée (1 instance par case, position aléatoire dans la case), puis amas autour
        /// des ancres. Une graine par instance (graine du spawner + index) : déterministe et parallèle.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        internal struct GeneratePlacementsJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<MeshPlacement> output;
            [ReadOnly] public NativeArray<float3> anchors;
            public uint seed;
            public int cellsX;
            public int gridCount;
            public float2 origin;
            public float2 step;
            public float height;
            public int clumpsPerCluster;
            public float clusterRadius;
            public float2 scaleRange;
            public int variantCount;

            public void Execute(int index)
            {
                var random = new Random(math.hash(new uint2(seed, (uint)index)) | 1u);
                float3 position;
                if (index < gridCount)
                {
                    int x = index % cellsX;
                    int z = index / cellsX;
                    position = new float3(origin.x + (x + random.NextFloat()) * step.x, height, origin.y + (z + random.NextFloat()) * step.y);
                }
                else
                {
                    float3 anchor = anchors[(index - gridCount) / clumpsPerCluster];
                    float angle = random.NextFloat() * math.PI * 2f;
                    float distance = clusterRadius * math.sqrt(random.NextFloat());
                    math.sincos(angle, out float s, out float c);
                    position = new float3(anchor.x + c * distance, height, anchor.z + s * distance);
                }
                output[index] = new MeshPlacement
                {
                    position = position,
                    yaw = random.NextFloat() * 360f,
                    scale = math.lerp(scaleRange.x, scaleRange.y, random.NextFloat()),
                    variant = random.NextInt(variantCount),
                    seed = random.NextFloat()
                };
            }
        }

        /// <summary>Boîte des positions au repos et plus grande échelle : result[0] = min, result[1] = max, result[1].w = échelle.</summary>
        [BurstCompile(CompileSynchronously = true)]
        internal struct PlacementBoundsJob : IJob
        {
            [ReadOnly] public NativeArray<MeshPlacement> placements;
            [WriteOnly] public NativeArray<float4> result;

            public void Execute()
            {
                float3 min = new float3(float.MaxValue);
                float3 max = new float3(float.MinValue);
                float maxScale = 0f;
                for (int i = 0; i < placements.Length; i++)
                {
                    float3 p = placements[i].position;
                    min = math.min(min, p);
                    max = math.max(max, p);
                    maxScale = math.max(maxScale, math.abs(placements[i].scale));
                }
                result[0] = new float4(min, 0f);
                result[1] = new float4(max, maxScale);
            }
        }

        /// <summary>Instances par variante ; une variante invalide (mesh ou matériau manquant) est redirigée vers 'disabledVariant'.</summary>
        [BurstCompile(CompileSynchronously = true)]
        internal struct CountVariantsJob : IJob
        {
            public NativeArray<GpuInstanceData> instances;
            [ReadOnly] public NativeArray<int> validVariants;
            public NativeArray<int> perVariant;
            public int disabledVariant;

            public void Execute()
            {
                for (int i = 0; i < instances.Length; i++)
                {
                    GpuInstanceData data = instances[i];
                    int v = (int)data.custom.w;
                    if (v >= 0 && v < validVariants.Length && validVariants[v] != 0)
                    {
                        perVariant[v]++;
                    }
                    else
                    {
                        data.custom.w = disabledVariant;
                        instances[i] = data;
                    }
                }
            }
        }
    }
}
