using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Jobs Burst des instances écrites en C# (MeshSpawner.CreateInstancesOnGpu à false, ou base.CreateInstances appelée par
    /// un comportement). Le placement et les instances par défaut sont construits sur GPU (InstancePlacement.compute).
    /// CompileSynchronously : dans l'éditeur, la première exécution attend la compilation Burst au lieu de tourner en C#.
    /// </summary>
    public static class MeshSpawnerJobs
    {
        public const int BatchSize = 4096;

        /// <summary>
        /// Matrice lacet (rotation Y, convention Unity) + échelle uniforme + translation, en 3 lignes. Même calcul que
        /// NyxPlacementInstance (InstancingPlacement.hlsl), au sinus près.
        /// </summary>
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

        /// <summary>Instances par défaut écrites en C# : position, lacet et échelle du placement, graine en custom.x.</summary>
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
    }
}
