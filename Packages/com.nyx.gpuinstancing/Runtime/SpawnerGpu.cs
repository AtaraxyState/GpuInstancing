using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nyx.GpuInstancing
{
    /// <summary>Outils GPU partagés par la construction et le culling des instances.</summary>
    internal static class SpawnerGpu
    {
        /// <summary>Threads par groupe des noyaux « une instance par thread » (= NYX_THREADS).</summary>
        public const int ThreadGroupSize = 64;
        const int MaxGroupsPerDimension = 65535;

        /// <summary>
        /// Lance 'kernel' avec un thread par instance (groupes de 64 threads) : au-delà de 65 535 groupes, ils sont répartis
        /// en 2D et _NyxGroupsX permet au noyau de retrouver son index (index = (groupe.y × _NyxGroupsX + groupe.x) × 64 + thread).
        /// </summary>
        public static void DispatchInstances(ComputeShader shader, int kernel, int count)
        {
            if (count <= 0)
                return;
            GroupCounts((count + ThreadGroupSize - 1) / ThreadGroupSize, out int groupsX, out int groupsY);
            shader.SetInt(ShaderIds.GroupsX, groupsX);
            shader.Dispatch(kernel, groupsX, groupsY, 1);
        }

        /// <summary>Répartition de 'groups' groupes en 2D, au plus 65 535 par dimension.</summary>
        public static void GroupCounts(int groups, out int groupsX, out int groupsY)
        {
            groupsX = Mathf.Clamp(groups, 1, MaxGroupsPerDimension);
            groupsY = Mathf.Max(1, (groups + groupsX - 1) / groupsX);
        }

        /// <summary>Taille d'un buffer (octets), 0 s'il n'existe pas ou a été libéré.</summary>
        public static long Size(ComputeBuffer buffer) => buffer != null && buffer.IsValid() ? (long)buffer.count * buffer.stride : 0L;

        /// <summary>Lecture GPU bloquante dans une NativeArray (sans passer par le tas managé si la plateforme le permet).</summary>
        public static void ReadBack<T>(ComputeBuffer buffer, NativeArray<T> output) where T : struct
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
    }

    /// <summary>Propriétés des compute shaders de culling (InstancingCulling.hlsl) et de construction (InstancePlacement.compute, InstanceVariants.compute, BubbleColumnsBuild.compute).</summary>
    internal static class ShaderIds
    {
        public static readonly int Instances = Shader.PropertyToID("_NyxInstances");
        public static readonly int Variants = Shader.PropertyToID("_NyxVariants");
        public static readonly int BucketOffsets = Shader.PropertyToID("_NyxBucketOffsets");
        public static readonly int Visible = Shader.PropertyToID("_NyxVisible");
        public static readonly int VisibleIndex = Shader.PropertyToID("_NyxVisibleIndex");
        public static readonly int Counters = Shader.PropertyToID("_NyxCounters");
        public static readonly int Args = Shader.PropertyToID("_NyxArgs");
        public static readonly int InstanceCount = Shader.PropertyToID("_NyxInstanceCount");
        public static readonly int BucketCount = Shader.PropertyToID("_NyxBucketCount");
        public static readonly int GroupsX = Shader.PropertyToID("_NyxGroupsX");
        public static readonly int InstanceMultiplier = Shader.PropertyToID("_NyxInstanceMultiplier");
        public static readonly int PlaneSetCount = Shader.PropertyToID("_NyxPlaneSetCount");
        public static readonly int FrustumPlanes = Shader.PropertyToID("_NyxFrustumPlanes");
        public static readonly int CameraPosition = Shader.PropertyToID("_NyxCameraPosition");
        public static readonly int CullParams = Shader.PropertyToID("_NyxCullParams");
        public static readonly int ShadowSweep = Shader.PropertyToID("_NyxShadowSweep");
        public static readonly int Time = Shader.PropertyToID("_NyxTime");
        public static readonly int RenderData = Shader.PropertyToID("_NyxRenderData");
        public static readonly int RenderIndex = Shader.PropertyToID("_NyxRenderIndex");
        public static readonly int BucketOffset = Shader.PropertyToID("_NyxBucketOffset");
        public static readonly int DeadVariant = Shader.PropertyToID("_NyxDeadVariant");
        public static readonly int ZonePointCount = Shader.PropertyToID("_NyxZonePointCount");
        public static readonly int ZoneVertices = Shader.PropertyToID("_NyxZoneVertices");
        public static readonly int ZoneBounds = Shader.PropertyToID("_NyxZoneBounds");
        public static readonly int ZoneHeight = Shader.PropertyToID("_NyxZoneHeight");
        public static readonly int ZoneNatural = Shader.PropertyToID("_NyxZoneNatural");
        public static readonly int ZoneOrigin = Shader.PropertyToID("_NyxZoneOrigin");
    }

    /// <summary>Sections nommées dans le Profiler. En vue Timeline, les jobs Burst de chaque section apparaissent sur les workers.</summary>
    internal static class SpawnerMarkers
    {
        public static readonly ProfilerMarker Rebuild = new ProfilerMarker("MeshSpawner.Rebuild");
        public static readonly ProfilerMarker BuildInstances = new ProfilerMarker("MeshSpawner.BuildInstances");
        public static readonly ProfilerMarker Upload = new ProfilerMarker("MeshSpawner.Upload");
        public static readonly ProfilerMarker Render = new ProfilerMarker("MeshSpawner.Render");
    }
}
