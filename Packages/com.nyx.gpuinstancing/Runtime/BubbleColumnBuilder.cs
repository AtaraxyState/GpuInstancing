using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Construction sur GPU des colonnes de bulles en boucle (BubbleColumnsBuild.compute) : le CPU décrit les colonnes
    /// (position de l'émetteur, rayon, vitesses, spirale), le GPU écrit chaque bulle (graine, phase aléatoire) directement
    /// dans le buffer d'instances.
    /// </summary>
    internal static class BubbleColumnBuilder
    {
        public const string ShaderResource = "NyxGpuInstancing/BubbleColumnsBuild";
        static readonly int ColumnsId = Shader.PropertyToID("_BubbleColumns");
        static readonly int ColumnCountId = Shader.PropertyToID("_BubbleColumnCount");
        static readonly int BubbleCountId = Shader.PropertyToID("_BubbleCount");
        static readonly int SeedId = Shader.PropertyToID("_BubbleSeed");
        static ComputeShader s_shader;
        static int s_kernel = -1;

        /// <summary>Colonne en boucle telle que la lit le noyau (= NyxBubbleColumn, BubbleColumnsBuild.compute, 52 octets).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Column
        {
            public float3 origin;
            public float radius;
            public float2 surfaceRadius;
            public float2 riseSpeed;
            public float spiralRadius;
            public float spiralFrequency;
            public float travel;          // jusqu'à la surface + creux max des vagues (m)
            public int firstInstance;     // première bulle de la colonne dans le tableau de sortie
            public uint emitterIndex;     // index dans la liste des émetteurs : graine stable si une autre colonne change
        }

        /// <summary>Compute shader et noyau (une fois pour tous les comportements). false s'il est introuvable.</summary>
        public static bool LoadShader()
        {
            if (s_shader == null)
            {
                s_kernel = -1;
                s_shader = Resources.Load<ComputeShader>(ShaderResource);
                if (s_shader != null && s_shader.HasKernel("NyxBuildBubbles"))
                    s_kernel = s_shader.FindKernel("NyxBuildBubbles");
            }
            return s_kernel >= 0;
        }

        /// <summary>
        /// Écrit les 'bubbleCount' bulles des 'columnCount' premières colonnes (débuts strictement croissants) dans 'instances',
        /// à partir de l'instance 0. Tirages : 'seed', émetteur, rang dans la colonne.
        /// </summary>
        public static void Build(ComputeBuffer instances, Column[] columns, int columnCount, int bubbleCount, int seed)
        {
            var buffer = new ComputeBuffer(columnCount, Marshal.SizeOf<Column>());
            try
            {
                buffer.SetData(columns, 0, 0, columnCount);
                s_shader.SetBuffer(s_kernel, ColumnsId, buffer);
                s_shader.SetBuffer(s_kernel, ShaderIds.Instances, instances);
                s_shader.SetInt(ColumnCountId, columnCount);
                s_shader.SetInt(BubbleCountId, bubbleCount);
                s_shader.SetInt(SeedId, seed);
                SpawnerGpu.DispatchInstances(s_shader, s_kernel, bubbleCount);
            }
            finally
            {
                buffer.Release();   // détruit par Unity après le dispatch qui le lit
            }
        }
    }
}
