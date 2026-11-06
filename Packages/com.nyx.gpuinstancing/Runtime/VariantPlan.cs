using System.Collections.Generic;
using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Variantes d'une reconstruction : LODs utilisables de chacune (0 = variante désactivée), variantes dessinables, nombre de
    /// draws. Vérifie les limites (MaxVariants, MaxBuckets) et pose les variantes dessinables pour les noyaux de construction
    /// (InstancingVariants.hlsl). Une instance d'une variante non dessinable pointe vers la variante désactivée (= Count).
    /// </summary>
    internal sealed class VariantPlan
    {
        static readonly int VariantCountId = Shader.PropertyToID("_NyxVariantCount");
        static readonly int ValidVariantsLowId = Shader.PropertyToID("_NyxValidVariantsLow");
        static readonly int ValidVariantsHighId = Shader.PropertyToID("_NyxValidVariantsHigh");

        VariantPlan(int[] lodCounts, int drawCount, uint validLow, uint validHigh)
        {
            LodCounts = lodCounts;
            DrawCount = drawCount;
            ValidLow = validLow;
            ValidHigh = validHigh;
        }

        /// <summary>Nombre de variantes.</summary>
        public int Count => LodCounts.Length;

        /// <summary>LODs utilisables de chaque variante, 0 = variante désactivée.</summary>
        public int[] LodCounts { get; }

        /// <summary>Draws (variantes × LODs).</summary>
        public int DrawCount { get; }

        /// <summary>Variantes dessinables, bits 0..31 et 32..63.</summary>
        public uint ValidLow { get; }
        public uint ValidHigh { get; }

        /// <summary>Index de la variante désactivée (= nombre de variantes).</summary>
        public int DeadVariant => Count;

        /// <summary>
        /// Plan des variantes, ou null si une limite est dépassée ('error' dit laquelle). 'warnings' reçoit les matériaux sans
        /// GPU Instancing (rien ne s'afficherait dans les builds).
        /// </summary>
        public static VariantPlan Create(IReadOnlyList<InstanceVariant> variants, List<string> warnings, out string error)
        {
            error = null;
            int variantCount = variants.Count;
            if (variantCount > MeshSpawner.MaxVariants)
            {
                error = $"{variantCount} variantes > {MeshSpawner.MaxVariants}. Réduisez les variantes.";
                return null;
            }
            var lodCounts = new int[variantCount];
            int drawCount = 0;
            uint validLow = 0, validHigh = 0;
            for (int v = 0; v < variantCount; v++)
            {
                lodCounts[v] = LodCount(variants[v]);
                drawCount += lodCounts[v];
                if (lodCounts[v] == 0)
                    continue;
                if (v < 32)
                    validLow |= 1u << v;
                else
                    validHigh |= 1u << (v - 32);
                // Avec « Instancing Variants : Strip Unused » (réglage par défaut), un build supprime les variantes
                // instanciées des shaders dont aucun matériau n'a l'instancing coché : rien ne s'afficherait.
                if (!variants[v].material.enableInstancing)
                    warnings.Add($"cochez « Enable GPU Instancing » sur le matériau '{variants[v].material.name}', sinon rien ne s'affichera dans les builds.");
            }
            if (drawCount > MeshSpawner.MaxBuckets)
            {
                error = $"{drawCount} draws (variantes × LODs) > {MeshSpawner.MaxBuckets}. Réduisez les variantes ou les LODs.";
                return null;
            }
            return new VariantPlan(lodCounts, drawCount, validLow, validHigh);
        }

        /// <summary>Variantes pour un noyau de construction : nombre, variantes dessinables, variante désactivée.</summary>
        public void Apply(ComputeShader shader)
        {
            shader.SetInt(VariantCountId, Count);
            shader.SetInt(ValidVariantsLowId, unchecked((int)ValidLow));
            shader.SetInt(ValidVariantsHighId, unchecked((int)ValidHigh));
            shader.SetInt(ShaderIds.DeadVariant, DeadVariant);
        }

        /// <summary>LODs utilisables d'une variante (meshes à la suite, matériau présent), 0 = variante désactivée.</summary>
        static int LodCount(InstanceVariant variant)
        {
            if (variant == null || variant.material == null || variant.lodMeshes == null)
                return 0;
            int count = 0;
            while (count < variant.lodMeshes.Length && count < MeshSpawner.MaxLods && variant.lodMeshes[count] != null)
                count++;
            return count;
        }
    }
}
