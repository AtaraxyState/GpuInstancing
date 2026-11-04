using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Donnée d'instance envoyée au GPU : 64 octets, miroir de NyxInstanceData (InstancingData.hlsl).
    /// Chaque système interprète row0..row2 et custom.xyz à sa façon ; seul custom.w est commun (index de variante).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuInstanceData
    {
        public const int Stride = 64;

        public Vector4 row0;
        public Vector4 row1;
        public Vector4 row2;
        public Vector4 custom;

        public int Variant
        {
            get => (int)custom.w;
            set => custom.w = value;
        }

        /// <summary>Instance à partir d'une matrice objet -> monde (seules les 3 premières lignes sont gardées).</summary>
        public static GpuInstanceData FromMatrix(Matrix4x4 matrix, int variant, float seed = 0f)
        {
            return new GpuInstanceData
            {
                row0 = matrix.GetRow(0),
                row1 = matrix.GetRow(1),
                row2 = matrix.GetRow(2),
                custom = new Vector4(seed, 0f, 0f, variant)
            };
        }
    }

    /// <summary>Miroir de NyxVariantInfo (InstancingData.hlsl), 64 octets.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GpuVariantInfo
    {
        public const int Stride = 64;

        public Vector4 boundsCenter;
        public Vector4 boundsExtents;
        public Vector4 lodDistancesSq;
        public uint firstBucket;
        public uint lodCount;
        public uint pad0;
        public uint pad1;

        /// <summary>Variante jamais dessinée (distance de disparition négative).</summary>
        public static GpuVariantInfo Disabled => new GpuVariantInfo
        {
            boundsExtents = Vector4.one,
            lodDistancesSq = new Vector4(float.MaxValue, float.MaxValue, float.MaxValue, -1f),
            lodCount = 1
        };
    }

    /// <summary>Une variante = un mesh (et ses LODs) + un matériau. Chaque LOD coûte 1 draw call par passe.</summary>
    [Serializable]
    public class InstanceVariant
    {
        [Tooltip("LOD0 en premier, 4 meshes maximum. 1 seul mesh = 1 seul draw call pour toute la variante.")]
        public Mesh[] lodMeshes = new Mesh[1];

        [Tooltip("Distance (m) de début de chaque LOD suivant : élément 0 = début du LOD1, élément 1 = début du LOD2...")]
        public float[] lodDistances = new float[0];

        [Tooltip("Matériau utilisant un shader Nyx/GPU Instancing/* (ou compatible procedural instancing).")]
        public Material material;

        [Min(0)] public int subMeshIndex;

        [Tooltip("Distance de disparition propre à cette variante (m). 0 = distance max du champ.")]
        [Min(0f)] public float cullDistance;
    }

    /// <summary>Gestion du nombre d'instances en VR stéréo.</summary>
    public enum StereoInstancingMode
    {
        /// <summary>Auto : doublement en Single Pass Instanced (PC), pas en multiview (Android) ni en multi-pass.</summary>
        Auto,
        /// <summary>Toujours doubler quand la caméra est stéréo (à forcer si les instances n'apparaissent que dans un œil).</summary>
        ForceSinglePassInstanced,
        /// <summary>Ne jamais doubler (à forcer si les instances apparaissent en double ou clignotent).</summary>
        ForceOff
    }
}
