using Unity.Collections;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Nombre d'instances par variante sur GPU (InstanceVariants.compute), pour les instances écrites par un comportement (en
    /// C#, ou sur GPU comme les bulles) : dimensionne les zones d'index des draws. Une instance dont la variante n'est pas
    /// dessinable passe à la variante désactivée. Lecture GPU de quelques entiers.
    /// </summary>
    internal static class GpuVariantCounter
    {
        public const string ShaderResource = "NyxGpuInstancing/InstanceVariants";
        static readonly int CountsId = Shader.PropertyToID("_NyxVariantCounts");
        static readonly uint[] s_counts = new uint[MeshSpawner.MaxVariants];
        static readonly uint[] s_zeros = new uint[MeshSpawner.MaxVariants];
        static ComputeShader s_shader;
        static int s_kernel = -1;

        /// <summary>Compute shader et noyau (une fois pour tous les spawners). false s'il est introuvable.</summary>
        public static bool LoadShader()
        {
            if (s_shader == null)
            {
                s_kernel = -1;
                s_shader = Resources.Load<ComputeShader>(ShaderResource);
                if (s_shader != null && s_shader.HasKernel("NyxCountVariants"))
                    s_kernel = s_shader.FindKernel("NyxCountVariants");
            }
            return s_kernel >= 0;
        }

        /// <summary>Instances [0, count) de 'instances' : nombre par variante dans perVariant (à zéro si count = 0).</summary>
        public static void Count(ComputeBuffer instances, int count, VariantPlan variants, NativeArray<int> perVariant)
        {
            for (int v = 0; v < perVariant.Length; v++)
                perVariant[v] = 0;
            if (count <= 0)
                return;
            ComputeShader cs = s_shader;
            var counts = new ComputeBuffer(MeshSpawner.MaxVariants, sizeof(uint));
            try
            {
                counts.SetData(s_zeros);
                variants.Apply(cs);
                cs.SetInt(ShaderIds.InstanceCount, count);
                cs.SetBuffer(s_kernel, ShaderIds.Instances, instances);
                cs.SetBuffer(s_kernel, CountsId, counts);
                SpawnerGpu.DispatchInstances(cs, s_kernel, count);
                counts.GetData(s_counts);
            }
            finally
            {
                counts.Release();
            }
            for (int v = 0; v < perVariant.Length; v++)
                perVariant[v] = (int)s_counts[v];
        }
    }
}
